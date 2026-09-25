using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ArcademiaGameLauncher.Services
{
    public interface ISfxPlayer
    {
        Task PlayAsync(string fileUrl, CancellationToken ct = default);
        Task PlayRandomPeriodicAsync(CancellationToken ct = default);
        void PlayEmbedded(string resourceName, float volume = 1f);
    }

    public sealed class SfxPlayer(IApiClient apiClient, ILogger<SfxPlayer> log) : ISfxPlayer
    {
        private readonly HttpClient _http = apiClient.Http;
        private readonly ILogger<SfxPlayer> _log = log;

        public Task PlayAsync(string fileUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(fileUrl))
                return Task.CompletedTask;

            _ = Task.Run(() => PlayOneShotAsync(fileUrl, ct), CancellationToken.None);

            return Task.CompletedTask;
        }

        private async Task PlayOneShotAsync(string fileUrl, CancellationToken ct)
        {
            try
            {
                var requestUri = ToRelativeIfSameHost(_http.BaseAddress, fileUrl);

                if (_log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("[Audio] GET {Url}", requestUri);

                using var resp = await _http
                    .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (IsRedirect(resp.StatusCode) && _log.IsEnabled(LogLevel.Warning))
                {
                    _log.LogWarning(
                        "[Audio] Unexpected redirect ({Status}) for {Url}",
                        resp.StatusCode,
                        requestUri
                    );
                }

                resp.EnsureSuccessStatusCode();

                await using var net = await resp
                    .Content.ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);
                using var ms = new MemoryStream(capacity: 64 * 1024);
                await net.CopyToAsync(ms, ct).ConfigureAwait(false);
                ms.Position = 0;

                var contentType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();

                if (_log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("[Audio] Content-Type: {ContentType}", contentType);

                using var reader = CreateReaderFor(requestUri.ToString(), contentType, ms);
                using var output = new WaveOutEvent();

                output.Init(reader);
                output.Play();

                while (output.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
                    await Task.Delay(100, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested && output.PlaybackState == PlaybackState.Playing)
                    output.Stop();
            }
            catch (OperationCanceledException)
            {
                if (_log.IsEnabled(LogLevel.Information))
                    _log.LogInformation("[Audio] Playback cancelled for {Url}", fileUrl);
            }
            catch (Exception ex)
            {
                if (_log.IsEnabled(LogLevel.Error))
                    _log.LogError(ex, "[Audio] Failed to play '{Url}'", fileUrl);
            }
        }

        public void PlayEmbedded(string resourceName, float volume = 1f)
        {
            if (string.IsNullOrWhiteSpace(resourceName))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                    if (resource is null)
                    {
                        _log.LogWarning("[Audio] Embedded sound '{Resource}' not found", resourceName);
                        return;
                    }
                    using var ms = new MemoryStream();
                    await resource.CopyToAsync(ms);
                    ms.Position = 0;
                    using var reader = CreateReaderFor(resourceName, null, ms);
                    using var output = new WaveOutEvent();
                    output.Init(new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = Math.Clamp(volume, 0f, 1f) });
                    output.Play();
                    while (output.PlaybackState == PlaybackState.Playing)
                        await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[Audio] Failed to play '{Resource}'", resourceName);
                }
            });
        }

        public Task PlayRandomPeriodicAsync(CancellationToken ct = default) =>
            PlayAsync("/api/Assets/RandomPeriodicSFX", ct);

        private static bool IsRedirect(HttpStatusCode code) =>
            code == HttpStatusCode.Moved
            || code == HttpStatusCode.Redirect
            || code == HttpStatusCode.TemporaryRedirect
            || (int)code == 308;

        private static Uri ToRelativeIfSameHost(Uri baseAddress, string url)
        {
            if (baseAddress is null)
                return new Uri(url, UriKind.RelativeOrAbsolute);

            if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
            {
                if (
                    abs.Host.Equals(baseAddress.Host, StringComparison.OrdinalIgnoreCase)
                    && abs.Scheme.Equals(baseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
                    && abs.Port == baseAddress.Port
                )
                {
                    return new Uri(abs.PathAndQuery + abs.Fragment, UriKind.Relative);
                }

                return abs;
            }

            return new Uri(url, UriKind.Relative);
        }

        private static WaveStream CreateReaderFor(string url, string contentType, Stream stream)
        {
            if (
                url.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                || contentType == "audio/wav"
                || contentType == "audio/x-wav"
            )
                return new WaveFileReader(stream);

            if (
                url.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                || contentType == "audio/mpeg"
            )
                return new Mp3FileReader(stream);

            if (
                url.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase)
                || contentType == "audio/aiff"
                || contentType == "audio/x-aiff"
            )
                return new AiffFileReader(stream);

            if (
                url.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || contentType == "audio/ogg"
                || contentType == "video/ogg"
                || contentType == "application/ogg"
            )
                return new VorbisWaveReader(stream);

            throw new NotSupportedException(
                $"Unsupported audio type for '{url}' ({contentType ?? "unknown"})."
            );
        }
    }
}
