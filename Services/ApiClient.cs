using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public interface IApiClient
    {
        HttpClient Http { get; }

        Task<ControllerMapping> GetControllerMappingAsync(CancellationToken cancellationToken);
        Task<Stream> GetSiteLogoAsync(CancellationToken cancellationToken);
        Task<string> GetLatestUpdaterVersionAsync(ILogger<UpdaterService> _logger);
        Task<Stream> GetUpdaterDownloadAsync(
            string versionNumber,
            CancellationToken cancellationToken
        );
        Task<bool> UpdateRemoteUpdaterVersionAsync(
            string newVersion,
            ILogger<UpdaterService> _logger
        );
        Task<IEnumerable<GameInfo>> GetMachineGamesAsync(
            ILogger<UpdaterService> _logger,
            CancellationToken cancellationToken
        );
        Task<(Stream Stream, long? ContentLength)> GetGameDownloadAsync(
            int gameId,
            string versionNumber,
            ILogger<UpdaterService> _logger,
            CancellationToken cancellationToken
        );
        Task<bool> UpdateRemoteGameVersionAsync(
            int gameId,
            string newVersion,
            ILogger<UpdaterService> _logger
        );

        Task<byte[]> GetGameThumbnailBytesAsync(int gameId, CancellationToken cancellationToken);

        Task<ScorePostResult> PostLeaderboardScoreAsync(
            SessionQueueItem item,
            CancellationToken cancellationToken
        );

        Task<ClaimPostResult> CreateClaimAsync(
            string targetScoreId,
            string sessionId,
            string apiKey,
            CancellationToken cancellationToken
        );

        Task CancelClaimAsync(string code, CancellationToken cancellationToken);
    }

    public class ApiClient(HttpClient http) : IApiClient
    {
        private readonly HttpClient _http = http;
        public HttpClient Http => _http;

        public async Task<ControllerMapping> GetControllerMappingAsync(
            CancellationToken cancellationToken
        )
        {
            var response = await _http.GetAsync("/api/Assets/ControllerMapping", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception(
                    $"Failed to download controller mapping: {response.StatusCode} - {error}"
                );
            }
            return await response.Content.ReadFromJsonAsync<ControllerMapping>(cancellationToken);
        }

        public async Task<Stream> GetSiteLogoAsync(CancellationToken cancellationToken)
        {
            var response = await _http.GetAsync("/api/Assets/Logo", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception(
                    $"Failed to download site logo: {response.StatusCode} - {error}"
                );
            }
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }

        public async Task<string> GetLatestUpdaterVersionAsync(ILogger<UpdaterService> _logger)
        {
            var response = await _http.GetAsync("/api/UpdaterVersions/Latest");

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = await response.Content.ReadAsStringAsync();

                if (
                    response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    || response.StatusCode == System.Net.HttpStatusCode.NotFound
                )
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "[ApiClient] Warning whilst executing GetLatestUpdaterVersionAsync: {message}",
                            errorMessage
                        );
                }
                else if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(
                        "[ApiClient] Unexpected error whilst executing GetLatestUpdaterVersionAsync: {StatusCode}",
                        response.StatusCode
                    );

                throw new InvalidOperationException("Failed to retrieve UpdaterInfo.");
            }
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }

        public async Task<Stream> GetUpdaterDownloadAsync(
            string versionNumber,
            CancellationToken cancellationToken
        )
        {
            var response = await _http.GetAsync(
                $"/api/UpdaterVersions/Download?versionNumber={versionNumber}",
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"Failed to download updater: {response.StatusCode} - {error}");
            }

            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }

        public async Task<bool> UpdateRemoteUpdaterVersionAsync(
            string newVersion,
            ILogger<UpdaterService> _logger
        )
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { VersionNumber = newVersion }),
                System.Text.Encoding.UTF8,
                "application/json"
            );
            var response = await _http.PutAsync("/api/UpdaterVersions/UpdateVersion", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = await response.Content.ReadAsStringAsync();

                if (
                    response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    || response.StatusCode == System.Net.HttpStatusCode.NotFound
                )
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "[ApiClient] Warning whilst executing UpdateRemoteUpdaterVersionAsync: {message}",
                            errorMessage
                        );
                }
                else if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(
                        "[ApiClient] Unexpected error whilst executing UpdateRemoteUpdaterVersionAsync: {StatusCode}",
                        response.StatusCode
                    );
            }
            response.EnsureSuccessStatusCode();

            return response.IsSuccessStatusCode;
        }

        public async Task<IEnumerable<GameInfo>> GetMachineGamesAsync(
            ILogger<UpdaterService> _logger,
            CancellationToken cancellationToken
        )
        {
            _logger.LogInformation("[ApiClient] Fetching machine games from API...");

            var response = await _http.GetAsync($"/api/GameAssignments/Machine", cancellationToken);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[ApiClient] Received response with status code: {StatusCode}",
                    response.StatusCode
                );

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = await response.Content.ReadAsStringAsync(cancellationToken);

                if (
                    response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    || response.StatusCode == System.Net.HttpStatusCode.NotFound
                )
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "[ApiClient] Warning whilst executing GetMachineGamesAsync: {message}",
                            errorMessage
                        );
                }
                else if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(
                        "[ApiClient] Unexpected error whilst executing GetMachineGamesAsync: {StatusCode}",
                        response.StatusCode
                    );

                _logger.LogInformation("[ApiClient] Returning empty game list due to error.");

                return [];
            }
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            var games = await JsonSerializer.DeserializeAsync<IEnumerable<GameInfo>>(
                stream,
                options
            );

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[ApiClient] Retrieved {GameCount} games from API.",
                    games?.Count() ?? 0
                );

            return games ?? [];
        }

        public async Task<(Stream Stream, long? ContentLength)> GetGameDownloadAsync(
            int gameId,
            string versionNumber,
            ILogger<UpdaterService> _logger,
            CancellationToken cancellationToken
        )
        {
            var response = await _http.GetAsync(
                $"/api/GameAssignments/{gameId}/Download?versionNumber={versionNumber ?? "0.0.0"}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"Failed to download game: {response.StatusCode} - {error}");
            }

            return (
                await response.Content.ReadAsStreamAsync(cancellationToken),
                response.Content.Headers.ContentLength
            );
        }

        public async Task<bool> UpdateRemoteGameVersionAsync(
            int gameId,
            string newVersion,
            ILogger<UpdaterService> _logger
        )
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { VersionNumber = newVersion }),
                System.Text.Encoding.UTF8,
                "application/json"
            );
            var response = await _http.PutAsync(
                $"/api/GameAssignments/{gameId}/UpdateVersion",
                content
            );

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = await response.Content.ReadAsStringAsync();

                if (
                    response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    || response.StatusCode == System.Net.HttpStatusCode.NotFound
                )
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "[ApiClient] Warning whilst executing UpdateRemoteGameVersionAsync: {message}",
                            errorMessage
                        );
                }
                else if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(
                        "[ApiClient] Unexpected error whilst executing UpdateRemoteGameVersionAsync: {StatusCode}",
                        response.StatusCode
                    );
            }
            response.EnsureSuccessStatusCode();

            return response.IsSuccessStatusCode;
        }

        public async Task<byte[]> GetGameThumbnailBytesAsync(
            int gameId,
            CancellationToken cancellationToken
        )
        {
            var response = await _http.GetAsync(
                $"/api/Games/{gameId}/Thumbnail",
                cancellationToken
            );
            if (!response.IsSuccessStatusCode)
                return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length > 0 ? bytes : null;
        }

        public async Task<ScorePostResult> PostLeaderboardScoreAsync(
            SessionQueueItem item,
            CancellationToken cancellationToken
        )
        {
            JsonElement? metadata = null;
            if (!string.IsNullOrWhiteSpace(item.MetadataJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(item.MetadataJson);
                    metadata = doc.RootElement.Clone();
                }
                catch (JsonException) { }
            }

            var payload = new
            {
                scoreId = item.ScoreId,
                sessionId = item.ExternalId,
                apiKey = item.ApiKey,
                boardSlug = item.BoardSlug,
                value = item.ScoreValue,
                playerName = item.PlayerName,
                achievedAt = item.AchievedAtUtc,
                metadata,
            };

            try
            {
                using var response = await _http.PostAsJsonAsync(
                    "/api/Leaderboards/Machine/Scores",
                    payload,
                    cancellationToken
                );
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    return new ScorePostResult(
                        ScorePostKind.Accepted,
                        root.TryGetProperty("scoreId", out var id) ? id.GetString() : item.ScoreId,
                        root.TryGetProperty("rank", out var rank) && rank.ValueKind == JsonValueKind.Number
                            ? rank.GetInt64()
                            : null,
                        root.TryGetProperty("duplicate", out var dup) && dup.ValueKind == JsonValueKind.True,
                        null
                    );
                }

                var status = (int)response.StatusCode;
                var message = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
                var transient = status is 401 or 408 or 429 || status >= 500;

                return new ScorePostResult(
                    transient ? ScorePostKind.Transient : ScorePostKind.Rejected,
                    item.ScoreId,
                    null,
                    false,
                    message
                );
            }
            catch (Exception ex)
                when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return new ScorePostResult(
                    ScorePostKind.Transient,
                    item.ScoreId,
                    null,
                    false,
                    ex.Message
                );
            }
        }

        public async Task<ClaimPostResult> CreateClaimAsync(
            string targetScoreId,
            string sessionId,
            string apiKey,
            CancellationToken cancellationToken
        )
        {
            var payload = new
            {
                targetScoreId,
                sessionId,
                apiKey,
            };

            try
            {
                using var response = await _http.PostAsJsonAsync(
                    "/api/Leaderboards/Machine/Claims",
                    payload,
                    cancellationToken
                );
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    return new ClaimPostResult(
                        ClaimPostKind.Accepted,
                        root.GetProperty("claimUrl").GetString(),
                        root.GetProperty("expiresAt").GetDateTime().ToUniversalTime(),
                        null
                    );
                }

                var status = (int)response.StatusCode;
                var message = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
                var transient = status is 401 or 408 or 429 || status >= 500;

                return new ClaimPostResult(
                    transient ? ClaimPostKind.Transient : ClaimPostKind.Rejected,
                    null,
                    default,
                    message
                );
            }
            catch (Exception ex)
                when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return new ClaimPostResult(ClaimPostKind.Transient, null, default, ex.Message);
            }
        }

        public async Task CancelClaimAsync(string code, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Delete,
                    "/api/Leaderboards/Machine/Claims"
                )
                {
                    Content = JsonContent.Create(new { code }),
                };
                using var response = await _http.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Best-effort — the claim will simply expire on its own if this fails.
            }
        }
    }
}
