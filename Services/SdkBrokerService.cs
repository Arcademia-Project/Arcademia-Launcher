using System;
using System.IO;
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

        private static readonly JsonSerializerOptions ResponseOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly ISessionTrackingService _session;
        private readonly IClaimCoordinator _claims;
        private readonly IApiClient _api;
        private readonly ILogger<SdkBrokerService> _logger;
        private readonly object _gate = new();

        private CancellationTokenSource _cts;

        public SdkBrokerService(
            ISessionTrackingService session,
            IClaimCoordinator claims,
            IApiClient api,
            ILogger<SdkBrokerService> logger
        )
        {
            _session = session;
            _claims = claims;
            _api = api;
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
                        1,
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

                    case "requestClaim":
                        return await HandleRequestClaimAsync(id, root);

                    case "getScores":
                        return await HandleGetScoresAsync(id, root, environment);

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

            return Serialize(
                new
                {
                    id,
                    ok = true,
                    status = outcome.Status,
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
