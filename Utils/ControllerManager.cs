using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using ArcademiaGameLauncher.Windows;
using Microsoft.Extensions.Logging;
using SharpDX.DirectInput;

namespace ArcademiaGameLauncher.Utils
{
    class ControllerManager
    {
        [DllImport(
            "user32.dll",
            CharSet = CharSet.Auto,
            ExactSpelling = true,
            CallingConvention = CallingConvention.Winapi
        )]
        public static extern short GetAsyncKeyState(int keyCode);

        private const int MinimumSlots = 2;
        private const int DeviceScanIntervalMs = 2000;

        private readonly MainWindow _mainWindow;
        private readonly ILogger<ControllerManager> _logger;
        private readonly Keyboard keyboard;

        private readonly int _pollingRate;
        private bool _isKeymapping;
        private bool _debugKeyDown;
        private bool _debugMode;
        private bool _debugModeRunning = false;

        private DirectInput _directInput;
        private readonly object _slotsLock = new();
        private readonly Dictionary<Guid, int> _slotByDevice = [];
        private readonly CancellationTokenSource _deviceWatcherCts = new();
        private List<ControllerState> _controllerStates = [];
        private ControllerMapping _mapping;

        public ControllerManager(
            MainWindow mainWindow,
            int pollingRate,
            ILogger<ControllerManager> logger
        )
        {
            _mainWindow = mainWindow;
            _pollingRate = pollingRate;
            _logger = logger;

            keyboard = new Keyboard();
            _isKeymapping = true;
            _debugMode = false;

            _directInput = new();

            for (int i = 0; i < MinimumSlots; i++)
                AddSlot();

            SyncDevices();
            StartDeviceWatcher();
            ListenForDebugKey();
        }

        public void Dispose()
        {
            _deviceWatcherCts.Cancel();

            foreach (var controllerState in _controllerStates)
            {
                controllerState.StopPolling();
                controllerState.Detach();
            }
            _directInput.Dispose();
        }

        private ControllerState AddSlot()
        {
            var controllerState = new ControllerState(
                _controllerStates.Count,
                () => _mainWindow.Key_Pressed(),
                SendKey
            );
            controllerState.UpdateMapping(_mapping);
            controllerState.SetKeymapping(_isKeymapping);
            controllerState.SetDebugMode(_debugMode);
            controllerState.StartPolling(_pollingRate);

            _controllerStates = [.. _controllerStates, controllerState];
            return controllerState;
        }

        private List<Guid> FindConnectedDevices()
        {
            List<Guid> deviceGuids = [];

            // Prefer Gamepads, fall back to Joysticks
            foreach (
                var deviceInstance in _directInput.GetDevices(
                    DeviceType.Gamepad,
                    DeviceEnumerationFlags.AttachedOnly
                )
            )
                deviceGuids.Add(deviceInstance.InstanceGuid);

            if (deviceGuids.Count == 0)
                foreach (
                    var deviceInstance in _directInput.GetDevices(
                        DeviceType.Joystick,
                        DeviceEnumerationFlags.AttachedOnly
                    )
                )
                    deviceGuids.Add(deviceInstance.InstanceGuid);

            return deviceGuids;
        }

        private void SyncDevices()
        {
            List<Guid> connected;
            try
            {
                connected = FindConnectedDevices();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ControllerManager] Could not enumerate controllers");
                return;
            }

            lock (_slotsLock)
            {
                foreach (var controllerState in _controllerStates)
                {
                    if (
                        controllerState.DeviceGuid is Guid guid
                        && (controllerState.IsDeviceLost || !connected.Contains(guid))
                    )
                    {
                        controllerState.Detach();
                        _logger.LogInformation(
                            "[ControllerManager] Controller disconnected from player {Player}",
                            controllerState.GetIndex() + 1
                        );
                    }
                }

                foreach (var guid in connected)
                {
                    if (_controllerStates.Exists(c => c.DeviceGuid == guid))
                        continue;

                    var slot = FindSlotFor(guid);
                    try
                    {
                        slot.Attach(new Joystick(_directInput, guid), guid);
                        _slotByDevice[guid] = slot.GetIndex();
                        _logger.LogInformation(
                            "[ControllerManager] Controller connected as player {Player}",
                            slot.GetIndex() + 1
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "[ControllerManager] Could not attach controller to player {Player}",
                            slot.GetIndex() + 1
                        );
                    }
                }
            }
        }

        private ControllerState FindSlotFor(Guid guid)
        {
            if (
                _slotByDevice.TryGetValue(guid, out int previousSlot)
                && previousSlot < _controllerStates.Count
                && !_controllerStates[previousSlot].HasDevice
            )
                return _controllerStates[previousSlot];

            var freeSlot = _controllerStates.Find(c =>
                !c.HasDevice && !_slotByDevice.ContainsValue(c.GetIndex())
            );
            freeSlot ??= _controllerStates.Find(c => !c.HasDevice);

            return freeSlot ?? AddSlot();
        }

        private void StartDeviceWatcher()
        {
            var token = _deviceWatcherCts.Token;
            var thread = new Thread(() =>
            {
                while (!token.WaitHandle.WaitOne(DeviceScanIntervalMs))
                    SyncDevices();
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "ControllerDeviceWatcher",
            };
            thread.Start();
        }

        public void StartPolling()
        {
            foreach (var controllerState in _controllerStates)
                controllerState.StartPolling(_pollingRate);
        }

        public void StopPolling()
        {
            foreach (var controllerState in _controllerStates)
                controllerState.StopPolling();
        }

        public int[] GetEitherLeftStickDirection()
        {
            int[] result = new int[2];
            foreach (var controllerState in _controllerStates)
            {
                int[] dir = controllerState.GetLeftStickDirection();
                if (dir[0] != 0 || dir[1] != 0)
                {
                    result[0] = dir[0];
                    result[1] = dir[1];
                    return result;
                }
            }

            return result;
        }

        public int[] GetPlayerLeftStickDirection(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex >= _controllerStates.Count)
                return new int[2];

            return _controllerStates[playerIndex].GetLeftStickDirection();
        }

        public bool GetEitherButtonState(ControllerState.ControllerActions button)
        {
            foreach (var controllerState in _controllerStates)
                if (controllerState.GetButtonState(button))
                    return true;

            return false;
        }

        public bool GetPlayerButtonState(int playerIndex, ControllerState.ControllerActions button)
        {
            if (playerIndex < 0 || playerIndex >= _controllerStates.Count)
                return false;

            return _controllerStates[playerIndex].GetButtonState(button);
        }

        public bool GetEitherButtonDownState(ControllerState.ControllerActions button)
        {
            foreach (var controllerState in _controllerStates)
                if (controllerState.GetButtonDownState(button))
                    return true;

            return false;
        }

        public bool GetPlayerButtonDownState(
            int playerIndex,
            ControllerState.ControllerActions button
        )
        {
            if (playerIndex < 0 || playerIndex >= _controllerStates.Count)
                return false;

            return _controllerStates[playerIndex].GetButtonDownState(button);
        }

        public void ReleaseAllButtons()
        {
            foreach (var controllerState in _controllerStates)
                controllerState.ReleaseButtons();
        }

        public void ReleasePlayerButtons(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex >= _controllerStates.Count)
                return;
            _controllerStates[playerIndex].ReleaseButtons();
        }

        public int GetExitButtonHeldFor()
        {
            int maxHeldFor = 0;

            foreach (var controllerState in _controllerStates)
            {
                int heldFor = controllerState.GetExitButtonHeldFor();
                if (heldFor > maxHeldFor)
                    maxHeldFor = heldFor;
            }

            return maxHeldFor;
        }

        public void ToggleKeymapping()
        {
            _isKeymapping = !_isKeymapping;

            foreach (var controllerState in _controllerStates)
                controllerState.SetKeymapping(_isKeymapping);
        }

        public void ToggleDebugMode()
        {
            _debugMode = !_debugMode;
            _logger.LogInformation($"[ControllerManager] Debug Mode: {_debugMode}");

            foreach (var controllerState in _controllerStates)
                controllerState.SetDebugMode(_debugMode);

            if (_debugMode)
                StartDebugMode();
        }

        // Send a key press (Keymapping)
        private void SendKey(string key, bool state)
        {
            try
            {
                // send numlock key press
                if (((ushort)GetAsyncKeyState(0x90) & 0xffff) != 0)
                {
                    keyboard.Send(Keyboard.ScanCodeShort.NUMLOCK);
                    keyboard.Release(Keyboard.ScanCodeShort.NUMLOCK);
                }

                // map the string to the key
                if (Enum.TryParse(key, out Keyboard.ScanCodeShort keyCode))
                {
                    // send the key press
                    if (state)
                        keyboard.Send(keyCode);
                    else
                        keyboard.Release(keyCode);
                }
            }
            catch
            {
                // Suppress input errors to prevent crash loop
            }
        }

        public int GetControllerCount()
        {
            return _controllerStates.Count;
        }

        private void ListenForDebugKey()
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    bool _debugKeyState =
                        (GetAsyncKeyState((int)Keyboard.VirtualKeyShort.F4) & 0x8000) != 0;

                    if (!_debugKeyState && _debugKeyDown)
                        ToggleDebugMode();

                    _debugKeyDown = _debugKeyState;

                    Thread.Sleep(_pollingRate);
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "DebugKeyListener",
            };
            thread.Start();
        }

        // Debug mode input listener
        private void StartDebugMode()
        {
            if (_debugModeRunning)
                return;

            var thread = new Thread(() =>
            {
                _debugModeRunning = true;

                while (_debugMode)
                {
                    foreach (var controllerState in _controllerStates)
                    {
                        Keybinds keybinds = controllerState.keybinds;

                        // --- Directional Input ---
                        int dirX = 0;
                        int dirY = 0;

                        // Up
                        if (((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.Up)) & 0x8000) != 0)
                            dirY = -1;

                        // Down
                        if (((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.Down)) & 0x8000) != 0)
                            dirY = 1;

                        // Left
                        if (((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.Left)) & 0x8000) != 0)
                            dirX = -1;

                        // Right
                        if (((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.Right)) & 0x8000) != 0)
                            dirX = 1;

                        controllerState.SetDebugStickState(dirX, dirY);

                        // --- Button Input ---

                        // Exit
                        controllerState.SetDebugAction(
                            ControllerState.ControllerActions.Exit,
                            ((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.Exit)) & 0x8000) != 0
                        );

                        // Start
                        controllerState.SetDebugAction(
                            ControllerState.ControllerActions.Start,
                            ((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.Start)) & 0x8000) != 0
                        );

                        // A
                        controllerState.SetDebugAction(
                            ControllerState.ControllerActions.A,
                            ((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.A)) & 0x8000) != 0
                        );

                        // B
                        controllerState.SetDebugAction(
                            ControllerState.ControllerActions.B,
                            ((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.B)) & 0x8000) != 0
                        );

                        // C
                        controllerState.SetDebugAction(
                            ControllerState.ControllerActions.C,
                            ((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.C)) & 0x8000) != 0
                        );

                        // D
                        controllerState.SetDebugAction(
                            ControllerState.ControllerActions.D,
                            ((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.D)) & 0x8000) != 0
                        );

                        // E
                        controllerState.SetDebugAction(
                            ControllerState.ControllerActions.E,
                            ((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.E)) & 0x8000) != 0
                        );

                        // F
                        controllerState.SetDebugAction(
                            ControllerState.ControllerActions.F,
                            ((ushort)GetAsyncKeyState(GetVirtualKey(keybinds.F)) & 0x8000) != 0
                        );
                    }

                    Thread.Sleep(_pollingRate);
                }

                _debugModeRunning = false;
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "DebugModeInputListener",
            };
            thread.Start();
        }

        public void UpdateMapping(ControllerMapping mapping)
        {
            _logger.LogInformation("[ControllerManager] Updating controller mappings...");
            _mapping = mapping;
            foreach (var controllerState in _controllerStates)
                controllerState.UpdateMapping(mapping);
        }

        private static int GetVirtualKey(string key)
        {
            if (Enum.TryParse(key, true, out Keyboard.VirtualKeyShort result))
                return (int)result;

            if (Enum.TryParse("KEY_" + key, true, out Keyboard.VirtualKeyShort resultPrefix))
                return (int)resultPrefix;

            return 0;
        }
    }
}
