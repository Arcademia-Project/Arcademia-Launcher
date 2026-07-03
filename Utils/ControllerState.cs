using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArcademiaGameLauncher.Models;
using Newtonsoft.Json.Linq;
using SharpDX.DirectInput;

namespace ArcademiaGameLauncher.Utils
{
    internal class ControllerState
    {
        public Keybinds keybinds;
        public bool _isKeymapping;
        public bool _debugMode;

        private readonly int index;
        private readonly bool[] buttonStates;
        private readonly bool[] buttonDownStates;
        private readonly bool[] buttonUpStates;
        private int leftStickX;
        private int leftStickY;
        private readonly int[] direction;
        private volatile int exitButtonHeldFor;

        private CancellationTokenSource _cts;
        private Thread _pollingThread;
        private int _pollingRate;

        // Deadzone and Midpoint values for the joystick
        readonly int joystickDeadzone = 7700;
        readonly int joystickMidpoint = 32511;

        public Joystick joystick;
        public JoystickState state;

        private readonly Action _onInputDetected;
        private readonly Action<string, bool> _onSendKey;

        // STATE TRACKING TO PREVENT FLOODING
        private bool _lastSentLeft = false;
        private bool _lastSentRight = false;
        private bool _lastSentUp = false;
        private bool _lastSentDown = false;
        private readonly Dictionary<string, bool> _lastActionStates = [];

        private readonly Dictionary<int, string> _buttonActionMap = [];

        private void UpdateActionState(string action)
        {
            bool isActionPressed = false;

            // Check if ANY button mapped to this action is currently pressed
            foreach (var kvp in _buttonActionMap)
            {
                if (
                    !string.IsNullOrWhiteSpace(kvp.Value)
                    && kvp.Value.Equals(action, StringComparison.OrdinalIgnoreCase)
                )
                {
                    if (buttonStates[kvp.Key])
                    {
                        isActionPressed = true;
                        break;
                    }
                }
            }

            // Check if the state has changed
            if (!_lastActionStates.ContainsKey(action))
                _lastActionStates[action] = false;

            bool wasActionPressed = _lastActionStates[action];

            if (isActionPressed != wasActionPressed)
            {
                // State changed, send the key
                _lastActionStates[action] = isActionPressed;

                if (isActionPressed)
                    _onInputDetected?.Invoke();

                switch (action.ToLower())
                {
                    case "exit":
                        _onSendKey?.Invoke(keybinds.Exit, isActionPressed);
                        break;
                    case "start":
                        _onSendKey?.Invoke(keybinds.Start, isActionPressed);
                        break;
                    case "a":
                        _onSendKey?.Invoke(keybinds.A, isActionPressed);
                        break;
                    case "b":
                        _onSendKey?.Invoke(keybinds.B, isActionPressed);
                        break;
                    case "c":
                        _onSendKey?.Invoke(keybinds.C, isActionPressed);
                        break;
                    case "d":
                        _onSendKey?.Invoke(keybinds.D, isActionPressed);
                        break;
                    case "e":
                        _onSendKey?.Invoke(keybinds.E, isActionPressed);
                        break;
                    case "f":
                        _onSendKey?.Invoke(keybinds.F, isActionPressed);
                        break;
                }
            }
        }

        public void UpdateMapping(ControllerMapping mapping)
        {
            if (mapping == null)
                return;

            _buttonActionMap[0] = mapping.Button0?.Trim().ToLower();
            _buttonActionMap[1] = mapping.Button1?.Trim().ToLower();
            _buttonActionMap[2] = mapping.Button2?.Trim().ToLower();
            _buttonActionMap[3] = mapping.Button3?.Trim().ToLower();
            _buttonActionMap[4] = mapping.Button4?.Trim().ToLower();
            _buttonActionMap[5] = mapping.Button5?.Trim().ToLower();
            _buttonActionMap[6] = mapping.Button6?.Trim().ToLower();
            _buttonActionMap[7] = mapping.Button7?.Trim().ToLower();
        }

        private void InitializeDefaultMapping()
        {
            _buttonActionMap[0] = "exit";
            _buttonActionMap[1] = "start";
            _buttonActionMap[2] = "a";
            _buttonActionMap[3] = "b";
            _buttonActionMap[4] = "c";
            _buttonActionMap[5] = "d";
            _buttonActionMap[6] = "e";
            _buttonActionMap[7] = "f";
        }

        public enum ControllerActions
        {
            Exit = 0,
            Start = 1,
            A = 2,
            B = 3,
            C = 4,
            D = 5,
            E = 6,
            F = 7,
        }

        public ControllerState(
            Joystick _joystick,
            int _index,
            Action onInputDetected,
            Action<string, bool> onSendKey
        )
        {
            // Set the joystick
            joystick = _joystick;
            // Set the index of the controller
            index = _index;
            // Set the input detected callback
            _onInputDetected = onInputDetected;
            _onSendKey = onSendKey;
            _isKeymapping = true;
            _debugMode = false;

            // Set the keybinds for the player
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                const string resourceName = "ArcademiaGameLauncher.Assets.json.ButtonConfig.json";

                // Read embedded JSON resource from assembly
                using Stream stream =
                    assembly.GetManifestResourceStream(resourceName)
                    ?? throw new FileNotFoundException(
                        "Embedded resource not found.",
                        resourceName
                    );
                using StreamReader reader = new(stream, Encoding.UTF8);
                string json = reader.ReadToEnd();

                JObject playerControls = JObject.Parse(json);
                keybinds = new Keybinds(playerControls, index);
            }
            catch { }

            direction = new int[2];

            // Initialize the button states
            state = new JoystickState();
            buttonStates = new bool[128];
            buttonDownStates = new bool[128];
            buttonUpStates = new bool[128];
            try
            {
                state = joystick.GetCurrentState();
            }
            catch { }

            InitializeDefaultMapping();
            try
            {
                // Try to load from file
                string mappingPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "ControllerMapping.json"
                );
                if (File.Exists(mappingPath))
                {
                    string json = File.ReadAllText(mappingPath);
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    };
                    var mapping = System.Text.Json.JsonSerializer.Deserialize<ControllerMapping>(
                        json
                    );
                    if (mapping != null)
                        UpdateMapping(mapping);
                }
            }
            catch { }
        }

        public void StartPolling(int pollingRate)
        {
            _pollingRate = pollingRate;

            if (_pollingThread != null && _pollingThread.IsAlive)
                return;

            _cts = new CancellationTokenSource();
            _pollingThread = new Thread(() =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    UpdateButtonStates();

                    // WaitOne blocking is more stable and has lower latency than await Task.Delay for input loops
                    if (_cts.Token.WaitHandle.WaitOne(_pollingRate))
                        break;
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = $"ControllerPolling_{index}",
            };

            _pollingThread.Start();
        }

        public void StopPolling()
        {
            _cts?.Cancel();
            try
            {
                _pollingThread?.Join(500);
            }
            catch { }
        }

        private void UpdateButtonStates()
        {
            try
            {
                // Poll the joystick for the current state
                joystick.Poll();
                state = joystick.GetCurrentState();

                // Update the joystick states
                SetLeftStickDirection(state.X, state.Y);

                // Update the button states
                for (int i = 0; i < buttonStates.Length; i++)
                    SetButtonState(i, state.Buttons[i]);

                // Update the exit button held for time
                bool isExitDown = false;
                foreach (var kvp in _buttonActionMap)
                {
                    if (
                        string.Equals(kvp.Value, "exit", StringComparison.OrdinalIgnoreCase)
                        && buttonStates[kvp.Key]
                    )
                    {
                        isExitDown = true;
                        break;
                    }
                }

                if (isExitDown)
                    exitButtonHeldFor += _pollingRate;
                else
                    exitButtonHeldFor = 0;
            }
            catch
            {
                // Handle joystick disconnects gracefully
            }
        }

        // Getter and Setter for the joystick direction
        public int[] GetLeftStickDirection() => direction;

        private void SetLeftStickDirection(int _x, int _y)
        {
            if (_debugMode)
                return;

            leftStickX = _x;
            leftStickY = _y;

            // --- HORIZONTAL AXIS ---
            bool reqLeft = false;
            bool reqRight = false;

            if (leftStickX > joystickMidpoint + joystickDeadzone)
            {
                direction[0] = 1;
                reqRight = true;
                if (_isKeymapping)
                    _onInputDetected?.Invoke();
            }
            else if (leftStickX < joystickMidpoint - joystickDeadzone)
            {
                direction[0] = -1;
                reqLeft = true;
                if (_isKeymapping)
                    _onInputDetected?.Invoke();
            }
            else
            {
                direction[0] = 0;
            }

            if (_isKeymapping)
            {
                if (reqLeft != _lastSentLeft)
                {
                    _onSendKey?.Invoke(keybinds.Left, reqLeft);
                    _lastSentLeft = reqLeft;
                }
                if (reqRight != _lastSentRight)
                {
                    _onSendKey?.Invoke(keybinds.Right, reqRight);
                    _lastSentRight = reqRight;
                }
            }

            bool reqUp = false;
            bool reqDown = false;

            if (leftStickY > joystickMidpoint + joystickDeadzone)
            {
                direction[1] = 1;
                reqDown = true;
                if (_isKeymapping)
                    _onInputDetected?.Invoke();
            }
            else if (leftStickY < joystickMidpoint - joystickDeadzone)
            {
                direction[1] = -1;
                reqUp = true;
                if (_isKeymapping)
                    _onInputDetected?.Invoke();
            }
            else
            {
                direction[1] = 0;
            }

            if (_isKeymapping)
            {
                if (reqUp != _lastSentUp)
                {
                    _onSendKey?.Invoke(keybinds.Up, reqUp);
                    _lastSentUp = reqUp;
                }
                if (reqDown != _lastSentDown)
                {
                    _onSendKey?.Invoke(keybinds.Down, reqDown);
                    _lastSentDown = reqDown;
                }
            }
        }

        // Getter and Setter for the button states
        public bool GetButtonState(ControllerActions action)
        {
            string actionName = action.ToString().ToLower();
            foreach (var kvp in _buttonActionMap)
            {
                if (kvp.Value == actionName)
                {
                    if (buttonStates[kvp.Key])
                        return true;
                }
            }
            return false;
        }

        public bool GetButtonDownState(ControllerActions action)
        {
            string actionName = action.ToString().ToLower();
            foreach (var kvp in _buttonActionMap)
            {
                if (kvp.Value == actionName)
                {
                    if (buttonDownStates[kvp.Key])
                        return true;
                }
            }
            return false;
        }

        public void SetButtonState(int _button, bool _buttonState)
        {
            if (_debugMode)
                return;

            InternalSetButtonState(_button, _buttonState);
        }

        private void InternalSetButtonState(int _button, bool _buttonState)
        {
            if (!buttonStates[_button] && _buttonState)
                buttonDownStates[_button] = true;
            else
                buttonDownStates[_button] = false;

            if (buttonStates[_button] && !_buttonState)
                buttonUpStates[_button] = true;
            else
                buttonUpStates[_button] = false;

            buttonStates[_button] = _buttonState;

            // Process the action associated with this button
            if (
                _isKeymapping
                && _buttonActionMap.TryGetValue(_button, out string action)
                && !string.IsNullOrWhiteSpace(action)
            )
            {
                UpdateActionState(action);
            }
        }

        public void ReleaseButtons()
        {
            // Release all physical buttons
            for (int i = 0; i < buttonStates.Length; i++)
            {
                buttonStates[i] = false;
                buttonDownStates[i] = false;
                buttonUpStates[i] = false;
            }

            // Release all logical actions
            var actions = new List<string>(_lastActionStates.Keys);
            foreach (var action in actions)
            {
                if (_lastActionStates[action])
                {
                    // Force release
                    _lastActionStates[action] = false;
                    switch (action.ToLower())
                    {
                        case "exit":
                            _onSendKey?.Invoke(keybinds.Exit, false);
                            break;
                        case "start":
                            _onSendKey?.Invoke(keybinds.Start, false);
                            break;
                        case "a":
                            _onSendKey?.Invoke(keybinds.A, false);
                            break;
                        case "b":
                            _onSendKey?.Invoke(keybinds.B, false);
                            break;
                        case "c":
                            _onSendKey?.Invoke(keybinds.C, false);
                            break;
                        case "d":
                            _onSendKey?.Invoke(keybinds.D, false);
                            break;
                        case "e":
                            _onSendKey?.Invoke(keybinds.E, false);
                            break;
                        case "f":
                            _onSendKey?.Invoke(keybinds.F, false);
                            break;
                    }
                }
            }
            _lastActionStates.Clear();

            // Release Directionals
            if (_lastSentLeft)
            {
                _onSendKey?.Invoke(keybinds.Left, false);
                _lastSentLeft = false;
            }
            if (_lastSentRight)
            {
                _onSendKey?.Invoke(keybinds.Right, false);
                _lastSentRight = false;
            }
            if (_lastSentUp)
            {
                _onSendKey?.Invoke(keybinds.Up, false);
                _lastSentUp = false;
            }
            if (_lastSentDown)
            {
                _onSendKey?.Invoke(keybinds.Down, false);
                _lastSentDown = false;
            }
        }

        public int GetExitButtonHeldFor() => exitButtonHeldFor;

        public void SetKeymapping(bool isKeymapping)
        {
            _isKeymapping = isKeymapping;

            if (!isKeymapping)
                ReleaseButtons();
        }

        public void SetDebugMode(bool debugMode)
        {
            _debugMode = debugMode;
            ReleaseButtons();
        }

        public void SetDebugStickState(int dirX, int dirY)
        {
            if (!_debugMode)
                return;

            direction[0] = dirX;
            direction[1] = dirY;

            bool reqLeft = dirX == -1;
            bool reqRight = dirX == 1;
            bool reqUp = dirY == -1;
            bool reqDown = dirY == 1;

            if ((reqLeft || reqRight || reqUp || reqDown) && _isKeymapping)
                _onInputDetected?.Invoke();

            if (_isKeymapping)
            {
                if (reqLeft != _lastSentLeft)
                {
                    _onSendKey?.Invoke(keybinds.Left, reqLeft);
                    _lastSentLeft = reqLeft;
                }
                if (reqRight != _lastSentRight)
                {
                    _onSendKey?.Invoke(keybinds.Right, reqRight);
                    _lastSentRight = reqRight;
                }
                if (reqUp != _lastSentUp)
                {
                    _onSendKey?.Invoke(keybinds.Up, reqUp);
                    _lastSentUp = reqUp;
                }
                if (reqDown != _lastSentDown)
                {
                    _onSendKey?.Invoke(keybinds.Down, reqDown);
                    _lastSentDown = reqDown;
                }
            }
        }

        public void SetDebugAction(ControllerActions action, bool state)
        {
            if (!_debugMode)
                return;

            string actionName = action.ToString().ToLower();

            // Find ALL buttons mapped to this action
            foreach (var kvp in _buttonActionMap)
            {
                if (
                    !string.IsNullOrWhiteSpace(kvp.Value)
                    && kvp.Value.Equals(actionName, StringComparison.OrdinalIgnoreCase)
                )
                {
                    // Update the physical button state, which will trigger UpdateActionState
                    InternalSetButtonState(kvp.Key, state);
                }
            }
        }

        // Getter for the index
        public int GetIndex() => index;
    }

    struct Keybinds
    {
        public string Up;
        public string Left;
        public string Down;
        public string Right;

        public string Exit;
        public string Start;
        public string A;
        public string B;
        public string C;
        public string D;
        public string E;
        public string F;

        public Keybinds(JObject _playerControls, int _index)
        {
            Up = _playerControls["Up"]?[_index]?.ToString() ?? "UP";
            Left = _playerControls["Left"]?[_index]?.ToString() ?? "LEFT";
            Down = _playerControls["Down"]?[_index]?.ToString() ?? "DOWN";
            Right = _playerControls["Right"]?[_index]?.ToString() ?? "RIGHT";

            Exit = _playerControls["Exit"]?[_index]?.ToString() ?? "ESCAPE";
            Start = _playerControls["Start"]?[_index]?.ToString() ?? "RETURN";
            A = _playerControls["A"]?[_index]?.ToString() ?? "Z";
            B = _playerControls["B"]?[_index]?.ToString() ?? "X";
            C = _playerControls["C"]?[_index]?.ToString() ?? "C";
            D = _playerControls["D"]?[_index]?.ToString() ?? "V";
            E = _playerControls["E"]?[_index]?.ToString() ?? "B";
            F = _playerControls["F"]?[_index]?.ToString() ?? "N";
        }
    }
}
