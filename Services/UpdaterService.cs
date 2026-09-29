using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;

namespace ArcademiaGameLauncher.Services
{
    public interface IUpdaterService
    {
        event EventHandler LogoDownloaded;
        event EventHandler<GameStateChangedEventArgs> GameStateChanged;
        event EventHandler<GameDatabaseFetchedEventArgs> GameDatabaseFetched;
        event EventHandler<GameUpdateCompletedEventArgs> GameUpdateCompleted;
        event EventHandler<GameDownloadProgressEventArgs> GameDownloadProgress;
        event EventHandler CloseGameAndUpdater;
        event EventHandler RelaunchUpdater;
        event EventHandler<ControllerMapping> ControllerMappingFetched;

        Task DownloadSiteLogo();
        Task CheckUpdaterAndUpdateAsync(CancellationToken cancellationToken);
        Task CheckGamesAndUpdateAsync(CancellationToken cancellationToken);
        Task CheckControllerMappingAsync(CancellationToken cancellationToken);
    }

    public class GameStateChangedEventArgs(GameState newState, string gameName)
    {
        public GameState NewState { get; } = newState;
        public string GameName { get; } = gameName;
    }

    public class GameDatabaseFetchedEventArgs(
        IEnumerable<GameInfo> games,
        IEnumerable<CollectionInfo> collections
    )
    {
        public SimplifiedGameInfo[] Games { get; } =
            [.. games.Select(game => new SimplifiedGameInfo(game))];
        public CollectionInfo[] Collections { get; } = [.. collections ?? []];
    }

    public class GameUpdateCompletedEventArgs(string gameName)
    {
        public string GameName { get; } = gameName;
    }

    public class GameDownloadProgressEventArgs(string gameName, int percent)
    {
        public string GameName { get; } = gameName;
        public int Percent { get; } = percent;
    }

    public class SimplifiedGameInfo(GameInfo game)
    {
        public int Id { get; } = game.Id;
        public string VersionNumber { get; } = game.VersionNumber;
        public string Name { get; } = game.Name;
        public string Description { get; } = game.Description;
        public string? ThumbnailUrl { get; } = game.ThumbnailUrl;
        public string[] Authors { get; } =
            game.Authors?.Select(author => author.Name).ToArray() ?? [];
        public Tag[] Tags { get; } = game.Tags?.ToArray() ?? [];
        public string NameOfExecutable { get; } = game.NameOfExecutable;
        public string FolderName { get; } = game.FolderName;
    }

    public class UpdaterService : IUpdaterService
    {
        public event EventHandler LogoDownloaded;

        protected void OnLogoDownloaded() => LogoDownloaded?.Invoke(this, EventArgs.Empty);

        public event EventHandler<GameStateChangedEventArgs> GameStateChanged;

        protected void OnStateChanged(GameState newState, string gameName) =>
            GameStateChanged?.Invoke(this, new GameStateChangedEventArgs(newState, gameName));

        public event EventHandler<GameDatabaseFetchedEventArgs> GameDatabaseFetched;

        protected void OnGameDatabaseFetched(
            IEnumerable<GameInfo> games,
            IEnumerable<CollectionInfo> collections
        ) =>
            GameDatabaseFetched?.Invoke(
                this,
                new GameDatabaseFetchedEventArgs(games, collections)
            );

        public event EventHandler<GameUpdateCompletedEventArgs> GameUpdateCompleted;

        protected void OnGameUpdateCompleted(string gameName) =>
            GameUpdateCompleted?.Invoke(this, new GameUpdateCompletedEventArgs(gameName));

        public event EventHandler<GameDownloadProgressEventArgs> GameDownloadProgress;

        protected void OnGameDownloadProgress(string gameName, int percent) =>
            GameDownloadProgress?.Invoke(
                this,
                new GameDownloadProgressEventArgs(gameName, percent)
            );

        public event EventHandler CloseGameAndUpdater;

        protected void OnCloseGameAndUpdater() =>
            CloseGameAndUpdater?.Invoke(this, EventArgs.Empty);

        public event EventHandler RelaunchUpdater;

        protected void OnRelaunchUpdater() => RelaunchUpdater?.Invoke(this, EventArgs.Empty);

        public event EventHandler<ControllerMapping> ControllerMappingFetched;

        protected void OnControllerMappingFetched(ControllerMapping mapping) =>
            ControllerMappingFetched?.Invoke(this, mapping);

        private readonly IApiClient _apiClient;
        private readonly ILogger<UpdaterService> _logger;
        private readonly string _applicationPath;
        private readonly string _updaterDir;
        private readonly string _gamesDir;
        private const string UpdaterExeName = "Research-Arcade-Updater.exe";
        private static readonly string[] UpdaterBinaryPatterns =
        [
            "*.dll",
            "*.exe",
            "*.pdb",
            "*.deps.json",
            "*.runtimeconfig.json",
            "*.dll.config",
        ];
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gameLocks = new(StringComparer.OrdinalIgnoreCase);

        public UpdaterService(
            IApiClient apiClient,
            ILogger<UpdaterService> logger,
            string applicationPath
        )
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _applicationPath =
                applicationPath ?? throw new ArgumentNullException(nameof(applicationPath));
            _updaterDir = Directory.GetCurrentDirectory();
            _gamesDir = Path.Combine(_applicationPath, "Games");
        }

        public async Task DownloadSiteLogo()
        {
            _logger.LogInformation("[UpdaterService] Downloading site icon...");

            try
            {
                var logoPath = Path.Combine(_applicationPath, "Arcademia_Logo.png");

                await using var logoStream = await _apiClient.GetSiteLogoAsync(
                    CancellationToken.None
                );

                await using (
                    var fileStream = new FileStream(
                        logoPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None
                    )
                )
                    await logoStream.CopyToAsync(fileStream);

                _logger.LogInformation("[UpdaterService] Site icon downloaded successfully.");

                OnLogoDownloaded();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[UpdaterService] Could not download site icon: {Message}",
                    ex.Message
                );
                OnLogoDownloaded();
            }
        }

        public async Task CheckUpdaterAndUpdateAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[UpdaterService] Checking for updater updates...");

            Version latestVersion;
            try
            {
                latestVersion = new(await _apiClient.GetLatestUpdaterVersionAsync(_logger));
            }
            catch (Exception)
            {
                return;
            }

            string stagingDir;
            try
            {
                stagingDir = await StageUpdaterAsync(latestVersion, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[UpdaterService] Could not download updater {VersionNumber}; keeping the current updater.",
                    latestVersion
                );
                return;
            }

            // Close the updater only once the new version is ready to swap in
            OnCloseGameAndUpdater();
            await WaitForUpdaterExitAsync();

            bool installed = false;
            try
            {
                SwapUpdaterFiles(stagingDir);
                installed = true;
                _logger.LogInformation(
                    "[UpdaterService] Installed updater {VersionNumber}.",
                    latestVersion
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UpdaterService] Failed to install updater {VersionNumber}.", latestVersion);
            }
            finally
            {
                TryDeleteDirectory(stagingDir);
            }

            if (installed)
            {
                try
                {
                    bool updateResult = await _apiClient.UpdateRemoteUpdaterVersionAsync(
                        latestVersion.ToString(),
                        _logger
                    );

                    if (!updateResult && _logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "[UpdaterService] Failed to update remote updater version to {VersionNumber}.",
                            latestVersion
                        );
                }
                catch (Exception) { }
            }

            OnRelaunchUpdater();
        }

        public async Task CheckGamesAndUpdateAsync(
            CancellationToken cancellationToken
        )
        {
            _logger.LogInformation("[UpdaterService] Checking for game updates...");
            try
            {
                var games = await _apiClient.GetMachineGamesAsync(_logger, cancellationToken);

                // If no games are found, log a warning and return
                if (games is null || !games.Any())
                {
                    _logger.LogWarning("[UpdaterService] No games found for this machine.");
                    return;
                }

                IEnumerable<CollectionInfo> collections;
                try
                {
                    collections = await _apiClient.GetMachineCollectionsAsync(
                        _logger,
                        cancellationToken
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "[UpdaterService] Could not fetch collections, treating as empty: {Message}",
                        ex.Message
                    );
                    collections = [];
                }

                // Callback to notify that the game database has been fetched
                OnGameDatabaseFetched(games, collections);

                // Update each game
                foreach (var game in games)
                {
                        _ = Task.Run(
                            async () =>
                            {
                                var gate = _gameLocks.GetOrAdd(game.FolderName, _ => new SemaphoreSlim(1, 1));
                                await gate.WaitAsync(cancellationToken);
                                try
                                {
                                    await UpdateGameAsync(game, cancellationToken);
                                }
                                finally
                                {
                                    gate.Release();
                                }
                            },
                            cancellationToken
                        );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[UpdaterService] Could not check for game updates: {Message}",
                    ex.Message
                );
            }
        }

        private async Task UpdateGameAsync(GameInfo game, CancellationToken cancellationToken)
        {
            OnStateChanged(GameState.checkingForUpdates, game.Name);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[UpdaterService] Checking for updates for {GameName}...",
                    game.Name
                );

            // If the installed version matches the remote version and the exe exists, skip the update
            if (
                ReadInstalledVersion(game) == game.VersionNumber
                && File.Exists(
                    Path.Combine(_gamesDir, game.FolderName, game.NameOfExecutable)
                )
            )
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "[UpdaterService] {GameName} is already up to date (v{VersionNumber}). Skipping update.",
                        game.Name,
                        game.VersionNumber
                    );

                OnGameUpdateCompleted(game.Name);

                return;
        }

        if (IsGameRunning(game))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "[UpdaterService] {GameName} is running. Deferring update to v{VersionNumber}.",
                    game.Name,
                    game.VersionNumber
                );

            OnGameUpdateCompleted(game.Name);

            return;
        }

        try
        {
            await DownloadGameAndExtractAsync(game, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[UpdaterService] Failed to install {GameName} v{VersionNumber}.",
                game.Name,
                game.VersionNumber
            );
            OnStateChanged(GameState.failed, game.Name);

            return;
        }

        try
        {
            bool updateResult = await _apiClient.UpdateRemoteGameVersionAsync(
                game.Id,
                game.VersionNumber,
                _logger
            );

            if (updateResult)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "[UpdaterService] Successfully updated {GameName} to version {VersionNumber}.",
                        game.Name,
                        game.VersionNumber
                    );
                OnGameUpdateCompleted(game.Name);
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(
                        "[UpdaterService] Failed to update {GameName} to version {VersionNumber}.",
                        game.Name,
                        game.VersionNumber
                    );
                OnStateChanged(GameState.failed, game.Name);
            }
        }
        catch (Exception)
        {
            OnGameUpdateCompleted(game.Name);
        }
        }

        private async Task<string> StageUpdaterAsync(
            Version versionNumber,
            CancellationToken cancellationToken
        )
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[UpdaterService] Downloading updater version: {VersionNumber}",
                    versionNumber
                );

            var packagesDir = Path.Combine(_updaterDir, "Packages");
            Directory.CreateDirectory(packagesDir);
            var zipFilePath = Path.Combine(packagesDir, $"updater-{versionNumber}.zip.part");
            var stagingDir = Path.Combine(_updaterDir, "Updater.staging");

            try
            {
                await using (
                    var zipStream = await _apiClient.GetUpdaterDownloadAsync(
                        versionNumber.ToString(),
                        cancellationToken
                    )
                )
                await using (
                    var fileStream = new FileStream(
                        zipFilePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None
                    )
                )
                    await zipStream.CopyToAsync(fileStream, cancellationToken);

                ValidatePackage(zipFilePath, UpdaterExeName);

                TryDeleteDirectory(stagingDir);
                Directory.CreateDirectory(stagingDir);
                new FastZip().ExtractZip(zipFilePath, stagingDir, null);

                if (!File.Exists(Path.Combine(stagingDir, UpdaterExeName)))
                    throw new InvalidDataException($"The updater package does not contain {UpdaterExeName}.");

                return stagingDir;
            }
            catch
            {
                TryDeleteDirectory(stagingDir);
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(zipFilePath))
                        File.Delete(zipFilePath);
                }
                catch { }
            }
        }

        private static void ValidatePackage(string zipFilePath, string requiredEntry)
        {
            using var zip = new ZipFile(zipFilePath);

            if (!zip.TestArchive(true))
                throw new InvalidDataException("The downloaded package is corrupt.");

            bool hasRequiredEntry = false;
            foreach (ZipEntry entry in zip)
            {
                var name = entry.Name.Replace('\\', '/');
                if (Path.IsPathRooted(name) || name.Split('/').Contains(".."))
                    throw new InvalidDataException($"The package contains an unsafe path: {entry.Name}");

                if (string.Equals(name, requiredEntry, StringComparison.OrdinalIgnoreCase))
                    hasRequiredEntry = true;
            }

            if (!hasRequiredEntry)
                throw new InvalidDataException($"The package does not contain {requiredEntry}.");
        }

        private static async Task WaitForUpdaterExitAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var running = Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(UpdaterExeName)
                );
                bool anyRunning = running.Any(p => !p.HasExited);
                foreach (var process in running)
                    process.Dispose();

                if (!anyRunning)
                    return;

                await Task.Delay(250);
            }
        }

        private void SwapUpdaterFiles(string stagingDir)
        {
            var stagedFiles = Directory
                .GetFiles(stagingDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(stagingDir, f))
                .Where(f => !string.Equals(f, "Config.json", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var toRemove = new HashSet<string>(stagedFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in UpdaterBinaryPatterns)
                foreach (var file in Directory.GetFiles(_updaterDir, pattern))
                    toRemove.Add(Path.GetFileName(file));
            toRemove.Remove("Config.json");

            foreach (var relativePath in toRemove)
            {
                var target = Path.Combine(_updaterDir, relativePath);
                if (File.Exists(target))
                    DeleteWithRetry(target);
            }

            foreach (var relativePath in stagedFiles)
            {
                var target = Path.Combine(_updaterDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(stagingDir, relativePath), target, true);
            }
        }

        private static void DeleteWithRetry(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return;
                }
                catch (Exception) when (attempt < 10)
                {
                    Thread.Sleep(300);
                }
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { }
        }

        private async Task DownloadGameAndExtractAsync(
            GameInfo game,
            CancellationToken cancellationToken
        )
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[UpdaterService] Downloading {GameName} v{VersionNumber}...",
                    game.Name,
                    game.VersionNumber
                );
            OnStateChanged(GameState.downloadingGame, game.Name);

            var gameDir = Path.Combine(_gamesDir, game.FolderName);
            var gameExists = File.Exists(Path.Combine(gameDir, game.NameOfExecutable));

            Directory.CreateDirectory(_gamesDir);

            // Download the game zip file
            var downloadResult = await _apiClient.GetGameDownloadAsync(
                game.Id,
                gameExists ? game.VersionNumber : null,
                _logger,
                cancellationToken
            );
            await using var zipStream = downloadResult.Stream;
            var contentLength = downloadResult.ContentLength;
            var zipFilePath = Path.Combine(_gamesDir, $"{game.FolderName}.zip.download");

            try
            {
                await DownloadToFileAsync(game, zipStream, contentLength, zipFilePath, cancellationToken);

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "[UpdaterService] Game downloaded successfully: {GameName}",
                        game.Name
                    );

                // Replace the game directory with a clean copy of the new version
                var markerPath = GetInstalledVersionPath(game);
                if (File.Exists(markerPath))
                    File.Delete(markerPath);
                if (Directory.Exists(gameDir))
                    Directory.Delete(gameDir, true);
                Directory.CreateDirectory(gameDir);

                FastZip fastZip = new();
                fastZip.ExtractZip(zipFilePath, gameDir, null);

                File.WriteAllText(markerPath, game.VersionNumber);
            }
            finally
            {
                try
                {
                    if (File.Exists(zipFilePath))
                        File.Delete(zipFilePath);
                }
                catch { }
            }
        }

        private async Task DownloadToFileAsync(
            GameInfo game,
            Stream zipStream,
            long? contentLength,
            string zipFilePath,
            CancellationToken cancellationToken
        )
        {
            await using (
                var fileStream = new FileStream(
                    zipFilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None
                )
            )
            {
                if (contentLength is > 0)
                {
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int lastPercent = -1;
                    int bytesRead;
                    while ((bytesRead = await zipStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(
                            buffer.AsMemory(0, bytesRead),
                            cancellationToken
                        );
                        totalRead += bytesRead;
                        int percent = (int)(totalRead * 100 / contentLength.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            OnGameDownloadProgress(game.Name, percent);
                        }
                    }
                }
                else
                {
                    await zipStream.CopyToAsync(fileStream, cancellationToken);
                }
            }
        }

        private string GetInstalledVersionPath(GameInfo game) =>
            Path.Combine(_gamesDir, game.FolderName, ".arcademia-version");

        private string ReadInstalledVersion(GameInfo game)
        {
            try
            {
                var markerPath = GetInstalledVersionPath(game);
                return File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private bool IsGameRunning(GameInfo game)
        {
            var gameDir = Path.GetFullPath(Path.Combine(_gamesDir, game.FolderName))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (
                var process in Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(game.NameOfExecutable)
                )
            )
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (
                            path == null
                            || path.StartsWith(gameDir, StringComparison.OrdinalIgnoreCase)
                        )
                            return true;
                    }
                    catch
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public async Task CheckControllerMappingAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[UpdaterService] Checking for controller mapping updates...");
            try
            {
                var mapping = await _apiClient.GetControllerMappingAsync(cancellationToken);
                if (mapping != null)
                {
                    _logger.LogInformation(
                        "[UpdaterService] Controller mapping fetched successfully."
                    );
                    OnControllerMappingFetched(mapping);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[UpdaterService] Could not fetch controller mapping: {Message}",
                    ex.Message
                );
            }
        }
    }
}
