using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public sealed record AchievementRefresh(
        CachedAchievementSet Set,
        IReadOnlyList<string> SessionUnlocks,
        bool Online
    );

    public interface IAchievementCache
    {
        CachedAchievementSet Get(int gameId);
        Task<AchievementRefresh> RefreshAsync(int gameId, string sessionId, CancellationToken cancellationToken);
        void RecordTeamHold(int gameId, string apiName, DateTime unlockedAtUtc, string claimedBy, bool localOnly);
        Task PrefetchAsync(IEnumerable<int> gameIds, CancellationToken cancellationToken);
    }

    public sealed class AchievementCache : IAchievementCache
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        private static readonly TimeSpan DefaultIconMaxAge = TimeSpan.FromHours(6);

        private readonly string _root;
        private readonly string _iconRoot;
        private readonly IApiClient _api;
        private readonly ILogger<AchievementCache> _logger;
        private readonly ConcurrentDictionary<int, CachedAchievementSet> _memory = new();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

        public AchievementCache(string applicationPath, IApiClient api, ILogger<AchievementCache> logger)
        {
            _root = Path.Combine(applicationPath, "Achievements");
            _iconRoot = Path.Combine(_root, "Icons");
            _api = api;
            _logger = logger;
            Directory.CreateDirectory(_iconRoot);
        }

        private string SetPath(int gameId) => Path.Combine(_root, $"game_{gameId}.json");

        public CachedAchievementSet Get(int gameId)
        {
            if (_memory.TryGetValue(gameId, out var cached))
                return cached;

            try
            {
                var path = SetPath(gameId);
                if (!File.Exists(path))
                    return null;
                var set = JsonSerializer.Deserialize<CachedAchievementSet>(File.ReadAllText(path), Options);
                if (set is not null)
                    _memory[gameId] = set;
                return set;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Achievements] Could not read cache for game {GameId}", gameId);
                return null;
            }
        }

        private void Save(CachedAchievementSet set)
        {
            _memory[set.GameId] = set;
            try
            {
                var path = SetPath(set.GameId);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(set, Options));
                File.Move(temp, path, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Achievements] Could not save cache for game {GameId}", set.GameId);
            }
        }

        public async Task<AchievementRefresh> RefreshAsync(
            int gameId,
            string sessionId,
            CancellationToken cancellationToken
        )
        {
            var gate = _locks.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                var response = await _api.GetAchievementGameAsync(gameId, sessionId, cancellationToken);
                if (response.Kind != ScorePostKind.Accepted || response.Body is not JsonElement root)
                    return new AchievementRefresh(Get(gameId), [], false);

                var enabled = root.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                if (!enabled)
                {
                    var disabled = new CachedAchievementSet
                    {
                        Enabled = false,
                        GameId = gameId,
                        FetchedAtUtc = DateTime.UtcNow,
                    };
                    Save(disabled);
                    return new AchievementRefresh(disabled, [], true);
                }

                var previous = Get(gameId);
                var set = new CachedAchievementSet
                {
                    Enabled = true,
                    GameId = gameId,
                    ClaimUrlBase = Str(root, "claimUrlBase"),
                    ClaimLifetimeSeconds = root.TryGetProperty("claimLifetimeSeconds", out var life)
                        && life.TryGetInt32(out var seconds)
                            ? seconds
                            : 300,
                    FetchedAtUtc = DateTime.UtcNow,
                };

                var sessionUnlocks = new List<string>();
                if (root.TryGetProperty("set", out var body) && body.ValueKind == JsonValueKind.Object)
                {
                    set.GameName = Str(body, "gameName");
                    set.Scope = Str(body, "scope");
                    set.TeamKey = Str(body, "teamKey");
                    set.TeamLabel = Str(body, "teamLabel");

                    if (body.TryGetProperty("achievements", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var a in list.EnumerateArray())
                            set.Achievements.Add(new CachedAchievement
                            {
                                ApiName = Str(a, "apiName"),
                                Name = Str(a, "name"),
                                Description = Str(a, "description") ?? "",
                                IconUrl = Str(a, "iconUrl"),
                                Hidden = a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True,
                                AllowPersonal = a.TryGetProperty("allowPersonal", out var p) && p.ValueKind == JsonValueKind.True,
                                SortOrder = a.TryGetProperty("sortOrder", out var o) && o.TryGetInt32(out var order) ? order : 0,
                            });

                    if (body.TryGetProperty("teamHolds", out var holds) && holds.ValueKind == JsonValueKind.Array)
                        foreach (var hold in holds.EnumerateArray())
                            set.TeamHolds.Add(new CachedTeamHold
                            {
                                ApiName = Str(hold, "apiName"),
                                FirstUnlockedAt = Str(hold, "firstUnlockedAt"),
                                ClaimedBy = Str(hold, "claimedBy"),
                                ClaimedAt = Str(hold, "claimedAt"),
                            });

                    if (body.TryGetProperty("unlockedThisSession", out var session) && session.ValueKind == JsonValueKind.Array)
                        foreach (var name in session.EnumerateArray())
                            if (name.ValueKind == JsonValueKind.String)
                                sessionUnlocks.Add(name.GetString());
                }

                if (previous?.TeamKey == set.TeamKey)
                    foreach (var local in previous.TeamHolds)
                        if (
                            local.LocalOnly
                            && DateTime.TryParse(local.FirstUnlockedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                            && DateTime.UtcNow - at.ToUniversalTime() < TimeSpan.FromDays(7)
                            && set.FindHold(local.ApiName) is null
                            && set.Find(local.ApiName) is not null
                        )
                            set.TeamHolds.Add(local);

                foreach (var a in set.Achievements)
                    a.IconPath = await EnsureIconAsync(a.IconUrl, cancellationToken);

                Save(set);
                return new AchievementRefresh(set, sessionUnlocks, true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Achievements] Refresh failed for game {GameId}", gameId);
                return new AchievementRefresh(Get(gameId), [], false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<string> EnsureIconAsync(string iconUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(iconUrl))
                return null;

            var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(iconUrl))).ToLowerInvariant();
            var extension = iconUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || iconUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? ".jpg"
                    : ".png";
            var path = Path.Combine(_iconRoot, name + extension);
            var isDefault = iconUrl.EndsWith("/default", StringComparison.OrdinalIgnoreCase);

            if (File.Exists(path) && (!isDefault || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < DefaultIconMaxAge))
                return path;

            var bytes = await _api.GetBytesAsync(iconUrl, cancellationToken);
            if (bytes is null)
                return File.Exists(path) ? path : null;

            try
            {
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "[Achievements] Could not save icon {Url}", iconUrl);
            }
            return File.Exists(path) ? path : null;
        }

        public void RecordTeamHold(int gameId, string apiName, DateTime unlockedAtUtc, string claimedBy, bool localOnly)
        {
            var set = Get(gameId);
            if (set is null || set.IsSessional || set.Find(apiName) is null)
                return;

            var hold = set.FindHold(apiName);
            if (hold is null)
                set.TeamHolds.Add(new CachedTeamHold
                {
                    ApiName = apiName,
                    FirstUnlockedAt = unlockedAtUtc.ToString("o"),
                    ClaimedBy = claimedBy,
                    LocalOnly = localOnly,
                });
            else if (hold.ClaimedBy is null && claimedBy is not null)
                hold.ClaimedBy = claimedBy;
            else
                return;

            Save(set);
        }

        public async Task PrefetchAsync(IEnumerable<int> gameIds, CancellationToken cancellationToken)
        {
            foreach (var gameId in gameIds.Distinct())
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                var cached = Get(gameId);
                if (cached is not null && DateTime.UtcNow - cached.FetchedAtUtc < TimeSpan.FromMinutes(30))
                    continue;
                await RefreshAsync(gameId, null, cancellationToken);
            }
        }

        private static string Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
