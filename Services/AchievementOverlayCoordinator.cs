using System;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Utils;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public interface IAchievementOverlayCoordinator
    {
        event Action<AchievementSnapshot> OpenRequested;
        event Action CloseRequested;
        bool IsOpen { get; }
        Task<string> OpenAsync();
        void Close();
    }

    public sealed class AchievementOverlayCoordinator : IAchievementOverlayCoordinator
    {
        private readonly IAchievementSessionService _achievements;
        private readonly ILogger<AchievementOverlayCoordinator> _logger;
        private readonly object _gate = new();
        private TaskCompletionSource<string> _open;

        public event Action<AchievementSnapshot> OpenRequested;
        public event Action CloseRequested;

        public bool IsOpen
        {
            get
            {
                lock (_gate)
                    return _open is not null;
            }
        }

        public AchievementOverlayCoordinator(
            IAchievementSessionService achievements,
            ISessionTrackingService session,
            ILogger<AchievementOverlayCoordinator> logger
        )
        {
            _achievements = achievements;
            _logger = logger;
            session.SessionEnded += Close;
        }

        public async Task<string> OpenAsync()
        {
            if (FullscreenDetector.IsExclusiveFullscreen())
                return "game";

            var snapshot = await _achievements.GetSnapshotAsync();
            if (snapshot.Set is { Enabled: false })
                return "disabled";

            TaskCompletionSource<string> open;
            lock (_gate)
            {
                if (_open is not null)
                    return "alreadyOpen";
                open = _open = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _logger.LogInformation("[Achievements] Opening overlay");
            OpenRequested?.Invoke(snapshot);
            return await open.Task;
        }

        public void Close()
        {
            TaskCompletionSource<string> open;
            lock (_gate)
            {
                open = _open;
                _open = null;
            }

            if (open is null)
                return;

            _logger.LogInformation("[Achievements] Closing overlay");
            CloseRequested?.Invoke();
            open.TrySetResult("closed");
        }
    }
}
