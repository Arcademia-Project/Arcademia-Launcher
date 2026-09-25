using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArcademiaGameLauncher.Models;
using ArcademiaGameLauncher.Utils;
using QRCoder;

namespace ArcademiaGameLauncher.Windows
{
    public sealed class SessionClaimWindow : Window
    {
        private const int MaxListed = 5;

        private static readonly FontFamily PixelFont = new(AchievementVisuals.Pack("Fonts/"), "./#Press Start 2P");

        private readonly Grid _root;
        private readonly TextBlock _status;
        private readonly TextBlock _countdown;
        private readonly Image _qr;
        private readonly StackPanel _list;
        private readonly TextBlock _title;
        private readonly TextBlock _hint;
        private string _lastCountdown = "";

        public bool IsOpen { get; private set; }

        public SessionClaimWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Width = 1200;
            Height = 700;
            Title = "Arcademia Claim";

            _root = new Grid { Background = Brushes.Black, Width = 1200, Height = 700 };
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(110) });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
            Content = _root;

            _title = Pixel("Claim Your Achievements", 26, Brushes.White);
            _root.Children.Add(_title);

            var middle = new Grid { Margin = new Thickness(60, 0, 60, 0) };
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(middle, 1);
            _root.Children.Add(middle);

            _qr = new Image { Width = 300, Height = 300 };
            RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
            middle.Children.Add(new Border
            {
                Width = 320,
                Height = 320,
                Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = _qr,
            });

            _list = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30, 0, 0, 0) };
            Grid.SetColumn(_list, 1);
            middle.Children.Add(_list);

            _status = Pixel("", 14, new SolidColorBrush(AchievementVisuals.Color(0xC9A0FF)));
            _status.TextWrapping = TextWrapping.Wrap;
            _status.TextAlignment = TextAlignment.Center;
            _status.Margin = new Thickness(60, 0, 60, 0);
            Grid.SetRow(_status, 2);
            _root.Children.Add(_status);

            _hint = Pixel("Press EXIT To Skip", 20, Brushes.White);
            Grid.SetRow(_hint, 3);
            _root.Children.Add(_hint);

            _countdown = Pixel("5:00", 36, Brushes.Red);
            Grid.SetRow(_countdown, 4);
            _root.Children.Add(_countdown);
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

        public void ShowOffer(SessionClaimOffer offer, string url, Window owner)
        {
            _lastCountdown = "";
            _qr.Source = RenderQrCode(url);
            _title.Text = offer.Unlocks.Count == 1 ? "Claim Your Achievement" : "Claim Your Achievements";
            _title.Foreground = Brushes.White;
            _qr.Opacity = 1;
            _hint.Visibility = Visibility.Visible;
            _countdown.Visibility = Visibility.Visible;
            _status.Text = "Scan to add them to your Arcademia account";
            _status.Foreground = new SolidColorBrush(AchievementVisuals.Color(0xC9A0FF));

            _list.Children.Clear();
            foreach (var unlock in offer.Unlocks.Take(MaxListed))
                _list.Children.Add(Row(unlock));
            var extra = offer.Unlocks.Count - MaxListed;
            if (extra > 0)
                _list.Children.Add(Line($"+ {extra} more", 14, AchievementVisuals.Color(0xA5ADB8), new Thickness(0, 6, 0, 0)));
            if (offer.UnclaimedScores > 0)
                _list.Children.Add(Line(
                    offer.UnclaimedScores == 1 ? "+ your score from this game" : $"+ your {offer.UnclaimedScores} scores from this game",
                    14,
                    AchievementVisuals.Color(0xA5ADB8),
                    new Thickness(0, 10, 0, 0)
                ));

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
            NativeWindows.KeepOnTop(new WindowInteropHelper(this).Handle);
        }

        private static TextBlock Line(string text, double size, Color color, Thickness margin) =>
            new()
            {
                Text = text,
                FontFamily = PixelFont,
                FontSize = size,
                Foreground = new SolidColorBrush(color),
                Margin = margin,
            };

        private static FrameworkElement Row(SessionUnlock unlock)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
            var icon = AchievementVisuals.Icon(unlock.IconPath);
            if (icon is not null)
            {
                var image = new Image { Source = icon, Width = 48, Height = 48, Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(
                    image,
                    Math.Max(icon.PixelWidth, icon.PixelHeight) <= 128 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality
                );
                row.Children.Add(image);
            }

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            text.Children.Add(new TextBlock
            {
                Text = unlock.Name,
                FontFamily = PixelFont,
                FontSize = 14,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 640,
            });
            text.Children.Add(new TextBlock
            {
                Text = unlock.AllowPersonal ? (unlock.TeamHadIt ? "For you" : "For you and your team") : "For your team",
                FontFamily = PixelFont,
                FontSize = 10,
                Foreground = new SolidColorBrush(AchievementVisuals.Color(0xA5ADB8)),
                Margin = new Thickness(0, 6, 0, 0),
            });
            row.Children.Add(text);
            return row;
        }

        public void SetOnline(bool online)
        {
            if (!IsOpen)
                return;
            _status.Text = online
                ? "Scan to add them to your Arcademia account"
                : "Offline: scan now, it saves when this machine reconnects";
            _status.Foreground = new SolidColorBrush(online ? AchievementVisuals.Color(0xC9A0FF) : AchievementVisuals.Color(0xF0B429));
        }

        public void ShowClaimed(string claimedBy)
        {
            if (!IsOpen)
                return;
            _title.Text = "Claimed!";
            _qr.Opacity = 0.12;
            _hint.Visibility = Visibility.Hidden;
            _countdown.Visibility = Visibility.Hidden;
            _title.Foreground = new SolidColorBrush(AchievementVisuals.Color(0x2EA043));
            _status.Text = string.IsNullOrEmpty(claimedBy) ? "Saved to your account" : $"Saved to {claimedBy}'s account";
            _status.Foreground = new SolidColorBrush(AchievementVisuals.Color(0x2EA043));
        }

        public void UpdateCountdown(int millisecondsRemaining)
        {
            if (!IsOpen)
                return;
            var seconds = Math.Max(0, millisecondsRemaining / 1000);
            var text = $"{seconds / 60}:{seconds % 60:00}";
            if (text == _lastCountdown)
                return;
            _lastCountdown = text;
            _countdown.Text = text;
        }

        public void KeepOnTop() => NativeWindows.KeepOnTop(new WindowInteropHelper(this).Handle);

        public void HideOffer()
        {
            IsOpen = false;
            Hide();
            _qr.Source = null;
            _list.Children.Clear();
        }

        private static BitmapImage RenderQrCode(string content)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var bytes = new PngByteQRCode(data).GetGraphic(20);

            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }
    }
}
