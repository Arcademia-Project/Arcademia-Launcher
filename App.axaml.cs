using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Services;
using ArcademiaGameLauncher.Utils;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;

namespace ArcademiaGameLauncher
{
    public partial class App : Application
    {
        private IHost _host = null!;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public static void Shutdown()
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();

            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) => OnExit();

            UseInstallRootAsWorkingDirectory();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                //.MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override(
                    "System.Net.Http.HttpClient",
                    Serilog.Events.LogEventLevel.Warning
                )
                .WriteTo.Console()
                .WriteTo.File(
                    "Logs/ArcadeClient-.log",
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 10,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true
                )
                .WriteTo.File(
                    "Logs/Debug/ArcadeClient-Debug-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 10,
                    retainedFileTimeLimit: TimeSpan.FromDays(3),
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true
                )
                .CreateLogger();

            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "Config.json")))
            {
                const string message =
                    "Config.json file not found. Please ensure it exists in the application directory.";
                Log.Fatal("[System] {Message}", message);
                desktop.MainWindow = ConfigurationErrorWindow(message);
                desktop.MainWindow.Show();
                return;
            }

            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    services.AddLogging();

                    // Setup Directories
                    string applicationPath;
                    if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "Launcher")))
                    {
                        applicationPath = Path.Combine(Directory.GetCurrentDirectory(), "Launcher");
                    }
                    else
                    {
                        applicationPath = Directory.GetCurrentDirectory();
                    }
                    services.AddSingleton(applicationPath);

                    // Load the Config.json file
                    string configPath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "Config.json"
                    );
                    if (!File.Exists(configPath))
                        throw new FileNotFoundException(
                            "Config.json file not found. Please ensure it exists in the application directory.",
                            configPath
                        );

                    JObject config = JObject.Parse(File.ReadAllText(configPath));
                    services.AddSingleton(config);

                    FullscreenDetector.HandOffToGameWhenFullscreen =
                        config["DrawAchievementsInGameWhenFullscreen"]?.Type == JTokenType.Boolean
                        && config["DrawAchievementsInGameWhenFullscreen"].Value<bool>();

                    // Register HTTP Client
                    var host = config["ApiHost"]?.ToString() ?? "https://localhost:5001";
                    var user = config["ApiUser"]?.ToString() ?? "Research-Arcade-User";
                    var pass = config["ApiPass"]?.ToString() ?? "Research-Arcade-Password";

                    var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

                    services
                        .AddHttpClient<IApiClient, ApiClient>(client =>
                        {
                            client.BaseAddress = new Uri(host);
                            client.DefaultRequestHeaders.Authorization =
                                new System.Net.Http.Headers.AuthenticationHeaderValue(
                                    "ArcadeMachine",
                                    creds
                                );
                            client.DefaultRequestHeaders.Add(
                                "X-Arcademia-Platform",
                                OperatingSystem.IsWindows() ? "Windows" : "Linux"
                            );
                        })
                        .ConfigurePrimaryHttpMessageHandler(() =>
                        {
                            return new HttpClientHandler { AllowAutoRedirect = false };
                        });

                    services.AddSingleton<IUpdaterService, UpdaterService>();
                    services.AddSingleton<ISfxPlayer, SfxPlayer>();
                    services.AddSingleton<IDispatcherQueueService, DispatcherQueueService>();
                    services.AddSingleton<ISessionTrackingService>(sp => new SessionTrackingService(
                        applicationPath,
                        sp.GetRequiredService<IApiClient>(),
                        sp.GetRequiredService<ILogger<SessionTrackingService>>()
                    ));
                    services.AddSingleton<IClaimCoordinator, ClaimCoordinator>();
                    services.AddSingleton<IAchievementCache>(sp => new AchievementCache(
                        applicationPath,
                        sp.GetRequiredService<IApiClient>(),
                        sp.GetRequiredService<ILogger<AchievementCache>>()
                    ));
                    services.AddSingleton<IAchievementSessionService, AchievementSessionService>();
                    services.AddSingleton<IAchievementOverlayCoordinator, AchievementOverlayCoordinator>();
                    services.AddSingleton<ISessionClaimCoordinator, SessionClaimCoordinator>();
                    services.AddSingleton<ISdkBrokerService, SdkBrokerService>();
                    services.AddSingleton<IGameLogService, GameLogService>();
                    services.AddSingleton<IGameRunner, GameRunner>();

                    services.AddSingleton<Windows.MainWindow>();
                })
                .Build();

            _host.Start();

            if (!OperatingSystem.IsWindows())
                _ = Task.Run(_host.Services.GetRequiredService<IGameRunner>().PrepareAsync);

            var sessionTracking = _host.Services.GetRequiredService<ISessionTrackingService>();
            Task.Run(sessionTracking.RecoverCrashAsync).GetAwaiter().GetResult();

            var mainWindow = _host.Services.GetRequiredService<Windows.MainWindow>();
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
        }

        private static Window ConfigurationErrorWindow(string message) =>
            new()
            {
                Title = "Configuration Error",
                Width = 640,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.Black,
                Content = new TextBlock
                {
                    Text = message,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            };

        private static void UseInstallRootAsWorkingDirectory()
        {
            var exeDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            var installRoot = exeDirectory.Parent;

            if (
                installRoot != null
                && string.Equals(exeDirectory.Name, "Launcher", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(installRoot.FullName, "Config.json"))
            )
                Directory.SetCurrentDirectory(installRoot.FullName);
        }

        private void OnExit()
        {
            _host?.Dispose();
            Log.CloseAndFlush();
        }
    }
}
