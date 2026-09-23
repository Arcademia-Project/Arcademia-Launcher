using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using ArcademiaGameLauncher.Models;
using ArcademiaGameLauncher.Services;
using ArcademiaGameLauncher.UserControls;
using ArcademiaGameLauncher.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serilog;
using XamlAnimatedGif;

namespace ArcademiaGameLauncher.Windows
{
    public partial class MainWindow : Window
    {
        private readonly ILogger<MainWindow> _logger;
        readonly bool production;

        [DllImport("User32.dll")]
        public static extern int GetAsyncKeyState(Int32 i);

        private readonly ISfxPlayer _sfxPlayer;
        private readonly IUpdaterService _updater;

        public readonly string _applicationPath;
        private readonly string _gameDirectoryPath;

        private readonly JObject _config;
        private JObject[] _gameInfoList;
        private JObject[] _currentGameWorkingList = [];
        private CollectionInfo[] _collectionInfoList = [];
        private CollectionInfo _activeCollectionInfo;
        private string _thumbnailCacheBuster = DateTime.Now.Ticks.ToString();

        private readonly Dictionary<string, ThumbnailEntry> _scrollThumbnailCache = new();
        private readonly List<string> _scrollGifTempFiles = new();
        private readonly object _scrollCacheLock = new();
        private static readonly string _scrollGifTempDir = Path.Combine(
            Path.GetTempPath(),
            "arcademia_scroll"
        );

        private readonly ControllerManager _controllerManager;

        private System.Timers.Timer _updateTimer;
        private bool _isTimerRunning = false;
        private readonly int _tickSpeed = 10;

        private string _currentStep = "Idle";
        private DateTime _lastSuccessfulTick = DateTime.Now;

        private readonly Stopwatch _frameStopwatch = Stopwatch.StartNew();
        private long _lastFrameElapsedMs = 0;
        private const int MaxFrameMs = 250;

        private long _lastUiHeartbeatTicks = DateTime.UtcNow.Ticks;
        private int _consecutiveUiStalls = 0;
        private const int UiStallCheckIntervalSeconds = 5;
        private const int UiStallsBeforeRecovery = 12;

        private int _selectionAnimationFrame = 0;
        private readonly int _selectionAnimationFrameRate = 100;

        // Colours for the Text Blocks Selection Animation
        private readonly SolidColorBrush[] _selectionAnimationFrames =
        [
            new(Color.FromArgb(0xFF, 0xFF, 0xD9, 0x66)),
            new(Color.FromArgb(0xFF, 0xE5, 0xC3, 0x5C)),
            new(Color.FromArgb(0xFF, 0xBF, 0xA3, 0x4C)),
            new(Color.FromArgb(0xFF, 0x9E, 0x86, 0x3F)),
            new(Color.FromArgb(0xFF, 0x7F, 0x6c, 0x33)),
            new(Color.FromArgb(0xFF, 0x9E, 0x86, 0x3F)),
            new(Color.FromArgb(0xFF, 0xBF, 0xA3, 0x4C)),
            new(Color.FromArgb(0xFF, 0xE5, 0xC3, 0x5C)),
        ];

        bool _isMaintenanceVisible = false;
        bool _isStartMenuVisible = false;
        bool _isHomeMenuVisible = false;
        bool _isSelectionMenuVisible = false;
        bool _isInputMenuVisible = false;
        bool _isCreditsVisible = false;
        bool _isInfoWindowVisible = false;
        bool _isInfoWindowIdleVisible = false;
        bool _isInfoWindowForceExitVisible = false;
        bool _isUpdatingInputMenu = false;
        bool _isUpdatingCredits = false;

        private int _globalCounter = 0;

        private readonly int _selectionUpdateInterval = 250;
        private int _selectionUpdateIntervalCounter = 0;
        private readonly int _selectionUpdateIntervalCounterMax = 10;
        private int _selectionUpdateCounter = 0;

        private int _currentlySelectedHomeIndex = 0;

        private int _currentlySelectedGameIndex;
        private int _previousPageIndex = 0;
        private const int _tilesPerPage = 15;
        private const int _gridColumns = 3;
        private bool _showingDebouncedGame = false;

        private bool _selectionMenuEnteredViaCollection = false;
        private bool _isBrowsingCollectionsTopLevel = false;

        private int _afkTimer = 0;
        private readonly int _noInputTimeout = 0;
        private bool _afkTimerActive = false;

        private int _timeSinceLastButton = 0;

        private readonly TextBlock[] _homeOptionsList;
        private readonly Grid[] _gameTilesList;
        private readonly Label[] _gameTitlesList;
        private readonly Image[] _gameImagesList;

        private readonly BitmapImage _placeholderBitmap;
        private readonly BitmapImage _collectionPlaceholderClosedBitmap;
        private readonly BitmapImage _collectionPlaceholderOpenBitmap;

        private readonly System.Windows.Shapes.Ellipse[] _inputMenuJoysticks;
        private readonly System.Windows.Shapes.Ellipse[][] _inputMenuButtons;

        private Process _currentlyRunningProcess = null;

        private GameState[] _gameTitleStates;

        private readonly InfoWindow _infoWindow;
        private readonly ClaimWindow _claimWindow;
        private bool _isClaimWindowVisible;
        private readonly EmojiParser _emojiParser;

        private readonly Socket _socket;

        // MAIN WINDOW

        private readonly IDispatcherQueueService _dispatcherQueue;
        private readonly IApiClient _apiClient;
        private readonly ISessionTrackingService _sessionTracking;
        private readonly ISdkBrokerService _sdkBroker;
        private readonly IClaimCoordinator _claimCoordinator;

        public MainWindow(
            ILogger<MainWindow> logger,
            ISfxPlayer sfxPlayer,
            IUpdaterService updaterService,
            IDispatcherQueueService dispatcherQueue,
            IApiClient apiClient,
            ISessionTrackingService sessionTracking,
            ISdkBrokerService sdkBroker,
            IClaimCoordinator claimCoordinator,
            JObject config,
            string applicationPath,
            ILoggerFactory loggerFactory
        )
        {
            _logger = logger;
            _sfxPlayer = sfxPlayer;
            _updater = updaterService;
            _dispatcherQueue = dispatcherQueue;
            _apiClient = apiClient;
            _sessionTracking = sessionTracking;
            _sdkBroker = sdkBroker;
            _claimCoordinator = claimCoordinator;
            _config = config;
            _applicationPath = applicationPath;

            production = _applicationPath.EndsWith("Launcher");
            _gameDirectoryPath = Path.Combine(_applicationPath, "Games");

            _placeholderBitmap = new BitmapImage();
            _placeholderBitmap.BeginInit();
            _placeholderBitmap.UriSource = new Uri(
                "/Assets/Images/ThumbnailPlaceholder.png",
                UriKind.Relative
            );
            _placeholderBitmap.CacheOption = BitmapCacheOption.OnLoad;
            _placeholderBitmap.CreateOptions = BitmapCreateOptions.DelayCreation;
            _placeholderBitmap.EndInit();
            _placeholderBitmap.Freeze();

            _collectionPlaceholderClosedBitmap = new BitmapImage();
            _collectionPlaceholderClosedBitmap.BeginInit();
            _collectionPlaceholderClosedBitmap.UriSource = new Uri(
                "pack://application:,,,/Assets/Images/CollectionPlaceholder_Closed.png",
                UriKind.Absolute
            );
            _collectionPlaceholderClosedBitmap.CacheOption = BitmapCacheOption.OnLoad;
            _collectionPlaceholderClosedBitmap.EndInit();
            _collectionPlaceholderClosedBitmap.Freeze();

            _collectionPlaceholderOpenBitmap = new BitmapImage();
            _collectionPlaceholderOpenBitmap.BeginInit();
            _collectionPlaceholderOpenBitmap.UriSource = new Uri(
                "pack://application:,,,/Assets/Images/CollectionPlaceholder_Open.png",
                UriKind.Absolute
            );
            _collectionPlaceholderOpenBitmap.CacheOption = BitmapCacheOption.OnLoad;
            _collectionPlaceholderOpenBitmap.EndInit();
            _collectionPlaceholderOpenBitmap.Freeze();

            // Setup closing event
            Closing += Window_Closing;

            // Load the info window
            _infoWindow = new();
            _claimWindow = new();
            _claimCoordinator.ClaimShown += ClaimCoordinator_ClaimShown;
            _claimCoordinator.ClaimTick += ClaimCoordinator_ClaimTick;
            _claimCoordinator.ClaimHidden += ClaimCoordinator_ClaimHidden;

            InitializeComponent();

            // Setup Input Joysticks
            _inputMenuJoysticks = [InputMenu_P1_Joy, InputMenu_P2_Joy];

            // Setup Input Buttons
            _inputMenuButtons = new System.Windows.Shapes.Ellipse[2][];
            _inputMenuButtons[0] =
            [
                InputMenu_P1_Exit,
                InputMenu_P1_Start,
                InputMenu_P1_A,
                InputMenu_P1_B,
                InputMenu_P1_C,
                InputMenu_P1_D,
                InputMenu_P1_E,
                InputMenu_P1_F,
            ];
            _inputMenuButtons[1] =
            [
                InputMenu_P2_Exit,
                InputMenu_P2_Start,
                InputMenu_P2_A,
                InputMenu_P2_B,
                InputMenu_P2_C,
                InputMenu_P2_D,
                InputMenu_P2_E,
                InputMenu_P2_F,
            ];

            _emojiParser = new();

            if (_config != null && _config.ContainsKey("NoInputTimeout_ms"))
                _noInputTimeout = _config.ContainsKey("NoInputTimeout_ms")
                    ? int.Parse(_config["NoInputTimeout_ms"].ToString())
                    : 120000; // Default to 2 minutes if not set in config

            // Create the games directory if it doesn't exist
            if (!Directory.Exists(_gameDirectoryPath))
                Directory.CreateDirectory(_gameDirectoryPath);

            // Socket Setup
            var host = _config["ApiHost"]?.ToString() ?? "https://localhost:5001";
            var user = _config["ApiUser"]?.ToString() ?? "Research-Arcade-User";
            var pass = _config["ApiPass"]?.ToString() ?? "Research-Arcade-Password";

            _socket = new Socket(
                host,
                user,
                pass,
                this,
                _sfxPlayer,
                _sessionTracking,
                _claimCoordinator,
                loggerFactory.CreateLogger<Socket>()
            );
            _ = _socket.SafeReportStatus("Idle");

            _updater = updaterService;

            _updater.ControllerMappingFetched += Updater_ControllerMappingFetched;
            _updater.LogoDownloaded += Updater_LogoDownloaded;
            _updater.GameStateChanged += Updater_GameStateChanged;
            _updater.GameDatabaseFetched += Updater_GameDatabaseFetched;
            _updater.GameUpdateCompleted += Updater_GameUpdateCompleted;
            _updater.GameDownloadProgress += Updater_GameDownloadProgress;
            _updater.CloseGameAndUpdater += Updater_CloseGameAndUpdater;
            _updater.RelaunchUpdater += Updater_RelaunchUpdater;

            _updater.DownloadSiteLogo();

            // Set the locations of each item on the start menu
            string logicalScreenWidth_str = TryFindResource("LogicalSizeWidth").ToString();
            string logicalScreenHeight_str = TryFindResource("LogicalSizeHeight").ToString();

            double logicalScreenWidth = double.Parse(logicalScreenWidth_str);
            double logicalScreenHeight = double.Parse(logicalScreenHeight_str);

            // StartMenu_Rect
            double RectActualWidth = Math.Cos(5f * (Math.PI / 180f)) * (double)StartMenu_Rect.Width;
            double RectActualHeight =
                Math.Sin(5f * Math.PI / 180f) * (double)StartMenu_Rect.Width
                + Math.Cos(5f * Math.PI / 180f) * (double)StartMenu_Rect.Height;

            Canvas.SetLeft(
                StartMenu_Rect,
                (logicalScreenWidth / 2f) - (RectActualWidth / 2f) + (logicalScreenWidth / 30f)
            );
            Canvas.SetTop(StartMenu_Rect, logicalScreenHeight / 2f - RectActualHeight / 2f);

            // StartMenu_ArcademiaLogo
            Canvas.SetLeft(
                StartMenu_ArcademiaLogo,
                logicalScreenWidth / 2f - StartMenu_ArcademiaLogo.Width / 2f
            );
            Canvas.SetTop(StartMenu_ArcademiaLogo, 100);

            // PressStartText
            Canvas.SetLeft(PressStartText, logicalScreenWidth / 2f - PressStartText.Width / 2f);
            Canvas.SetTop(PressStartText, logicalScreenHeight / 2f - PressStartText.Height / 2f);

            // Set width and height of the logos
            double logoWidth = logicalScreenWidth * 0.06f;

            // UoL_Logo
            UoL_Logo.Width = logoWidth;
            UoL_Logo.Height = logoWidth;

            // intlab_Logo
            intlab_Logo.Width = logoWidth;
            intlab_Logo.Height = logoWidth;
            Canvas.SetRight(intlab_Logo, 10 + logoWidth);

            // CSS_Logo
            CSS_Logo.Width = logoWidth;
            CSS_Logo.Height = logoWidth;
            Canvas.SetRight(CSS_Logo, 2 * (10 + logoWidth));

            // Show the Start Menu
            StartMenu.Visibility = Visibility.Visible;
            _isStartMenuVisible = true;

            // Start the Debug Watchdog
            StartWatchdog();

            // Set the Copyright text
            Copyright.Text =
                "Copyright ©️ 2018 - "
                + DateTime.Now.Year
                + "\nUniversity of Lincoln,\nAll rights reserved.";

            // Initialize the TextBlock arrays
            _homeOptionsList = [GameLibraryText, InputMenuText, AboutText, ExitText];
            _gameTilesList =
            [
                GameTile0,
                GameTile1,
                GameTile2,
                GameTile3,
                GameTile4,
                GameTile5,
                GameTile6,
                GameTile7,
                GameTile8,
                GameTile9,
                GameTile10,
                GameTile11,
                GameTile12,
                GameTile13,
                GameTile14,
            ];
            _gameTitlesList =
            [
                GameTitleText0,
                GameTitleText1,
                GameTitleText2,
                GameTitleText3,
                GameTitleText4,
                GameTitleText5,
                GameTitleText6,
                GameTitleText7,
                GameTitleText8,
                GameTitleText9,
                GameTitleText10,
                GameTitleText11,
                GameTitleText12,
                GameTitleText13,
                GameTitleText14,
            ];
            _gameImagesList =
            [
                GameImage0,
                GameImage1,
                GameImage2,
                GameImage3,
                GameImage4,
                GameImage5,
                GameImage6,
                GameImage7,
                GameImage8,
                GameImage9,
                GameImage10,
                GameImage11,
                GameImage12,
                GameImage13,
                GameImage14,
            ];

            // Fall back to the placeholder for a tile if its thumbnail fails to load
            // (e.g. the backend is unreachable), instead of leaving a broken image
            for (int tileIndex = 0; tileIndex < _gameImagesList.Length; tileIndex++)
            {
                var capturedTileImage = _gameImagesList[tileIndex];
                var capturedTileIndex = tileIndex;
                AnimationBehavior.AddErrorHandler(
                    capturedTileImage,
                    (s, e) =>
                    {
                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            if (!_isBrowsingCollectionsTopLevel)
                            {
                                SetImageSource(capturedTileImage, _placeholderBitmap, false);
                                return;
                            }

                            int globalIndex = capturedTileIndex + _previousPageIndex * _tilesPerPage;
                            bool isSelected = globalIndex == _currentlySelectedGameIndex;
                            AnimationBehavior.SetSourceUri(capturedTileImage, null);
                            SetImageSource(
                                capturedTileImage,
                                isSelected
                                    ? _collectionPlaceholderOpenBitmap
                                    : _collectionPlaceholderClosedBitmap,
                                isCollectionPlaceholder: true
                            );
                        });
                    }
                );
            }

            AnimationBehavior.AddErrorHandler(
                Gif_GameThumbnail,
                (s, e) =>
                {
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        AnimationBehavior.SetSourceUri(Gif_GameThumbnail, null);
                    });
                }
            );

            LoadGameDatabase();
            InitHomeThumbnailScroll();

            // Perform an initial update of the game info display
            _currentlySelectedGameIndex = 0;
            UpdateGameInfoDisplay();

            // Initialize the updateTimer
            InitializeUpdateTimer();

            var controllerManagerLogger = LoggerFactory
                .Create(b => b.AddSerilog())
                .CreateLogger<ControllerManager>();
            _controllerManager = new ControllerManager(this, _tickSpeed, controllerManagerLogger);

            Task.Run(async () =>
            {
                while (true)
                {
                    // Wait random time (30-60 mins)
                    int delay = new Random(Guid.NewGuid().GetHashCode()).Next(
                        30 * 60 * 1000,
                        60 * 60 * 1000
                    );
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "[Audio Loop] Waiting {Minutes} minutes for next SFX.",
                            delay / 1000 / 60
                        );

                    await Task.Delay(delay);

                    try
                    {
                        _logger.LogInformation("[Audio Loop] Attempting to play Random SFX...");

                        await Task.Run(async () =>
                            {
                                await _sfxPlayer.PlayRandomPeriodicAsync();
                            })
                            .ConfigureAwait(false);

                        _logger.LogInformation("[Audio Loop] Finished playing Random SFX.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Audio Loop] Failed to play periodic SFX");
                    }
                }
            });

            Task.Run(async () =>
            {
                try
                {
                    await _updater.CheckControllerMappingAsync(CancellationToken.None);

                    if (production)
                        await CheckForUpdaterUpdates();
                    await CheckForGameDatabaseChanges();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Updater Loop] Initial check failed");
                }

                while (true)
                {
                    await Task.Delay(30 * 60 * 1000); // 30 Minutes
                    try
                    {
                        _logger.LogInformation("[Updater Loop] Starting scheduled update check...");

                        if (production)
                            await CheckForUpdaterUpdates();

                        await CheckForGameDatabaseChanges();
                        await _updater.CheckControllerMappingAsync(CancellationToken.None);

                        _logger.LogInformation("[Updater Loop] Scheduled update check finished.");
                    }
                    catch (TaskCanceledException tcx)
                    {
                        if (_logger.IsEnabled(LogLevel.Error))
                            _logger.LogError(tcx, "[Updater Loop] Scheduled update check canceled");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Updater Loop] Error during update check");
                    }
                }
            });

            _logger.LogInformation("[MainWindow] Initialized.");
        }

        // Initialization

        private void InitializeUpdateTimer()
        {
            // Timer Setup
            _updateTimer = new System.Timers.Timer(10) { AutoReset = false };
            _updateTimer.Elapsed += OnTimedEvent;
            _updateTimer.Start();
        }

        private void LoadGameDatabase()
        {
            _thumbnailCacheBuster = DateTime.Now.Ticks.ToString();
            // Load the game database from the GameDatabase.json file
            _gameInfoList = GameDatabaseService.LoadGameDatabase(_gameDirectoryPath);
            _collectionInfoList = CollectionDatabaseService.LoadCollectionDatabase(
                _gameDirectoryPath
            );
            RefreshGameWorkingListFromSource();

            if (_currentGameWorkingList.Length > 0)
            {
                // Load the game titles into the TextBlocks
                try
                {
                    _logger.LogDebug("[Load Database] LoadGameDatabase: Queued");
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Load Database] LoadGameDatabase: Start");

                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        for (
                            int i = _previousPageIndex * _tilesPerPage;
                            i < (_previousPageIndex + 1) * _tilesPerPage;
                            i++
                        )
                        {
                            if (i < _currentGameWorkingList.Length)
                            {
                                _gameTitlesList[i % _tilesPerPage]
                                    .FitTextToLabel(
                                        desiredText: _emojiParser.ReplaceColonNames(
                                            _currentGameWorkingList[i]["Name"].ToString()
                                        ),
                                        targetFontSize: 24,
                                        maxLines: 1,
                                        minFontSize: 8,
                                        precision: 0.1
                                    );
                                _gameTilesList[i % _tilesPerPage].Visibility = Visibility.Visible;
                            }
                            else
                                _gameTilesList[i % _tilesPerPage].Visibility = Visibility.Hidden;
                        }
                    });

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Load Database] LoadGameDatabase: End");
                }
                catch (TaskCanceledException tcx)
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.LogError(tcx, "[Load Database] LoadGameDatabase: Task Canceled");
                }
            }
        }

        private JObject[] BuildWorkingListForCollection(CollectionInfo collection)
        {
            if (collection?.GameIds == null || _gameInfoList == null)
                return [];

            var lookup = new Dictionary<int, JObject>();
            foreach (var game in _gameInfoList)
            {
                if (game?["Id"] == null)
                    continue;
                lookup[(int)game["Id"]] = game;
            }

            var result = new List<JObject>();
            foreach (var gameId in collection.GameIds)
                if (lookup.TryGetValue(gameId, out var game))
                    result.Add(game);

            return [.. result];
        }

        private JObject[] BuildWorkingListForCollectionsTopLevel()
        {
            if (_collectionInfoList == null)
                return [];

            var result = new List<JObject>();
            foreach (var collection in _collectionInfoList)
            {
                int gameCount = collection.GameIds.Count;
                string gameNoun = gameCount == 1 ? "game" : "games";

                result.Add(
                    new JObject
                    {
                        ["Id"] = collection.CollectionId,
                        ["Name"] = collection.Name,
                        ["Description"] = string.IsNullOrEmpty(collection.Description)
                            ? $"{gameCount} {gameNoun} in this collection."
                            : collection.Description,
                        ["ThumbnailUrlClosed"] = collection.ClosedImageURL ?? "",
                        ["ThumbnailUrlOpen"] = collection.OpenImageURL ?? "",
                        ["VersionNumber"] = $"{gameCount} {gameNoun.ToUpperInvariant()}",
                        ["Authors"] = new JArray(),
                        ["Tags"] = new JArray(),
                        ["FolderName"] = "",
                        ["NameOfExecutable"] = "",
                    }
                );
            }

            return [.. result];
        }

        private static string ResolveCollectionThumbnailUrl(JObject collectionItem, bool preferOpen)
        {
            string closedUrl = collectionItem["ThumbnailUrlClosed"]?.ToString() ?? "";
            string openUrl = collectionItem["ThumbnailUrlOpen"]?.ToString() ?? "";

            return preferOpen
                ? (!string.IsNullOrEmpty(openUrl) ? openUrl : closedUrl)
                : (!string.IsNullOrEmpty(closedUrl) ? closedUrl : openUrl);
        }

        private void RecomputeGameWorkingList()
        {
            _currentGameWorkingList = _isBrowsingCollectionsTopLevel
                ? BuildWorkingListForCollectionsTopLevel()
                : _activeCollectionInfo != null
                    ? BuildWorkingListForCollection(_activeCollectionInfo)
                    : (_gameInfoList ?? []);
        }

        private void RefreshGameWorkingListFromSource()
        {
            RecomputeGameWorkingList();
            _gameTitleStates = _isBrowsingCollectionsTopLevel
                ? Enumerable.Repeat(GameState.ready, _currentGameWorkingList.Length).ToArray()
                : GameDatabaseService.ValidateGameExecutables(
                    _currentGameWorkingList,
                    _gameDirectoryPath
                );
        }

        private void RefreshActiveCollectionReference()
        {
            if (_activeCollectionInfo == null)
                return;

            _activeCollectionInfo = Array.Find(
                _collectionInfoList,
                c => c.CollectionId == _activeCollectionInfo.CollectionId
            );
        }

        private void InitHomeThumbnailScroll()
        {
            if (_gameInfoList == null || _gameInfoList.Length == 0)
                return;

            // GameDatabase.json does not store an Id field, but ThumbnailUrl contains
            // the full API thumbnail URL for games that have one uploaded.
            var urls = new List<string>(_gameInfoList.Length);
            foreach (var game in _gameInfoList)
            {
                if (game == null)
                    continue;
                string url = game["ThumbnailUrl"]?.ToString();
                if (!string.IsNullOrEmpty(url))
                    urls.Add(url);
            }

            LoadHomeThumbnailScrollFromUrls(urls);
        }

        private void LoadHomeThumbnailScrollFromUrls(IEnumerable<string> thumbnailUrls)
        {
            var urls = new List<string>();
            foreach (var url in thumbnailUrls)
                if (!string.IsNullOrEmpty(url))
                    urls.Add(url);

            if (urls.Count == 0)
                return;

            _ = Task.Run(async () =>
            {
                Directory.CreateDirectory(_scrollGifTempDir);
                var entries = new List<ThumbnailEntry>(urls.Count);

                foreach (string url in urls)
                {
                    try
                    {
                        ThumbnailEntry entry;
                        bool cached;
                        lock (_scrollCacheLock)
                            cached = _scrollThumbnailCache.TryGetValue(url, out entry);

                        if (!cached)
                        {
                            var response = await _apiClient.Http.GetAsync(url);
                            if (!response.IsSuccessStatusCode)
                                continue;

                            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                            if (bytes == null || bytes.Length == 0)
                                continue;

                            bool isGif =
                                response.Content.Headers.ContentType?.MediaType == "image/gif"
                                || (
                                    bytes.Length > 3
                                    && bytes[0] == 0x47
                                    && bytes[1] == 0x49
                                    && bytes[2] == 0x46
                                );

                            if (isGif)
                            {
                                // Write to a local temp file
                                string tempPath = Path.Combine(
                                    _scrollGifTempDir,
                                    Guid.NewGuid().ToString("N") + ".gif"
                                );
                                await File.WriteAllBytesAsync(tempPath, bytes);

                                // Decode first frame as a frozen BitmapImage so the slot isn't empty while AnimationBehavior initialises the animation
                                var firstFrame = new BitmapImage();
                                firstFrame.BeginInit();
                                firstFrame.StreamSource = new MemoryStream(bytes);
                                firstFrame.CacheOption = BitmapCacheOption.OnLoad;
                                firstFrame.EndInit();
                                firstFrame.Freeze();

                                entry = new ThumbnailEntry(firstFrame, tempPath);
                                lock (_scrollCacheLock)
                                {
                                    _scrollThumbnailCache[url] = entry;
                                    _scrollGifTempFiles.Add(tempPath);
                                }
                            }
                            else
                            {
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.StreamSource = new MemoryStream(bytes);
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.EndInit();
                                bmp.Freeze();
                                entry = new ThumbnailEntry(bmp);

                                lock (_scrollCacheLock)
                                    _scrollThumbnailCache[url] = entry;
                            }
                        }

                        entries.Add(entry);
                    }
                    catch
                    {
                        // Skip thumbnails that fail to download or decode
                    }
                }

                if (entries.Count > 0)
                    await Dispatcher.InvokeAsync(() => HomeThumbnailScroll.LoadThumbnails(entries));
            });
        }

        // Updater Methods

        public async Task CheckForUpdaterUpdates() =>
            await _updater.CheckUpdaterAndUpdateAsync(CancellationToken.None);

        public async Task CheckControllerMapping() =>
            await _updater.CheckControllerMappingAsync(CancellationToken.None);

        public async Task<bool> CheckForGameDatabaseChanges()
        {
            try
            {
                await _updater.CheckGamesAndUpdateAsync(_gameInfoList, CancellationToken.None);
                return true;
            }
            catch (Exception)
            {
                try
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Load Database] CheckForGameDatabaseChanges: Start");

                    _thumbnailCacheBuster = DateTime.Now.Ticks.ToString();
                    _gameInfoList = GameDatabaseService.LoadGameDatabase(_applicationPath);
                    _collectionInfoList = CollectionDatabaseService.LoadCollectionDatabase(
                        _gameDirectoryPath
                    );
                    RefreshActiveCollectionReference();
                    RefreshGameWorkingListFromSource();

                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        for (
                            int i = _previousPageIndex * _tilesPerPage;
                            i < (_previousPageIndex + 1) * _tilesPerPage;
                            i++
                        )
                        {
                            if (i < _currentGameWorkingList.Length)
                            {
                                _gameTitlesList[i % _tilesPerPage].Content = "Loading...";
                                _gameTilesList[i % _tilesPerPage].Visibility = Visibility.Visible;
                            }
                            else
                                _gameTilesList[i % _tilesPerPage].Visibility = Visibility.Hidden;
                        }
                    });

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Load Database] CheckForGameDatabaseChanges: End");

                    return true;
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.LogError(ex, "[Load Database] LoadGameDatabase: Exception");

                    return false;
                }
            }
        }

        // Custom TextBlock Buttons

        private void GameLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            _activeCollectionInfo = null;
            _selectionMenuEnteredViaCollection = false;
            _isBrowsingCollectionsTopLevel = _collectionInfoList != null
                && _collectionInfoList.Length > 0;
            RefreshGameWorkingListFromSource();

            try
            {
                _logger.LogDebug("[Navigation] GameLibraryButton_Click: Queued");
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] GameLibraryButton_Click: Start");

                    StartMenu.Visibility = Visibility.Collapsed;
                    _isStartMenuVisible = false;
                    HomeMenu.Visibility = Visibility.Collapsed;
                    _isHomeMenuVisible = false;
                    SelectionMenu.Visibility = Visibility.Visible;
                    _isSelectionMenuVisible = true;
                    InputMenu.Visibility = Visibility.Collapsed;
                    _isInputMenuVisible = false;

                    SelectionMenuHeaderText.Text = _isBrowsingCollectionsTopLevel
                        ? "Collections"
                        : "Games";

                    ChangePage(0);

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] GameLibraryButton_Click: End");
                });
            }
            catch (TaskCanceledException tcx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(tcx, "[Navigation] GameLibraryButton_Click: Task Canceled");
            }

            ApplyWindowZOrder();

            _currentlySelectedGameIndex = _currentGameWorkingList.Length > 0 ? 0 : -1;
            DebounceUpdateGameInfoDisplay();
        }

        private void OpenSelectedCollection()
        {
            if (
                !_isBrowsingCollectionsTopLevel
                || _currentlySelectedGameIndex < 0
                || _currentlySelectedGameIndex >= _currentGameWorkingList.Length
            )
                return;

            var selectedId = (int)_currentGameWorkingList[_currentlySelectedGameIndex]["Id"];
            var collection = Array.Find(
                _collectionInfoList,
                c => c.CollectionId == selectedId
            );
            if (collection == null)
                return;

            _activeCollectionInfo = collection;
            _isBrowsingCollectionsTopLevel = false;
            _selectionMenuEnteredViaCollection = true;
            RefreshGameWorkingListFromSource();

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                SelectionMenuHeaderText.Text = collection.Name;
                ChangePage(0);
            });

            _currentlySelectedGameIndex = _currentGameWorkingList.Length > 0 ? 0 : -1;
            DebounceUpdateGameInfoDisplay();
        }

        private void InputMenuButton_Click(object sender, RoutedEventArgs e)
        {
            // Show the Input Menu
            try
            {
                _logger.LogDebug("[Navigation] InputMenuButton_Click: Queued");
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] InputMenuButton_Click: Start");

                    StartMenu.Visibility = Visibility.Collapsed;
                    _isStartMenuVisible = false;
                    HomeMenu.Visibility = Visibility.Collapsed;
                    _isHomeMenuVisible = false;
                    SelectionMenu.Visibility = Visibility.Collapsed;
                    _isSelectionMenuVisible = false;
                    InputMenu.Visibility = Visibility.Visible;
                    _isInputMenuVisible = true;

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] InputMenuButton_Click: End");
                });
            }
            catch (TaskCanceledException tcx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(tcx, "[Navigation] InputMenuButton_Click: Task Canceled");
            }

            // Set the focus to the game launcher
            ApplyWindowZOrder();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            // Show the About Menu
            try
            {
                _logger.LogDebug("[Navigation] AboutButton_Click: Queued");
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] AboutButton_Click: Start");

                    StartMenu.Visibility = Visibility.Collapsed;
                    _isStartMenuVisible = false;
                    HomeMenu.Visibility = Visibility.Visible;
                    _isHomeMenuVisible = true;
                    SelectionMenu.Visibility = Visibility.Collapsed;
                    _isSelectionMenuVisible = false;
                    InputMenu.Visibility = Visibility.Collapsed;
                    _isInputMenuVisible = false;

                    HomeThumbnailScroll.Opacity = 0.2;
                    CreditsPanel.Visibility = Visibility.Visible;
                    _isCreditsVisible = true;

                    // Show the CreditsPanel Logos
                    UoL_Logo.Visibility = Visibility.Visible;
                    intlab_Logo.Visibility = Visibility.Visible;
                    CSS_Logo.Visibility = Visibility.Visible;

                    // Set Canvas.Top of the CreditsPanel to the screen height
                    string logicalScreenHeight_str = TryFindResource("LogicalSizeHeight")
                        .ToString();
                    double logicalScreenHeight = double.Parse(logicalScreenHeight_str);

                    Canvas.SetTop(CreditsPanel, logicalScreenHeight);

                    // Generate the Credits
                    CreditsGenerator.Generate(CreditsPanel, GifTemplateElement_Parent);

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] AboutButton_Click: End");
                });
            }
            catch (TaskCanceledException tcx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(tcx, "[Navigation] AboutButton_Click: Task Canceled");
            }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            // Show the Start Menu
            try
            {
                _logger.LogDebug("[Navigation] ExitButton_Click: Queued");
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] ExitButton_Click: Start");

                    StartMenu.Visibility = Visibility.Visible;
                    _isStartMenuVisible = true;
                    HomeMenu.Visibility = Visibility.Collapsed;
                    _isHomeMenuVisible = false;
                    SelectionMenu.Visibility = Visibility.Collapsed;
                    _isSelectionMenuVisible = false;
                    InputMenu.Visibility = Visibility.Collapsed;
                    _isInputMenuVisible = false;

                    HomeThumbnailScroll.Opacity = 1;
                    CreditsPanel.Visibility = Visibility.Collapsed;
                    _isCreditsVisible = false;

                    // Hide the CreditsPanel Logos
                    UoL_Logo.Visibility = Visibility.Collapsed;
                    intlab_Logo.Visibility = Visibility.Collapsed;
                    CSS_Logo.Visibility = Visibility.Collapsed;

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] ExitButton_Click: End");
                });
            }
            catch (TaskCanceledException tcx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(tcx, "[Navigation] ExitButton_Click: Task Canceled");
            }

            // Set the focus to the game launcher
            ApplyWindowZOrder();

            // Reset AFK Timer after Half a Second
            Task.Delay(500)
                .ContinueWith(t =>
                {
                    _afkTimerActive = false;
                    _afkTimer = 0;
                });
        }

        // ToggleButton Methods

        private void BackFromGameLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            bool returnToCollections = _selectionMenuEnteredViaCollection;
            int? exitedCollectionId = _activeCollectionInfo?.CollectionId;

            _activeCollectionInfo = null;
            _selectionMenuEnteredViaCollection = false;
            _isBrowsingCollectionsTopLevel =
                returnToCollections && _collectionInfoList != null && _collectionInfoList.Length > 0;
            RefreshGameWorkingListFromSource();

            // Re-select the collection that was just backed out of, instead of resetting to the first one
            int returnSelectedIndex = 0;
            if (_isBrowsingCollectionsTopLevel && exitedCollectionId.HasValue)
            {
                int foundIndex = Array.FindIndex(
                    _currentGameWorkingList,
                    g => g != null && (int)g["Id"] == exitedCollectionId.Value
                );
                if (foundIndex >= 0)
                    returnSelectedIndex = foundIndex;
            }

            try
            {
                _logger.LogDebug("[Navigation] BackFromGameLibraryButton_Click: Queued");
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] BackFromGameLibraryButton_Click: Start");

                    StartMenu.Visibility = Visibility.Collapsed;
                    _isStartMenuVisible = false;
                    InputMenu.Visibility = Visibility.Collapsed;
                    _isInputMenuVisible = false;

                    if (_isBrowsingCollectionsTopLevel)
                    {
                        SelectionMenuHeaderText.Text = "Collections";
                        ChangePage(returnSelectedIndex / _tilesPerPage);
                    }
                    else
                    {
                        SelectionMenu.Visibility = Visibility.Collapsed;
                        _isSelectionMenuVisible = false;
                        HomeMenu.Visibility = Visibility.Visible;
                        _isHomeMenuVisible = true;
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Navigation] BackFromGameLibraryButton_Click: End");
                });
            }
            catch (TaskCanceledException tcx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(
                        tcx,
                        "[Navigation] BackFromGameLibraryButton_Click: Task Canceled"
                    );
            }

            // Set the focus to the game launcher
            ApplyWindowZOrder();

            if (_isBrowsingCollectionsTopLevel)
            {
                _currentlySelectedGameIndex =
                    _currentGameWorkingList.Length > 0 ? returnSelectedIndex : -1;
                DebounceUpdateGameInfoDisplay();
            }
            else
            {
                // Set the currently selected Home Index to 0 and highlight the current Home Menu Option
                _currentlySelectedHomeIndex = 0;
                HighlightCurrentHomeMenuOption();
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBrowsingCollectionsTopLevel)
            {
                OpenSelectedCollection();
                return;
            }

            // If the game info display is not showing the currently selected game, return
            if (
                !_showingDebouncedGame
                || _gameTitleStates[_currentlySelectedGameIndex] != GameState.ready
            )
                return;

            // Get the current game folder, game info, and game executable
            string currentGameFolder = _currentGameWorkingList[_currentlySelectedGameIndex]
                ["FolderName"]
                .ToString();
            string currentGameExe = Path.Combine(
                _gameDirectoryPath,
                currentGameFolder,
                _currentGameWorkingList[_currentlySelectedGameIndex]["NameOfExecutable"].ToString()
            );

            // Start the game if the game executable exists and the launcher is ready
            if (File.Exists(currentGameExe))
            {
                // Create a new ProcessStartInfo object and set the Working Directory to the game directory
                ProcessStartInfo startInfo = new(currentGameExe)
                {
                    WorkingDirectory = Path.Combine(_gameDirectoryPath, currentGameFolder),
                };

                if (_currentlyRunningProcess == null || _currentlyRunningProcess.HasExited)
                {
                    var sdkSessionId = Guid.NewGuid().ToString();
                    var sdkEnvironment = _sdkBroker.Start(sdkSessionId);
                    startInfo.Environment[SdkBrokerService.PipeVariable] = sdkEnvironment.PipeName;
                    startInfo.Environment[SdkBrokerService.NonceVariable] = sdkEnvironment.Nonce;
                    startInfo.Environment[SdkBrokerService.SessionVariable] =
                        sdkEnvironment.SessionId;

                    Process startedProcess;
                    try
                    {
                        _currentlyRunningProcess = startedProcess = Process.Start(startInfo);
                    }
                    catch
                    {
                        _sdkBroker.Stop();
                        throw;
                    }
                    StyleStartButtonState(GameState.launching);

                    _ = _socket.SafeReportStatus(
                        "Playing",
                        _currentGameWorkingList[_currentlySelectedGameIndex]["Name"].ToString()
                    );

                    var gameId = (int)
                        _currentGameWorkingList[_currentlySelectedGameIndex]["Id"];
                    _ = _sessionTracking.StartSessionAsync(
                        sdkSessionId,
                        gameId,
                        startedProcess?.StartTime ?? DateTime.UtcNow
                    );

                    await FocusGameWindowAsync(startedProcess);
                    ApplyWindowZOrder();

                    if (startedProcess == null || startedProcess.HasExited)
                    {
                        SetGameTitleState(_currentlySelectedGameIndex, GameState.ready);
                        StyleStartButtonState(_currentlySelectedGameIndex);
                        return;
                    }
                }
                else
                {
                    try
                    {
                        _currentlyRunningProcess.Refresh();
                        var handle = _currentlyRunningProcess.MainWindowHandle;
                        if (handle != IntPtr.Zero)
                            WindowHelper.ForceForeground(handle);
                    }
                    catch { }
                    ApplyWindowZOrder();
                }

                SetGameTitleState(_currentlySelectedGameIndex, GameState.runningGame);
                StyleStartButtonState(_currentlySelectedGameIndex);
            }
        }

        // Event Handlers

        public void Key_Pressed()
        {
            // Keylogger for AFK Timer
            if (_afkTimerActive)
            {
                _afkTimer = 0;
            }
            else
            {
                _afkTimerActive = true;
                _afkTimer = 0;
                _timeSinceLastButton = 0;

                // Show the Home Menu
                try
                {
                    _logger.LogDebug("[First Input] Key_Pressed: Queued");
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("[First Input] Key_Pressed: Start");

                        StartMenu.Visibility = Visibility.Collapsed;
                        _isStartMenuVisible = false;
                        HomeMenu.Visibility = Visibility.Visible;
                        _isHomeMenuVisible = true;
                        SelectionMenu.Visibility = Visibility.Collapsed;
                        _isSelectionMenuVisible = false;
                        InputMenu.Visibility = Visibility.Collapsed;
                        _isInputMenuVisible = false;

                        _currentlySelectedHomeIndex = 0;
                        HighlightCurrentHomeMenuOption();

                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("[First Input] Key_Pressed: End");
                    });
                }
                catch (TaskCanceledException tcx)
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.LogError(tcx, "[First Input] Key_Pressed: Task Canceled");
                }
            }

            ApplyWindowZOrder();
        }

        private void ResetControllerStates()
        {
            _timeSinceLastButton = 0;
            _controllerManager.ReleaseAllButtons();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            if (_infoWindow != null)
            {
                // _infoWindow.Owner = this; // Commented out to allow the Game window to be between InfoWindow and Launcher
            }
        }

        private async void StartWatchdog()
        {
            await Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(UiStallCheckIntervalSeconds * 1000);

                    var timeSinceLastTick = DateTime.Now - _lastSuccessfulTick;
                    if (timeSinceLastTick.TotalSeconds > UiStallCheckIntervalSeconds)
                    {
                        _logger.LogError(
                            "[WATCHDOG] Background tick loop hasn't run in {Seconds}s "
                                + "(last step: {Step}). The tick loop runs on the thread pool, "
                                + "so this alone doesn't prove the UI is frozen.",
                            timeSinceLastTick.TotalSeconds,
                            _currentStep
                        );
                    }

                    bool uiResponded = false;
                    try
                    {
                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher != null)
                        {
                            var pingTask = dispatcher
                                .InvokeAsync(
                                    () =>
                                        Interlocked.Exchange(
                                            ref _lastUiHeartbeatTicks,
                                            DateTime.UtcNow.Ticks
                                        ),
                                    System.Windows.Threading.DispatcherPriority.Send
                                )
                                .Task;
                            var winner = await Task.WhenAny(
                                pingTask,
                                Task.Delay(UiStallCheckIntervalSeconds * 1000 - 1000)
                            );
                            uiResponded = winner == pingTask;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[WATCHDOG] Failed to probe UI dispatcher");
                    }

                    if (!uiResponded)
                    {
                        _consecutiveUiStalls++;
                        LogFreezeDiagnostics(_consecutiveUiStalls);

                        if (_consecutiveUiStalls >= UiStallsBeforeRecovery)
                        {
                            RecoverFromFrozenUiThread();
                            return;
                        }
                    }
                    else
                    {
                        _consecutiveUiStalls = 0;
                    }
                }
            });
        }

        private void LogFreezeDiagnostics(int consecutiveStalls)
        {
            try
            {
                ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
                ThreadPool.GetMinThreads(out int minWorker, out int minIo);
                ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
                var (gdiObjects, userObjects) = WindowHelper.GetGdiUserHandleCounts();

                using var currentProcess = Process.GetCurrentProcess();
                currentProcess.Refresh();

                _logger.LogError(
                    "[WATCHDOG] UI dispatcher unresponsive for ~{Seconds}s (stall #{Count}). "
                        + "LastStep={Step} "
                        + "WorkerThreads(avail/min/max)={AvailWorker}/{MinWorker}/{MaxWorker} "
                        + "IOThreads(avail/min/max)={AvailIo}/{MinIo}/{MaxIo} "
                        + "GDIObjects={GdiObjects} UserObjects={UserObjects} "
                        + "HandleCount={HandleCount} ThreadCount={ThreadCount} "
                        + "WorkingSetMB={WorkingSetMb} ManagedMemoryMB={ManagedMemoryMb} "
                        + "Gen0/1/2Collections={Gen0}/{Gen1}/{Gen2}",
                    consecutiveStalls * UiStallCheckIntervalSeconds,
                    consecutiveStalls,
                    _currentStep,
                    availWorker,
                    minWorker,
                    maxWorker,
                    availIo,
                    minIo,
                    maxIo,
                    gdiObjects,
                    userObjects,
                    currentProcess.HandleCount,
                    currentProcess.Threads.Count,
                    currentProcess.WorkingSet64 / (1024 * 1024),
                    GC.GetTotalMemory(false) / (1024 * 1024),
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WATCHDOG] Failed to collect freeze diagnostics");
            }
        }

        private void RecoverFromFrozenUiThread()
        {
            try
            {
                _logger.LogCritical(
                    "[WATCHDOG] UI dispatcher confirmed unresponsive for ~{Seconds}s. "
                        + "Relaunching and terminating this instance so the launcher can "
                        + "keep running unattended.",
                    _consecutiveUiStalls * UiStallCheckIntervalSeconds
                );
                Log.CloseAndFlush();

                string exePath =
                    Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    Process.Start(
                        new ProcessStartInfo(exePath)
                        {
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                            UseShellExecute = true,
                        }
                    );
                }
            }
            catch { }
            finally
            {
                Process.GetCurrentProcess().Kill();
            }
        }

        private void OnTimedEvent(object sender, ElapsedEventArgs e)
        {
            if (_isTimerRunning)
                return;

            _isTimerRunning = true;

            _lastSuccessfulTick = DateTime.Now;

            long nowElapsedMs = _frameStopwatch.ElapsedMilliseconds;
            int frameMs = (int)Math.Clamp(nowElapsedMs - _lastFrameElapsedMs, 0, MaxFrameMs);
            _lastFrameElapsedMs = nowElapsedMs;

            try
            {
                _currentStep = "Maintenance Check";
                if (_isMaintenanceVisible)
                    return;

                _currentStep = "Keyboard Check";
                if (GetAsyncKeyState(69) != 0)
                    Window_Closing(null, null);

                _currentStep = "Exit Logic";
                HandleExitLogic();

                _currentStep = "AFK Check";
                HandleAFKCheck();

                _currentStep = "UI Animations";
                AnimateUI(frameMs);

                _currentStep = "Counters";
                if (_afkTimerActive)
                    _afkTimer += frameMs;
                if (_selectionUpdateCounter > _selectionUpdateInterval)
                    _selectionUpdateIntervalCounter = 0;

                _selectionUpdateCounter += frameMs;
                _timeSinceLastButton += frameMs;

                _globalCounter += frameMs;

                if (_globalCounter >= int.MaxValue)
                    _globalCounter = 0;

                _currentStep = "Finished";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[Timer Loop] Exception in step: {_currentStep}");
            }
            finally
            {
                _isTimerRunning = false;
                if (_updateTimer != null)
                {
                    try
                    {
                        _updateTimer.Start();
                    }
                    catch { }
                }
            }
        }

        private async Task FocusGameWindowAsync(Process process)
        {
            if (process == null)
                return;

            var deadline = DateTime.UtcNow.AddSeconds(10);
            IntPtr handle = IntPtr.Zero;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (process.HasExited)
                        return;
                    process.Refresh();
                    handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                        break;
                }
                catch
                {
                    return;
                }
                await Task.Delay(200);
            }

            if (handle == IntPtr.Zero)
            {
                _logger.LogWarning("[Focus] Game window not found within timeout");
                return;
            }

            _logger.LogDebug("[Focus] Game window found, applying focus");
            WindowHelper.ForceForeground(handle);
            ApplyWindowZOrder();

            var settleDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < settleDeadline)
            {
                await Task.Delay(300);
                try
                {
                    if (process.HasExited)
                        return;
                    process.Refresh();
                    var currentHandle = process.MainWindowHandle;
                    if (currentHandle != IntPtr.Zero)
                    {
                        handle = currentHandle;
                        WindowHelper.ForceForeground(handle);
                    }
                }
                catch
                {
                    return;
                }
                ApplyWindowZOrder();
            }
        }

        private void ApplyWindowZOrder()
        {
            Dispatcher?.InvokeAsync(() =>
            {
                bool gameRunning =
                    _currentlyRunningProcess != null && !_currentlyRunningProcess.HasExited;

                IntPtr gameHandle = IntPtr.Zero;
                if (gameRunning)
                {
                    try
                    {
                        _currentlyRunningProcess.Refresh();
                        gameHandle = _currentlyRunningProcess.MainWindowHandle;
                    }
                    catch { }
                }

                if (
                    gameRunning
                    && gameHandle == IntPtr.Zero
                    && !_isClaimWindowVisible
                    && !_isInfoWindowVisible
                )
                    return;

                IntPtr infoHandle = IntPtr.Zero;
                if (_infoWindow != null)
                {
                    try
                    {
                        infoHandle = new WindowInteropHelper(_infoWindow).Handle;
                    }
                    catch { }
                }

                IntPtr claimHandle = IntPtr.Zero;
                if (_claimWindow != null)
                {
                    try
                    {
                        claimHandle = new WindowInteropHelper(_claimWindow).Handle;
                    }
                    catch { }
                }

                IntPtr launcherHandle = IntPtr.Zero;
                try
                {
                    launcherHandle = new WindowInteropHelper(this).Handle;
                }
                catch { }

                if (_isClaimWindowVisible)
                    WindowHelper.SetWindowOrder(claimHandle, gameHandle, launcherHandle);
                else if (_isInfoWindowVisible)
                    WindowHelper.SetWindowOrder(infoHandle, gameHandle, launcherHandle);
                else if (gameHandle != IntPtr.Zero)
                    WindowHelper.SetWindowOrder(gameHandle, infoHandle, launcherHandle);
                else
                    WindowHelper.SetWindowOrder(launcherHandle, infoHandle, IntPtr.Zero);
            });
        }

        private void UpdateClaimWindowPosition()
        {
            Dispatcher?.InvokeAsync(() =>
            {
                if (_claimWindow == null)
                    return;

                double targetWidth = this.ActualWidth;
                double targetHeight = this.ActualHeight;

                double claimWidth =
                    _claimWindow.ActualWidth > 0 ? _claimWindow.ActualWidth : _claimWindow.Width;
                double claimHeight =
                    _claimWindow.ActualHeight > 0 ? _claimWindow.ActualHeight : _claimWindow.Height;

                if (double.IsNaN(claimWidth))
                    claimWidth = 1000;
                if (double.IsNaN(claimHeight))
                    claimHeight = 600;

                _claimWindow.Left = this.Left + (targetWidth - claimWidth) / 2;
                _claimWindow.Top = this.Top + (targetHeight - claimHeight) / 2;
            });
        }

        private void ClaimCoordinator_ClaimShown(string claimUrl, DateTime expiresAtUtc)
        {
            _isClaimWindowVisible = true;
            UpdateClaimWindowPosition();
            _claimWindow.ShowWindow(claimUrl);
            ApplyWindowZOrder();
        }

        private void ClaimCoordinator_ClaimTick(int millisecondsRemaining) =>
            _claimWindow.UpdateCountdown(millisecondsRemaining);

        private void ClaimCoordinator_ClaimHidden()
        {
            _isClaimWindowVisible = false;
            _claimWindow.HideWindow();
            ApplyWindowZOrder();
        }

        private void UpdateInfoWindowPosition()
        {
            Dispatcher?.InvokeAsync(() =>
            {
                if (_infoWindow != null)
                {
                    double targetWidth = this.ActualWidth;
                    double targetHeight = this.ActualHeight;

                    double infoWidth =
                        _infoWindow.ActualWidth > 0 ? _infoWindow.ActualWidth : _infoWindow.Width;
                    double infoHeight =
                        _infoWindow.ActualHeight > 0
                            ? _infoWindow.ActualHeight
                            : _infoWindow.Height;

                    if (double.IsNaN(infoWidth))
                        infoWidth = 1000;
                    if (double.IsNaN(infoHeight))
                        infoHeight = 600;

                    _infoWindow.Left = this.Left + (targetWidth - infoWidth) / 2;
                    _infoWindow.Top = this.Top + (targetHeight - infoHeight) / 2;
                }
            });
        }

        private void HandleExitLogic()
        {
            if (_isClaimWindowVisible)
            {
                if (
                    _controllerManager.GetEitherButtonDownState(
                        ControllerState.ControllerActions.Exit
                    )
                )
                    _claimCoordinator.CancelActive();
                return;
            }

            // If a game is running
            if (_currentlyRunningProcess != null && !_currentlyRunningProcess.HasExited)
            {
                int exitHeldFor = _controllerManager.GetExitButtonHeldFor();

                // If the exit button has been held for at least 3 seconds
                if (exitHeldFor >= 3000)
                {
                    _logger.LogInformation("[Force Exit] Force-exiting game.");

                    _dispatcherQueue.EnqueueUnique(
                        "ForceExit",
                        () =>
                        {
                            _infoWindow?.HideWindow();
                            _isInfoWindowVisible = false;
                            _isInfoWindowIdleVisible = false;
                            _isInfoWindowForceExitVisible = false;

                            SetGameTitleState(_currentlySelectedGameIndex, GameState.ready);
                            ResetControllerStates();

                            try
                            {
                                if (
                                    _currentlyRunningProcess != null
                                    && !_currentlyRunningProcess.HasExited
                                )
                                {
                                    _currentlyRunningProcess.Kill();

                                    _logger.LogInformation(
                                        "[Force Exit] Process killed successfully."
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[Force Exit] Failed to kill process.");
                            }

                            _ = _sessionTracking.EndSessionAsync("ForceExit");
                            _currentlyRunningProcess = null;
                            _ = _socket.SafeReportStatus("Idle");

                            ApplyWindowZOrder();
                        }
                    );
                }
                // If the exit button has been held for at least 1 second
                else if (exitHeldFor >= 1000)
                {
                    if (_isInfoWindowVisible && _isInfoWindowForceExitVisible)
                        _infoWindow?.UpdateCountdown(3000 - exitHeldFor);
                    else if (!_isInfoWindowVisible && !_isInfoWindowIdleVisible)
                    {
                        _isInfoWindowVisible = true;
                        _isInfoWindowForceExitVisible = true;

                        _infoWindow?.SetCloseGameName(
                            _currentGameWorkingList[_currentlySelectedGameIndex]["Name"].ToString()
                        );
                        UpdateInfoWindowPosition();
                        _infoWindow?.ShowWindow(InfoWindowType.ForceExit);
                        _infoWindow?.UpdateCountdown(3000 - exitHeldFor);
                    }

                    if (exitHeldFor % 500 < _tickSpeed)
                        ApplyWindowZOrder();
                }
                // If the info window is visible but the exit button is not held
                else if (_isInfoWindowVisible && _isInfoWindowForceExitVisible && exitHeldFor < 500)
                {
                    _infoWindow?.HideWindow();

                    ApplyWindowZOrder();

                    _isInfoWindowVisible = false;
                    _isInfoWindowIdleVisible = false;
                    _isInfoWindowForceExitVisible = false;
                }
            }
            // If a game was running but has exited
            else if (_currentlyRunningProcess != null && _currentlyRunningProcess.HasExited)
            {
                _logger.LogInformation("[Game Exit] Game exited naturally.");

                SetGameTitleState(_currentlySelectedGameIndex, GameState.ready);
                ResetControllerStates();
                _ = _socket.SafeReportStatus("Idle");
                _ = _sessionTracking.EndSessionAsync("Natural");
                _currentlyRunningProcess = null;

                ApplyWindowZOrder();
            }
            // If no game is running but the force exit info window is visible
            else if (_isInfoWindowVisible && _isInfoWindowForceExitVisible)
            {
                _infoWindow?.HideWindow();

                ApplyWindowZOrder();

                _isInfoWindowVisible = false;
                _isInfoWindowForceExitVisible = false;
            }
        }

        private void HandleAFKCheck()
        {
            if (_isClaimWindowVisible)
            {
                _afkTimer = 0;
                return;
            }

            // If the player is AFK for too long
            if (_afkTimer >= _noInputTimeout + 5000)
            {
                _logger.LogInformation("[AFK Check] AFK timer expired. Force-exiting game.");

                _infoWindow?.UpdateCountdown(0);
                _afkTimerActive = false;
                _afkTimer = 0;

                _infoWindow?.HideWindow();
                _isInfoWindowVisible = false;
                _isInfoWindowIdleVisible = false;
                _isInfoWindowForceExitVisible = false;

                // If a game is running, kill it
                if (_currentlyRunningProcess != null && !_currentlyRunningProcess.HasExited)
                {
                    SetGameTitleState(_currentlySelectedGameIndex, GameState.ready);
                    ResetControllerStates();
                    try
                    {
                        _currentlyRunningProcess.Kill();
                    }
                    catch { }
                    _ = _sessionTracking.EndSessionAsync("AFK");
                    _ = _socket.SafeReportStatus("Idle");
                    _currentlyRunningProcess = null;
                }

                _dispatcherQueue.EnqueueUnique(
                    "ResetView",
                    () =>
                    {
                        StartMenu.Visibility = Visibility.Visible;
                        _isStartMenuVisible = true;
                        HomeMenu.Visibility = Visibility.Collapsed;
                        _isHomeMenuVisible = false;
                        SelectionMenu.Visibility = Visibility.Collapsed;
                        _isSelectionMenuVisible = false;
                        InputMenu.Visibility = Visibility.Collapsed;
                        _isInputMenuVisible = false;

                        ApplyWindowZOrder();
                    }
                );
            }
            // If the player is AFK
            else if (_afkTimer >= _noInputTimeout)
            {
                if (!_isInfoWindowIdleVisible)
                {
                    _logger.LogInformation("[AFK Check] Detected AFK. Showing idle window");

                    _isInfoWindowVisible = true;
                    _isInfoWindowIdleVisible = true;

                    _infoWindow?.SetCloseGameName(
                        _currentlyRunningProcess != null
                            ? _currentGameWorkingList[_currentlySelectedGameIndex]["Name"]
                                .ToString()
                            : null
                    );

                    UpdateInfoWindowPosition();
                    _infoWindow?.ShowWindow(InfoWindowType.Idle);
                    ApplyWindowZOrder();
                }

                if (_afkTimer % 100 < _tickSpeed)
                    _infoWindow?.UpdateCountdown(_noInputTimeout + 5000 - _afkTimer);
            }
            // If the info window is visible but the player is no longer AFK
            else if (_isInfoWindowVisible && _isInfoWindowIdleVisible)
            {
                _infoWindow?.HideWindow();
                _timeSinceLastButton = 0;

                _isInfoWindowVisible = false;
                _isInfoWindowIdleVisible = false;
                _isInfoWindowForceExitVisible = false;

                ApplyWindowZOrder();
            }
        }

        private void AnimateUI(int frameMs)
        {
            if (
                (_isHomeMenuVisible || _isSelectionMenuVisible)
                && _globalCounter % _selectionAnimationFrameRate < frameMs
            )
            {
                if (_selectionAnimationFrame < _selectionAnimationFrames.Length - 1)
                    _selectionAnimationFrame++;
                else
                    _selectionAnimationFrame = 0;

                if (_isHomeMenuVisible)
                    HighlightCurrentHomeMenuOption();
                else if (_isSelectionMenuVisible)
                    HighlightCurrentGameMenuOption();
            }

            if (_isInputMenuVisible)
                UpdateInputMenuFeedback();

            if (_isHomeMenuVisible || _isSelectionMenuVisible)
                UpdateCurrentSelection();

            if (_isCreditsVisible)
                AutoScrollCredits(frameMs);

            if (_isStartMenuVisible)
            {
                if (_timeSinceLastButton % 300 < frameMs)
                {
                    Application.Current?.Dispatcher?.InvokeAsync(
                        () =>
                            PressStartText.Visibility =
                                PressStartText.Visibility == Visibility.Visible
                                    ? Visibility.Hidden
                                    : Visibility.Visible
                    );
                }
            }
        }

        private void Updater_LogoDownloaded(object sender, EventArgs e)
        {
            // Check if the logo file exists
            if (!File.Exists(Path.Combine(_applicationPath, "Arcademia_Logo.png")))
                return;

            try
            {
                _logger.LogDebug("[Updater] Updater_LogoDownloaded: Queued");
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Updater] Updater_LogoDownloaded: Start");

                    StartMenu_ArcademiaLogo.Source = new BitmapImage(
                        new Uri(Path.Combine(_applicationPath, "Arcademia_Logo.png"))
                    );
                    HomeMenu_ArcademiaLogo.Source = new BitmapImage(
                        new Uri(Path.Combine(_applicationPath, "Arcademia_Logo.png"))
                    );

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Updater] Updater_LogoDownloaded: End");
                });
            }
            catch (TaskCanceledException tcx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(tcx, "[Updater] Updater_LogoDownloaded: Task Canceled");
            }
        }

        private void Updater_GameStateChanged(object sender, GameStateChangedEventArgs e)
        {
            int gameIndex = -1;
            for (int i = 0; i < _currentGameWorkingList.Length; i++)
            {
                if (_currentGameWorkingList[i]["Name"].ToString() == e.GameName)
                {
                    gameIndex = i;
                    break;
                }
            }

            // If the game is not found, return
            if (gameIndex == -1)
            {
                Console.WriteLine($"{e.GameName} not found in current game working list.");
                return;
            }

            // Set the game title state to the new state
            SetGameTitleState(gameIndex, e.NewState);
        }

        private void Updater_GameDownloadProgress(object sender, GameDownloadProgressEventArgs e)
        {
            if (
                _currentGameWorkingList == null
                || _currentlySelectedGameIndex < 0
                || _currentlySelectedGameIndex >= _currentGameWorkingList.Length
            )
                return;
            if (
                _currentGameWorkingList[_currentlySelectedGameIndex]["Name"]?.ToString()
                != e.GameName
            )
                return;

            _dispatcherQueue.EnqueueUnique(
                "DownloadProgress",
                () =>
                {
                    StartButton.Content = $"Downloading Game... {e.Percent}%";
                    SetStartButtonFill(e.Percent / 100.0, _fillDownloading);
                }
            );
        }

        private void Updater_GameDatabaseFetched(object sender, GameDatabaseFetchedEventArgs e)
        {
            _thumbnailCacheBuster = DateTime.Now.Ticks.ToString();
            SimplifiedGameInfo[] games = e.Games;

            // Convert to JsonArray for local storage
            JArray gameInfoArray = [];
            foreach (var game in games)
            {
                JArray gameTagsArray = [];
                foreach (var tag in game.Tags)
                {
                    JObject tagObject = new() { ["Name"] = tag.Name };

                    // If the tag has a colour, add it to the tagObject
                    if (tag.Colour != null)
                        tagObject["Colour"] = tag.Colour.ToString();

                    gameTagsArray.Add(tagObject);
                }

                JObject gameInfo = new()
                {
                    ["Id"] = game.Id,
                    ["VersionNumber"] = game.VersionNumber,
                    ["Name"] = game.Name,
                    ["Description"] = game.Description,
                    ["ThumbnailUrl"] = game.ThumbnailUrl,
                    ["Authors"] = new JArray(game.Authors),
                    ["Tags"] = gameTagsArray,
                    ["NameOfExecutable"] = game.NameOfExecutable,
                    ["FolderName"] = game.FolderName,
                };
                gameInfoArray.Add(gameInfo);
            }

            // Write the game info array to the local game database file
            File.WriteAllText(
                Path.Combine(_gameDirectoryPath, "GameDatabase.json"),
                gameInfoArray.ToString((Newtonsoft.Json.Formatting)Formatting.Indented)
            );

            CollectionInfo[] collections = e.Collections ?? [];
            File.WriteAllText(
                Path.Combine(_gameDirectoryPath, "CollectionDatabase.json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    collections,
                    (Newtonsoft.Json.Formatting)Formatting.Indented
                )
            );

            // Update the gameInfoList with the new game info array
            // Show the game titles as "Loading..." until the game database is updated
            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("[Updater] Updater_GameDatabaseFetched: Start");

                // Update the gameInfoList with the new game info array
                _gameInfoList = new JObject[gameInfoArray.Count];
                for (int i = 0; i < gameInfoArray.Count; i++)
                    _gameInfoList[i] = (JObject)gameInfoArray[i];

                _collectionInfoList = collections;
                RefreshActiveCollectionReference();
                RecomputeGameWorkingList();

                _gameTitleStates = new GameState[_currentGameWorkingList.Length];
                for (int i = 0; i < _currentGameWorkingList.Length; i++)
                    _gameTitleStates[i] = GameState.fetchingInfo;

                for (
                    int i = _previousPageIndex * _tilesPerPage;
                    i < (_previousPageIndex + 1) * _tilesPerPage;
                    i++
                )
                {
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        if (i < _currentGameWorkingList.Length)
                        {
                            if (
                                (_gameTitlesList[i % _tilesPerPage].Content as string)
                                != _currentGameWorkingList[i]["Name"].ToString()
                            )
                                _gameTitlesList[i % _tilesPerPage].Content = "Loading...";
                            _gameTilesList[i % _tilesPerPage].Visibility = Visibility.Visible;
                        }
                        else
                            _gameTilesList[i % _tilesPerPage].Visibility = Visibility.Hidden;
                    });
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("[Updater] Updater_GameDatabaseFetched: End");
            }
            catch (TaskCanceledException tcx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(tcx, "[Updater] Updater_GameDatabaseFetched: Task Canceled");
            }

            // Refresh the home screen thumbnail scroll with the latest ThumbnailUrls from the API.
            // Clear the cache so newly-uploaded thumbnails are fetched fresh rather than stale.
            List<string> oldTempFiles;
            lock (_scrollCacheLock)
            {
                _scrollThumbnailCache.Clear();
                oldTempFiles = new List<string>(_scrollGifTempFiles);
                _scrollGifTempFiles.Clear();
            }
            foreach (var f in oldTempFiles)
                try
                {
                    File.Delete(f);
                }
                catch { }

            var freshUrls = new List<string>(games.Length);
            foreach (var game in games)
                if (!string.IsNullOrEmpty(game.ThumbnailUrl))
                    freshUrls.Add(game.ThumbnailUrl);
            LoadHomeThumbnailScrollFromUrls(freshUrls);

            // If the selection menu is already open, refresh the tile page so game names and
            // thumbnails appear immediately instead of waiting for each update check to complete.
            if (_isSelectionMenuVisible)
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    ChangePage(_previousPageIndex);
                    HighlightCurrentGameMenuOption();
                });

            // Update the game info display if the currently selected game is in the new game database
            DebounceUpdateGameInfoDisplay();
        }

        private void Updater_GameUpdateCompleted(object sender, GameUpdateCompletedEventArgs e)
        {
            int gameIndex = -1;
            for (int i = 0; i < _currentGameWorkingList.Length; i++)
            {
                if (_currentGameWorkingList[i]["Name"].ToString() == e.GameName)
                {
                    gameIndex = i;
                    break;
                }
            }

            // If the game is not found, return
            if (gameIndex == -1)
            {
                Console.WriteLine($"{e.GameName} not found in current game working list.");
                return;
            }

            _logger.LogDebug("[Updater] Updater_GameUpdateCompleted: Queued");

            // Pre-calculate image URI off UI thread
            Uri imageUri = null;
            bool isVisible =
                gameIndex >= _previousPageIndex * _tilesPerPage
                && gameIndex < (_previousPageIndex + 1) * _tilesPerPage;

            if (isVisible)
            {
                var gameInfo = _currentGameWorkingList[gameIndex % _tilesPerPage];
                string thumbnailUrl = gameInfo["ThumbnailUrl"].ToString();

                if (thumbnailUrl.StartsWith("http"))
                {
                    string cacheBusterUrl =
                        thumbnailUrl
                        + (thumbnailUrl.Contains('?') ? "&" : "?")
                        + "cb="
                        + _thumbnailCacheBuster;
                    imageUri = new Uri(cacheBusterUrl, UriKind.Absolute);
                }
                else
                {
                    string localPath = Path.Combine(
                        _gameDirectoryPath,
                        gameInfo["FolderName"].ToString(),
                        thumbnailUrl
                    );
                    if (File.Exists(localPath))
                    {
                        imageUri = new Uri(localPath, UriKind.Absolute);
                    }
                }
            }

            string gameName = _currentGameWorkingList[gameIndex]["Name"].ToString();

            _dispatcherQueue.EnqueueUnique(
                $"UpdateGameTile_{gameIndex}",
                () =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Updater] Updater_GameUpdateCompleted: Start");

                    if (isVisible)
                    {
                        // Update the game title text block
                        _gameTitlesList[gameIndex % _tilesPerPage]
                            .FitTextToLabel(
                                desiredText: _emojiParser.ReplaceColonNames(gameName),
                                targetFontSize: 24,
                                maxLines: 1,
                                minFontSize: 8,
                                precision: 0.1
                            );
                        _gameTitlesList[gameIndex % _tilesPerPage].Visibility = Visibility.Visible;

                        // Update the game image
                        if (imageUri != null)
                        {
                            AnimationBehavior.SetSourceUri(
                                _gameImagesList[gameIndex % _tilesPerPage],
                                imageUri
                            );
                        }
                    }

                    SetGameTitleState(gameIndex, GameState.ready);

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Updater] Updater_GameUpdateCompleted: End");
                }
            );

            if (_currentlySelectedGameIndex == gameIndex)
                DebounceUpdateGameInfoDisplay();
        }

        private void Updater_CloseGameAndUpdater(object sender, EventArgs e)
        {
            // Alert the user that the application is Undergoing Maintenance
            try
            {
                _logger.LogDebug("[Updater] Updater_CloseGameAndUpdater: Queued");
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Updater] Updater_CloseGameAndUpdater: Start");

                    StartMenu.Visibility = Visibility.Collapsed;
                    _isStartMenuVisible = false;
                    HomeMenu.Visibility = Visibility.Collapsed;
                    _isHomeMenuVisible = false;
                    SelectionMenu.Visibility = Visibility.Collapsed;
                    _isSelectionMenuVisible = false;

                    MaintenanceScreen.Visibility = Visibility.Visible;
                    _isMaintenanceVisible = true;

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("[Updater] Updater_CloseGameAndUpdater: End");
                });
            }
            catch (TaskCanceledException tcx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(tcx, "[Updater] Updater_CloseGameAndUpdater: Task Canceled");
            }

            // Close the currently running process
            if (_currentlyRunningProcess != null && !_currentlyRunningProcess.HasExited)
            {
                _currentlyRunningProcess.Kill();

                _ = _socket.SafeReportStatus("Idle");
                _currentlyRunningProcess = null;
            }

            // Find the Updater process and close it
            Process[] processes = Process.GetProcessesByName("Research-Arcade-Updater");
            foreach (Process process in processes)
                process.Kill();
        }

        private void Updater_RelaunchUpdater(object sender, EventArgs e)
        {
            // Start the new updater
            Process.Start(
                Path.Combine(Directory.GetCurrentDirectory(), "Research-Arcade-Updater.exe")
            );

            RestartLauncher();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Close the currently running process
            if (_currentlyRunningProcess != null && !_currentlyRunningProcess.HasExited)
            {
                _currentlyRunningProcess.Kill();

                _ = _socket.SafeReportStatus("Idle");
                _currentlyRunningProcess = null;
            }

            // Stop polling controllers
            _controllerManager?.Dispose();

            // Stop the updateTimer
            _updateTimer?.Stop();
            _updateTimer = null;

            // Close the application
            RestartLauncher();
        }

        // Misc

        public void RestartLauncher() =>
            Dispatcher?.Invoke(() =>
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("[System] RestartLauncher: Start");
                Application.Current?.Shutdown();
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("[System] RestartLauncher: End");
            });

        // Credits

        private const double CreditsScrollPixelsPerMs = 0.05;

        private void AutoScrollCredits(int frameMs)
        {
            if (_isUpdatingCredits)
                return;
            _isUpdatingCredits = true;

            _dispatcherQueue.EnqueueUnique(
                "AutoScrollCredits",
                () =>
                {
                    try
                    {
                        // Change Canvas.Top of the CreditsPanel
                        double currentTop = Canvas.GetTop(CreditsPanel);
                        double newTop = currentTop - CreditsScrollPixelsPerMs * frameMs;
                        Canvas.SetTop(CreditsPanel, newTop);

                        // If the CreditsPanel is off the screen, reset it to the bottom
                        string logicalScreenHeight_str = TryFindResource("LogicalSizeHeight")
                            .ToString();
                        double logicalScreenHeight = double.Parse(logicalScreenHeight_str);

                        if (newTop < -CreditsPanel.ActualHeight)
                            Canvas.SetTop(CreditsPanel, logicalScreenHeight);
                    }
                    finally
                    {
                        _isUpdatingCredits = false;
                    }
                }
            );
        }

        // Getters & Setters

        private SolidColorBrush GetCurrentSelectionAnimationBrush() =>
            _selectionAnimationFrames[_selectionAnimationFrame];

        private void SetGameTitleState(int _index, GameState _state)
        {
            // Set the game title state
            _gameTitleStates[_index] = _state;

            // Style the Start Button based on the game title state
            if (_currentlySelectedGameIndex == _index)
                StyleStartButtonState(_index);
        }

        // Update Methods

        private void UpdateCurrentSelection()
        {
            bool updatedIntervalCounter = false;

            // If the infoWindow is visible, don't listen for inputs
            if (_isInfoWindowVisible)
                return;

            // If theres a game running, don't listen for inputs
            if (_currentlyRunningProcess != null && !_currentlyRunningProcess.HasExited)
                return;

            // Use a multiplier to speed up the selection update when the stick is held in either direction
            double multiplier = 1.00;
            if (_selectionUpdateIntervalCounter > 0)
                multiplier =
                    (double)1.00
                    - (
                        (double)_selectionUpdateIntervalCounter
                        / ((double)_selectionUpdateIntervalCounterMax * 1.6)
                    );

            // If the selection update counter is greater than the selection update interval, update the selection
            if (_selectionUpdateCounter >= _selectionUpdateInterval * multiplier)
            {
                int[] leftStickDirection = _controllerManager.GetEitherLeftStickDirection();
                bool gameSelectionChanged = false;

                if (leftStickDirection[0] != 0 || leftStickDirection[1] != 0)
                {
                    // Reset the selection update counter and increment the selection update interval counter
                    _selectionUpdateCounter = 0;
                    if (
                        _selectionUpdateIntervalCounter < _selectionUpdateIntervalCounterMax
                        && !updatedIntervalCounter
                    )
                    {
                        _selectionUpdateIntervalCounter++;
                        updatedIntervalCounter = true;
                    }
                }

                // If the left or right stick's direction is Up
                if (leftStickDirection[1] == -1)
                {
                    // If the Home Menu is visible, decrement the currently selected Home Index
                    if (_isHomeMenuVisible)
                    {
                        _currentlySelectedHomeIndex -= 1;
                        if (_currentlySelectedHomeIndex < 0)
                            _currentlySelectedHomeIndex = 0;
                    }
                    // If the Selection Menu is visible, decrement the currently selected Game Index
                    else if (_isSelectionMenuVisible && _currentGameWorkingList != null)
                    {
                        int maxIndex = _currentGameWorkingList.Length - 1;

                        if (_currentlySelectedGameIndex <= -1)
                            _currentlySelectedGameIndex = -1; // Stay on Back
                        else
                        {
                            int col = _currentlySelectedGameIndex % _gridColumns;
                            int row = _currentlySelectedGameIndex / _gridColumns;

                            // Move up one row
                            row -= 1;

                            // Going above top row selects Back
                            if (row < 0)
                                _currentlySelectedGameIndex = -1;
                            else
                            {
                                int candidate = row * _gridColumns + col;

                                // If that slot doesn't exist (short final row), walk upward until valid
                                while (candidate > maxIndex && row >= 0)
                                {
                                    row--;
                                    candidate = row * _gridColumns + col;
                                }

                                if (row >= 0)
                                {
                                    _currentlySelectedGameIndex = candidate;
                                    gameSelectionChanged = true;
                                }
                                else
                                    _currentlySelectedGameIndex = -1;
                            }
                        }
                    }
                }
                // If the left or right stick's direction is Down
                else if (leftStickDirection[1] == 1)
                {
                    // If the Home Menu is visible, increment the currently selected Home Index
                    if (_isHomeMenuVisible)
                    {
                        _currentlySelectedHomeIndex += 1;
                        if (_currentlySelectedHomeIndex > _homeOptionsList.Length - 1)
                            _currentlySelectedHomeIndex = _homeOptionsList.Length - 1;
                    }
                    // If the Selection Menu is visible, increment the currently selected Game Index
                    else if (_isSelectionMenuVisible && _currentGameWorkingList != null)
                    {
                        int maxIndex = _currentGameWorkingList.Length - 1;

                        // Down from Back goes to first tile
                        if (_currentlySelectedGameIndex == -1 && maxIndex >= 0)
                        {
                            _currentlySelectedGameIndex = 0;
                            gameSelectionChanged = true;
                        }
                        else
                        {
                            int cols = _gridColumns;

                            int col = _currentlySelectedGameIndex % cols;
                            int row = _currentlySelectedGameIndex / cols;

                            int candidate = _currentlySelectedGameIndex + cols;

                            if (candidate <= maxIndex)
                            {
                                _currentlySelectedGameIndex = candidate;
                                gameSelectionChanged = true;
                            }
                            else
                            {
                                int lastRow = maxIndex / cols;

                                if (lastRow > row)
                                {
                                    int lastRowCandidate = lastRow * cols + col;

                                    // Clamp to last valid index if this column doesn't exist on last row
                                    if (lastRowCandidate > maxIndex)
                                        lastRowCandidate = maxIndex;

                                    _currentlySelectedGameIndex = lastRowCandidate;
                                    gameSelectionChanged = true;
                                }
                            }
                        }
                    }
                }
                // If the left or right stick's direction is Left
                if (leftStickDirection[0] == -1)
                {
                    if (_isSelectionMenuVisible && _currentGameWorkingList != null)
                    {
                        int maxIndex = _currentGameWorkingList.Length - 1;

                        if (_currentlySelectedGameIndex > 0)
                        {
                            int col = _currentlySelectedGameIndex % _gridColumns;
                            if (col > 0)
                            {
                                _currentlySelectedGameIndex -= 1;
                                gameSelectionChanged = true;
                            }
                        }
                    }
                }
                // If the left or right stick's direction is Right
                else if (leftStickDirection[0] == 1)
                {
                    if (_isSelectionMenuVisible && _currentGameWorkingList != null)
                    {
                        int maxIndex = _currentGameWorkingList.Length - 1;

                        if (_currentlySelectedGameIndex == -1 && maxIndex >= 0)
                        {
                            _currentlySelectedGameIndex = 0;
                            gameSelectionChanged = true;
                        }
                        else
                        {
                            int col = _currentlySelectedGameIndex % _gridColumns;
                            int candidate = _currentlySelectedGameIndex + 1;

                            // Only move right if still in same row and valid
                            if (col < _gridColumns - 1 && candidate <= maxIndex)
                            {
                                _currentlySelectedGameIndex = candidate;
                                gameSelectionChanged = true;
                            }
                        }
                    }
                }
                else
                    _selectionUpdateIntervalCounter = 0;

                if (gameSelectionChanged)
                    DebounceUpdateGameInfoDisplay();
            }

            // Check if the Start/A button is pressed
            if (
                _timeSinceLastButton > 250
                && (
                    _controllerManager.GetEitherButtonDownState(
                        ControllerState.ControllerActions.Start
                    )
                    || _controllerManager.GetEitherButtonDownState(
                        ControllerState.ControllerActions.A
                    )
                )
            )
            {
                // Reset the time since the last button press
                _timeSinceLastButton = 0;

                // Check if the Home Menu is visible
                if (_isHomeMenuVisible)
                {
                    if (_homeOptionsList == null)
                    {
                        _logger.LogError(
                            "[MainWindow] _homeOptionsList is null in Start/A handling"
                        );
                    }
                    else if (
                        _currentlySelectedHomeIndex < 0
                        || _currentlySelectedHomeIndex >= _homeOptionsList.Length
                    )
                    {
                        if (_logger.IsEnabled(LogLevel.Error))
                            _logger.LogError(
                                "[MainWindow] HomeIndex out of range: {CurrentlySelectedHomeIndex}",
                                _currentlySelectedHomeIndex
                            );
                    }
                    else
                    {
                        // If the Game Library option is selected
                        if (_homeOptionsList[_currentlySelectedHomeIndex] == GameLibraryText)
                            // Show the Selection Menu
                            GameLibraryButton_Click(null, null);
                        // If the About option is selected
                        else if (_homeOptionsList[_currentlySelectedHomeIndex] == AboutText)
                            // Show the Credits
                            AboutButton_Click(null, null);
                        else if (_homeOptionsList[_currentlySelectedHomeIndex] == InputMenuText)
                            // Show the Input Menu
                            InputMenuButton_Click(null, null);
                        // If the Exit option is selected
                        else if (_homeOptionsList[_currentlySelectedHomeIndex] == ExitText)
                            // Go back to the Start Menu
                            ExitButton_Click(null, null);
                    }
                }
                // Else check if the Selection Menu is visible
                else if (_isSelectionMenuVisible)
                {
                    // If a game is selected, attempt to start the game
                    if (_currentlySelectedGameIndex >= 0)
                        Task.Run(() =>
                        {
                            StartButton_Click(null, null);
                        });
                    // If the back button is selected, return to the Home Menu
                    else
                        BackFromGameLibraryButton_Click(null, null);
                }
            }

            // Check if the Exit/B button is pressed
            if (
                _timeSinceLastButton > 250
                && (
                    _controllerManager.GetEitherButtonDownState(
                        ControllerState.ControllerActions.Exit
                    )
                    || _controllerManager.GetEitherButtonDownState(
                        ControllerState.ControllerActions.B
                    )
                )
            )
            {
                // Reset the time since the last button press
                _timeSinceLastButton = 0;

                // If the Home Menu is visible
                if (_isHomeMenuVisible)
                {
                    // Go back to the Start Menu
                    ExitButton_Click(null, null);
                }
                // Else if the Selection Menu is visible
                else if (_isSelectionMenuVisible)
                {
                    // Go back to the Home Menu
                    BackFromGameLibraryButton_Click(null, null);
                }
            }
        }

        private void HighlightCurrentHomeMenuOption()
        {
            if (_homeOptionsList == null)
            {
                _logger.LogError(
                    "[MainWindow] HighlightCurrentHomeMenuOption: _homeOptionsList is null!"
                );
                return;
            }
            if (
                _currentlySelectedHomeIndex < 0
                || _currentlySelectedHomeIndex >= _homeOptionsList.Length
            )
            {
                _logger.LogError(
                    "[MainWindow] HighlightCurrentHomeMenuOption: index out of range: {_currentlySelectedHomeIndex}",
                    _currentlySelectedHomeIndex
                );
                return;
            }

            _dispatcherQueue.EnqueueUnique(
                "HighlightHome",
                () =>
                {
                    // Reset the colour of all Home Menu Options and remove the "<" character if present
                    foreach (TextBlock option in _homeOptionsList)
                    {
                        option.Foreground = new SolidColorBrush(
                            Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                        );
                        if (option.Text.EndsWith(" <"))
                            option.Text = option.Text[..^2];
                    }

                    // Highlight the currently selected Home Menu Option and add the "<" character
                    _homeOptionsList[_currentlySelectedHomeIndex].Foreground =
                        GetCurrentSelectionAnimationBrush();
                    if (!_homeOptionsList[_currentlySelectedHomeIndex].Text.EndsWith(" <"))
                        _homeOptionsList[_currentlySelectedHomeIndex].Text += " <";
                }
            );
        }

        private void HighlightCurrentGameMenuOption()
        {
            _dispatcherQueue.EnqueueUnique(
                "HighlightGame",
                () =>
                {
                    // Reset the colour of all Game Menu Options
                    foreach (Label title in _gameTitlesList)
                        title.Foreground = new SolidColorBrush(
                            Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                        );

                    // Check if the current page needs to be changed
                    int pageIndex = _currentlySelectedGameIndex / _tilesPerPage;
                    if (pageIndex != _previousPageIndex)
                        ChangePage(pageIndex);
                    else
                        UpdateCollectionTileFolderStates();

                    //If a game is selected
                    if (_currentlySelectedGameIndex >= 0)
                    {
                        // Highlight the currently selected Game Menu Option
                        _gameTitlesList[_currentlySelectedGameIndex % _tilesPerPage].Foreground =
                            GetCurrentSelectionAnimationBrush();

                        // Style the Back Button
                        BackFromGameLibraryButton.IsChecked = false;
                    }

                    // Check if the page needs to be changed
                    if (_currentlySelectedGameIndex < 0)
                    {
                        // Reset the game info display
                        ResetGameInfoDisplay();

                        // Highlight the Back Button and disable the Start Button
                        BackFromGameLibraryButton.IsChecked = true;
                        StartButton.IsChecked = false;
                        StartButton.Content = _isBrowsingCollectionsTopLevel
                            ? "Select a Collection"
                            : "Select a Game";
                        StartButton.IsEnabled = false;
                    }
                }
            );
        }


        private void UpdateInputMenuFeedback()
        {
            if (_isUpdatingInputMenu)
                return;
            _isUpdatingInputMenu = true;

            _dispatcherQueue.EnqueueUnique(
                "UpdateInput",
                () =>
                {
                    try
                    {
                        SolidColorBrush activeFillColour = new(
                            Color.FromArgb(0xFF, 0x00, 0xFF, 0x00)
                        );
                        SolidColorBrush activeBorderColour = new(
                            Color.FromArgb(0xFF, 0x00, 0xBB, 0x00)
                        );

                        SolidColorBrush inactiveFillColour = new(
                            Color.FromArgb(0xFF, 0xFF, 0x00, 0x00)
                        );
                        SolidColorBrush inactiveBorderColour = new(
                            Color.FromArgb(0xFF, 0xBB, 0x00, 0x00)
                        );

                        int exitHeldMilliseconds = 1500;

                        // Update the held countdown text
                        int exitHeldFor = _controllerManager.GetExitButtonHeldFor();

                        if (exitHeldFor > 0)
                            InputMenu_HoldBackCountdownText.Text = (
                                (double)(exitHeldMilliseconds - exitHeldFor) / 1000
                            ).ToString("0.0");
                        else
                            InputMenu_HoldBackCountdownText.Text = "";

                        // Check if the exit button has been held for 1.5 seconds, if so, go back to the Start Menu
                        if (exitHeldFor >= exitHeldMilliseconds)
                            ExitButton_Click(null, null);

                        // For each Controller State
                        for (int i = 0; i < _controllerManager.GetControllerCount(); i++)
                        {
                            // Joystick Input
                            int[] leftStickDirection =
                                _controllerManager.GetPlayerLeftStickDirection(i);
                            _inputMenuJoysticks[i].Margin = new(
                                leftStickDirection[0] * 50,
                                leftStickDirection[1] * 50,
                                0,
                                0
                            );

                            // For each button in the Input Menu
                            for (int j = 0; j < _inputMenuButtons[i].Length; j++)
                            {
                                if (_inputMenuButtons[i][j] == null)
                                    continue;

                                // If the user is pressing the button, highlight the button
                                if (
                                    _controllerManager.GetPlayerButtonState(
                                        i,
                                        (ControllerState.ControllerActions)j
                                    )
                                )
                                {
                                    _inputMenuButtons[i][j].Fill = activeFillColour;
                                    _inputMenuButtons[i][j].Stroke = activeBorderColour;
                                }
                                else
                                {
                                    _inputMenuButtons[i][j].Fill = inactiveFillColour;
                                    _inputMenuButtons[i][j].Stroke = inactiveBorderColour;
                                }
                            }
                        }
                    }
                    finally
                    {
                        _isUpdatingInputMenu = false;
                    }
                }
            );
        }

        private void ChangePage(int _pageIndex)
        {
            // Check if the page index is within the bounds of the game info files list
            if (_pageIndex < 0)
                _pageIndex = 0;
            else if (_pageIndex > _currentGameWorkingList.Length / _tilesPerPage)
                _pageIndex = _currentGameWorkingList.Length / _tilesPerPage;

            // Set the previous page index to the current page index
            _previousPageIndex = _pageIndex;
            _lastFolderStateSelectedIndex = _currentlySelectedGameIndex;

            ResetTiles();

            // Show the up scroll arrow if there is a previous page and the down scroll arrow if there is a next page
            if (_pageIndex > 0)
                ScrollArrow_Up.Visibility = Visibility.Visible;
            else
                ScrollArrow_Up.Visibility = Visibility.Collapsed;

            if (_currentGameWorkingList.Length > (_pageIndex + 1) * _tilesPerPage)
                ScrollArrow_Down.Visibility = Visibility.Visible;
            else
                ScrollArrow_Down.Visibility = Visibility.Collapsed;

            // Capture state for background task
            var games = _currentGameWorkingList;
            var appPath = _applicationPath;
            var gameDirPath = _gameDirectoryPath;
            var tilesPerPage = _tilesPerPage;
            var emojiParser = _emojiParser;
            var isBrowsingCollections = _isBrowsingCollectionsTopLevel;
            var selectedIndex = _currentlySelectedGameIndex;
            var pageIndex = _pageIndex;
            var cacheBuster = _thumbnailCacheBuster;

            Task.Run(() =>
            {
                var tileUpdates =
                    new List<(int Index, string Name, string ImageUri, bool IsHttp)>();

                for (int i = 0; i < tilesPerPage; i++)
                {
                    int globalIndex = i + pageIndex * tilesPerPage;
                    if (globalIndex >= games.Length)
                        break;

                    if (games[globalIndex] == null)
                        continue;

                    string name = games[globalIndex]["Name"].ToString();
                    string thumbUrl = isBrowsingCollections
                        ? ResolveCollectionThumbnailUrl(
                            games[globalIndex],
                            preferOpen: globalIndex == selectedIndex
                        )
                        : games[globalIndex]["ThumbnailUrl"].ToString();
                    string folderName = games[globalIndex]["FolderName"].ToString();
                    string imageUri = null;
                    bool isHttp = false;

                    if (thumbUrl.StartsWith("http"))
                    {
                        imageUri =
                            thumbUrl + (thumbUrl.Contains('?') ? "&" : "?") + "cb=" + cacheBuster;
                        isHttp = true;
                    }
                    else
                    {
                        string localPath = Path.Combine(gameDirPath, folderName, thumbUrl);
                        if (File.Exists(localPath))
                        {
                            imageUri = localPath;
                            isHttp = false;
                        }
                    }

                    tileUpdates.Add((i, name, imageUri, isHttp));
                }

                _dispatcherQueue.EnqueueUnique(
                    "ApplyPageTiles",
                    () =>
                    {
                        // Re-check if we are still on the same page (in case user scrolled fast)
                        if (_previousPageIndex != pageIndex)
                            return;

                        foreach (var update in tileUpdates)
                        {
                            var tileIndex = update.Index;

                            // Set the text to the game title and make it visible
                            _gameTitlesList[tileIndex]
                                .FitTextToLabel(
                                    desiredText: emojiParser.ReplaceColonNames(update.Name),
                                    targetFontSize: 24,
                                    maxLines: 1,
                                    minFontSize: 8,
                                    precision: 0.1
                                );
                            _gameTilesList[tileIndex].Visibility = Visibility.Visible;

                            // Set the image thumbnail
                            if (String.IsNullOrEmpty(update.ImageUri))
                            {
                                int globalIndex = tileIndex + pageIndex * tilesPerPage;
                                var placeholder = isBrowsingCollections
                                    ? (
                                        globalIndex == selectedIndex
                                            ? _collectionPlaceholderOpenBitmap
                                            : _collectionPlaceholderClosedBitmap
                                    )
                                    : _placeholderBitmap;
                                AnimationBehavior.SetSourceUri(_gameImagesList[tileIndex], null);
                                SetImageSource(_gameImagesList[tileIndex], placeholder, isBrowsingCollections);
                            }
                            else if (update.IsHttp)
                            {
                                ResetImagePresentation(_gameImagesList[tileIndex]);
                                AnimationBehavior.SetSourceUri(
                                    _gameImagesList[tileIndex],
                                    new Uri(update.ImageUri, UriKind.Absolute)
                                );
                            }
                            else
                            {
                                // Fallback or keep placeholder (ResetTiles sets placeholder)
                            }
                        }
                    }
                );
            });
        }

        private void UpdateGameInfoDisplay(bool reset = true)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("[UI] UpdateGameInfoDisplay: Start");
            if (_currentGameWorkingList == null || _currentGameWorkingList.Length == 0)
                _currentlySelectedGameIndex = -1;

            // Update the game info
            if (
                _currentlySelectedGameIndex != -1
                && _currentGameWorkingList[_currentlySelectedGameIndex] != null
            )
            {
                if (reset)
                    ResetGameInfoDisplay();

                // Capture state for background task
                var index = _currentlySelectedGameIndex;
                var game = _currentGameWorkingList[index];
                var gameDirPath = _gameDirectoryPath;
                var emojiParser = _emojiParser;
                var cacheBuster = _thumbnailCacheBuster;
                var isBrowsingCollections = _isBrowsingCollectionsTopLevel;

                Task.Run(() =>
                {
                    string thumbUrl = isBrowsingCollections
                        ? ResolveCollectionThumbnailUrl(game, preferOpen: true)
                        : game["ThumbnailUrl"].ToString();
                    string folderName = game["FolderName"].ToString();
                    string imageUri = null;

                    if (thumbUrl.StartsWith("http"))
                    {
                        imageUri =
                            thumbUrl + (thumbUrl.Contains('?') ? "&" : "?") + "cb=" + cacheBuster;
                    }
                    else
                    {
                        string localPath = Path.Combine(gameDirPath, folderName, thumbUrl);
                        if (File.Exists(localPath))
                        {
                            imageUri = localPath;
                        }
                    }

                    _dispatcherQueue.EnqueueUnique(
                        "ApplyGameInfo",
                        () =>
                        {
                            if (_currentlySelectedGameIndex != index)
                                return;

                            StartButton.IsChecked = true;

                            // Set the Game Thumbnail
                            SetImageSource(
                                NonGif_GameThumbnail,
                                isBrowsingCollections ? _collectionPlaceholderOpenBitmap : _placeholderBitmap,
                                isBrowsingCollections
                            );

                            if (imageUri != null)
                            {
                                AnimationBehavior.SetSourceUri(
                                    Gif_GameThumbnail,
                                    new Uri(imageUri, UriKind.Absolute)
                                );
                            }
                            else
                            {
                                AnimationBehavior.SetSourceUri(Gif_GameThumbnail, null);
                            }

                            // Set the Game Info and Authors
                            GameTitle.FitTextToTextBlock(
                                desiredText: emojiParser.ReplaceColonNames(game["Name"].ToString()),
                                targetFontSize: 32,
                                maxLines: 1,
                                minFontSize: 8,
                                precision: 0.1
                            );
                            string authorsText = string.Join(
                                ", ",
                                game["Authors"].ToObject<string[]>()
                            );
                            if (string.IsNullOrWhiteSpace(authorsText))
                            {
                                GameAuthors.Text = "";
                            }
                            else
                            {
                                GameAuthors.FitTextToTextBlock(
                                    desiredText: authorsText,
                                    targetFontSize: 14,
                                    maxLines: 2,
                                    minFontSize: 8,
                                    precision: 0.1
                                );
                            }

                            // Fetch the Game Tag Elements (Borders and TextBlocks)
                            Border[] GameTagBorder =
                            [
                                GameTagBorder0,
                                GameTagBorder1,
                                GameTagBorder2,
                                GameTagBorder3,
                                GameTagBorder4,
                                GameTagBorder5,
                                GameTagBorder6,
                                GameTagBorder7,
                                GameTagBorder8,
                            ];
                            TextBlock[] GameTag =
                            [
                                GameTag0,
                                GameTag1,
                                GameTag2,
                                GameTag3,
                                GameTag4,
                                GameTag5,
                                GameTag6,
                                GameTag7,
                                GameTag8,
                            ];
                            JArray tags = (JArray)game["Tags"];

                            // For each Stated Game Tag
                            for (int j = 0; j < GameTag.Length; j++)
                            {
                                if (j >= tags.Count)
                                {
                                    // Hide any unused Game Tag elements
                                    GameTagBorder[j].Visibility = Visibility.Hidden;
                                    GameTag[j].Text = "";
                                    continue;
                                }

                                // Change Visibility
                                GameTagBorder[j].Visibility = Visibility.Visible;

                                // Change Text Content
                                GameTag[j].Text = emojiParser.ReplaceColonNames(
                                    tags[j]["Name"].ToString()
                                );

                                // Change Border and Text Colour
                                string colour = "#FF777777";

                                // If the Colour is not null or empty, set the colour
                                if (tags[j]["Colour"] != null && tags[j]["Colour"].ToString() != "")
                                    colour = tags[j]["Colour"].ToString();

                                // Set the Border and Text Colour
                                GameTag[j].Foreground = new SolidColorBrush(
                                    (Color)ColorConverter.ConvertFromString(colour)
                                );
                                GameTagBorder[j].BorderBrush = new SolidColorBrush(
                                    (Color)ColorConverter.ConvertFromString(colour)
                                );
                            }

                            // Set the Game Description and Version
                            GameDescription.FitTextToTextBlock(
                                desiredText: emojiParser.ReplaceColonNames(
                                    // Make sure to replace \n with an actual newline character in the description
                                    game["Description"].ToString().Replace("\\n", "\n")
                                ),
                                targetFontSize: 14,
                                maxLines: 100,
                                minFontSize: 8,
                                precision: 0.1
                            );
                            VersionText.Text = isBrowsingCollections
                                ? game["VersionNumber"].ToString()
                                : "v" + game["VersionNumber"].ToString();

                            _showingDebouncedGame = true;
                        }
                    );
                });
            }

            if (_currentlySelectedGameIndex >= 0)
                StyleStartButtonState(_currentlySelectedGameIndex);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("[UI] UpdateGameInfoDisplay: End");
        }

        public void StyleStartButtonState(int _index) =>
            StyleStartButtonState(_gameTitleStates[_index]);

        private static readonly SolidColorBrush _fillChecking = new(
            Color.FromRgb(0xFF, 0xC1, 0x07)
        ); // Amber
        private static readonly SolidColorBrush _fillDownloading = new(
            Color.FromRgb(0x4C, 0xAF, 0x50)
        ); // Green
        private static readonly SolidColorBrush _fillFailed = new(Color.FromRgb(0xF4, 0x43, 0x36)); // Red
        private static readonly SolidColorBrush _fillActive = new(Color.FromRgb(0x21, 0x96, 0xF3)); // Blue
        private static readonly SolidColorBrush _fillNeutral = new(Color.FromRgb(0x60, 0x7D, 0x8B)); // Blue-grey

        private void SetStartButtonFill(double scaleX, Brush brush)
        {
            if (
                StartButton.Template.FindName("PART_Fill", StartButton)
                is not System.Windows.Shapes.Rectangle fill
            )
                return;

            if (fill.RenderTransform is ScaleTransform scale && !scale.IsFrozen)
                scale.ScaleX = scaleX;
            else
                fill.RenderTransform = new ScaleTransform(scaleX, 1.0);

            fill.Fill = brush;
        }

        private void StyleStartButtonState(GameState _gameState)
        {
            try
            {
                _logger.LogDebug("[UI] StyleStartButtonState: Queued");
                var isBrowsingCollections = _isBrowsingCollectionsTopLevel;
                _dispatcherQueue.EnqueueUnique(
                    "StyleStartButton",
                    () =>
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("[UI] StyleStartButtonState: Start");

                        if (isBrowsingCollections)
                        {
                            StartButton.IsChecked = true;
                            StartButton.Content = "Open Collection";
                            SetStartButtonFill(0.0, Brushes.Transparent);
                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation("[UI] StyleStartButtonState: End");
                            return;
                        }

                        switch (_gameState)
                        {
                            case GameState.fetchingInfo:
                                StartButton.IsChecked = false;
                                StartButton.Content = "Fetching Game Info...";
                                SetStartButtonFill(1.0, _fillNeutral);
                                break;
                            case GameState.checkingForUpdates:
                                StartButton.IsChecked = false;
                                StartButton.Content = "Checking for Updates...";
                                SetStartButtonFill(1.0, _fillChecking);
                                break;
                            case GameState.downloadingGame:
                                StartButton.IsChecked = false;
                                StartButton.Content = "Downloading Game...";
                                SetStartButtonFill(0.0, _fillDownloading);
                                break;
                            case GameState.downloadingUpdate:
                                StartButton.IsChecked = false;
                                StartButton.Content = "Updating Game...";
                                SetStartButtonFill(0.0, _fillDownloading);
                                break;
                            case GameState.failed:
                                StartButton.IsChecked = false;
                                StartButton.Content = "Failed";
                                SetStartButtonFill(1.0, _fillFailed);
                                break;
                            case GameState.loadingInfo:
                                StartButton.IsChecked = false;
                                StartButton.Content = "Loading Game Info...";
                                SetStartButtonFill(1.0, _fillNeutral);
                                break;
                            case GameState.ready:
                                StartButton.IsChecked = true;
                                StartButton.Content = "Start";
                                SetStartButtonFill(0.0, Brushes.Transparent);
                                break;
                            case GameState.launching:
                                StartButton.IsChecked = false;
                                StartButton.Content = "Launching Game...";
                                SetStartButtonFill(1.0, _fillActive);
                                break;
                            case GameState.runningGame:
                                StartButton.IsChecked = false;
                                StartButton.Content = "Running Game...";
                                SetStartButtonFill(1.0, _fillActive);
                                break;
                            default:
                                break;
                        }

                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("[UI] StyleStartButtonState: End");
                    }
                );
            }
            catch (TaskCanceledException tcx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(tcx, "[UI] StyleStartButtonState: Task Canceled");
            }
        }

        // Reset Methods

        private static readonly ScaleTransform s_collectionPlaceholderScale = CreateFrozenHalfScale();

        private static ScaleTransform CreateFrozenHalfScale()
        {
            var transform = new ScaleTransform(0.5, 0.5);
            transform.Freeze();
            return transform;
        }

        private static void SetImageSource(Image imageElement, ImageSource source, bool isCollectionPlaceholder)
        {
            imageElement.Source = source;
            RenderOptions.SetBitmapScalingMode(
                imageElement,
                isCollectionPlaceholder ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Unspecified
            );
            imageElement.RenderTransformOrigin = new Point(0.5, 0.5);
            imageElement.RenderTransform = isCollectionPlaceholder
                ? s_collectionPlaceholderScale
                : Transform.Identity;
        }

        private static void ResetImagePresentation(Image imageElement)
        {
            RenderOptions.SetBitmapScalingMode(imageElement, BitmapScalingMode.Unspecified);
            imageElement.RenderTransform = Transform.Identity;
        }

        private int _lastFolderStateSelectedIndex = int.MinValue;

        private void UpdateCollectionTileFolderStates()
        {
            if (!_isBrowsingCollectionsTopLevel || _currentGameWorkingList == null)
                return;

            if (_lastFolderStateSelectedIndex == _currentlySelectedGameIndex)
                return;
            _lastFolderStateSelectedIndex = _currentlySelectedGameIndex;

            for (int i = 0; i < _tilesPerPage; i++)
            {
                int globalIndex = i + _previousPageIndex * _tilesPerPage;
                if (
                    globalIndex >= _currentGameWorkingList.Length
                    || _currentGameWorkingList[globalIndex] == null
                )
                    continue;

                // open/closed folder states on hover
                bool isSelected = globalIndex == _currentlySelectedGameIndex;
                string thumbUrl = ResolveCollectionThumbnailUrl(
                    _currentGameWorkingList[globalIndex],
                    preferOpen: isSelected
                );

                if (string.IsNullOrEmpty(thumbUrl))
                {
                    AnimationBehavior.SetSourceUri(_gameImagesList[i], null);
                    SetImageSource(
                        _gameImagesList[i],
                        isSelected ? _collectionPlaceholderOpenBitmap : _collectionPlaceholderClosedBitmap,
                        isCollectionPlaceholder: true
                    );
                }
                else
                {
                    ResetImagePresentation(_gameImagesList[i]);
                    AnimationBehavior.SetSourceUri(_gameImagesList[i], new Uri(thumbUrl, UriKind.Absolute));
                }
            }
        }

        private void ResetTiles()
        {
            var placeholder = _isBrowsingCollectionsTopLevel
                ? _collectionPlaceholderClosedBitmap
                : _placeholderBitmap;

            for (int i = 0; i < _tilesPerPage; i++)
            {
                // Reset the visibility of all titles
                _gameTilesList[i].Visibility = Visibility.Hidden;
                // Reset the text of all titles
                _gameTitlesList[i].Content = "Loading...";
                AnimationBehavior.SetSourceUri(_gameImagesList[i], null);
                SetImageSource(_gameImagesList[i], placeholder, _isBrowsingCollectionsTopLevel);
            }
        }

        private void ResetGameInfoDisplay()
        {
            var isBrowsingCollections = _isBrowsingCollectionsTopLevel;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                // Reset the Thumbnail
                SetImageSource(
                    NonGif_GameThumbnail,
                    isBrowsingCollections ? _collectionPlaceholderClosedBitmap : _placeholderBitmap,
                    isBrowsingCollections
                );
                AnimationBehavior.SetSourceUri(Gif_GameThumbnail, null);

                // Reset the Text Content of each element
                GameTitle.Text = isBrowsingCollections ? "Select A Collection" : "Select A Game";
                GameAuthors.Text = "";
                GameDescription.Text = isBrowsingCollections
                    ? "Select a collection using the joystick and by pressing A."
                    : "Select a game using the joystick and by pressing A.";
                VersionText.Text = "";

                GameTag0.Text = "";
                GameTag1.Text = "";
                GameTag2.Text = "";
                GameTag3.Text = "";
                GameTag4.Text = "";
                GameTag5.Text = "";
                GameTag6.Text = "";
                GameTag7.Text = "";
                GameTag8.Text = "";

                // Reset the Visibility of each Game Tag
                GameTagBorder0.Visibility = Visibility.Hidden;
                GameTagBorder1.Visibility = Visibility.Hidden;
                GameTagBorder2.Visibility = Visibility.Hidden;
                GameTagBorder3.Visibility = Visibility.Hidden;
                GameTagBorder4.Visibility = Visibility.Hidden;
                GameTagBorder5.Visibility = Visibility.Hidden;
                GameTagBorder6.Visibility = Visibility.Hidden;
                GameTagBorder7.Visibility = Visibility.Hidden;
                GameTagBorder8.Visibility = Visibility.Hidden;

                // Reset the Border and Text Colour of each Game Tag
                GameTag0.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));
                GameTag1.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));
                GameTag2.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));
                GameTag3.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));
                GameTag4.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));
                GameTag5.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));
                GameTag6.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));
                GameTag7.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));
                GameTag8.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));

                GameTagBorder0.BorderBrush = new SolidColorBrush(
                    Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                );
                GameTagBorder1.BorderBrush = new SolidColorBrush(
                    Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                );
                GameTagBorder2.BorderBrush = new SolidColorBrush(
                    Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                );
                GameTagBorder3.BorderBrush = new SolidColorBrush(
                    Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                );
                GameTagBorder4.BorderBrush = new SolidColorBrush(
                    Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                );
                GameTagBorder5.BorderBrush = new SolidColorBrush(
                    Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                );
                GameTagBorder6.BorderBrush = new SolidColorBrush(
                    Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                );
                GameTagBorder7.BorderBrush = new SolidColorBrush(
                    Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                );
                GameTagBorder8.BorderBrush = new SolidColorBrush(
                    Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                );
            });
        }

        // Debounce Update Game Info Display

        private void DebounceUpdateGameInfoDisplay()
        {
            if (_currentGameWorkingList == null)
            {
                _currentlySelectedGameIndex = -1;
                return;
            }

            if (
                _currentlySelectedGameIndex >= 0
                && _currentlySelectedGameIndex < _currentGameWorkingList.Length
            )
                StyleStartButtonState(GameState.loadingInfo);

            _showingDebouncedGame = false;
            _dispatcherQueue.EnqueueUnique("UpdateGameInfo", () => UpdateGameInfoDisplay(false));
        }

        private void Updater_ControllerMappingFetched(object sender, ControllerMapping mapping)
        {
            Application.Current?.Dispatcher?.Invoke(async () =>
            {
                try
                {
                    // Save to file
                    var json = System.Text.Json.JsonSerializer.Serialize(mapping);
                    var path = Path.Combine(_applicationPath, "ControllerMapping.json");
                    await File.WriteAllTextAsync(path, json);
                    _logger.LogInformation("[Controller Mapping] Saved mapping to {Path}", path);

                    // Update Controller Manager
                    _controllerManager.UpdateMapping(mapping);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Controller Mapping] Failed to save/update mapping.");
                }
            });
        }
    }
}
