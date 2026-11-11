using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ArcademiaGameLauncher.Platform
{
    internal static class Sdl
    {
        private const string Lib = "SDL2";

        public const uint InitAudio = 0x00000010;
        public const uint InitJoystick = 0x00000200;
        public const ushort AudioF32 = 0x8120;

        public static readonly object Gate = new();

        [StructLayout(LayoutKind.Sequential)]
        public struct AudioSpec
        {
            public int freq;
            public ushort format;
            public byte channels;
            public byte silence;
            public ushort samples;
            public ushort padding;
            public uint size;
            public nint callback;
            public nint userdata;
        }

        static Sdl()
        {
            NativeLibrary.SetDllImportResolver(typeof(Sdl).Assembly, Resolve);
        }

        private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (name != Lib)
                return 0;

            string[] candidates = OperatingSystem.IsWindows()
                ? ["SDL2.dll"]
                : ["libSDL2-2.0.so", "libSDL2-2.0.so.0", "libSDL2.so"];

            foreach (var candidate in candidates)
            {
                var local = Path.Combine(AppContext.BaseDirectory, candidate);
                if (File.Exists(local) && NativeLibrary.TryLoad(local, out var handle))
                    return handle;
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out handle))
                    return handle;
            }

            return 0;
        }

        private static bool _initialised;
        private static uint _initialisedFlags;

        public static bool EnsureInitialised(uint flags)
        {
            lock (Gate)
            {
                if (_initialised && (_initialisedFlags & flags) == flags)
                    return true;

                try
                {
                    SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
                    if ((_initialised ? SDL_InitSubSystem(flags) : SDL_Init(flags)) != 0)
                        return false;

                    _initialised = true;
                    _initialisedFlags |= flags;
                    return true;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    return false;
                }
            }
        }

        public static string LastError
        {
            get
            {
                try
                {
                    return Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";
                }
                catch
                {
                    return "";
                }
            }
        }

        [DllImport(Lib)]
        private static extern int SDL_Init(uint flags);

        [DllImport(Lib)]
        private static extern int SDL_InitSubSystem(uint flags);

        [DllImport(Lib)]
        private static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Lib)]
        private static extern nint SDL_GetError();

        [DllImport(Lib)]
        public static extern int SDL_NumJoysticks();

        [DllImport(Lib)]
        public static extern int SDL_JoystickGetDeviceInstanceID(int deviceIndex);

        [DllImport(Lib)]
        public static extern nint SDL_JoystickOpen(int deviceIndex);

        [DllImport(Lib)]
        public static extern void SDL_JoystickClose(nint joystick);

        [DllImport(Lib)]
        public static extern void SDL_JoystickUpdate();

        [DllImport(Lib)]
        public static extern int SDL_JoystickGetAttached(nint joystick);

        [DllImport(Lib)]
        public static extern int SDL_JoystickInstanceID(nint joystick);

        [DllImport(Lib)]
        public static extern int SDL_JoystickNumAxes(nint joystick);

        [DllImport(Lib)]
        public static extern int SDL_JoystickNumButtons(nint joystick);

        [DllImport(Lib)]
        public static extern int SDL_JoystickNumHats(nint joystick);

        [DllImport(Lib)]
        public static extern short SDL_JoystickGetAxis(nint joystick, int axis);

        [DllImport(Lib)]
        public static extern byte SDL_JoystickGetButton(nint joystick, int button);

        [DllImport(Lib)]
        public static extern byte SDL_JoystickGetHat(nint joystick, int hat);

        [DllImport(Lib)]
        public static extern uint SDL_OpenAudioDevice(nint device, int isCapture, ref AudioSpec desired, out AudioSpec obtained, int allowedChanges);

        [DllImport(Lib)]
        public static extern int SDL_QueueAudio(uint device, float[] data, uint length);

        [DllImport(Lib)]
        public static extern uint SDL_GetQueuedAudioSize(uint device);

        [DllImport(Lib)]
        public static extern void SDL_ClearQueuedAudio(uint device);

        [DllImport(Lib)]
        public static extern void SDL_PauseAudioDevice(uint device, int pauseOn);

        [DllImport(Lib)]
        public static extern void SDL_CloseAudioDevice(uint device);
    }
}
