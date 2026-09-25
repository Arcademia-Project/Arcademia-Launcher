using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ArcademiaGameLauncher.Services;
using ArcademiaGameLauncher.Utils;

namespace ArcademiaGameLauncher.Windows
{
    public sealed class AchievementToastWindow : Window
    {
        private const double ToastWidth = 400;
        private const double ToastHeight = 96;
        private const double Margin = 24;
        private const double Gap = 10;
        private const double Lifetime = 5.0;
        private const double EnterDuration = 0.4;
        private const double ShiftDuration = EnterDuration;
        private const double ExitDuration = 0.35;
        private const double GlintDuration = 0.9;
        private const double GlintDelay = 0.15;
        private const double GlintWidth = 70;
        private const double LogoHeight = 150;
        private const int MaxVisible = 3;
        private const double RegionWidth = ToastWidth + Margin * 2;
        private const double RegionHeight = Margin + MaxVisible * (ToastHeight + Gap) + 20;

        private sealed class Toast
        {
            public FrameworkElement Root;
            public FrameworkElement Glint;
            public bool TopHalf;
            public int Slot;
            public double FromY;
            public double ToY;
            public double MoveStart;
            public double MoveDuration;
            public double ShownAt;
            public bool Exiting;
            public double ExitStart;
        }

        private readonly Queue<AchievementToast> _pending = new();
        private readonly List<Toast> _visible = [];
        private readonly Canvas _canvas;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Func<IntPtr> _gameWindow;
        private readonly Action _onToastEntered;
        private double _lastEnter = -10;
        private double _lastTopmost;
        private bool _rendering;

        public AchievementToastWindow(Func<IntPtr> gameWindow, Action onToastEntered)
        {
            _gameWindow = gameWindow;
            _onToastEntered = onToastEntered;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            Title = "Arcademia Achievements";

            _canvas = new Canvas
            {
                Width = RegionWidth,
                Height = RegionHeight,
                ClipToBounds = true,
                IsHitTestVisible = false,
            };
            Content = _canvas;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeWindows.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: true);
        }

        public void Enqueue(AchievementToast toast)
        {
            _pending.Enqueue(toast);
            if (_rendering)
                return;

            PositionOnGameMonitor();
            if (!IsVisible)
                Show();
            NativeWindows.KeepOnTop(new WindowInteropHelper(this).Handle);
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void PositionOnGameMonitor()
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            var bounds = NativeWindows.ToastArea(_gameWindow?.Invoke() ?? IntPtr.Zero);
            var dpi = VisualTreeHelper.GetDpi(this);
            var monitorWidth = bounds.Width / dpi.DpiScaleX;
            var monitorHeight = bounds.Height / dpi.DpiScaleY;
            var scale = Math.Max(0.5, monitorHeight / 1080.0);

            _canvas.LayoutTransform = new ScaleTransform(scale, scale);
            Width = RegionWidth * scale;
            Height = RegionHeight * scale;
            Left = bounds.X / dpi.DpiScaleX + monitorWidth - Width;
            Top = bounds.Y / dpi.DpiScaleY + monitorHeight - Height;
        }

        private static double SlotY(int slot) => Margin + slot * (ToastHeight + Gap);

        private static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

        private static double EaseIn(double t) => Math.Pow(Math.Clamp(t, 0, 1), 3);

        private static double EaseInOut(double t)
        {
            t = Math.Clamp(t, 0, 1);
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        private static void Place(Toast toast, double x, double bottomY)
        {
            Canvas.SetLeft(toast.Root, RegionWidth - Margin - ToastWidth + x);
            Canvas.SetTop(toast.Root, RegionHeight - bottomY - ToastHeight);
        }

        private void OnRendering(object sender, EventArgs e)
        {
            var now = _clock.Elapsed.TotalSeconds;
            var exiting = false;

            for (var i = _visible.Count - 1; i >= 0; i--)
            {
                var toast = _visible[i];
                AnimateGlint(toast, now);

                if (toast.Exiting)
                {
                    var t = (now - toast.ExitStart) / ExitDuration;
                    var eased = EaseIn(t);
                    Place(toast, (ToastWidth + Margin * 2) * eased, toast.ToY);
                    toast.Root.Opacity = 1 - eased * 0.4;
                    if (t >= 1)
                    {
                        _canvas.Children.Remove(toast.Root);
                        _visible.RemoveAt(i);
                    }
                    else
                        exiting = true;
                    continue;
                }

                var mt = toast.MoveDuration <= 0 ? 1 : (now - toast.MoveStart) / toast.MoveDuration;
                Place(toast, 0, toast.FromY + (toast.ToY - toast.FromY) * EaseOut(mt));
            }

            if (!exiting && _visible.Count > 0)
            {
                var oldest = _visible[^1];
                if (now - oldest.MoveStart >= oldest.MoveDuration && now - oldest.ShownAt >= Lifetime)
                {
                    oldest.Exiting = true;
                    oldest.ExitStart = now;
                    exiting = true;
                }
            }

            if (!exiting && _pending.Count > 0 && _visible.Count < MaxVisible && now - _lastEnter >= EnterDuration)
                Enter(_pending.Dequeue(), now);

            if (now - _lastTopmost > 0.5)
            {
                _lastTopmost = now;
                NativeWindows.KeepOnTop(new WindowInteropHelper(this).Handle);
            }

            if (_visible.Count == 0 && _pending.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _rendering = false;
                Hide();
            }
        }

        private static void AnimateGlint(Toast toast, double now)
        {
            if (toast.Glint is null)
                return;

            var t = (now - toast.ShownAt - GlintDelay) / GlintDuration;
            if (t < 0)
                return;
            if (t > 1)
            {
                toast.Glint.Visibility = Visibility.Collapsed;
                toast.Glint = null;
                return;
            }

            Canvas.SetLeft(toast.Glint, -GlintWidth * 2 + (ToastWidth + GlintWidth * 3) * EaseInOut(t));
        }

        private void Enter(AchievementToast data, double now)
        {
            foreach (var toast in _visible)
            {
                var current = RegionHeight - Canvas.GetTop(toast.Root) - ToastHeight;
                toast.FromY = double.IsNaN(current) ? toast.ToY : current;
                toast.Slot++;
                toast.ToY = SlotY(toast.Slot);
                toast.MoveStart = now;
                toast.MoveDuration = ShiftDuration;
            }

            var topHalf = _visible.Count == 0 || !_visible[0].TopHalf;
            var created = Build(data, topHalf);
            created.TopHalf = topHalf;
            created.FromY = -ToastHeight - Margin;
            created.ToY = SlotY(0);
            created.MoveStart = now;
            created.MoveDuration = EnterDuration;
            created.ShownAt = now + EnterDuration;
            _canvas.Children.Add(created.Root);
            Place(created, 0, created.FromY);
            _visible.Insert(0, created);
            _lastEnter = now;

            try
            {
                _onToastEntered?.Invoke();
            }
            catch (Exception) { }
        }

        private static Toast Build(AchievementToast data, bool topHalf)
        {
            var fresh = !data.TeamHadIt;

            var root = new Grid
            {
                Width = ToastWidth,
                Height = ToastHeight,
                IsHitTestVisible = false,
            };

            root.Children.Add(
                fresh
                    ? new Border
                    {
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(AchievementVisuals.Color(0x9333EA, 0.7)),
                        Background = new LinearGradientBrush(
                            AchievementVisuals.Color(0x3B2650),
                            AchievementVisuals.Color(0x24182E),
                            90
                        ),
                    }
                    : new Border { Background = new SolidColorBrush(AchievementVisuals.Color(0x24232A)) }
            );

            var clip = new Canvas { ClipToBounds = true, Margin = new Thickness(1) };
            root.Children.Add(clip);

            var logo = AchievementVisuals.LogoHalf(topHalf);
            if (logo is not null)
            {
                var logoWidth = LogoHeight * logo.PixelWidth / logo.PixelHeight;
                var image = new Image
                {
                    Source = logo,
                    Width = logoWidth,
                    Height = LogoHeight,
                    Opacity = fresh ? 0.07 : 0.045,
                    Stretch = Stretch.Fill,
                };
                Canvas.SetLeft(image, ToastWidth - 2 - 6 - logoWidth);
                Canvas.SetTop(image, topHalf ? ToastHeight - 2 + Gap / 2 - LogoHeight : -Gap / 2);
                clip.Children.Add(image);
            }

            FrameworkElement glint = null;
            if (fresh)
            {
                var sweep = new Rectangle
                {
                    Width = GlintWidth,
                    Height = ToastHeight * 2.2,
                    Fill = new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new(AchievementVisuals.Color(0xFFFFFF, 0), 0),
                            new(AchievementVisuals.Color(0xFFFFFF, 0.22), 0.5),
                            new(AchievementVisuals.Color(0xFFFFFF, 0), 1),
                        },
                        0
                    ),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(22),
                };
                Canvas.SetLeft(sweep, -GlintWidth * 2);
                Canvas.SetTop(sweep, (ToastHeight - ToastHeight * 2.2) / 2);
                clip.Children.Add(sweep);
                glint = sweep;
            }

            var content = new Canvas();
            root.Children.Add(content);

            var icon = AchievementVisuals.Icon(data.IconPath);
            if (icon is not null)
            {
                var iconImage = new Image
                {
                    Source = icon,
                    Width = 68,
                    Height = 68,
                    Stretch = Stretch.UniformToFill,
                };
                RenderOptions.SetBitmapScalingMode(
                    iconImage,
                    Math.Max(icon.PixelWidth, icon.PixelHeight) <= 128
                        ? BitmapScalingMode.NearestNeighbor
                        : BitmapScalingMode.HighQuality
                );
                Canvas.SetLeft(iconImage, 14);
                Canvas.SetTop(iconImage, (ToastHeight - 68) / 2);
                content.Children.Add(iconImage);
            }

            var team = string.IsNullOrEmpty(data.TeamLabel) ? "your team" : data.TeamLabel;
            var title = fresh ? "Achievement Unlocked" : "Already held by " + team;
            var sub = fresh
                ? (string.IsNullOrEmpty(data.Description) ? "Claim it after the game" : data.Description)
                : (data.AllowPersonal ? "Claim it for yourself after the game" : "Your team already has this one");

            content.Children.Add(Text(title, 14, FontWeights.Bold, fresh ? AchievementVisuals.Color(0xC9A0FF) : AchievementVisuals.Color(0xA5ADB8), 13));
            content.Children.Add(Text(data.Name, 20, FontWeights.Bold, fresh ? Colors.White : AchievementVisuals.Color(0xDBDBE0), 31));
            content.Children.Add(Text(sub, 14, FontWeights.Normal, AchievementVisuals.Color(0xA5ADB8), 60));

            return new Toast { Root = root, Glint = glint };
        }

        private static TextBlock Text(string value, double size, FontWeight weight, Color color, double top)
        {
            var block = new TextBlock
            {
                Text = value ?? "",
                FontFamily = AchievementVisuals.Font,
                FontSize = size,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = ToastWidth - 96 - 14,
            };
            Canvas.SetLeft(block, 96);
            Canvas.SetTop(block, top);
            return block;
        }
    }
}
