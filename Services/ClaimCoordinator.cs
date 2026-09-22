using System;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public interface IClaimCoordinator
    {
        event Action<string, DateTime> ClaimShown;
        event Action<int> ClaimTick;
        event Action ClaimHidden;

        Task<ClaimOutcome> RequestClaimAsync(
            string targetScoreId,
            string apiKey,
            CancellationToken cancellationToken
        );

        void OnScoreClaimed(string scoreId);
        void CancelActive();
    }

    public sealed class ClaimCoordinator : IClaimCoordinator, IDisposable
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

        private readonly IApiClient _api;
        private readonly ISessionTrackingService _session;
        private readonly ILogger<ClaimCoordinator> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _stateLock = new();

        private string _activeScoreId;
        private string _activeCode;
        private TaskCompletionSource<string> _activeCompletion;
        private Timer _tickTimer;
        private DateTime _activeExpiresAtUtc;

        public event Action<string, DateTime> ClaimShown;
        public event Action<int> ClaimTick;
        public event Action ClaimHidden;

        public ClaimCoordinator(
            IApiClient api,
            ISessionTrackingService session,
            ILogger<ClaimCoordinator> logger
        )
        {
            _api = api;
            _session = session;
            _logger = logger;
            _session.SessionEnded += () => CancelActive();
        }

        public async Task<ClaimOutcome> RequestClaimAsync(
            string targetScoreId,
            string apiKey,
            CancellationToken cancellationToken
        )
        {
            var sessionId = _session.CurrentExternalId;
            if (sessionId is null)
                return new ClaimOutcome("rejected", "No game session is active.");

            CancelActive();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var created = await _api.CreateClaimAsync(
                    targetScoreId,
                    sessionId,
                    apiKey,
                    cancellationToken
                );

                if (created.Kind != ClaimPostKind.Accepted)
                {
                    _logger.LogWarning(
                        "[Claim] CreateClaim failed for score {ScoreId}: {Message}",
                        targetScoreId,
                        created.Message
                    );
                    return new ClaimOutcome(
                        created.Kind == ClaimPostKind.Transient ? "offline" : "rejected",
                        created.Message
                    );
                }

                var code = ExtractCode(created.ClaimUrl);
                var completion = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );

                lock (_stateLock)
                {
                    _activeScoreId = targetScoreId;
                    _activeCode = code;
                    _activeCompletion = completion;
                    _activeExpiresAtUtc = created.ExpiresAtUtc;
                }

                _logger.LogInformation(
                    "[Claim] Showing claim popup for score {ScoreId}, expires {ExpiresAt}",
                    targetScoreId,
                    created.ExpiresAtUtc
                );

                ClaimShown?.Invoke(created.ClaimUrl, created.ExpiresAtUtc);
                _tickTimer = new Timer(_ => Tick(), null, TimeSpan.Zero, TickInterval);

                string status;
                var remaining = created.ExpiresAtUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    status = "expired";
                else
                {
                    var winner = await Task.WhenAny(
                        completion.Task,
                        Task.Delay(remaining, cancellationToken)
                    );
                    status = winner == completion.Task ? await completion.Task : "expired";
                }

                _logger.LogInformation(
                    "[Claim] Resolved score {ScoreId}: {Status}",
                    targetScoreId,
                    status
                );

                return new ClaimOutcome(status, null);
            }
            finally
            {
                lock (_stateLock)
                {
                    _tickTimer?.Dispose();
                    _tickTimer = null;
                    _activeScoreId = null;
                    _activeCode = null;
                    _activeCompletion = null;
                }

                ClaimHidden?.Invoke();
                _gate.Release();
            }
        }

        public void OnScoreClaimed(string scoreId)
        {
            TaskCompletionSource<string> completion;
            lock (_stateLock)
                completion = _activeScoreId == scoreId ? _activeCompletion : null;

            completion?.TrySetResult("saved");
        }

        public void CancelActive()
        {
            TaskCompletionSource<string> completion;
            string code;
            lock (_stateLock)
            {
                completion = _activeCompletion;
                code = _activeCode;
            }

            if (completion is null)
                return;

            if (!string.IsNullOrEmpty(code))
                _ = _api.CancelClaimAsync(code, CancellationToken.None);

            completion.TrySetResult("cancelled");
        }

        private void Tick()
        {
            DateTime expiresAt;
            lock (_stateLock)
            {
                if (_activeCompletion is null)
                    return;
                expiresAt = _activeExpiresAtUtc;
            }

            var remainingMs = (int)Math.Max(0, (expiresAt - DateTime.UtcNow).TotalMilliseconds);
            ClaimTick?.Invoke(remainingMs);
        }

        private static string ExtractCode(string claimUrl)
        {
            try
            {
                var uri = new Uri(claimUrl);
                var query = uri.Query.TrimStart('?');
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2 && parts[0] == "code")
                        return Uri.UnescapeDataString(parts[1]);
                }
            }
            catch (UriFormatException) { }

            return null;
        }

        public void Dispose()
        {
            _tickTimer?.Dispose();
            _gate.Dispose();
        }
    }
}
