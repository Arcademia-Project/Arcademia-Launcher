using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArcademiaGameLauncher.Utils
{
    public static class AchievementVisuals
    {
        public static readonly FontFamily Font = new("Segoe UI");

        public static readonly string AssemblyName = typeof(AchievementVisuals).Assembly.GetName().Name;

        public static Uri Pack(string path) =>
            new($"pack://application:,,,/{AssemblyName};component/{path}", UriKind.Absolute);

        private static BitmapSource _logoTop;
        private static BitmapSource _logoBottom;
        private static readonly ConcurrentDictionary<string, BitmapSource> Icons = new();
        private static readonly ConcurrentDictionary<string, BitmapSource> GreyIcons = new();

        public static Color Color(int rgb, double alpha = 1) =>
            System.Windows.Media.Color.FromArgb(
                (byte)Math.Round(alpha * 255),
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF)
            );

        public static BitmapSource LogoHalf(bool top)
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

        public static BitmapSource Icon(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            return Icons.GetOrAdd(path, p =>
            {
                try
                {
                    return LoadFile(p);
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }

        public static BitmapSource GreyIcon(string path)
        {
            var icon = Icon(path);
            return icon is null ? null : GreyIcons.GetOrAdd(path, _ => Greyscale(icon));
        }

        private static BitmapSource Load(Uri uri)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }

        private static BitmapSource LoadFile(string path)
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }

        public static BitmapSource Greyscale(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

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

            var result = BitmapSource.Create(
                converted.PixelWidth,
                converted.PixelHeight,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride
            );
            result.Freeze();
            return result;
        }
    }
}
