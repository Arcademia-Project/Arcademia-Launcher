using System;
using System.Text.Json;

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

    public sealed record ScoreReadRequest(
        string BoardSlug,
        string Scope,
        string Mode,
        string Ranks,
        string ScoreId,
        int Before,
        int After,
        string ApiKey
    );

    public sealed record ScoreReadResult(ScorePostKind Kind, JsonElement? Body, string Message);
}
