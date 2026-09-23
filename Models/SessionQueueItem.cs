using System;

namespace ArcademiaGameLauncher.Models
{
    public class SessionQueueItem
    {
        public string Type { get; set; } = null!;
        public string ExternalId { get; set; } = null!;

        public int? GameId { get; set; }
        public string? LauncherStartedAtUtc { get; set; }

        public string? EndReason { get; set; }
        public string? EndedAtUtc { get; set; }

        public string ScoreId { get; set; }
        public string BoardSlug { get; set; }
        public long? ScoreValue { get; set; }
        public string PlayerName { get; set; }
        public string MetadataJson { get; set; }
        public string ApiKey { get; set; }
        public string AchievedAtUtc { get; set; }

        public string QueuedAtUtc { get; set; } = null!;
        public int AttemptCount { get; set; }
    }
}
