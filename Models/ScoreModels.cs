using System;

namespace ArcademiaGameLauncher.Models
{
    public enum ScorePostKind
    {
        Accepted,
        Rejected,
        Transient,
    }

    public sealed record ScorePostResult(
        ScorePostKind Kind,
        string ScoreId,
        long? Rank,
        bool Duplicate,
        string Message
    );

    public sealed record ScoreRequest(
        string BoardSlug,
        long Value,
        string PlayerName,
        string ScoreId,
        string MetadataJson,
        string ApiKey
    );

    public sealed record ScoreOutcome(
        string Status,
        string ScoreId,
        long? Rank,
        bool Duplicate,
        string Message
    );
}
