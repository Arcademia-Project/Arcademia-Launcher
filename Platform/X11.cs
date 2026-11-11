using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ArcademiaGameLauncher.Platform
{
    internal static class X11
    {
        private const string Lib = "libX11.so.6";
        private const string XtstLib = "libXtst.so.6";

        private const long SubstructureNotifyMask = 1L << 19;
        private const long SubstructureRedirectMask = 1L << 20;
        private const int ClientMessage = 33;
        private const int IsViewable = 2;
        private const int NetWmStateRemove = 0;
        private const int NetWmStateAdd = 1;

        [StructLayout(LayoutKind.Sequential, Size = 192)]
        private struct XClientMessageEvent
        {
            public int type;
            public nint serial;
            public int send_event;
            public nint display;
            public nint window;
            public nint message_type;
            public int format;
            public nint l0;
            public nint l1;
            public nint l2;
            public nint l3;
            public nint l4;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XWindowAttributes
        {
            public int x;
            public int y;
            public int width;
            public int height;
            public int border_width;
            public int depth;
            public nint visual;
            public nint root;
            public int @class;
            public int bit_gravity;
            public int win_gravity;
            public int backing_store;
            public nint backing_planes;
            public nint backing_pixel;
            public int save_under;
            public nint colormap;
            public int map_installed;
            public int map_state;
            public nint all_event_masks;
            public nint your_event_mask;
            public nint do_not_propagate_mask;
            public int override_redirect;
            public nint screen;
        }

        [DllImport(Lib)]
        private static extern nint XOpenDisplay(nint name);

        [DllImport(Lib)]
        private static extern nint XDefaultRootWindow(nint display);

        [DllImport(Lib)]
        private static extern int XDefaultScreen(nint display);

        [DllImport(Lib)]
        private static extern int XDisplayWidth(nint display, int screen);

        [DllImport(Lib)]
        private static extern int XDisplayHeight(nint display, int screen);

        [DllImport(Lib)]
        private static extern nint XInternAtom(nint display, string name, bool onlyIfExists);

        [DllImport(Lib)]
        private static extern int XGetWindowProperty(
            nint display,
            nint window,
            nint property,
            nint offset,
            nint length,
            bool delete,
            nint reqType,
            out nint actualType,
            out int actualFormat,
            out nint itemCount,
            out nint bytesAfter,
            out nint data
        );

        [DllImport(Lib)]
        private static extern int XFree(nint data);

        [StructLayout(LayoutKind.Sequential)]
        private struct XClassHint
        {
            public nint res_name;
            public nint res_class;
        }

        [DllImport(Lib)]
        private static extern int XGetClassHint(nint display, nint window, out XClassHint hint);

        [DllImport(Lib)]
        private static extern int XSendEvent(nint display, nint window, bool propagate, nint eventMask, ref XClientMessageEvent ev);

        [DllImport(Lib)]
        private static extern int XRaiseWindow(nint display, nint window);

        [DllImport(Lib)]
        private static extern int XLowerWindow(nint display, nint window);

        [DllImport(Lib)]
        private static extern int XMoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);

        [DllImport(Lib)]
        private static extern int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);

        [DllImport(Lib)]
        private static extern bool XTranslateCoordinates(nint display, nint src, nint dest, int srcX, int srcY, out int destX, out int destY, out nint child);

        [DllImport(Lib)]
        private static extern int XQueryKeymap(nint display, byte[] keys);

        [DllImport(Lib)]
        private static extern byte XKeysymToKeycode(nint display, nint keysym);

        [DllImport(Lib)]
        private static extern int XFlush(nint display);

        [DllImport(Lib)]
        private static extern nint XSetErrorHandler(XErrorHandler handler);

        [DllImport(XtstLib)]
        private static extern int XTestFakeKeyEvent(nint display, uint keycode, bool isPress, nint delay);

        private delegate int XErrorHandler(nint display, nint errorEvent);

        private static readonly object Gate = new();
        private static readonly XErrorHandler IgnoreErrors = (_, _) => 0;
        private static nint _display;
        private static bool _failed;

        public static bool IsAvailable =>
            OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        private static T Use<T>(Func<nint, T> action, T fallback)
        {
            if (!IsAvailable)
                return fallback;

            lock (Gate)
            {
                if (_failed)
                    return fallback;

                try
                {
                    if (_display == 0)
                    {
                        XSetErrorHandler(IgnoreErrors);
                        _display = XOpenDisplay(0);
                        if (_display == 0)
                        {
                            _failed = true;
                            return fallback;
                        }
                    }

                    var result = action(_display);
                    XFlush(_display);
                    return result;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    _failed = true;
                    return fallback;
                }
            }
        }

        private static void Use(Action<nint> action) =>
            Use(display =>
            {
                action(display);
                return true;
            }, false);

        public static nint FindLargestWindow(ISet<int> processIds) =>
            Use(display =>
            {
                nint best = 0;
                long bestArea = 0;
                var wmPid = Atom(display, "_NET_WM_PID");

                foreach (var window in ReadLongs(display, XDefaultRootWindow(display), Atom(display, "_NET_CLIENT_LIST")))
                {
                    var pids = ReadLongs(display, window, wmPid);
                    if (pids.Length == 0 || !processIds.Contains((int)pids[0]))
                        continue;

                    if (XGetWindowAttributes(display, window, out var attributes) == 0 || attributes.map_state != IsViewable)
                        continue;

                    long area = (long)attributes.width * attributes.height;
                    if (area > bestArea)
                    {
                        best = window;
                        bestArea = area;
                    }
                }

                return best;
            }, (nint)0);

        public static nint FindLargestWindowByClass(string name) =>
            Use(display =>
            {
                nint best = 0;
                long bestArea = 0;

                foreach (var window in ReadLongs(display, XDefaultRootWindow(display), Atom(display, "_NET_CLIENT_LIST")))
                {
                    if (XGetClassHint(display, window, out var hint) == 0)
                        continue;

                    var resName = hint.res_name != 0 ? Marshal.PtrToStringAnsi(hint.res_name) : null;
                    var resClass = hint.res_class != 0 ? Marshal.PtrToStringAnsi(hint.res_class) : null;
                    if (hint.res_name != 0)
                        XFree(hint.res_name);
                    if (hint.res_class != 0)
                        XFree(hint.res_class);

                    if (!string.Equals(resName, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(resClass, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (XGetWindowAttributes(display, window, out var attributes) == 0 || attributes.map_state != IsViewable)
                        continue;

                    long area = (long)attributes.width * attributes.height;
                    if (area > bestArea)
                    {
                        best = window;
                        bestArea = area;
                    }
                }

                return best;
            }, (nint)0);

        public static nint ActiveWindow() =>
            Use(display =>
            {
                var values = ReadLongs(display, XDefaultRootWindow(display), Atom(display, "_NET_ACTIVE_WINDOW"));
                return values.Length > 0 ? values[0] : 0;
            }, (nint)0);

        public static void Activate(nint window)
        {
            if (window == 0)
                return;

            Use(display =>
            {
                SendToRoot(display, window, "_NET_ACTIVE_WINDOW", 2, 0, 0);
                XRaiseWindow(display, window);
            });
        }

        public static void SetAbove(nint window, bool above)
        {
            if (window == 0)
                return;

            Use(display => SetAboveState(display, window, above));
        }

        public static void Raise(nint window)
        {
            if (window == 0)
                return;

            Use(display => XRaiseWindow(display, window));
        }

        public static void Lower(nint window)
        {
            if (window == 0)
                return;

            Use(display =>
            {
                SetAboveState(display, window, false);
                XLowerWindow(display, window);
            });
        }

        public static void KeepOnTop(nint window)
        {
            if (window == 0)
                return;

            Use(display =>
            {
                SetAboveState(display, window, true);
                XRaiseWindow(display, window);
            });
        }

        public static void SetWindowOrder(nint top, nint middle, nint bottom)
        {
            Use(display =>
            {
                if (middle != 0 && middle != top)
                    SetAboveState(display, middle, false);
                if (bottom != 0 && bottom != top)
                    SetAboveState(display, bottom, false);

                if (bottom != 0)
                    XRaiseWindow(display, bottom);
                if (middle != 0)
                    XRaiseWindow(display, middle);
                if (top != 0)
                {
                    SetAboveState(display, top, true);
                    XRaiseWindow(display, top);
                }
            });
        }

        public static void MakeOverlay(nint window)
        {
            if (window == 0)
                return;

            Use(display =>
            {
                SendToRoot(display, window, "_NET_WM_STATE", NetWmStateAdd, Atom(display, "_NET_WM_STATE_SKIP_TASKBAR"), Atom(display, "_NET_WM_STATE_SKIP_PAGER"));
                SetAboveState(display, window, true);
            });
        }

        public static bool TryGetBounds(nint window, out int x, out int y, out int width, out int height)
        {
            int rx = 0, ry = 0, rw = 0, rh = 0;
            var ok = Use(display =>
            {
                if (XGetWindowAttributes(display, window, out var attributes) == 0)
                    return false;

                if (!XTranslateCoordinates(display, window, XDefaultRootWindow(display), 0, 0, out rx, out ry, out _))
                    return false;

                rw = attributes.width;
                rh = attributes.height;
                return true;
            }, false);

            x = rx;
            y = ry;
            width = rw;
            height = rh;
            return ok && window != 0;
        }

        public static void SetBounds(nint window, int x, int y, int width, int height)
        {
            if (window == 0 || width <= 0 || height <= 0)
                return;

            Use(display => XMoveResizeWindow(display, window, x, y, (uint)width, (uint)height));
        }

        public static (int Width, int Height) ScreenSize() =>
            Use(display =>
            {
                var screen = XDefaultScreen(display);
                return (XDisplayWidth(display, screen), XDisplayHeight(display, screen));
            }, (1920, 1080));

        public static (int X, int Y, int Width, int Height)? WorkArea() =>
            Use<(int, int, int, int)?>(display =>
            {
                var values = ReadLongs(display, XDefaultRootWindow(display), Atom(display, "_NET_WORKAREA"));
                return values.Length >= 4 ? ((int)values[0], (int)values[1], (int)values[2], (int)values[3]) : null;
            }, null);

        public static bool IsFullscreen(nint window)
        {
            if (window == 0)
                return false;

            return Use(display =>
            {
                var fullscreen = Atom(display, "_NET_WM_STATE_FULLSCREEN");
                foreach (var state in ReadLongs(display, window, Atom(display, "_NET_WM_STATE")))
                    if (state == fullscreen)
                        return true;
                return false;
            }, false);
        }

        public static bool IsKeyDown(nint keysym) =>
            Use(display =>
            {
                var keycode = XKeysymToKeycode(display, keysym);
                if (keycode == 0)
                    return false;

                var keys = new byte[32];
                XQueryKeymap(display, keys);
                return (keys[keycode / 8] & (1 << (keycode % 8))) != 0;
            }, false);

        public static void SendKey(uint keycode, bool pressed)
        {
            if (keycode == 0)
                return;

            Use(display => XTestFakeKeyEvent(display, keycode, pressed, 0));
        }

        private static nint Atom(nint display, string name) => XInternAtom(display, name, false);

        private static void SetAboveState(nint display, nint window, bool above) =>
            SendToRoot(display, window, "_NET_WM_STATE", above ? NetWmStateAdd : NetWmStateRemove, Atom(display, "_NET_WM_STATE_ABOVE"), 0);

        private static void SendToRoot(nint display, nint window, string messageType, nint l0, nint l1, nint l2)
        {
            var ev = new XClientMessageEvent
            {
                type = ClientMessage,
                send_event = 1,
                display = display,
                window = window,
                message_type = Atom(display, messageType),
                format = 32,
                l0 = l0,
                l1 = l1,
                l2 = l2,
                l3 = 1,
            };

            XSendEvent(display, XDefaultRootWindow(display), false, (nint)(SubstructureRedirectMask | SubstructureNotifyMask), ref ev);
        }

        private static nint[] ReadLongs(nint display, nint window, nint property)
        {
            if (XGetWindowProperty(display, window, property, 0, 4096, false, 0, out _, out var format, out var count, out _, out var data) != 0)
                return [];

            try
            {
                if (data == 0 || format != 32 || count <= 0)
                    return [];

                var values = new nint[count];
                for (int i = 0; i < values.Length; i++)
                    values[i] = Marshal.ReadIntPtr(data, i * IntPtr.Size);
                return values;
            }
            finally
            {
                if (data != 0)
                    XFree(data);
            }
        }
    }
}
