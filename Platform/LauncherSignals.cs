using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ArcademiaGameLauncher.Platform
{
    public sealed class LauncherSignals : IDisposable
    {
        public const string HeartbeatFileName = ".arcademia-launcher.heartbeat";
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

        private readonly PosixSignalRegistration _terminate;
        private readonly string _heartbeatPath;
        private DispatcherTimer _heartbeat;

        private LauncherSignals(Action closeRequested, string rootPath)
        {
            _heartbeatPath = Path.Combine(rootPath, HeartbeatFileName);
            _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Task.Run(closeRequested);
            });
        }

        public static LauncherSignals Listen(Action closeRequested) =>
            new(closeRequested, Directory.GetCurrentDirectory());

        internal static LauncherSignals Listen(Action closeRequested, string rootPath) => new(closeRequested, rootPath);

        public void StartHeartbeat()
        {
            if (_heartbeat != null)
                return;

            Beat();
            _heartbeat = new DispatcherTimer(HeartbeatInterval, DispatcherPriority.Background, (_, _) => Beat());
            _heartbeat.Start();
        }

        private void Beat()
        {
            try
            {
                File.WriteAllText(_heartbeatPath, DateTime.UtcNow.ToString("o"));
            }
            catch { }
        }

        public void Dispose()
        {
            _heartbeat?.Stop();
            _terminate.Dispose();
            try
            {
                File.Delete(_heartbeatPath);
            }
            catch { }
        }
    }
}
