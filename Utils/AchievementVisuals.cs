using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace ArcademiaGameLauncher.Utils
{
    public static class AchievementVisuals
    {
        public static readonly string AssemblyName = typeof(AchievementVisuals).Assembly.GetName().Name;

        public static Uri Pack(string path) => new($"avares://{AssemblyName}/{path}", UriKind.Absolute);

        public static readonly FontFamily BundledFont = new($"avares://{AssemblyName}/Fonts#Press Start 2P");

        public static readonly FontFamily Font = BundledFont;

        private static readonly Lazy<GlyphTypeface> BundledGlyphs = new(() =>
            FontManager.Current.TryGetGlyphTypeface(new Typeface(BundledFont), out var glyphTypeface)
                ? glyphTypeface
                : null
        );

        public static string SafeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";

            var glyphs = BundledGlyphs.Value;
            var builder = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value is '\n' or '\r' or '\t' || glyphs == null || glyphs.CharacterToGlyphMap.ContainsGlyph(rune.Value))
                    builder.Append(rune.ToString());
                else if (!Rune.IsControl(rune) && rune.Value is not (0x200D or 0xFE0F))
                    builder.Append('?');
            }
            return builder.ToString();
        }

        private static Bitmap _logoTop;
        private static Bitmap _logoBottom;
        private static readonly ConcurrentDictionary<string, Bitmap> Icons = new();
        private static readonly ConcurrentDictionary<string, Bitmap> GreyIcons = new();

        public static Color Color(int rgb, double alpha = 1) =>
            Avalonia.Media.Color.FromArgb(
                (byte)Math.Round(alpha * 255),
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF)
            );

        public static Bitmap LogoHalf(bool top)
        {
            try
            {
                if (top)
                    return _logoTop ??= Greyscale(Load(Pack("Assets/Images/ToastLogoTop.png")));
                return _logoBottom ??= Greyscale(Load(Pack("Assets/Images/ToastLogoBottom.png")));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Bitmap Icon(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            return Icons.GetOrAdd(path, p =>
            {
                try
                {
                    return new Bitmap(new MemoryStream(File.ReadAllBytes(p)));
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }

        public static Bitmap GreyIcon(string path)
        {
            var icon = Icon(path);
            return icon is null ? null : GreyIcons.GetOrAdd(path, p => Greyscale(File.ReadAllBytes(p)));
        }

        private static byte[] Load(Uri uri)
        {
            using var stream = AssetLoader.Open(uri);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        public static Bitmap Greyscale(byte[] encoded)
        {
            using var decoded = SKBitmap.Decode(encoded);
            using var converted = decoded.Copy(SKColorType.Bgra8888);
            var pixels = converted.Bytes;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var luminance = (byte)Math.Clamp(
                    pixels[i] * 0.114 + pixels[i + 1] * 0.587 + pixels[i + 2] * 0.299,
                    0,
                    255
                );
                pixels[i] = luminance;
                pixels[i + 1] = luminance;
                pixels[i + 2] = luminance;
            }

            using var output = new SKBitmap(converted.Info);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, output.GetPixels(), pixels.Length);
            using var image = SKImage.FromBitmap(output);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return new Bitmap(png.AsStream());
        }
    }
}
