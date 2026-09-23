using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

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
        Task CheckGamesAndUpdateAsync(JObject[] gameinfoList, CancellationToken cancellationToken);
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
            try
            {
                Version latestVersion = new(await _apiClient.GetLatestUpdaterVersionAsync(_logger));

                // Close the updater if it's running and allow time for it to close
                OnCloseGameAndUpdater();
                await Task.Delay(1000, cancellationToken);

                await DownloadUpdaterAndExtractAsync(latestVersion, cancellationToken);

                try
                {
                    bool updateResult = await _apiClient.UpdateRemoteUpdaterVersionAsync(
                        latestVersion.ToString(),
                        _logger
                    );

                    if (updateResult)
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation(
                                "[UpdaterService] Successfully updated updater version to {VersionNumber}.",
                                latestVersion
                            );
                        OnRelaunchUpdater();
                    }
                    else if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "[UpdaterService] Failed to update remote updater version to {VersionNumber}.",
                            latestVersion
                        );
                }
                catch (Exception) { }
            }
            catch (Exception) { }
        }

        public async Task CheckGamesAndUpdateAsync(
            JObject[] gameinfoList,
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
                            OnStateChanged(GameState.checkingForUpdates, game.Name);
                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation(
                                    "[UpdaterService] Checking for updates for {GameName}...",
                                    game.Name
                                );

                            // If the local version matches the remote version and the exe exists, skip the update
                            if (
                                gameinfoList.Any(g =>
                                    g["Name"].ToString() == game.Name
                                    && g["VersionNumber"].ToString() == game.VersionNumber
                                )
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

                            await DownloadGameAndExtractAsync(game, cancellationToken);

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

        private async Task DownloadUpdaterAndExtractAsync(
            Version versionNumber,
            CancellationToken cancellationToken
        )
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[UpdaterService] Downloading updater version: {VersionNumber}",
                    versionNumber
                );

            // Delete the old updater files (except the Launcher folder and Config.json)
            foreach (string file in Directory.GetFiles(_updaterDir))
                if (
                    Path.GetFileName(file).ToLower() != "launcher"
                    && Path.GetFileName(file).ToLower() != "config.json"
                )
                    File.Delete(file);

            // Download the updater zip file
            await using var zipStream = await _apiClient.GetUpdaterDownloadAsync(
                versionNumber.ToString(),
                cancellationToken
            );
            var zipFilePath = Path.Combine(_updaterDir, $"{versionNumber}.zip");

            await using (
                var fileStream = new FileStream(
                    zipFilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None
                )
            )
                await zipStream.CopyToAsync(fileStream, cancellationToken);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[UpdaterService] Updater downloaded successfully: {VersionNumber}",
                    versionNumber
                );

            // Extract the zip file
            FastZip fastZip = new();
            fastZip.ExtractZip(zipFilePath, _updaterDir, null);

            // Delete the zip file
            File.Delete(zipFilePath);
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

            // Ensure the game directory exists
            if (!Directory.Exists(gameDir))
                Directory.CreateDirectory(gameDir);
            else // Clear the game directory if it already exists
                foreach (string file in Directory.GetFiles(gameDir))
                    File.Delete(file);

            // Download the game zip file
            var downloadResult = await _apiClient.GetGameDownloadAsync(
                game.Id,
                gameExists ? game.VersionNumber : null,
                _logger,
                cancellationToken
            );
            await using var zipStream = downloadResult.Stream;
            var contentLength = downloadResult.ContentLength;
            var zipFilePath = Path.Combine(gameDir, $"{game.FolderName}.zip");

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

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[UpdaterService] Game downloaded successfully: {GameName}",
                    game.Name
                );

            // Extract the zip file
            FastZip fastZip = new();
            fastZip.ExtractZip(zipFilePath, gameDir, null);

            // Delete the zip file
            File.Delete(zipFilePath);
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
