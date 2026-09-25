using System;
using System.IO;
using System.Linq;

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

        public static string Resolve(string folder, string baseName, DateTime localDate)
        {
            if (!Directory.Exists(folder))
                return null;

            var season = SeasonFor(localDate);
            return (season is null ? null : Find(folder, baseName + "_" + season)) ?? Find(folder, baseName);
        }

        private static string Find(string folder, string name) =>
            Extensions
                .Select(ext => Path.Combine(folder, name + ext))
                .FirstOrDefault(File.Exists);
    }
}
