using System;
using System.IO;
using System.Runtime.InteropServices;
using ArcademiaGameLauncher.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using ArcademiaGameLauncher.Utils;
using QRCoder;

namespace ArcademiaGameLauncher.Windows
{
    public partial class ClaimWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags
        );

        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        private IntPtr _windowHandle;
        private string _lastTimeString = "";
        private bool _isOpen;

        public ClaimWindow()
        {
            InitializeComponent();
            ShowActivated = false;
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _windowHandle = WindowHelper.HandleOf(this);
        }

        public void ShowWindow(string claimUrl)
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                _isOpen = true;
                _lastTimeString = "";

                QrImage.Source = RenderQrCode(claimUrl);

                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                Topmost = false;
                Topmost = true;

                Show();
                Activate();

                WindowHelper.ForceForeground(this);
            });
        }

        public void HideWindow()
        {
            Application.Current?.Dispatcher?.InvokeAsync(
                () =>
                {
                    _isOpen = false;

                    if (_windowHandle != IntPtr.Zero && !OperatingSystem.IsWindows())
                        X11.Lower(_windowHandle);
                    else if (_windowHandle != IntPtr.Zero)
                        SetWindowPos(
                            _windowHandle,
                            HWND_BOTTOM,
                            0,
                            0,
                            0,
                            0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                        );

                    Topmost = false;
                    Hide();
                    QrImage.Source = null;
                },
                Avalonia.Threading.DispatcherPriority.Send
            );
        }

        public void ForceForeground()
        {
            WindowHelper.ForceForeground(this);
        }

        public void UpdateCountdown(int millisecondsRemaining)
        {
            if (!_isOpen)
                return;

            var seconds = Math.Max(0, millisecondsRemaining / 1000);
            var text = $"{seconds / 60}:{seconds % 60:00}";

            if (text == _lastTimeString)
                return;
            _lastTimeString = text;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                CountdownText.Text = text;
            });
        }

        private static Bitmap RenderQrCode(string content)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var pngQr = new PngByteQRCode(data);
            var bytes = pngQr.GetGraphic(20);

            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
    }
}
