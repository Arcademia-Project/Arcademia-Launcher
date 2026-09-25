using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public interface ISessionClaimCoordinator
    {
        event Action<SessionClaimOffer, string, DateTime, bool> Shown;
        event Action<int> Tick;
        event Action<bool> OnlineChanged;
        event Action<string> Claimed;
        event Action Hidden;
        bool IsActive { get; }
        Task OfferAsync(SessionClaimOffer offer);
        void Cancel();
        void OnSessionClaimed(string sessionId);
    }

    public sealed class SessionClaimCoordinator : ISessionClaimCoordinator
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ClaimedDisplay = TimeSpan.FromSeconds(4);

        private readonly ISessionTrackingService _session;
        private readonly IApiClient _api;
        private readonly ILogger<SessionClaimCoordinator> _logger;
        private readonly object _gate = new();

        private string _sessionId;
        private TaskCompletionSource<string> _completion;

        public event Action<SessionClaimOffer, string, DateTime, bool> Shown;
        public event Action<int> Tick;
        public event Action<bool> OnlineChanged;
        public event Action<string> Claimed;
        public event Action Hidden;

        public bool IsActive
        {
            get
            {
                lock (_gate)
                    return _completion is not null;
            }
        }

        public SessionClaimCoordinator(
            ISessionTrackingService session,
            IApiClient api,
            ILogger<SessionClaimCoordinator> logger
        )
        {
            _session = session;
            _api = api;
            _logger = logger;
        }

        public static string GenerateCode() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        public static string HashCode(string code) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

        public async Task OfferAsync(SessionClaimOffer offer)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_completion is not null)
                    return;
                _completion = completion;
                _sessionId = offer.SessionId;
            }

            var code = GenerateCode();
            var hash = HashCode(code);
            var url = offer.ClaimUrlBase + Uri.EscapeDataString(code);
            var shownAt = DateTime.UtcNow;
            var expiresAt = shownAt.AddSeconds(offer.LifetimeSeconds);
            using var stop = new CancellationTokenSource();

            try
            {
                Shown?.Invoke(offer, url, expiresAt, true);
                _logger.LogInformation(
                    "[SessionClaim] Showing claim for session {SessionId} ({Count} achievement(s))",
                    offer.SessionId,
                    offer.Unlocks.Count
                );

                var (registered, queued) = await _session.RegisterSessionClaimAsync(offer.SessionId, hash, shownAt);
                var online = !queued && registered.Kind == ClaimPostKind.Accepted;
                OnlineChanged?.Invoke(online || registered.Kind == ClaimPostKind.Rejected);

                if (registered.Kind == ClaimPostKind.Accepted && registered.Status == "saved")
                    completion.TrySetResult(registered.ClaimedBy ?? "");
                else if (registered.Kind == ClaimPostKind.Rejected)
                    _logger.LogWarning("[SessionClaim] Registration rejected: {Message}", registered.Message);

                _ = TickLoopAsync(expiresAt, stop.Token);
                _ = PollLoopAsync(hash, completion, stop.Token);

                var remaining = expiresAt - DateTime.UtcNow;
                var winner = await Task.WhenAny(completion.Task, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero));
                stop.Cancel();

                if (winner != completion.Task)
                {
                    _logger.LogInformation("[SessionClaim] Claim for {SessionId} expired", offer.SessionId);
                    return;
                }

                var result = await completion.Task;
                if (result == "\u0000cancelled")
                {
                    _logger.LogInformation("[SessionClaim] Claim for {SessionId} dismissed", offer.SessionId);
                    if (!await _session.RemoveQueuedSessionClaimAsync(hash))
                        await _api.CancelSessionClaimAsync(hash, CancellationToken.None);
                    return;
                }

                var claimedBy = result;
                if (string.IsNullOrEmpty(claimedBy))
                {
                    var check = await _api.GetSessionClaimStatusAsync(hash, CancellationToken.None);
                    claimedBy = check.ClaimedBy ?? "";
                }

                _logger.LogInformation("[SessionClaim] Session {SessionId} claimed by {User}", offer.SessionId, claimedBy);
                Claimed?.Invoke(claimedBy);
                await Task.Delay(ClaimedDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SessionClaim] Claim flow failed for {SessionId}", offer.SessionId);
            }
            finally
            {
                stop.Cancel();
                lock (_gate)
                {
                    _completion = null;
                    _sessionId = null;
                }
                Hidden?.Invoke();
            }
        }

        private async Task TickLoopAsync(DateTime expiresAt, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Tick?.Invoke((int)Math.Max(0, (expiresAt - DateTime.UtcNow).TotalMilliseconds));
                    await Task.Delay(TickInterval, ct);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task PollLoopAsync(string hash, TaskCompletionSource<string> completion, CancellationToken ct)
        {
            var wasOnline = true;
            try
            {
                while (!ct.IsCancellationRequested && !completion.Task.IsCompleted)
                {
                    await Task.Delay(PollInterval, ct);
                    var status = await _api.GetSessionClaimStatusAsync(hash, ct);
                    var online = status.Kind != ClaimPostKind.Transient;
                    if (online != wasOnline)
                    {
                        wasOnline = online;
                        OnlineChanged?.Invoke(online);
                    }
                    if (status.Kind == ClaimPostKind.Accepted && status.Status == "saved")
                        completion.TrySetResult(status.ClaimedBy ?? "");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SessionClaim] Status polling stopped");
            }
        }

        public void Cancel()
        {
            TaskCompletionSource<string> completion;
            lock (_gate)
                completion = _completion;
            completion?.TrySetResult("\u0000cancelled");
        }

        public void OnSessionClaimed(string sessionId)
        {
            TaskCompletionSource<string> completion;
            lock (_gate)
                completion = string.Equals(_sessionId, sessionId, StringComparison.OrdinalIgnoreCase) ? _completion : null;
            completion?.TrySetResult("");
        }
    }
}
