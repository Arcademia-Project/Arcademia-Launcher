using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ArcademiaGameLauncher.Models;
using ArcademiaGameLauncher.Services;
using ArcademiaGameLauncher.Utils;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

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
        private readonly DispatcherTimer _scrollFrames;
        private readonly Transitions _fadeIn = new()
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(180),
                Easing = new QuadraticEaseOut(),
            },
        };

        private static readonly IBrush SelectedBorder = new ImmutableSolidColorBrush(AchievementVisuals.Color(0xC9A0FF));
        private static readonly IBrush UnselectedBorder = new ImmutableSolidColorBrush(Colors.Transparent);

        public bool IsOpen { get; private set; }

        public AchievementsOverlayWindow(Func<IntPtr> gameWindow)
        {
            _gameWindow = gameWindow;

            WindowDecorations = WindowDecorations.None;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = true;
            Closing += (_, e) => e.Cancel = true;
            CanResize = false;
            Title = "Arcademia Achievements Overlay";
            _scrollFrames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnScrollFrame);
            _scrollFrames.Stop();

            _root = new Grid { Background = new SolidColorBrush(AchievementVisuals.Color(0x08030D, 0.78)) };
            Content = _root;
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            NativeWindows.MakeOverlay(WindowHelper.HandleOf(this), clickThrough: false, activatable: true);
        }

        public void ShowSnapshot(AchievementSnapshot snapshot)
        {
            var bounds = NativeWindows.MonitorBounds(_gameWindow?.Invoke() ?? IntPtr.Zero);
            var dpi = Screens.Primary?.Scaling ?? 1.0;
            Position = new PixelPoint(bounds.X, bounds.Y);
            Width = bounds.Width / dpi;
            Height = bounds.Height / dpi;
            var scale = Math.Max(0.5, Math.Min(Height / 1080.0, Width / 1100.0));

            _root.Children.Clear();
            var panel = BuildPanel(snapshot);
            _root.Children.Add(new LayoutTransformControl
            {
                LayoutTransform = new ScaleTransform(scale, scale),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = panel,
            });

            IsOpen = true;
            Transitions = null;
            Opacity = 0;
            Show();
            NativeWindows.KeepOnTop(WindowHelper.HandleOf(this));
            Transitions = _fadeIn;
            Opacity = 1;
        }

        public IntPtr Handle => WindowHelper.HandleOf(this);

        public void KeepOnTop() => NativeWindows.KeepOnTop(Handle);

        public void HideOverlay()
        {
            IsOpen = false;
            Transitions = null;
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
            var offset = _scrollAnimating ? _scrollTarget : _scroll.Offset.Y;

            if (index == 0)
                AnimateScrollTo(0);
            else if (index == _rows.Count - 1)
                AnimateScrollTo(ScrollableHeight(_scroll));
            else if (rowTop - margin < offset)
                AnimateScrollTo(rowTop - margin);
            else if (rowBottom + margin > offset + _scroll.Viewport.Height)
                AnimateScrollTo(rowBottom + margin - _scroll.Viewport.Height);
        }

        private void AnimateScrollTo(double target)
        {
            if (_scroll is null)
                return;

            _scrollFrom = _scroll.Offset.Y;
            _scrollTarget = Math.Clamp(target, 0, ScrollableHeight(_scroll));
            _scrollStart = DateTime.UtcNow;

            if (!_scrollAnimating)
            {
                _scrollAnimating = true;
                _scrollFrames.Start();
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
            _scroll.Offset = new Vector(_scroll.Offset.X, _scrollFrom + (_scrollTarget - _scrollFrom) * eased);

            if (progress >= 1)
                StopScrollAnimation();
        }

        private void StopScrollAnimation()
        {
            if (!_scrollAnimating)
                return;

            _scrollAnimating = false;
            _scrollFrames.Stop();
        }

        private static double ScrollableHeight(ScrollViewer scroll) =>
            Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

        private void UpdateScrollFeedback()
        {
            if (_scroll is null)
                return;

            var scrollable = ScrollableHeight(_scroll);
            var offset = _scroll.Offset.Y;
            var moreAbove = offset > 0.5;
            var moreBelow = offset < scrollable - 0.5;

            var mask = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            };
            mask.GradientStops.Add(new GradientStop(moreAbove ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.08));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.92));
            mask.GradientStops.Add(new GradientStop(moreBelow ? Colors.Transparent : Colors.Black, 1));
            _scroll.OpacityMask = mask;

            if (_scrollTrack is null || _scrollThumb is null)
                return;

            if (scrollable <= 0.5 || _scroll.Extent.Height <= 0)
            {
                _scrollTrack.IsVisible = false;
                return;
            }

            _scrollTrack.IsVisible = true;
            var trackHeight = _scrollTrack.Bounds.Height;
            var thumbHeight = Math.Max(36, trackHeight * _scroll.Viewport.Height / _scroll.Extent.Height);
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

        private static Control BuildPanelShell(out Grid body)
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

        private Control BuildPanel(AchievementSnapshot snapshot)
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

            body.Children.Add(Text("ACHIEVEMENTS", 15, AchievementVisuals.Color(0xC9A0FF), FontWeight.Bold, 26));
            body.Children.Add(Text(set.GameName ?? "", 30, Colors.White, FontWeight.Bold, 46));
            body.Children.Add(Text(
                $"{unlocked} of {rows.Count} unlocked  ·  {teamLine}" + (snapshot.Offline ? "  ·  offline" : ""),
                16, AchievementVisuals.Color(0xA5ADB8), FontWeight.Normal, 92));

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
                IsVisible = false,
                Child = _scrollThumb,
            };
            _scrollTrack.SizeChanged += (_, _) => UpdateScrollFeedback();
            body.Children.Add(_scrollTrack);

            var hint = Text("Press EXIT to close  ·  Up / Down to scroll", 15, AchievementVisuals.Color(0xA5ADB8), FontWeight.Normal, 0);
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.Margin = new Thickness(36, 0, 36, 26);
            body.Children.Add(hint);

            if (_rows.Count > 0)
            {
                _counter = Text($"1 / {_rows.Count}", 15, AchievementVisuals.Color(0xC9A0FF), FontWeight.Bold, 0);
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
                RenderOptions.SetBitmapInterpolationMode(
                    icon,
                    Math.Max(source.PixelSize.Width, source.PixelSize.Height) <= 128
                        ? BitmapInterpolationMode.None
                        : BitmapInterpolationMode.HighQuality
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
                FontWeight = FontWeight.Bold,
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
