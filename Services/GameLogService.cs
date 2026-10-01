using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public interface IGameLogService
    {
        void Prepare(
            ProcessStartInfo startInfo,
            string sessionId,
            int gameId,
            string gameName,
            string versionNumber,
            bool isUnity
        );
        void Attach(Process process);
        void StartFailed(Exception exception);
        string MachineName { get; set; }
        Task ListAsync(string token, int gameId);
        Task UploadFileAsync(string token, int gameId, string path);
        Task DeleteSessionAsync(string token, int gameId, string session);
    }

    public sealed class GameLogService : IGameLogService
    {
        public static readonly string RootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Arcademia_Game_Logs"
        );

        private const string SessionFileName = "session.log";
        private const string UnityFileName = "Player.log";
        private const long MaxBytesPerGame = 50L * 1024 * 1024;
        private const long MaxBytesPerSession = 20L * 1024 * 1024;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(5);

        private readonly IApiClient _api;
        private readonly ILogger<GameLogService> _logger;
        private readonly object _gate = new();
        private SessionLog _active;

        public string MachineName { get; set; }

        public GameLogService(
            IApiClient api,
            ISessionTrackingService sessionTracking,
            ILogger<GameLogService> logger
        )
        {
            _api = api;
            _logger = logger;
            sessionTracking.SessionEndedWithReason += OnSessionEnded;
            _ = Task.Run(PruneAll);
        }

        public void Prepare(
            ProcessStartInfo startInfo,
            string sessionId,
            int gameId,
            string gameName,
            string versionNumber,
            bool isUnity
        )
        {
            try
            {
                var gameDirectory = Path.Combine(RootPath, gameId.ToString(CultureInfo.InvariantCulture));
                Prune(gameDirectory);

                var started = DateTime.UtcNow;
                var directory = Path.Combine(
                    gameDirectory,
                    $"{started:yyyy-MM-dd_HH-mm-ss}_{sessionId[..8]}"
                );
                Directory.CreateDirectory(directory);

                var log = new SessionLog(sessionId, directory, started);
                log.WriteRaw(
                    $"Arcademia Game Log{Environment.NewLine}"
                        + $"Game: {gameName} (Id {gameId}){Environment.NewLine}"
                        + $"Version: {versionNumber ?? "Unknown"}{Environment.NewLine}"
                        + $"Session: {sessionId}{Environment.NewLine}"
                        + $"Executable: {Path.GetFileName(startInfo.FileName)}{Environment.NewLine}"
                        + $"Machine: {(string.IsNullOrWhiteSpace(MachineName) ? $"Unregistered ({Environment.MachineName})" : MachineName)}{Environment.NewLine}"
                        + $"Started: {started:O}{Environment.NewLine}"
                        + (isUnity ? $"Unity log: {UnityFileName}{Environment.NewLine}" : "")
                        + new string('-', 60)
                        + Environment.NewLine
                );

                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;

                if (isUnity)
                {
                    startInfo.ArgumentList.Add("-logFile");
                    startInfo.ArgumentList.Add(Path.Combine(directory, UnityFileName));
                }

                lock (_gate)
                {
                    _active?.Close();
                    _active = log;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GameLogs] Failed to prepare a log for game {GameId}", gameId);
            }
        }

        public void Attach(Process process)
        {
            SessionLog log;
            lock (_gate)
                log = _active;

            if (log == null || process == null)
                return;

            try
            {
                process.EnableRaisingEvents = true;
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        log.WriteLine("OUT", e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        log.WriteLine("ERR", e.Data);
                };
                process.Exited += (_, _) => _ = Task.Run(() => FinishAsync(process, log));

                if (process.StartInfo.RedirectStandardOutput)
                    process.BeginOutputReadLine();
                if (process.StartInfo.RedirectStandardError)
                    process.BeginErrorReadLine();

                log.WriteLine("LAUNCHER", $"Process started (PID {process.Id}).");

                if (process.HasExited)
                    _ = Task.Run(() => FinishAsync(process, log));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GameLogs] Failed to attach to the game process");
            }
        }

        public void StartFailed(Exception exception)
        {
            SessionLog log;
            lock (_gate)
            {
                log = _active;
                _active = null;
            }

            log?.WriteLine("LAUNCHER", $"The game failed to start: {exception.Message}");
            log?.Close();
        }

        private void OnSessionEnded(string sessionId, string reason)
        {
            SessionLog log;
            lock (_gate)
                log = _active;

            if (log != null && log.SessionId == sessionId)
                log.WriteLine("LAUNCHER", $"Session ended by the launcher. Reason: {reason}.");
        }

        private async Task FinishAsync(Process process, SessionLog log)
        {
            if (!log.BeginFinish())
                return;

            try
            {
                await Task.WhenAny(Task.Run(() => process.WaitForExit()), Task.Delay(5000));

                var exitCode = process.ExitCode;
                var ended = DateTime.UtcNow;
                var description = DescribeExitCode(exitCode);
                log.WriteLine(
                    "LAUNCHER",
                    $"Process exited with code {exitCode} (0x{unchecked((uint)exitCode):X8}"
                        + (description == null ? ")" : $", {description})")
                        + $" after {(ended - log.StartedUtc):hh\\:mm\\:ss}."
                );
            }
            catch (Exception ex)
            {
                log.WriteLine("LAUNCHER", $"Could not read the exit code: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
            log.Close();
        }

        private static string DescribeExitCode(int exitCode) =>
            unchecked((uint)exitCode) switch
            {
                0 => "clean exit",
                0xFFFFFFFF => "killed by the launcher",
                0xC0000005 => "access violation",
                0xC00000FD => "stack overflow",
                0xC0000409 => "stack buffer overrun or fail-fast",
                0xC0000374 => "heap corruption",
                0xC0000094 => "integer divide by zero",
                0xC000001D => "illegal instruction",
                0xC0000135 => "missing DLL",
                0xC0000142 => "DLL initialisation failed",
                0xC000013A => "terminated by Ctrl+C",
                0xE0434352 => "unhandled .NET exception",
                0x80000003 => "breakpoint",
                _ => null,
            };

        public async Task ListAsync(string token, int gameId)
        {
            using var timeout = new CancellationTokenSource(UploadTimeout);
            try
            {
                var gameDirectory = GameDirectory(gameId);
                var running = RunningDirectory();
                var files = Directory.Exists(gameDirectory)
                    ? Directory
                        .GetDirectories(gameDirectory)
                        .Where(session => !SameDirectory(session, running))
                        .SelectMany(session =>
                            Directory
                                .GetFiles(session)
                                .Select(file => new FileInfo(file))
                                .Select(info => new
                                {
                                    Path = $"{Path.GetFileName(session)}/{info.Name}",
                                    Size = info.Length,
                                    Modified = new DateTimeOffset(info.LastWriteTimeUtc),
                                })
                        )
                        .ToList()
                    : [];

                if (files.Count == 0)
                {
                    await _api.UploadGameLogsAsync(token, null, timeout.Token);
                    return;
                }

                await using var listing = new MemoryStream(
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(files)
                );
                await _api.UploadGameLogsAsync(token, listing, timeout.Token, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GameLogs] Failed to list logs for game {GameId}", gameId);
            }
        }

        public async Task UploadFileAsync(string token, int gameId, string path)
        {
            using var timeout = new CancellationTokenSource(UploadTimeout);
            try
            {
                var gameDirectory = Path.GetFullPath(GameDirectory(gameId)) + Path.DirectorySeparatorChar;
                var fullPath = string.IsNullOrWhiteSpace(path)
                    ? null
                    : Path.GetFullPath(Path.Combine(gameDirectory, path.Replace('/', Path.DirectorySeparatorChar)));

                if (
                    fullPath == null
                    || !fullPath.StartsWith(gameDirectory, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(fullPath)
                    || SameDirectory(Path.GetDirectoryName(fullPath), RunningDirectory())
                )
                {
                    await _api.UploadGameLogsAsync(token, null, timeout.Token);
                    return;
                }

                await using var content = new MemoryStream();
                await using (
                    var source = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete
                    )
                )
                    await source.CopyToAsync(content, timeout.Token);

                content.Position = 0;
                await _api.UploadGameLogsAsync(token, content, timeout.Token, "text/plain");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GameLogs] Failed to send {Path} for game {GameId}", path, gameId);
            }
        }

        public async Task DeleteSessionAsync(string token, int gameId, string session)
        {
            using var timeout = new CancellationTokenSource(UploadTimeout);
            try
            {
                var gameDirectory = Path.GetFullPath(GameDirectory(gameId)) + Path.DirectorySeparatorChar;
                var valid =
                    !string.IsNullOrWhiteSpace(session)
                    && session.IndexOfAny(['/', '\\', ':']) < 0
                    && session != "."
                    && session != "..";
                var sessionDirectory = valid ? Path.GetFullPath(Path.Combine(gameDirectory, session)) : null;

                if (
                    sessionDirectory == null
                    || !sessionDirectory.StartsWith(gameDirectory, StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(sessionDirectory)
                    || SameDirectory(sessionDirectory, RunningDirectory())
                )
                {
                    await _api.UploadGameLogsAsync(token, null, timeout.Token);
                    return;
                }

                Directory.Delete(sessionDirectory, true);
                _logger.LogInformation(
                    "[GameLogs] Deleted session {Session} for game {GameId}",
                    session,
                    gameId
                );

                await using var result = new MemoryStream(Encoding.UTF8.GetBytes("deleted"));
                await _api.UploadGameLogsAsync(token, result, timeout.Token, "text/plain");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GameLogs] Failed to delete session {Session} for game {GameId}", session, gameId);
            }
        }

        private string RunningDirectory()
        {
            lock (_gate)
                return _active != null && !_active.Closed ? _active.Directory : null;
        }

        private static bool SameDirectory(string a, string b) =>
            a != null
            && b != null
            && string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase
            );

        private static string GameDirectory(int gameId) =>
            Path.Combine(RootPath, gameId.ToString(CultureInfo.InvariantCulture));

        private void PruneAll()
        {
            try
            {
                if (!Directory.Exists(RootPath))
                    return;

                foreach (var gameDirectory in Directory.GetDirectories(RootPath))
                    Prune(gameDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GameLogs] Failed to prune old game logs");
            }
        }

        private void Prune(string gameDirectory)
        {
            if (!Directory.Exists(gameDirectory))
                return;

            string activeDirectory;
            lock (_gate)
                activeDirectory = _active?.Directory;

            var sessions = Directory
                .GetDirectories(gameDirectory)
                .Where(d => d != activeDirectory)
                .Select(d => new DirectoryInfo(d))
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();

            var cutoff = DateTime.UtcNow - MaxAge;
            var remaining = sessions
                .Select(d => (Info: d, Size: d.EnumerateFiles().Sum(f => f.Length)))
                .ToList();
            var total = remaining.Sum(s => s.Size);

            foreach (var session in remaining.ToList())
            {
                if (session.Info.LastWriteTimeUtc >= cutoff && total <= MaxBytesPerGame)
                    break;

                try
                {
                    session.Info.Delete(true);
                    total -= session.Size;
                    remaining.Remove(session);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GameLogs] Could not delete {Directory}", session.Info.FullName);
                }
            }
        }

        private sealed class SessionLog(string sessionId, string directory, DateTime startedUtc)
        {
            private readonly object _gate = new();
            private readonly string _path = Path.Combine(directory, SessionFileName);
            private StreamWriter _writer;
            private long _written;
            private bool _truncated;
            private bool _closed;
            private int _finishing;

            public string SessionId { get; } = sessionId;
            public string Directory { get; } = directory;
            public DateTime StartedUtc { get; } = startedUtc;

            public bool Closed
            {
                get
                {
                    lock (_gate)
                        return _closed;
                }
            }

            public bool BeginFinish() => Interlocked.Exchange(ref _finishing, 1) == 0;

            public void WriteLine(string channel, string text) =>
                WriteRaw(
                    $"[{DateTime.UtcNow:HH:mm:ss.fff}] [{channel}] {text}{Environment.NewLine}",
                    channel == "LAUNCHER"
                );

            public void WriteRaw(string text, bool force = true)
            {
                lock (_gate)
                {
                    try
                    {
                        if (!force && _written >= MaxBytesPerSession)
                        {
                            if (_truncated)
                                return;
                            _truncated = true;
                            text =
                                $"[{DateTime.UtcNow:HH:mm:ss.fff}] [LAUNCHER] Log size limit reached, further game output is not recorded.{Environment.NewLine}";
                        }

                        if (_closed)
                        {
                            File.AppendAllText(_path, text, Encoding.UTF8);
                            return;
                        }

                        _writer ??= new StreamWriter(
                            new FileStream(
                                _path,
                                FileMode.Append,
                                FileAccess.Write,
                                FileShare.ReadWrite | FileShare.Delete
                            ),
                            new UTF8Encoding(false)
                        )
                        {
                            AutoFlush = true,
                        };

                        _writer.Write(text);
                        _written += text.Length;
                    }
                    catch { }
                }
            }

            public void Close()
            {
                lock (_gate)
                {
                    _closed = true;
                    try
                    {
                        _writer?.Dispose();
                    }
                    catch { }
                    _writer = null;
                }
            }
        }
    }
}
