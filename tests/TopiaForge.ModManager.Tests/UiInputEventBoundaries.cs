namespace TopiaForge.ModManager.Tests
{
    internal static class UiInputEventBoundaries
    {
        internal const string Sources = """
            using System;
            using System.Collections.Generic;
            using UnityEngine;
            using UnityEngine.InputSystem;
            using UnityEngine.InputSystem.Controls;
            using UnityEngine.InputSystem.LowLevel;

            namespace UnityEngine.InputSystem
            {
                internal enum Key { None, Space = 1, Enter = 2, Tab, A = 15, D, W, S, Digit1 = 40, Escape = 60, LeftArrow, RightArrow, UpArrow, DownArrow, NumpadEnter = 75, F8 = 95 }
                internal enum InputDeviceChange { Removed, Disconnected, SoftReset, HardReset }
                internal class InputControl { }
                internal class InputDevice { internal int deviceId; }
                internal static class InputSystem
                {
                    internal static event Action<InputEventPtr, InputDevice>? onEvent;
                    internal static event Action<InputDevice, InputDeviceChange>? onDeviceChange;

                    internal static void Deliver(InputEventPtr packet, InputDevice device)
                    {
                        onEvent?.Invoke(packet, device);
                        packet.Apply();
                    }

                    internal static void Change(InputDevice device, InputDeviceChange change) => onDeviceChange?.Invoke(device, change);
                }
                internal sealed class Keyboard : InputDevice
                {
                    internal static Keyboard? current;
                    internal readonly KeyControl escape = new(Key.Escape), enter = new(Key.Enter), digit1 = new(Key.Digit1), gallery = new(Key.F8);
                    internal readonly KeyControl upArrowKey = new(Key.UpArrow), downArrowKey = new(Key.DownArrow),
                        leftArrowKey = new(Key.LeftArrow), rightArrowKey = new(Key.RightArrow), tabKey = new(Key.Tab),
                        numpadEnterKey = new(Key.NumpadEnter), spaceKey = new(Key.Space);
                    internal readonly ButtonControl anyKey = new();
                    internal readonly List<KeyControl> allKeys;
                    internal Keyboard(int id)
                    {
                        deviceId = id;
                        current = this;
                        allKeys = new() { escape, enter, digit1, gallery, upArrowKey, downArrowKey, leftArrowKey,
                            rightArrowKey, tabKey, numpadEnterKey, spaceKey };
                    }
                    internal KeyControl escapeKey => escape;
                    internal KeyControl enterKey => enter;
                    internal KeyControl this[Key key] => allKeys.Find(control => control.keyCode == key)!;
                }
                internal sealed class Gamepad : InputDevice
                {
                    internal readonly ButtonControl buttonEast = new(), buttonSouth = new(), buttonWest = new(), buttonNorth = new(),
                        leftShoulder = new(), rightShoulder = new(), selectButton = new(), startButton = new(), leftStickButton = new(), rightStickButton = new(),
                        leftTrigger = new(), rightTrigger = new();
                    internal readonly Vector2Control leftStick = new(), rightStick = new();
                    internal readonly DpadControl dpad = new();
                    internal Gamepad(int id) { deviceId = id; }
                }
                internal sealed class Mouse : InputDevice
                {
                    internal readonly ButtonControl leftButton = new(), rightButton = new(), middleButton = new(), backButton = new(), forwardButton = new();
                    internal readonly Vector2Control scroll = new(), delta = new();
                    internal Mouse(int id) { deviceId = id; }
                }
            }

            namespace UnityEngine.InputSystem.Controls
            {
                internal class AxisControl : InputControl
                {
                    internal float Current;
                    internal bool ReadValueFromEvent(InputEventPtr input, out float value) => input.TryRead(this, out value);
                    internal float ReadValue() => Current;
                    internal void ResetToDefaultStateInEvent(InputEventPtr input) => input.Reset(this);
                }
                internal class ButtonControl : AxisControl
                {
                    internal bool IsValueConsideredPressed(float value) => value >= .5f;
                    internal bool isPressed => IsValueConsideredPressed(Current);
                    internal bool wasPressedThisFrame => isPressed;
                }
                internal sealed class KeyControl : ButtonControl
                {
                    internal Key keyCode;
                    internal KeyControl(Key key) { keyCode = key; }
                }
                internal class Vector2Control : InputControl
                {
                    internal readonly AxisControl x = new(), y = new();
                    internal Vector2 ReadValue() => new(x.Current, y.Current);
                    internal bool ReadValueFromEvent(InputEventPtr input, out Vector2 value)
                    {
                        var hasX = x.ReadValueFromEvent(input, out var horizontal);
                        var hasY = y.ReadValueFromEvent(input, out var vertical);
                        value = new(horizontal, vertical);
                        return hasX || hasY;
                    }
                    internal void ResetToDefaultStateInEvent(InputEventPtr input)
                    {
                        if (x.ReadValueFromEvent(input, out _)) x.ResetToDefaultStateInEvent(input);
                        if (y.ReadValueFromEvent(input, out _)) y.ResetToDefaultStateInEvent(input);
                    }
                }
                internal sealed class DpadControl : Vector2Control { }
            }

            namespace UnityEngine.InputSystem.LowLevel
            {
                internal struct StateEvent { }
                internal struct DeltaStateEvent { }
                internal struct DeviceResetEvent { }
                internal readonly struct InputEventPtr
                {
                    private readonly Type type;
                    private readonly Dictionary<InputControl, float> values;
                    internal InputEventPtr(Type type, params (InputControl Control, float Value)[] controls)
                    {
                        this.type = type;
                        values = new();
                        foreach (var control in controls) values[control.Control] = control.Value;
                    }
                    internal bool IsA<T>() => type == typeof(T);
                    internal float Value(InputControl control) => values[control];
                    internal bool TryRead(InputControl control, out float value) => values.TryGetValue(control, out value);
                    internal void Reset(InputControl control) => values[control] = 0f;

                    internal void Apply()
                    {
                        foreach (var (control, value) in values)
                            if (control is AxisControl axis) axis.Current = value;
                    }
                }
            }

            """;
    }
}
