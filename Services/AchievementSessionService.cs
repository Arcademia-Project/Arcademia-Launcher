using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using ArcademiaGameLauncher.Utils;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public sealed record AchievementToast(
        string Name,
        string Description,
        string IconPath,
        bool TeamHadIt,
        string TeamLabel,
        bool AllowPersonal
    );

    public sealed record AchievementUnlockResponse(
        bool Ok,
        string Status,
        string Error,
        string Message,
        bool Offline,
        string Render,
        CachedAchievement Achievement,
        bool TeamHadIt,
        string TeamLabel,
        string TeamClaimedBy,
        string Scope,
        DateTime AchievedAtUtc
    );

    public sealed record AchievementSnapshot(
        CachedAchievementSet Set,
        IReadOnlyList<string> UnlockedThisSession,
        bool Offline
    );

    public interface IAchievementSessionService
    {
        event Action<AchievementToast> ToastRequested;
        void BeginSession(string sessionId, int gameId, string gameName);
        SessionClaimOffer EndSession();
        Task<AchievementSnapshot> GetSnapshotAsync();
        Task<AchievementUnlockResponse> UnlockAsync(
            string apiKey,
            string apiName,
            string unlockId,
            DateTime achievedAtUtc
        );
        void RecordScoreSubmitted(string scoreId);
        void RecordScoreClaimed(string scoreId);
    }

    public sealed class AchievementSessionService : IAchievementSessionService
    {
        private static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(4);

        private readonly IAchievementCache _cache;
        private readonly ISessionTrackingService _session;
        private readonly ILogger<AchievementSessionService> _logger;
        private readonly object _gate = new();

        private string _sessionId;
        private int _gameId;
        private string _gameName;
        private bool _online;
        private Task _refresh = Task.CompletedTask;
        private readonly Dictionary<string, SessionUnlock> _unlocks = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
        private readonly HashSet<string> _scores = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _claimedScores = new(StringComparer.OrdinalIgnoreCase);

        public event Action<AchievementToast> ToastRequested;

        public AchievementSessionService(
            IAchievementCache cache,
            ISessionTrackingService session,
            ILogger<AchievementSessionService> logger
        )
        {
            _cache = cache;
            _session = session;
            _logger = logger;
        }

        public void BeginSession(string sessionId, int gameId, string gameName)
        {
            lock (_gate)
            {
                _sessionId = sessionId;
                _gameId = gameId;
                _gameName = gameName;
                _online = false;
                _unlocks.Clear();
                _pending.Clear();
                _scores.Clear();
                _claimedScores.Clear();
                _refresh = RefreshAsync(sessionId, gameId);
            }
        }

        private async Task RefreshAsync(string sessionId, int gameId)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var result = await _cache.RefreshAsync(gameId, null, cts.Token);
                lock (_gate)
                    if (_sessionId == sessionId)
                        _online = result.Online;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Achievements] Could not refresh achievements for game {GameId}", gameId);
            }
        }

        private async Task<CachedAchievementSet> CurrentSetAsync()
        {
            Task refresh;
            int gameId;
            lock (_gate)
            {
                refresh = _refresh;
                gameId = _gameId;
            }
            await Task.WhenAny(refresh, Task.Delay(RefreshWait));
            return _cache.Get(gameId);
        }

        public async Task<AchievementSnapshot> GetSnapshotAsync()
        {
            var set = await CurrentSetAsync();
            lock (_gate)
                return new AchievementSnapshot(set, _unlocks.Keys.ToList(), !_online);
        }

        public void RecordScoreSubmitted(string scoreId)
        {
            if (string.IsNullOrEmpty(scoreId))
                return;
            lock (_gate)
                _scores.Add(scoreId);
        }

        public void RecordScoreClaimed(string scoreId)
        {
            if (string.IsNullOrEmpty(scoreId))
                return;
            lock (_gate)
                _claimedScores.Add(scoreId);
        }

        private static AchievementUnlockResponse Fail(string error, string message) =>
            new(false, error, error, message, false, null, null, false, null, null, null, default);

        public async Task<AchievementUnlockResponse> UnlockAsync(
            string apiKey,
            string apiName,
            string unlockId,
            DateTime achievedAtUtc
        )
        {
            string sessionId;
            int gameId;
            lock (_gate)
            {
                sessionId = _sessionId;
                gameId = _gameId;
            }

            if (sessionId is null)
                return Fail("session_ended", "The game session is no longer active.");

            var set = await CurrentSetAsync();
            if (set is { Enabled: false })
                return Fail("disabled", "Achievements are not enabled for this game.");

            var achievement = set?.Find(apiName);
            if (set is not null && achievement is null)
            {
                using var cts = new CancellationTokenSource(RefreshWait);
                try
                {
                    set = (await _cache.RefreshAsync(gameId, null, cts.Token)).Set ?? set;
                }
                catch (OperationCanceledException) { }
                achievement = set?.Find(apiName);
                if (set is not null && achievement is null)
                    return new AchievementUnlockResponse(
                        false, "unknown", "unknown",
                        $"No achievement with the API name \"{apiName}\" exists for this game.",
                        false, null, null, false, null, null, set.Scope, default
                    );
            }

            lock (_gate)
            {
                if (_unlocks.TryGetValue(apiName, out var done))
                    return AlreadyUnlocked(set, achievement, done);
                if (!_pending.Add(apiName))
                    return AlreadyUnlocked(set, achievement, null);
            }

            try
            {
                var holdBefore = set?.FindHold(apiName);
                var (result, queued) = await _session.UnlockAchievementAsync(apiKey, apiName, unlockId, achievedAtUtc);

                if (result.Kind == AchievementPostKind.Unknown)
                    return new AchievementUnlockResponse(
                        false, "unknown", "unknown", result.Message, false, null, null, false, null, null, set?.Scope, default
                    );
                if (result.Kind == AchievementPostKind.Rejected)
                    return Fail("rejected", result.Message);
                if (achievement is null)
                    return Fail("offline", "Achievements for this game have not been downloaded yet and the manager can't be reached.");

                var offline = result.Kind == AchievementPostKind.Transient || queued;
                var status = offline ? "unlocked" : result.Status ?? "unlocked";
                var teamHadIt = offline ? holdBefore is not null : result.TeamHadIt;
                var teamLabel = offline ? set.TeamLabel : result.TeamLabel ?? set.TeamLabel;
                var claimedBy = offline ? holdBefore?.ClaimedBy : result.TeamClaimedBy;

                var unlock = new SessionUnlock
                {
                    ApiName = apiName,
                    Name = achievement.Name,
                    IconPath = achievement.IconPath,
                    AllowPersonal = achievement.AllowPersonal,
                    TeamHadIt = teamHadIt,
                    TeamClaimed = claimedBy is not null,
                    AchievedAtUtc = achievedAtUtc,
                };

                lock (_gate)
                    if (_sessionId == sessionId)
                        _unlocks[apiName] = unlock;

                if (status != "unlocked")
                    return new AchievementUnlockResponse(
                        true, status, null, null, offline, null, achievement, teamHadIt, teamLabel, claimedBy, set.Scope, achievedAtUtc
                    );

                if (!set.IsSessional)
                    _cache.RecordTeamHold(gameId, apiName, achievedAtUtc, null, offline);

                var render = FullscreenDetector.IsExclusiveFullscreen() ? "game" : "launcher";
                if (render == "launcher")
                    ToastRequested?.Invoke(new AchievementToast(
                        achievement.Name,
                        achievement.Description,
                        achievement.IconPath,
                        teamHadIt,
                        teamLabel,
                        achievement.AllowPersonal
                    ));

                _logger.LogInformation(
                    "[Achievements] Unlocked {ApiName} (team had it: {TeamHadIt}, offline: {Offline}, render: {Render})",
                    apiName, teamHadIt, offline, render
                );

                return new AchievementUnlockResponse(
                    true, "unlocked", null, null, offline, render, achievement, teamHadIt, teamLabel, claimedBy, set.Scope, achievedAtUtc
                );
            }
            finally
            {
                lock (_gate)
                    _pending.Remove(apiName);
            }
        }

        private static AchievementUnlockResponse AlreadyUnlocked(
            CachedAchievementSet set,
            CachedAchievement achievement,
            SessionUnlock done
        ) =>
            new(
                true, "alreadyUnlocked", null, null, false, null, achievement,
                done?.TeamHadIt ?? false, set?.TeamLabel, null, set?.Scope, done?.AchievedAtUtc ?? default
            );

        public SessionClaimOffer EndSession()
        {
            List<SessionUnlock> unlocks;
            string sessionId;
            int gameId;
            string gameName;
            int unclaimedScores;
            lock (_gate)
            {
                sessionId = _sessionId;
                gameId = _gameId;
                gameName = _gameName;
                unlocks = _unlocks.Values.OrderBy(u => u.AchievedAtUtc).ToList();
                unclaimedScores = _scores.Count(s => !_claimedScores.Contains(s));
                _sessionId = null;
            }

            if (sessionId is null || unlocks.Count == 0)
                return null;

            if (!unlocks.Any(u => u.AllowPersonal || !u.TeamClaimed))
            {
                _logger.LogInformation("[Achievements] Nothing claimable in session {SessionId}", sessionId);
                return null;
            }

            var set = _cache.Get(gameId);
            if (string.IsNullOrWhiteSpace(set?.ClaimUrlBase))
            {
                _logger.LogWarning("[Achievements] No claim URL known for game {GameId}; skipping the claim screen", gameId);
                return null;
            }

            return new SessionClaimOffer(
                sessionId,
                gameId,
                set.GameName ?? gameName,
                set.ClaimUrlBase,
                set.ClaimLifetimeSeconds > 0 ? set.ClaimLifetimeSeconds : 300,
                unlocks,
                unclaimedScores
            );
        }
    }
}
