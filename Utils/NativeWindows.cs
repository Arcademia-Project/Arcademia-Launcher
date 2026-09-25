using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace ArcademiaGameLauncher.Utils
{
    public static class NativeWindows
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TRANSPARENT = 0x00000020;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_LAYERED = 0x00080000;
        private const long WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        public static void MakeOverlay(IntPtr handle, bool clickThrough)
        {
            if (handle == IntPtr.Zero)
                return;
            var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
            style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            if (clickThrough)
                style |= WS_EX_TRANSPARENT;
            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style));
        }

        public static void KeepOnTop(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public static Int32Rect MonitorBounds(IntPtr window)
        {
            var monitor = window != IntPtr.Zero
                ? MonitorFromWindow(window, MONITOR_DEFAULTTOPRIMARY)
                : MonitorFromPoint(new NativePoint(), MONITOR_DEFAULTTOPRIMARY);

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                return new Int32Rect(
                    0,
                    0,
                    (int)SystemParameters.PrimaryScreenWidth,
                    (int)SystemParameters.PrimaryScreenHeight
                );

            return new Int32Rect(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top
            );
        }

        public static Int32Rect ToastArea(IntPtr window)
        {
            var monitor = window != IntPtr.Zero
                ? MonitorFromWindow(window, MONITOR_DEFAULTTOPRIMARY)
                : MonitorFromPoint(new NativePoint(), MONITOR_DEFAULTTOPRIMARY);

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                return MonitorBounds(window);

            var coversMonitor =
                window != IntPtr.Zero
                && GetWindowRect(window, out var rect)
                && rect.Left <= info.Monitor.Left
                && rect.Top <= info.Monitor.Top
                && rect.Right >= info.Monitor.Right
                && rect.Bottom >= info.Monitor.Bottom;

            var area = coversMonitor ? info.Monitor : info.Work;
            return new Int32Rect(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);
        }
    }
}
