using System.Reflection;

namespace ArcademiaGameLauncher
{
    public static class AppInfo
    {
        public static string Version { get; } =
            typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        public static string VersionLabel { get; } = $"[v{Version} Release Build]";
    }
}
