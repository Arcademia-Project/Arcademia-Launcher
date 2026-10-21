namespace ArcademiaGameLauncher.Models
{
    public class SessionActivity
    {
        public int ActiveSeconds { get; set; }
        public int IdleSeconds { get; set; }
        public int IdlePeriods { get; set; }
        public int LongestIdleSeconds { get; set; }
        public int Players { get; set; }
    }

    public sealed record EndedSession(string ExternalId, string EndReason, int? ActiveSeconds);
}
