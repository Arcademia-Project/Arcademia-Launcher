using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ArcademiaGameLauncher.Models;
using ArcademiaGameLauncher.Services;
using ArcademiaGameLauncher.Utils;

namespace ArcademiaGameLauncher.Windows
{
    public sealed class AchievementsOverlayWindow : Window
    {
        private const double PanelWidth = 920;
        private const double PanelHeight = 860;
        private const double RowHeight = 92;
        private const double RowGap = 8;
        private const double ListPadding = 4;
        private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(180);

        private readonly Func<IntPtr> _gameWindow;
        private readonly Grid _root;
        private ScrollViewer _scroll;
        private readonly List<Border> _rows = [];
        private int _selected;
        private Border _scrollTrack;
        private Border _scrollThumb;
        private TextBlock _counter;
        private double _scrollFrom;
        private double _scrollTarget;
        private DateTime _scrollStart;
        private bool _scrollAnimating;

        private static readonly SolidColorBrush SelectedBorder = Freeze(new SolidColorBrush(AchievementVisuals.Color(0xC9A0FF)));
        private static readonly SolidColorBrush UnselectedBorder = Freeze(new SolidColorBrush(Colors.Transparent));

        private static SolidColorBrush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        public bool IsOpen { get; private set; }

        public AchievementsOverlayWindow(Func<IntPtr> gameWindow)
        {
            _gameWindow = gameWindow;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = true;
            Closing += (_, e) => e.Cancel = true;
            ResizeMode = ResizeMode.NoResize;
            Title = "Arcademia Achievements Overlay";

            _root = new Grid { Background = new SolidColorBrush(AchievementVisuals.Color(0x08030D, 0.78)) };
            Content = _root;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeWindows.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: false, activatable: true);
        }

        public void ShowSnapshot(AchievementSnapshot snapshot)
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            var bounds = NativeWindows.MonitorBounds(_gameWindow?.Invoke() ?? IntPtr.Zero);
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = bounds.X / dpi.DpiScaleX;
            Top = bounds.Y / dpi.DpiScaleY;
            Width = bounds.Width / dpi.DpiScaleX;
            Height = bounds.Height / dpi.DpiScaleY;
            var scale = Math.Max(0.5, Math.Min(Height / 1080.0, Width / 1100.0));

            _root.Children.Clear();
            var panel = BuildPanel(snapshot);
            panel.LayoutTransform = new ScaleTransform(scale, scale);
            _root.Children.Add(panel);

            IsOpen = true;
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Show();
            NativeWindows.KeepOnTop(handle);
            BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                }
            );
        }

        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        public void KeepOnTop() => NativeWindows.KeepOnTop(Handle);

        public void HideOverlay()
        {
            IsOpen = false;
            BeginAnimation(OpacityProperty, null);
            StopScrollAnimation();
            Hide();
            _root.Children.Clear();
            _scroll = null;
            _rows.Clear();
            _scrollTrack = null;
            _scrollThumb = null;
            _counter = null;
        }

        public void Scroll(int direction)
        {
            if (_scroll is null || direction == 0 || _rows.Count == 0)
                return;

            var next = Math.Clamp(_selected + direction, 0, _rows.Count - 1);
            if (next == _selected)
                return;

            Select(next);
        }

        private void Select(int index)
        {
            if (_selected >= 0 && _selected < _rows.Count)
                _rows[_selected].BorderBrush = UnselectedBorder;

            _selected = index;
            _rows[index].BorderBrush = SelectedBorder;

            if (_counter is not null)
                _counter.Text = $"{index + 1} / {_rows.Count}";

            if (_scroll is null)
                return;

            var rowTop = ListPadding + index * (RowHeight + RowGap);
            var rowBottom = rowTop + RowHeight;
            var margin = RowGap * 2;
            var offset = _scrollAnimating ? _scrollTarget : _scroll.VerticalOffset;

            if (index == 0)
                AnimateScrollTo(0);
            else if (index == _rows.Count - 1)
                AnimateScrollTo(_scroll.ScrollableHeight);
            else if (rowTop - margin < offset)
                AnimateScrollTo(rowTop - margin);
            else if (rowBottom + margin > offset + _scroll.ViewportHeight)
                AnimateScrollTo(rowBottom + margin - _scroll.ViewportHeight);
        }

        private void AnimateScrollTo(double target)
        {
            if (_scroll is null)
                return;

            _scrollFrom = _scroll.VerticalOffset;
            _scrollTarget = Math.Clamp(target, 0, _scroll.ScrollableHeight);
            _scrollStart = DateTime.UtcNow;

            if (!_scrollAnimating)
            {
                _scrollAnimating = true;
                CompositionTarget.Rendering += OnScrollFrame;
            }
        }

        private void OnScrollFrame(object sender, EventArgs e)
        {
            if (_scroll is null)
            {
                StopScrollAnimation();
                return;
            }

            var progress = Math.Min(1, (DateTime.UtcNow - _scrollStart).TotalMilliseconds / ScrollDuration.TotalMilliseconds);
            var eased = 1 - Math.Pow(1 - progress, 3);
            _scroll.ScrollToVerticalOffset(_scrollFrom + (_scrollTarget - _scrollFrom) * eased);

            if (progress >= 1)
                StopScrollAnimation();
        }

        private void StopScrollAnimation()
        {
            if (!_scrollAnimating)
                return;

            _scrollAnimating = false;
            CompositionTarget.Rendering -= OnScrollFrame;
        }

        private void UpdateScrollFeedback()
        {
            if (_scroll is null)
                return;

            var scrollable = _scroll.ScrollableHeight;
            var offset = _scroll.VerticalOffset;
            var moreAbove = offset > 0.5;
            var moreBelow = offset < scrollable - 0.5;

            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(moreAbove ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.08));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.92));
            mask.GradientStops.Add(new GradientStop(moreBelow ? Colors.Transparent : Colors.Black, 1));
            mask.Freeze();
            _scroll.OpacityMask = mask;

            if (_scrollTrack is null || _scrollThumb is null)
                return;

            if (scrollable <= 0.5 || _scroll.ExtentHeight <= 0)
            {
                _scrollTrack.Visibility = Visibility.Collapsed;
                return;
            }

            _scrollTrack.Visibility = Visibility.Visible;
            var trackHeight = _scrollTrack.ActualHeight;
            var thumbHeight = Math.Max(36, trackHeight * _scroll.ViewportHeight / _scroll.ExtentHeight);
            _scrollThumb.Height = thumbHeight;
            _scrollThumb.Margin = new Thickness(0, (trackHeight - thumbHeight) * offset / scrollable, 0, 0);
        }

        private static TextBlock Text(string value, double size, Color color, FontWeight weight, double top, double left = 36)
        {
            var block = new TextBlock
            {
                Text = AchievementVisuals.SafeText(value),
                FontFamily = AchievementVisuals.Font,
                FontSize = size,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(left, top, 36, 0),
            };
            return block;
        }

        private static string ScopeNoun(string scope) =>
            scope switch
            {
                "Local" => "machine",
                "National" => "country",
                _ => "site",
            };

        private static FrameworkElement BuildPanelShell(out Grid body)
        {
            body = new Grid();
            return new Border
            {
                Width = PanelWidth,
                Height = PanelHeight,
                Background = new SolidColorBrush(AchievementVisuals.Color(0x2F1F37)),
                BorderBrush = new SolidColorBrush(AchievementVisuals.Color(0x9333EA, 0.5)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = body,
            };
        }

        private FrameworkElement BuildPanel(AchievementSnapshot snapshot)
        {
            var shell = BuildPanelShell(out var body);
            var set = snapshot.Set ?? new CachedAchievementSet { Enabled = true };
            var session = snapshot.UnlockedThisSession.ToHashSet(StringComparer.Ordinal);

            var rows = set.Achievements
                .Select(a => new
                {
                    Achievement = a,
                    Hold = set.FindHold(a.ApiName),
                    ThisSession = session.Contains(a.ApiName),
                })
                .Select(r => new { r.Achievement, r.Hold, r.ThisSession, Unlocked = r.ThisSession || r.Hold is not null })
                .OrderBy(r => r.Unlocked ? 0 : 1)
                .ThenBy(r => r.Achievement.SortOrder)
                .ToList();

            var unlocked = rows.Count(r => r.Unlocked);
            var teamLine = set.IsSessional
                ? "Every playthrough starts fresh"
                : "Team: " + (string.IsNullOrEmpty(set.TeamLabel) ? "this " + ScopeNoun(set.Scope) : set.TeamLabel);

            body.Children.Add(Text("ACHIEVEMENTS", 15, AchievementVisuals.Color(0xC9A0FF), FontWeights.Bold, 26));
            body.Children.Add(Text(set.GameName ?? "", 30, Colors.White, FontWeights.Bold, 46));
            body.Children.Add(Text(
                $"{unlocked} of {rows.Count} unlocked  ·  {teamLine}" + (snapshot.Offline ? "  ·  offline" : ""),
                16, AchievementVisuals.Color(0xA5ADB8), FontWeights.Normal, 92));

            var track = new Border
            {
                Height = 8,
                Margin = new Thickness(36, 124, 36, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(AchievementVisuals.Color(0x47384E)),
            };
            var fill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(AchievementVisuals.Color(0x9333EA)),
                Width = rows.Count == 0 ? 0 : (PanelWidth - 72) * unlocked / rows.Count,
            };
            track.Child = fill;
            body.Children.Add(track);

            _rows.Clear();
            _selected = 0;

            var list = new StackPanel { Margin = new Thickness(12, ListPadding, 20, 12) };
            if (rows.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = AchievementVisuals.SafeText("This game has no achievements yet."),
                    FontFamily = AchievementVisuals.Font,
                    FontSize = 18,
                    Foreground = new SolidColorBrush(AchievementVisuals.Color(0xA5ADB8)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0),
                });

            foreach (var r in rows)
            {
                var row = BuildRow(set, r.Achievement, r.Hold, r.ThisSession, r.Unlocked);
                _rows.Add(row);
                list.Children.Add(row);
            }

            _scroll = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(24, 150, 24, 70),
                Focusable = false,
            };
            _scroll.ScrollChanged += (_, _) => UpdateScrollFeedback();
            body.Children.Add(_scroll);

            _scrollThumb = new Border
            {
                Width = 6,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(AchievementVisuals.Color(0xC9A0FF, 0.85)),
            };
            _scrollTrack = new Border
            {
                Width = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 158, 22, 78),
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(AchievementVisuals.Color(0x47384E)),
                Visibility = Visibility.Collapsed,
                Child = _scrollThumb,
            };
            _scrollTrack.SizeChanged += (_, _) => UpdateScrollFeedback();
            body.Children.Add(_scrollTrack);

            var hint = Text("Press EXIT to close  ·  Up / Down to scroll", 15, AchievementVisuals.Color(0xA5ADB8), FontWeights.Normal, 0);
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.Margin = new Thickness(36, 0, 36, 26);
            body.Children.Add(hint);

            if (_rows.Count > 0)
            {
                _counter = Text($"1 / {_rows.Count}", 15, AchievementVisuals.Color(0xC9A0FF), FontWeights.Bold, 0);
                _counter.TextAlignment = TextAlignment.Right;
                _counter.VerticalAlignment = VerticalAlignment.Bottom;
                _counter.Margin = new Thickness(36, 0, 36, 26);
                body.Children.Add(_counter);
                _rows[0].BorderBrush = SelectedBorder;
            }

            return shell;
        }

        private static Border BuildRow(
            CachedAchievementSet set,
            CachedAchievement a,
            CachedTeamHold hold,
            bool thisSession,
            bool unlocked
        )
        {
            var secret = a.Hidden && !unlocked;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

            var source = unlocked ? AchievementVisuals.Icon(a.IconPath) : AchievementVisuals.GreyIcon(a.IconPath);
            if (source is not null)
            {
                var icon = new Image
                {
                    Source = source,
                    Width = 64,
                    Height = 64,
                    Stretch = Stretch.UniformToFill,
                    Opacity = unlocked ? 1 : secret ? 0.25 : 0.45,
                };
                RenderOptions.SetBitmapScalingMode(
                    icon,
                    Math.Max(source.PixelWidth, source.PixelHeight) <= 128
                        ? BitmapScalingMode.NearestNeighbor
                        : BitmapScalingMode.HighQuality
                );
                row.Children.Add(icon);
            }

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(text, 1);
            text.Children.Add(new TextBlock
            {
                Text = AchievementVisuals.SafeText(secret ? "Hidden achievement" : a.Name),
                FontFamily = AchievementVisuals.Font,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(unlocked ? Colors.White : AchievementVisuals.Color(0x9E9EA8)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = AchievementVisuals.SafeText(secret ? "Keep playing to discover this one" : a.Description),
                FontFamily = AchievementVisuals.Font,
                FontSize = 15,
                Foreground = new SolidColorBrush(unlocked ? AchievementVisuals.Color(0xA5ADB8) : AchievementVisuals.Color(0x80808C)),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 40,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(text);

            string status;
            Color color;
            if (thisSession && hold is null)
            {
                status = "Unlocked this game";
                color = AchievementVisuals.Color(0xC9A0FF);
            }
            else if (thisSession)
            {
                status = "Unlocked this game\n" + TeamStatus(set, hold);
                color = AchievementVisuals.Color(0xC9A0FF);
            }
            else if (hold is not null)
            {
                status = TeamStatus(set, hold);
                color = AchievementVisuals.Color(0xA5ADB8);
            }
            else
            {
                status = a.AllowPersonal ? "Locked" : "Locked · team only";
                color = AchievementVisuals.Color(0x80808C);
            }

            var statusText = new TextBlock
            {
                Text = AchievementVisuals.SafeText(status),
                FontFamily = AchievementVisuals.Font,
                FontSize = 14,
                Foreground = new SolidColorBrush(color),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(statusText, 2);
            row.Children.Add(statusText);

            return new Border
            {
                Height = RowHeight,
                Margin = new Thickness(0, 0, 0, RowGap),
                BorderThickness = new Thickness(2),
                BorderBrush = UnselectedBorder,
                Background = new SolidColorBrush(unlocked ? AchievementVisuals.Color(0x47384E) : AchievementVisuals.Color(0x291C30)),
                Child = row,
            };
        }

        private static string TeamStatus(CachedAchievementSet set, CachedTeamHold hold)
        {
            var team = string.IsNullOrEmpty(set.TeamLabel) ? "Team" : set.TeamLabel;
            var who = string.IsNullOrEmpty(hold.ClaimedBy) ? "anonymously" : "by " + hold.ClaimedBy;
            var date = DateTime.TryParse(hold.FirstUnlockedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                ? "\n" + when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : "";
            return team + ", " + who + date;
        }
    }
}
