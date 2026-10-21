using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ArcademiaGameLauncher.Utils;

namespace ArcademiaGameLauncher.Windows
{
    public sealed class RatingWindow : Window
    {
        public const int MaxStars = 5;

        private static readonly FontFamily PixelFont = new(AchievementVisuals.Pack("Fonts/"), "./#Press Start 2P");
        private static readonly Brush Gold = new SolidColorBrush(AchievementVisuals.Color(0xF0B429));
        private static readonly Brush Dim = new SolidColorBrush(AchievementVisuals.Color(0x4A4F57));
        private static readonly Brush Muted = new SolidColorBrush(AchievementVisuals.Color(0xA5ADB8));

        private readonly TextBlock _title;
        private readonly TextBlock _hint;
        private readonly TextBlock _thanks;
        private readonly Polygon[] _stars = new Polygon[MaxStars];
        private readonly Border _timeBar;
        private readonly StackPanel _starRow;

        public bool IsOpen { get; private set; }
        public int Selected { get; private set; }

        public RatingWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Width = 1000;
            Height = 460;
            Title = "Arcademia Rating";

            var root = new Grid { Background = Brushes.Black, Width = Width, Height = Height };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            Content = root;

            _title = Pixel("Enjoy that?", 24, Brushes.White);
            _title.TextWrapping = TextWrapping.Wrap;
            _title.TextAlignment = TextAlignment.Center;
            _title.Margin = new Thickness(40, 20, 40, 0);
            root.Children.Add(_title);

            _starRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            for (var i = 0; i < MaxStars; i++)
            {
                _stars[i] = Star();
                _starRow.Children.Add(_stars[i]);
            }
            Grid.SetRow(_starRow, 1);
            root.Children.Add(_starRow);

            _thanks = Pixel("Thanks for rating!", 22, Gold);
            _thanks.Visibility = Visibility.Collapsed;
            Grid.SetRow(_thanks, 1);
            root.Children.Add(_thanks);

            _hint = Pixel("LEFT/RIGHT choose   A rate   EXIT skip", 14, Muted);
            Grid.SetRow(_hint, 2);
            root.Children.Add(_hint);

            _timeBar = new Border
            {
                Height = 6,
                Background = Gold,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(40, 0, 40, 0),
                Width = Width - 80,
            };
            Grid.SetRow(_timeBar, 3);
            root.Children.Add(_timeBar);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeWindows.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: false);
        }

        private static TextBlock Pixel(string text, double size, Brush brush) =>
            new()
            {
                Text = text,
                FontFamily = PixelFont,
                FontSize = size,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        private static Polygon Star()
        {
            var points = new PointCollection();
            for (var i = 0; i < 10; i++)
            {
                var radius = i % 2 == 0 ? 50.0 : 21.0;
                var angle = Math.PI / 5 * i - Math.PI / 2;
                points.Add(new Point(55 + radius * Math.Cos(angle), 55 + radius * Math.Sin(angle)));
            }
            return new Polygon
            {
                Points = points,
                Width = 110,
                Height = 110,
                Margin = new Thickness(14, 0, 14, 0),
                StrokeThickness = 4,
                StrokeLineJoin = PenLineJoin.Round,
            };
        }

        public void ShowPrompt(string gameName, Window owner)
        {
            Selected = 0;
            _title.Text = string.IsNullOrWhiteSpace(gameName) ? "Enjoy that? Rate it!" : $"Enjoy {gameName}? Rate it!";
            _starRow.Visibility = Visibility.Visible;
            _hint.Visibility = Visibility.Visible;
            _thanks.Visibility = Visibility.Collapsed;
            _timeBar.Visibility = Visibility.Visible;
            Render();
            SetTimeLeft(1);

            if (owner is not null && owner.ActualWidth > 0)
            {
                Left = owner.Left + (owner.ActualWidth - Width) / 2;
                Top = owner.Top + (owner.ActualHeight - Height) / 2;
            }
            else
            {
                Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
                Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
            }

            IsOpen = true;
            Show();
            KeepOnTop();
        }

        public void Move(int direction)
        {
            if (!IsOpen)
                return;
            Selected = Math.Clamp(Selected + direction, 1, MaxStars);
            Render();
        }

        public void ShowThanks()
        {
            _starRow.Visibility = Visibility.Collapsed;
            _hint.Visibility = Visibility.Collapsed;
            _timeBar.Visibility = Visibility.Hidden;
            _thanks.Visibility = Visibility.Visible;
        }

        public void SetTimeLeft(double fraction) =>
            _timeBar.Width = Math.Max(0, (Width - 80) * Math.Clamp(fraction, 0, 1));

        public void KeepOnTop() => NativeWindows.KeepOnTop(new WindowInteropHelper(this).Handle);

        public void HidePrompt()
        {
            IsOpen = false;
            Hide();
        }

        private void Render()
        {
            for (var i = 0; i < MaxStars; i++)
            {
                var lit = i < Selected;
                _stars[i].Fill = lit ? Gold : Brushes.Transparent;
                _stars[i].Stroke = lit ? Gold : Dim;
            }
        }
    }
}
