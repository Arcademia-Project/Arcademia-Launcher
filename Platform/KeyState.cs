using System;
using System.Runtime.InteropServices;

namespace ArcademiaGameLauncher.Platform
{
    public static partial class KeyState
    {
        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState(int virtualKey);

        public static bool IsDown(int virtualKey)
        {
            if (virtualKey == 0)
                return false;

            if (OperatingSystem.IsWindows())
                return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

            var keysym = ToKeysym(virtualKey);
            return keysym != 0 && X11.IsKeyDown(keysym);
        }

        public static bool IsNumLockOn() =>
            OperatingSystem.IsWindows() && ((ushort)GetAsyncKeyState(0x90) & 0xffff) != 0;

        internal static nint ToKeysym(int virtualKey) =>
            virtualKey switch
            {
                >= 0x30 and <= 0x39 => virtualKey,
                >= 0x41 and <= 0x5A => virtualKey + 0x20,
                >= 0x60 and <= 0x69 => 0xFFB0 + (virtualKey - 0x60),
                >= 0x70 and <= 0x87 => 0xFFBE + (virtualKey - 0x70),
                0x08 => 0xFF08,
                0x09 => 0xFF09,
                0x0D => 0xFF0D,
                0x10 or 0xA0 => 0xFFE1,
                0xA1 => 0xFFE2,
                0x11 or 0xA2 => 0xFFE3,
                0xA3 => 0xFFE4,
                0x12 or 0xA4 => 0xFFE9,
                0xA5 => 0xFFEA,
                0x13 => 0xFF13,
                0x14 => 0xFFE5,
                0x1B => 0xFF1B,
                0x20 => 0x20,
                0x21 => 0xFF55,
                0x22 => 0xFF56,
                0x23 => 0xFF57,
                0x24 => 0xFF50,
                0x25 => 0xFF51,
                0x26 => 0xFF52,
                0x27 => 0xFF53,
                0x28 => 0xFF54,
                0x2D => 0xFF63,
                0x2E => 0xFFFF,
                0x6A => 0xFFAA,
                0x6B => 0xFFAB,
                0x6D => 0xFFAD,
                0x6E => 0xFFAE,
                0x6F => 0xFFAF,
                0x90 => 0xFF7F,
                0x91 => 0xFF14,
                0xBA => 0x3B,
                0xBB => 0x3D,
                0xBC => 0x2C,
                0xBD => 0x2D,
                0xBE => 0x2E,
                0xBF => 0x2F,
                0xC0 => 0x60,
                0xDB => 0x5B,
                0xDC => 0x5C,
                0xDD => 0x5D,
                0xDE => 0x27,
                _ => 0,
            };

        internal static uint ScanCodeToX11Keycode(short scanCode, bool extended)
        {
            if (scanCode <= 0)
                return 0;

            int evdev = extended
                ? scanCode switch
                {
                    72 => 103,
                    80 => 108,
                    75 => 105,
                    77 => 106,
                    71 => 102,
                    79 => 107,
                    82 => 110,
                    83 => 111,
                    73 => 104,
                    81 => 109,
                    53 => 98,
                    29 => 97,
                    56 => 100,
                    _ => scanCode,
                }
                : scanCode;

            return (uint)(evdev + 8);
        }
    }
}
