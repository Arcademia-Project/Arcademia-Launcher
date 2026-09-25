using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
        private const double ScrollStep = RowHeight + 8;

        private readonly Func<IntPtr> _gameWindow;
        private readonly Grid _root;
        private ScrollViewer _scroll;

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
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            Title = "Arcademia Achievements Overlay";

            _root = new Grid { Background = new SolidColorBrush(AchievementVisuals.Color(0x08030D, 0.78)) };
            Content = _root;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeWindows.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: false);
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
            Opacity = 1;
            Show();
            NativeWindows.KeepOnTop(handle);
        }

        public void KeepOnTop() => NativeWindows.KeepOnTop(new WindowInteropHelper(this).Handle);

        public void HideOverlay()
        {
            IsOpen = false;
            Hide();
            _root.Children.Clear();
            _scroll = null;
        }

        public void Scroll(int direction)
        {
            if (_scroll is null || direction == 0)
                return;
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + direction * ScrollStep);
        }

        private static TextBlock Text(string value, double size, Color color, FontWeight weight, double top, double left = 36)
        {
            var block = new TextBlock
            {
                Text = value ?? "",
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

            var list = new StackPanel { Margin = new Thickness(12, 4, 12, 12) };
            if (rows.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = "This game has no achievements yet.",
                    FontFamily = AchievementVisuals.Font,
                    FontSize = 18,
                    Foreground = new SolidColorBrush(AchievementVisuals.Color(0xA5ADB8)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0),
                });

            foreach (var r in rows)
                list.Children.Add(BuildRow(set, r.Achievement, r.Hold, r.ThisSession, r.Unlocked));

            _scroll = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(24, 150, 24, 70),
                Focusable = false,
            };
            body.Children.Add(_scroll);

            var hint = Text("Press EXIT to close  ·  Up / Down to scroll", 15, AchievementVisuals.Color(0xA5ADB8), FontWeights.Normal, 0);
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.Margin = new Thickness(36, 0, 36, 26);
            body.Children.Add(hint);

            return shell;
        }

        private static FrameworkElement BuildRow(
            CachedAchievementSet set,
            CachedAchievement a,
            CachedTeamHold hold,
            bool thisSession,
            bool unlocked
        )
        {
            var secret = a.Hidden && !unlocked;
            var row = new Grid
            {
                Height = RowHeight,
                Margin = new Thickness(0, 0, 0, 8),
                Background = new SolidColorBrush(unlocked ? AchievementVisuals.Color(0x47384E) : AchievementVisuals.Color(0x291C30)),
            };
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
                Text = secret ? "Hidden achievement" : a.Name,
                FontFamily = AchievementVisuals.Font,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(unlocked ? Colors.White : AchievementVisuals.Color(0x9E9EA8)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = secret ? "Keep playing to discover this one" : a.Description,
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
                Text = status,
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

            return row;
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
