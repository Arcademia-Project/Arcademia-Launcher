using System;
using System.Collections.Generic;
using SharpDX.DirectInput;

namespace ArcademiaGameLauncher.Platform
{
    internal interface IGamepadBackend : IDisposable
    {
        List<Guid> FindConnectedDevices();
        IGamepadDevice Open(Guid deviceGuid);
    }

    internal interface IGamepadDevice : IDisposable
    {
        void Acquire();
        void Read(out int x, out int y, bool[] buttons);
    }

    internal static class Gamepads
    {
        public static IGamepadBackend CreateBackend() =>
            OperatingSystem.IsWindows() ? new DirectInputGamepads() : new SdlGamepads();
    }

    internal sealed class DirectInputGamepads : IGamepadBackend
    {
        private readonly DirectInput _directInput = new();

        public List<Guid> FindConnectedDevices()
        {
            List<Guid> deviceGuids = [];

            foreach (var deviceInstance in _directInput.GetDevices(DeviceType.Gamepad, DeviceEnumerationFlags.AttachedOnly))
                deviceGuids.Add(deviceInstance.InstanceGuid);

            if (deviceGuids.Count == 0)
                foreach (var deviceInstance in _directInput.GetDevices(DeviceType.Joystick, DeviceEnumerationFlags.AttachedOnly))
                    deviceGuids.Add(deviceInstance.InstanceGuid);

            return deviceGuids;
        }

        public IGamepadDevice Open(Guid deviceGuid) => new DirectInputGamepad(new Joystick(_directInput, deviceGuid));

        public void Dispose() => _directInput.Dispose();
    }

    internal sealed class DirectInputGamepad : IGamepadDevice
    {
        private readonly Joystick _joystick;

        public DirectInputGamepad(Joystick joystick)
        {
            _joystick = joystick;
            _joystick.Properties.BufferSize = 128;
            _joystick.Acquire();

            try
            {
                _joystick.GetCurrentState();
            }
            catch { }
        }

        public void Acquire() => _joystick.Acquire();

        public void Read(out int x, out int y, bool[] buttons)
        {
            _joystick.Poll();
            var state = _joystick.GetCurrentState();

            x = state.X;
            y = state.Y;
            for (int i = 0; i < buttons.Length; i++)
                buttons[i] = state.Buttons[i];
        }

        public void Dispose()
        {
            try
            {
                _joystick.Unacquire();
            }
            catch { }
            try
            {
                _joystick.Dispose();
            }
            catch { }
        }
    }

    internal sealed class SdlGamepads : IGamepadBackend
    {
        private readonly bool _available = Sdl.EnsureInitialised(Sdl.InitJoystick);

        public static Guid ToGuid(int instanceId)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(instanceId).CopyTo(bytes, 0);
            bytes[15] = 0x5D;
            return new Guid(bytes);
        }

        public List<Guid> FindConnectedDevices()
        {
            List<Guid> devices = [];
            if (!_available)
                return devices;

            lock (Sdl.Gate)
            {
                Sdl.SDL_JoystickUpdate();
                int count = Sdl.SDL_NumJoysticks();
                for (int i = 0; i < count; i++)
                {
                    int instanceId = Sdl.SDL_JoystickGetDeviceInstanceID(i);
                    if (instanceId >= 0)
                        devices.Add(ToGuid(instanceId));
                }
            }

            return devices;
        }

        public IGamepadDevice Open(Guid deviceGuid)
        {
            lock (Sdl.Gate)
            {
                int count = Sdl.SDL_NumJoysticks();
                for (int i = 0; i < count; i++)
                {
                    if (ToGuid(Sdl.SDL_JoystickGetDeviceInstanceID(i)) != deviceGuid)
                        continue;

                    var joystick = Sdl.SDL_JoystickOpen(i);
                    if (joystick == 0)
                        throw new InvalidOperationException($"Could not open controller: {Sdl.LastError}");
                    return new SdlGamepad(joystick);
                }
            }

            throw new InvalidOperationException("The controller is no longer connected.");
        }

        public void Dispose() { }
    }

    internal sealed class SdlGamepad(nint joystick) : IGamepadDevice
    {
        private const int AxisOffset = 32768;
        private const int HatUp = 0x01;
        private const int HatRight = 0x02;
        private const int HatDown = 0x04;
        private const int HatLeft = 0x08;

        private nint _joystick = joystick;

        public void Acquire() { }

        public void Read(out int x, out int y, bool[] buttons)
        {
            lock (Sdl.Gate)
            {
                if (_joystick == 0 || Sdl.SDL_JoystickGetAttached(_joystick) == 0)
                    throw new InvalidOperationException("The controller was disconnected.");

                Sdl.SDL_JoystickUpdate();

                int axes = Sdl.SDL_JoystickNumAxes(_joystick);
                x = axes > 0 ? Sdl.SDL_JoystickGetAxis(_joystick, 0) + AxisOffset : AxisOffset;
                y = axes > 1 ? Sdl.SDL_JoystickGetAxis(_joystick, 1) + AxisOffset : AxisOffset;

                if (Sdl.SDL_JoystickNumHats(_joystick) > 0)
                {
                    int hat = Sdl.SDL_JoystickGetHat(_joystick, 0);
                    if ((hat & HatLeft) != 0)
                        x = 0;
                    else if ((hat & HatRight) != 0)
                        x = ushort.MaxValue;
                    if ((hat & HatUp) != 0)
                        y = 0;
                    else if ((hat & HatDown) != 0)
                        y = ushort.MaxValue;
                }

                int available = Sdl.SDL_JoystickNumButtons(_joystick);
                for (int i = 0; i < buttons.Length; i++)
                    buttons[i] = i < available && Sdl.SDL_JoystickGetButton(_joystick, i) != 0;
            }
        }

        public void Dispose()
        {
            lock (Sdl.Gate)
            {
                if (_joystick != 0)
                    Sdl.SDL_JoystickClose(_joystick);
                _joystick = 0;
            }
        }
    }
}
