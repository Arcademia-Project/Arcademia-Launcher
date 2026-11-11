using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace ArcademiaGameLauncher.Services
{
    public interface IGameRunner
    {
        void Wrap(ProcessStartInfo startInfo, int? bridgePort);
        Task PrepareAsync();
    }

    public sealed class GameRunner : IGameRunner
    {
        public const string BridgePortVariable = "ARCADEMIA_BRIDGE_PORT";
        public const string BridgeFileName = "Arcademia-WineBridge.exe";

        private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(15);

        private readonly JObject _config;
        private readonly ILogger<GameRunner> _logger;
        private readonly string _rootPath;

        public GameRunner(JObject config, ILogger<GameRunner> logger)
        {
            _config = config;
            _logger = logger;
            _rootPath = Directory.GetCurrentDirectory();
        }

        public string PrefixPath =>
            Setting("WinePrefix") is string prefix ? Path.GetFullPath(prefix, _rootPath) : Path.Combine(_rootPath, "Proton", "prefix");

        public string Runner => Setting("WineRunner") ?? FindOnPath("umu-run") ?? FindOnPath("wine") ?? "umu-run";

        public bool UsesUmu => Path.GetFileName(Runner).StartsWith("umu", StringComparison.OrdinalIgnoreCase);

        public static string BridgePath => Path.Combine(AppContext.BaseDirectory, "WineBridge", BridgeFileName);

        public static bool NeedsCompatibilityLayer(string executable) =>
            !OperatingSystem.IsWindows() && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        public static string ToWindowsPath(string path) => "Z:" + Path.GetFullPath(path).Replace('/', '\\');

        public void Wrap(ProcessStartInfo startInfo, int? bridgePort)
        {
            if (OperatingSystem.IsWindows())
                return;

            var executable = startInfo.FileName;
            if (!NeedsCompatibilityLayer(executable))
            {
                EnsureExecutable(executable);
                return;
            }

            var arguments = startInfo.ArgumentList.ToList();
            for (int i = 0; i < arguments.Count - 1; i++)
                if (string.Equals(arguments[i], "-logFile", StringComparison.OrdinalIgnoreCase))
                    arguments[i + 1] = ToWindowsPath(arguments[i + 1]);

            startInfo.FileName = Runner;
            startInfo.ArgumentList.Clear();

            if (bridgePort is int port && File.Exists(BridgePath))
            {
                startInfo.ArgumentList.Add(BridgePath);
                startInfo.Environment[BridgePortVariable] = port.ToString();
            }
            else
                _logger.LogWarning("[Runner] Starting without the SDK bridge, achievements and leaderboards will be unavailable");

            startInfo.ArgumentList.Add(ToWindowsPath(executable));
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            ApplyEnvironment(startInfo.Environment);
            _logger.LogInformation("[Runner] Starting {Game} with {Runner} in {Prefix}", Path.GetFileName(executable), Runner, PrefixPath);
        }

        public async Task PrepareAsync()
        {
            if (OperatingSystem.IsWindows() || File.Exists(Path.Combine(PrefixPath, "system.reg")) || File.Exists(Path.Combine(PrefixPath, "pfx", "system.reg")))
                return;

            var startInfo = new ProcessStartInfo(Runner)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("wineboot");
            startInfo.ArgumentList.Add("-u");
            ApplyEnvironment(startInfo.Environment);

            _logger.LogInformation("[Runner] Preparing the {Runner} prefix at {Prefix}, the first run can take several minutes", Runner, PrefixPath);
            try
            {
                using var process = Process.Start(startInfo);
                if (process == null)
                    return;

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var exited = process.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(PrepareTimeout)) != exited)
                {
                    _logger.LogWarning("[Runner] Preparing the prefix took too long, it will finish on the first game launch");
                    return;
                }

                _logger.LogInformation("[Runner] Prefix ready (exit code {ExitCode})", process.ExitCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Runner] Could not prepare the prefix with {Runner}: {Message}", Runner, ex.Message);
            }
        }

        public static int KillSession(string sessionId)
        {
            var marker = "\0" + SdkBrokerService.SessionVariable + "=" + sessionId + "\0";
            var killed = 0;

            try
            {
                foreach (var directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid == Environment.ProcessId)
                        continue;

                    try
                    {
                        var environment = "\0" + File.ReadAllText(Path.Combine(directory, "environ")) + "\0";
                        if (!environment.Contains(marker, StringComparison.Ordinal))
                            continue;

                        using var process = Process.GetProcessById(pid);
                        process.Kill(true);
                        killed++;
                    }
                    catch { }
                }
            }
            catch { }

            return killed;
        }

        public static bool IsDirectoryInUse(string directory)
        {
            var unix = Path.GetFullPath(directory).TrimEnd('/') + "/";
            var windows = ToWindowsPath(directory).TrimEnd('\\') + "\\";

            try
            {
                foreach (var process in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(process), out _))
                        continue;

                    try
                    {
                        var commandLine = File.ReadAllText(Path.Combine(process, "cmdline"));
                        if (
                            commandLine.Contains(unix, StringComparison.Ordinal)
                            || commandLine.Contains(windows, StringComparison.OrdinalIgnoreCase)
                        )
                            return true;
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        private void ApplyEnvironment(IDictionary<string, string> environment)
        {
            Directory.CreateDirectory(PrefixPath);
            environment["WINEPREFIX"] = PrefixPath;
            if (!environment.ContainsKey("WINEDEBUG"))
                environment["WINEDEBUG"] = "-all";

            if (!UsesUmu)
                return;

            environment["GAMEID"] = Setting("UmuGameId") ?? "umu-default";
            if (Setting("ProtonPath") is string protonPath)
                environment["PROTONPATH"] = protonPath;
        }

        private string Setting(string key)
        {
            var value = _config?[key]?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static void EnsureExecutable(string path)
        {
            try
            {
                if (File.Exists(path) && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) == 0)
                    File.SetUnixFileMode(
                        path,
                        File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                    );
            }
            catch { }
        }

        private static string FindOnPath(string name)
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(directory))
                    continue;
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    }
}
