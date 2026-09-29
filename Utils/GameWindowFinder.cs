using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ArcademiaGameLauncher.Utils
{
    public static class GameWindowFinder
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const uint GW_OWNER = 4;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr InvalidHandle = new(-1);

        private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "ConsoleWindowClass",
            "PseudoConsoleWindow",
            "CASCADIA_HOSTING_WINDOW_CLASS",
            "IME",
            "MSCTFIME UI",
        };

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static IntPtr Find(int rootProcessId)
        {
            var processIds = ProcessTree(rootProcessId);
            var best = IntPtr.Zero;
            long bestArea = 0;

            EnumWindows(
                (hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out var processId);
                    if (!processIds.Contains(processId) || !IsGameWindowCandidate(hWnd, out var area))
                        return true;

                    if (area > bestArea)
                    {
                        best = hWnd;
                        bestArea = area;
                    }
                    return true;
                },
                IntPtr.Zero
            );

            return best;
        }

        private static bool IsGameWindowCandidate(IntPtr hWnd, out long area)
        {
            area = 0;

            if (!IsWindowVisible(hWnd) || GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
                return false;

            if ((GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
                return false;

            var className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);
            if (IgnoredClasses.Contains(className.ToString()))
                return false;

            if (!GetWindowRect(hWnd, out var rect))
                return false;

            area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            return area > 0;
        }

        private static DateTime? StartTime(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.StartTime;
            }
            catch
            {
                return null;
            }
        }

        private static bool StartedAfter(uint processId, DateTime? rootStarted) =>
            rootStarted is not DateTime root || StartTime((int)processId) is not DateTime started || started >= root;

        private static HashSet<uint> ProcessTree(int rootProcessId)
        {
            var tree = new HashSet<uint> { (uint)rootProcessId };
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == InvalidHandle || snapshot == IntPtr.Zero)
                return tree;

            try
            {
                var parents = new List<(uint Id, uint Parent)>();
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                if (Process32First(snapshot, ref entry))
                {
                    do
                        parents.Add((entry.ProcessId, entry.ParentProcessId));
                    while (Process32Next(snapshot, ref entry));
                }

                var rootStarted = StartTime(rootProcessId);
                bool added;
                do
                {
                    added = false;
                    foreach (var (id, parent) in parents)
                        if (tree.Contains(parent) && !tree.Contains(id) && StartedAfter(id, rootStarted) && tree.Add(id))
                            added = true;
                } while (added);
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return tree;
        }
    }
}
