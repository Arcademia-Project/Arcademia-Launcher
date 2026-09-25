using System;
using System.Collections.Generic;

namespace ArcademiaGameLauncher.Models
{
    public sealed class CachedAchievement
    {
        public string ApiName { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
        public bool Hidden { get; set; }
        public bool AllowPersonal { get; set; }
        public int SortOrder { get; set; }
    }

    public sealed class CachedTeamHold
    {
        public string ApiName { get; set; }
        public string FirstUnlockedAt { get; set; }
        public string ClaimedBy { get; set; }
        public string ClaimedAt { get; set; }
        public bool LocalOnly { get; set; }
    }

    public sealed class CachedAchievementSet
    {
        public bool Enabled { get; set; }
        public int GameId { get; set; }
        public string GameName { get; set; }
        public string Scope { get; set; }
        public string TeamKey { get; set; }
        public string TeamLabel { get; set; }
        public string ClaimUrlBase { get; set; }
        public int ClaimLifetimeSeconds { get; set; } = 300;
        public List<CachedAchievement> Achievements { get; set; } = [];
        public List<CachedTeamHold> TeamHolds { get; set; } = [];
        public DateTime FetchedAtUtc { get; set; }

        public CachedAchievement Find(string apiName) =>
            Achievements.Find(a => string.Equals(a.ApiName, apiName, StringComparison.Ordinal));

        public CachedTeamHold FindHold(string apiName) =>
            TeamHolds.Find(h => string.Equals(h.ApiName, apiName, StringComparison.Ordinal));

        public bool IsSessional => string.Equals(Scope, "Sessional", StringComparison.Ordinal);
    }

    public enum AchievementPostKind
    {
        Accepted,
        Unknown,
        Rejected,
        Transient,
    }

    public sealed record AchievementUnlockResult(
        AchievementPostKind Kind,
        string Status,
        bool TeamHadIt,
        string TeamLabel,
        string TeamClaimedBy,
        string Message
    );

    public sealed record SessionClaimRegisterResult(
        ClaimPostKind Kind,
        string Status,
        string ClaimedBy,
        string Message
    );

    public sealed class SessionUnlock
    {
        public string ApiName { get; init; }
        public string Name { get; init; }
        public string IconPath { get; init; }
        public bool AllowPersonal { get; init; }
        public bool TeamHadIt { get; init; }
        public bool TeamClaimed { get; init; }
        public DateTime AchievedAtUtc { get; init; }
    }

    public sealed record SessionClaimOffer(
        string SessionId,
        int GameId,
        string GameName,
        string ClaimUrlBase,
        int LifetimeSeconds,
        IReadOnlyList<SessionUnlock> Unlocks,
        int UnclaimedScores
    );
}
