using System;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace ArcademiaGameLauncher.Utils
{
    public class AnimationBehavior : AvaloniaObject
    {
        public static readonly AttachedProperty<Uri> SourceUriProperty =
            AvaloniaProperty.RegisterAttached<AnimationBehavior, Image, Uri>("SourceUri");

        public static readonly AttachedProperty<bool> CacheFramesInMemoryProperty =
            AvaloniaProperty.RegisterAttached<AnimationBehavior, Image, bool>("CacheFramesInMemory");

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly ConditionalWeakTable<Image, ImageState> States = new();

        private sealed class ImageState
        {
            public int Version;
            public GifAnimator Animator;
            public EventHandler Loaded;
            public EventHandler Error;
        }

        static AnimationBehavior()
        {
            SourceUriProperty.Changed.AddClassHandler<Image>((image, e) => Load(image, e.GetNewValue<Uri>()));
        }

        public static Uri GetSourceUri(Image image) => image.GetValue(SourceUriProperty);

        public static void SetSourceUri(Image image, Uri value)
        {
            if (Equals(image.GetValue(SourceUriProperty), value))
                Load(image, value);
            else
                image.SetValue(SourceUriProperty, value);
        }

        public static bool GetCacheFramesInMemory(Image image) => image.GetValue(CacheFramesInMemoryProperty);

        public static void SetCacheFramesInMemory(Image image, bool value) => image.SetValue(CacheFramesInMemoryProperty, value);

        public static void AddLoadedHandler(Image image, EventHandler handler) => States.GetOrCreateValue(image).Loaded += handler;

        public static void AddErrorHandler(Image image, EventHandler handler) => States.GetOrCreateValue(image).Error += handler;

        private static void Load(Image image, Uri uri)
        {
            var state = States.GetOrCreateValue(image);
            var version = ++state.Version;
            state.Animator?.Dispose();
            state.Animator = null;

            if (uri == null)
            {
                image.Source = null;
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = await ReadAsync(uri).ConfigureAwait(false);
                    var codec = SKCodec.Create(new MemoryStream(bytes)) ?? throw new InvalidDataException($"Unsupported image: {uri}");
                    var animated = codec.FrameCount > 1;
                    Bitmap still = null;
                    if (!animated)
                    {
                        codec.Dispose();
                        still = new Bitmap(new MemoryStream(bytes));
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (state.Version != version)
                        {
                            if (animated)
                                codec.Dispose();
                            return;
                        }

                        if (animated)
                            state.Animator = new GifAnimator(image, codec);
                        else
                            image.Source = still;

                        state.Loaded?.Invoke(image, EventArgs.Empty);
                    });
                }
                catch (Exception)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (state.Version == version)
                            state.Error?.Invoke(image, EventArgs.Empty);
                    });
                }
            });
        }

        private static async Task<byte[]> ReadAsync(Uri uri)
        {
            if (!uri.IsAbsoluteUri)
                return ReadAsset(new Uri($"avares://{AchievementVisuals.AssemblyName}/{uri.OriginalString.TrimStart('/')}"));

            if (uri.Scheme is "http" or "https")
                return await Http.GetByteArrayAsync(uri).ConfigureAwait(false);

            if (uri.Scheme == "avares")
                return ReadAsset(uri);

            if (uri.IsFile)
                return await File.ReadAllBytesAsync(uri.LocalPath).ConfigureAwait(false);

            throw new NotSupportedException($"Unsupported image location: {uri}");
        }

        private static byte[] ReadAsset(Uri uri)
        {
            using var stream = AssetLoader.Open(uri);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        private sealed class GifAnimator : IDisposable
        {
            private readonly Image _image;
            private readonly SKCodec _codec;
            private readonly SKCodecFrameInfo[] _frames;
            private readonly SKImageInfo _info;
            private readonly SKBitmap _canvas;
            private readonly WriteableBitmap[] _buffers = new WriteableBitmap[2];
            private readonly DispatcherTimer _timer;
            private readonly int _repetitions;
            private int _frame;
            private int _lastDecoded = -1;
            private int _plays;
            private int _front;
            private bool _disposed;

            public GifAnimator(Image image, SKCodec codec)
            {
                _image = image;
                _codec = codec;
                _frames = codec.FrameInfo;
                _repetitions = codec.RepetitionCount;
                _info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                _canvas = new SKBitmap(_info);

                for (int i = 0; i < _buffers.Length; i++)
                    _buffers[i] = new WriteableBitmap(
                        new PixelSize(_info.Width, _info.Height),
                        new Vector(96, 96),
                        Avalonia.Platform.PixelFormat.Bgra8888,
                        AlphaFormat.Premul
                    );

                _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher.UIThread);
                _timer.Tick += (_, _) => Advance();
                ShowFrame();
                _timer.Interval = Duration(_frame);
                _timer.Start();
            }

            private static TimeSpan DurationOf(SKCodecFrameInfo frame) =>
                TimeSpan.FromMilliseconds(frame.Duration < 20 ? 100 : frame.Duration);

            private TimeSpan Duration(int frame) => DurationOf(_frames[frame]);

            private void Advance()
            {
                if (_disposed)
                    return;

                var next = _frame + 1;
                if (next >= _frames.Length)
                {
                    _plays++;
                    if (_repetitions >= 0 && _plays > _repetitions)
                    {
                        _timer.Stop();
                        return;
                    }
                    next = 0;
                }

                _frame = next;
                _timer.Interval = Duration(_frame);

                if (_image.IsEffectivelyVisible)
                    ShowFrame();
            }

            private void Decode(int index)
            {
                var required = _frames[index].RequiredFrame;
                if (required >= 0 && required != _lastDecoded)
                    Decode(required);

                _codec.GetPixels(_info, _canvas.GetPixels(), new SKCodecOptions(index, required));
                _lastDecoded = index;
            }

            private void ShowFrame()
            {
                try
                {
                    Decode(_frame);
                }
                catch (Exception)
                {
                    return;
                }

                _front ^= 1;
                var target = _buffers[_front];
                using (var framebuffer = target.Lock())
                {
                    var source = _canvas.GetPixels();
                    var rowBytes = Math.Min(framebuffer.RowBytes, _canvas.RowBytes);
                    unsafe
                    {
                        for (int y = 0; y < _info.Height; y++)
                            Buffer.MemoryCopy(
                                (byte*)source + y * _canvas.RowBytes,
                                (byte*)framebuffer.Address + y * framebuffer.RowBytes,
                                framebuffer.RowBytes,
                                rowBytes
                            );
                    }
                }

                _image.Source = target;
            }

            public void Dispose()
            {
                _disposed = true;
                _timer.Stop();
                _codec.Dispose();
                _canvas.Dispose();
            }
        }
    }
}
