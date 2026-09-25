using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public sealed record SdkSessionEnvironment(string PipeName, string Nonce, string SessionId);

    public interface ISdkBrokerService
    {
        SdkSessionEnvironment Start(string sessionId);
        void Stop();
    }

    public sealed class SdkBrokerService : ISdkBrokerService, IDisposable
    {
        public const string PipeVariable = "ARCADEMIA_PIPE";
        public const string NonceVariable = "ARCADEMIA_NONCE";
        public const string SessionVariable = "ARCADEMIA_SESSION_ID";

        private const int MaxRequestChars = 16 * 1024;
        private const int MaxConnections = 4;

        private static readonly JsonSerializerOptions ResponseOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly ISessionTrackingService _session;
        private readonly IClaimCoordinator _claims;
        private readonly IApiClient _api;
        private readonly IAchievementSessionService _achievements;
        private readonly IAchievementOverlayCoordinator _overlay;
        private readonly ILogger<SdkBrokerService> _logger;
        private readonly object _gate = new();

        private CancellationTokenSource _cts;

        public SdkBrokerService(
            ISessionTrackingService session,
            IClaimCoordinator claims,
            IApiClient api,
            IAchievementSessionService achievements,
            IAchievementOverlayCoordinator overlay,
            ILogger<SdkBrokerService> logger
        )
        {
            _session = session;
            _claims = claims;
            _api = api;
            _achievements = achievements;
            _overlay = overlay;
            _logger = logger;
            _session.SessionEnded += Stop;
        }

        public SdkSessionEnvironment Start(string sessionId)
        {
            Stop();

            var environment = new SdkSessionEnvironment(
                $"arcademia-{Guid.NewGuid():N}",
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                sessionId
            );

            var cts = new CancellationTokenSource();
            lock (_gate)
                _cts = cts;

            for (var i = 0; i < MaxConnections; i++)
                _ = Task.Run(() => RunAsync(environment, cts.Token));
            _logger.LogInformation("[SDK] Broker listening for session {SessionId}", sessionId);
            return environment;
        }

        public void Stop()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                cts = _cts;
                _cts = null;
            }

            if (cts is null)
                return;

            try
            {
                cts.Cancel();
            }
            finally
            {
                cts.Dispose();
            }

            _logger.LogInformation("[SDK] Broker stopped");
        }

        public void Dispose()
        {
            _session.SessionEnded -= Stop;
            Stop();
        }

        private async Task RunAsync(SdkSessionEnvironment environment, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        environment.PipeName,
                        PipeDirection.InOut,
                        MaxConnections,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                    );

                    await server.WaitForConnectionAsync(ct);

                    using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
                    await using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true)
                    {
                        AutoFlush = true,
                    };

                    while (server.IsConnected && !ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line is null)
                            break;

                        var response = await HandleAsync(line, environment);
                        await writer.WriteLineAsync(response.AsMemory(), ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SDK] Pipe connection error");
                    try
                    {
                        await Task.Delay(250, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task<string> HandleAsync(string line, SdkSessionEnvironment environment)
        {
            string id = null;

            if (line.Length > MaxRequestChars)
                return Error(id, "request_too_large", "Request is too large.");

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return Error(id, "bad_request", "Request must be a JSON object.");

                id = GetString(root, "id");

                if (!NonceMatches(GetString(root, "nonce"), environment.Nonce))
                    return Error(id, "unauthorized", "Invalid session token.");

                if (_session.CurrentExternalId != environment.SessionId)
                    return Error(id, "session_ended", "The game session is no longer active.");

                switch (GetString(root, "op"))
                {
                    case "hello":
                        return Serialize(
                            new
                            {
                                id,
                                ok = true,
                                mode = "launcher",
                                sessionId = environment.SessionId,
                            }
                        );

                    case "submitScore":
                        return await HandleSubmitScoreAsync(id, root);

                    case "setPlayerName":
                        return await HandleSetPlayerNameAsync(id, root);

                    case "requestClaim":
                        return await HandleRequestClaimAsync(id, root);

                    case "getScores":
                        return await HandleGetScoresAsync(id, root, environment);

                    case "achievementsHello":
                    case "getAchievements":
                        return await HandleGetAchievementsAsync(id, environment);

                    case "unlockAchievement":
                        return await HandleUnlockAchievementAsync(id, root);

                    case "openAchievements":
                        return await HandleOpenAchievementsAsync(id);

                    default:
                        return Error(id, "unknown_op", "Unknown operation.");
                }
            }
            catch (JsonException)
            {
                return Error(id, "bad_request", "Malformed JSON.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SDK] Unhandled error handling request");
                return Error(id, "internal_error", "The launcher could not process the request.");
            }
        }

        private async Task<string> HandleSubmitScoreAsync(string id, JsonElement root)
        {
            var boardSlug = GetString(root, "boardSlug");
            var apiKey = GetString(root, "apiKey");

            if (string.IsNullOrWhiteSpace(boardSlug) || boardSlug.Length > 50)
                return Error(id, "invalid_request", "boardSlug is required.");
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 128)
                return Error(id, "invalid_request", "apiKey is required.");

            if (
                !root.TryGetProperty("value", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.Number
                || !valueElement.TryGetInt64(out var value)
            )
                return Error(id, "invalid_request", "value must be an integer.");

            var playerName = GetString(root, "playerName");
            if (playerName is { Length: > 64 })
                return Error(id, "invalid_request", "playerName is too long.");

            var scoreId = Guid.NewGuid().ToString();
            var requestedScoreId = GetString(root, "scoreId");
            if (requestedScoreId is not null)
            {
                if (!Guid.TryParse(requestedScoreId, out var parsed))
                    return Error(id, "invalid_request", "scoreId must be a GUID.");
                scoreId = parsed.ToString();
            }

            string metadataJson = null;
            if (
                root.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind != JsonValueKind.Null
                && metadata.ValueKind != JsonValueKind.Undefined
            )
            {
                if (metadata.ValueKind != JsonValueKind.Object)
                    return Error(id, "invalid_request", "metadata must be a JSON object.");
                metadataJson = metadata.GetRawText();
            }

            var outcome = await _session.SubmitScoreAsync(
                new ScoreRequest(boardSlug, value, playerName, scoreId, metadataJson, apiKey)
            );
            if (outcome.Status is "submitted" or "queued")
                _achievements.RecordScoreSubmitted(outcome.ScoreId);

            return Serialize(
                new
                {
                    id,
                    ok = true,
                    status = outcome.Status,
                    scoreId = outcome.ScoreId,
                    rank = outcome.Rank,
                    duplicate = outcome.Duplicate ? (bool?)true : null,
                    message = outcome.Message,
                }
            );
        }

        private async Task<string> HandleRequestClaimAsync(string id, JsonElement root)
        {
            var apiKey = GetString(root, "apiKey");
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 128)
                return Error(id, "invalid_request", "apiKey is required.");

            var targetScoreId = GetString(root, "scoreId");
            if (!Guid.TryParse(targetScoreId, out _))
                return Error(id, "invalid_request", "scoreId must be a GUID.");

            var outcome = await _claims.RequestClaimAsync(
                targetScoreId,
                apiKey,
                CancellationToken.None
            );
            if (outcome.Status == "saved")
                _achievements.RecordScoreClaimed(targetScoreId);

            return Serialize(
                new
                {
                    id,
                    ok = true,
                    status = outcome.Status,
                    scoreId = targetScoreId,
                    playerName = outcome.PlayerName,
                    claimed = outcome.Status == "saved" ? (bool?)true : null,
                    message = outcome.Message,
                }
            );
        }

        private async Task<string> HandleSetPlayerNameAsync(string id, JsonElement root)
        {
            var apiKey = GetString(root, "apiKey");
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 128)
                return Error(id, "invalid_request", "apiKey is required.");

            if (!Guid.TryParse(GetString(root, "scoreId"), out var scoreId))
                return Error(id, "invalid_request", "scoreId must be a GUID.");

            var playerName = GetString(root, "playerName");
            if (playerName is { Length: > 64 })
                return Error(id, "invalid_request", "playerName is too long.");

            var outcome = await _session.SetPlayerNameAsync(scoreId.ToString(), playerName, apiKey);

            return Serialize(
                new
                {
                    id,
                    ok = true,
                    status = outcome.Status,
                    scoreId = outcome.ScoreId,
                    playerName = outcome.PlayerName,
                    message = outcome.Message,
                }
            );
        }

        private async Task<string> HandleGetScoresAsync(
            string id,
            JsonElement root,
            SdkSessionEnvironment environment
        )
        {
            var boardSlug = GetString(root, "boardSlug");
            var apiKey = GetString(root, "apiKey");
            var scope = GetString(root, "scope");
            var mode = GetString(root, "mode");
            var ranks = GetString(root, "ranks");
            var scoreId = GetString(root, "scoreId");

            if (string.IsNullOrWhiteSpace(boardSlug) || boardSlug.Length > 50)
                return Error(id, "invalid_request", "boardSlug is required.");
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 128)
                return Error(id, "invalid_request", "apiKey is required.");
            if (scope is { Length: > 20 })
                return Error(id, "invalid_request", "scope is too long.");
            if (mode is { Length: > 10 })
                return Error(id, "invalid_request", "mode is too long.");
            if (ranks is { Length: > 200 })
                return Error(id, "invalid_request", "ranks is too long.");
            if (scoreId is not null && !Guid.TryParse(scoreId, out _))
                return Error(id, "invalid_request", "scoreId must be a GUID.");
            if (!TryGetCount(root, "before", out var before) || !TryGetCount(root, "after", out var after))
                return Error(id, "invalid_request", "before and after must be between 0 and 50.");

            var result = await _api.ReadLeaderboardScoresAsync(
                new ScoreReadRequest(boardSlug, scope, mode, ranks, scoreId, before, after, apiKey),
                environment.SessionId,
                CancellationToken.None
            );

            if (result.Kind == ScorePostKind.Transient)
                return Error(id, "offline", result.Message ?? "The leaderboard service could not be reached.");
            if (result.Kind == ScorePostKind.Rejected)
                return Error(id, "rejected", result.Message ?? "The leaderboard request was rejected.");

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (id is null)
                    writer.WriteNull("id");
                else
                    writer.WriteString("id", id);
                writer.WriteBoolean("ok", true);
                foreach (var property in result.Body!.Value.EnumerateObject())
                    if (property.Name is not ("id" or "ok"))
                        property.WriteTo(writer);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static object AchievementJson(CachedAchievement a) =>
            a is null
                ? null
                : new
                {
                    apiName = a.ApiName,
                    name = a.Name,
                    description = a.Description,
                    iconUrl = a.IconUrl,
                    iconPath = a.IconPath,
                    hidden = a.Hidden,
                    allowPersonal = a.AllowPersonal,
                    sortOrder = a.SortOrder,
                };

        private async Task<string> HandleGetAchievementsAsync(string id, SdkSessionEnvironment environment)
        {
            var snapshot = await _achievements.GetSnapshotAsync();
            var set = snapshot.Set;
            if (set is null)
                return Error(id, "offline", "Achievements for this game have not been downloaded yet.");
            if (!set.Enabled)
                return Error(id, "disabled", "Achievements are not enabled for this game.");

            return Serialize(
                new
                {
                    id,
                    ok = true,
                    mode = "launcher",
                    sessionId = environment.SessionId,
                    offline = snapshot.Offline ? (bool?)true : null,
                    set = new
                    {
                        gameId = set.GameId,
                        gameName = set.GameName,
                        scope = set.Scope,
                        teamKey = set.TeamKey,
                        teamLabel = set.TeamLabel,
                        achievements = set.Achievements.OrderBy(a => a.SortOrder).Select(AchievementJson),
                        teamHolds = set.TeamHolds.Select(h => new
                        {
                            apiName = h.ApiName,
                            firstUnlockedAt = h.FirstUnlockedAt,
                            claimedBy = h.ClaimedBy,
                            claimedAt = h.ClaimedAt,
                        }),
                        unlockedThisSession = snapshot.UnlockedThisSession,
                    },
                }
            );
        }

        private async Task<string> HandleUnlockAchievementAsync(string id, JsonElement root)
        {
            var apiKey = GetString(root, "apiKey");
            var apiName = GetString(root, "apiName");

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 128)
                return Error(id, "invalid_request", "apiKey is required.");
            if (string.IsNullOrWhiteSpace(apiName) || apiName.Length > 64)
                return Error(id, "invalid_request", "apiName is required.");

            var unlockId = Guid.NewGuid();
            var requestedId = GetString(root, "unlockId");
            if (requestedId is not null && !Guid.TryParse(requestedId, out unlockId))
                return Error(id, "invalid_request", "unlockId must be a GUID.");

            var achievedAt = DateTime.UtcNow;
            var requestedAt = GetString(root, "achievedAt");
            if (
                requestedAt is not null
                && DateTime.TryParse(
                    requestedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed
                )
            )
            {
                var utc = parsed.ToUniversalTime();
                if (Math.Abs((utc - achievedAt).TotalMinutes) < 5)
                    achievedAt = utc;
            }

            var outcome = await _achievements.UnlockAsync(apiKey, apiName.Trim(), unlockId.ToString(), achievedAt);

            if (!outcome.Ok)
                return Serialize(
                    new
                    {
                        id,
                        ok = false,
                        status = outcome.Status,
                        error = outcome.Error,
                        message = outcome.Message,
                    }
                );

            return Serialize(
                new
                {
                    id,
                    ok = true,
                    status = outcome.Status,
                    offline = outcome.Offline ? (bool?)true : null,
                    render = outcome.Render,
                    unlock = new
                    {
                        status = outcome.Status,
                        achievement = AchievementJson(outcome.Achievement),
                        teamHadIt = outcome.TeamHadIt,
                        teamLabel = outcome.TeamLabel,
                        teamClaimedBy = outcome.TeamClaimedBy,
                        scope = outcome.Scope,
                        achievedAt = outcome.AchievedAtUtc == default
                            ? null
                            : outcome.AchievedAtUtc.ToString("o"),
                    },
                }
            );
        }

        private async Task<string> HandleOpenAchievementsAsync(string id)
        {
            var result = await _overlay.OpenAsync();
            return result switch
            {
                "game" => Serialize(new { id, ok = true, status = "renderInGame", render = "game" }),
                "closed" => Serialize(new { id, ok = true, status = "closed" }),
                _ => Error(id, result, result switch
                {
                    "alreadyOpen" => "The achievements overlay is already open.",
                    "disabled" => "Achievements are not enabled for this game.",
                    _ => "The achievements overlay could not be opened.",
                }),
            };
        }

        private static bool TryGetCount(JsonElement root, string name, out int value)
        {
            value = 0;
            if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
                return true;
            return element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out value)
                && value is >= 0 and <= 50;
        }

        private static string GetString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

        private static bool NonceMatches(string presented, string expected) =>
            presented is not null
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented),
                Encoding.UTF8.GetBytes(expected)
            );

        private static string Error(string id, string code, string message) =>
            Serialize(
                new
                {
                    id,
                    ok = false,
                    error = code,
                    message,
                }
            );

        private static string Serialize(object value) =>
            JsonSerializer.Serialize(value, ResponseOptions);
    }
}
