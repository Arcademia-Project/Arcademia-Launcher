using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using ArcademiaGameLauncher.Services;
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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 10,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true
                )
                .CreateLogger();

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
                    {
                        MessageBox.Show(
                            "Config.json file not found. Please ensure it exists in the application directory.",
                            "Configuration Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error
                        );
                        Application.Current?.Shutdown();
                        return;
                    }

                    JObject config = JObject.Parse(File.ReadAllText(configPath));
                    services.AddSingleton(config);

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
                    services.AddSingleton<ISdkBrokerService, SdkBrokerService>();

                    services.AddSingleton<Windows.MainWindow>();
                })
                .Build();

            _host.Start();

            var sessionTracking = _host.Services.GetRequiredService<ISessionTrackingService>();
            sessionTracking.RecoverCrashAsync().GetAwaiter().GetResult();

            var mainWindow = _host.Services.GetRequiredService<Windows.MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
