using System;
using System.Linq;
using System.Reflection;

namespace ArcademiaGameLauncher.Utils
{
    public static class SeasonalSound
    {
        private static readonly string[] Extensions = [".wav", ".mp3", ".ogg"];

        public static string SeasonFor(DateTime localDate) =>
            localDate.Month switch
            {
                10 when localDate.Day >= 24 => "Halloween",
                12 => "Christmas",
                _ => null,
            };

        private const string ResourcePrefix = "ArcademiaGameLauncher.Assets.Sounds.";

        private static readonly Lazy<string[]> ResourceNames = new(() =>
            Assembly.GetExecutingAssembly().GetManifestResourceNames()
        );

        public static string Resolve(string baseName, DateTime localDate)
        {
            var season = SeasonFor(localDate);
            return (season is null ? null : Find(baseName + "_" + season)) ?? Find(baseName);
        }

        private static string Find(string name) =>
            Extensions
                .Select(ext => ResourcePrefix + name + ext)
                .FirstOrDefault(candidate => ResourceNames.Value.Contains(candidate, StringComparer.OrdinalIgnoreCase));
    }
}
