using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ArcademiaGameLauncher.Utils
{
    public static class WindowHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        private const uint GR_GDIOBJECTS = 0;
        private const uint GR_USEROBJECTS = 1;

        public static (uint GdiObjects, uint UserObjects) GetGdiUserHandleCounts()
        {
            using var currentProcess = Process.GetCurrentProcess();
            return (
                GetGuiResources(currentProcess.Handle, GR_GDIOBJECTS),
                GetGuiResources(currentProcess.Handle, GR_USEROBJECTS)
            );
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

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

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new(-2);
        private static readonly IntPtr HWND_BOTTOM = new(1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        public static void ForceForeground(Window window)
        {
            if (window == null)
                return;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                var windowHandle = new WindowInteropHelper(window).Handle;
                ForceForeground(windowHandle);
            });
        }

        public static void ForceForeground(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                IntPtr foregroundWnd = GetForegroundWindow();
                if (foregroundWnd != windowHandle)
                {
                    uint threadId1 = GetWindowThreadProcessId(foregroundWnd, IntPtr.Zero);
                    uint threadId2 = GetWindowThreadProcessId(windowHandle, IntPtr.Zero);

                    if (threadId1 != threadId2)
                    {
                        AttachThreadInput(threadId2, threadId1, true);
                        SetForegroundWindow(windowHandle);
                        BringWindowToTop(windowHandle);

                        SetWindowPos(
                            windowHandle,
                            HWND_NOTOPMOST,
                            0,
                            0,
                            0,
                            0,
                            SWP_NOMOVE | SWP_NOSIZE
                        );
                        SetWindowPos(
                            windowHandle,
                            HWND_TOPMOST,
                            0,
                            0,
                            0,
                            0,
                            SWP_NOMOVE | SWP_NOSIZE
                        );

                        AttachThreadInput(threadId2, threadId1, false);
                    }
                    else
                    {
                        SetForegroundWindow(windowHandle);
                        BringWindowToTop(windowHandle);

                        SetWindowPos(
                            windowHandle,
                            HWND_NOTOPMOST,
                            0,
                            0,
                            0,
                            0,
                            SWP_NOMOVE | SWP_NOSIZE
                        );
                        SetWindowPos(
                            windowHandle,
                            HWND_TOPMOST,
                            0,
                            0,
                            0,
                            0,
                            SWP_NOMOVE | SWP_NOSIZE
                        );
                    }
                }
            });
        }

        public static void EnsureTopMost(Window window)
        {
            if (window == null)
                return;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                var windowHandle = new WindowInteropHelper(window).Handle;
                EnsureTopMost(windowHandle);
            });
        }

        public static void EnsureTopMost(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                SetWindowPos(
                    windowHandle,
                    HWND_TOPMOST,
                    0,
                    0,
                    0,
                    0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                );
            });
        }

        public static void SendToBack(Window window)
        {
            if (window == null)
                return;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                var windowHandle = new WindowInteropHelper(window).Handle;
                SendToBack(windowHandle);
            });
        }

        public static void SendToBack(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                SetWindowPos(
                    windowHandle,
                    HWND_BOTTOM,
                    0,
                    0,
                    0,
                    0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                );
            });
        }

        public static void SetWindowOrder(Window top, Window middle, Window bottom)
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                IntPtr topHandle = top != null ? new WindowInteropHelper(top).Handle : IntPtr.Zero;
                IntPtr middleHandle =
                    middle != null ? new WindowInteropHelper(middle).Handle : IntPtr.Zero;
                IntPtr bottomHandle =
                    bottom != null ? new WindowInteropHelper(bottom).Handle : IntPtr.Zero;

                SetWindowOrder(topHandle, middleHandle, bottomHandle);
            });
        }

        public static void SetWindowOrder(
            IntPtr topHandle,
            IntPtr middleHandle,
            IntPtr bottomHandle
        )
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                if (middleHandle != IntPtr.Zero && middleHandle != topHandle)
                {
                    SetWindowPos(
                        middleHandle,
                        HWND_NOTOPMOST,
                        0,
                        0,
                        0,
                        0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                    );
                }

                if (bottomHandle != IntPtr.Zero && bottomHandle != topHandle)
                {
                    SetWindowPos(
                        bottomHandle,
                        HWND_NOTOPMOST,
                        0,
                        0,
                        0,
                        0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                    );
                }

                if (topHandle != IntPtr.Zero)
                {
                    SetWindowPos(
                        topHandle,
                        HWND_TOPMOST,
                        0,
                        0,
                        0,
                        0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                    );
                }

                if (middleHandle != IntPtr.Zero)
                {
                    IntPtr insertAfter = topHandle != IntPtr.Zero ? topHandle : HWND_TOPMOST;
                    SetWindowPos(
                        middleHandle,
                        insertAfter,
                        0,
                        0,
                        0,
                        0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                    );
                }

                if (bottomHandle != IntPtr.Zero)
                {
                    IntPtr insertAfter =
                        middleHandle != IntPtr.Zero
                            ? middleHandle
                            : (topHandle != IntPtr.Zero ? topHandle : HWND_TOPMOST);
                    SetWindowPos(
                        bottomHandle,
                        insertAfter,
                        0,
                        0,
                        0,
                        0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                    );
                }
            });
        }
    }
}
