using System;

namespace ArcademiaGameLauncher.Models
{
    public enum ClaimPostKind
    {
        Accepted,
        Rejected,
        Transient,
    }

    public sealed record ClaimPostResult(
        ClaimPostKind Kind,
        string ClaimUrl,
        DateTime ExpiresAtUtc,
        string Message
    );

    public sealed record ClaimOutcome(string Status, string Message, string PlayerName = null);

    public sealed record ClaimStatusResult(ClaimPostKind Kind, string Status, string PlayerName, string Message);
}
