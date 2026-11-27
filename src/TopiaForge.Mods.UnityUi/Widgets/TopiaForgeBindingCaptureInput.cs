using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace TopiaForge.Mods.UnityUi
{
    // This runs before the normal kit filter and before Input System actions read the event.
    // Raw capture and normal Back/navigation use separate presses and separate gates.
    internal static class TopiaForgeBindingCaptureInput
    {
        private const int PadAxisStart = 16;
        private const int Dpad = 24;

        private static readonly TopiaForgeInputGate Gate = new TopiaForgeInputGate();
        private static readonly Dictionary<string, int> KeyCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly InputBinding[] Keys = CreateKeyboardBindings();
        private static readonly InputBinding[] MouseButtons = Array.ConvertAll((InputMouseButton[])Enum.GetValues(typeof(InputMouseButton)), InputBinding.MouseButton);
        private static readonly InputBinding[] PadButtons = Array.ConvertAll((InputGamepadButton[])Enum.GetValues(typeof(InputGamepadButton)), InputBinding.GamepadButton);
        private static readonly InputBinding[] PadAxes = Array.ConvertAll((InputGamepadAxis[])Enum.GetValues(typeof(InputGamepadAxis)), axis => InputBinding.GamepadAxis(axis));
        private static readonly InputBinding MouseWheel = InputBinding.Axis(InputAxis.Scroll);

        private static TopiaForgeInputBindingField? activeField;
        private static InputBinding? pending;
        private static int pendingDevice;
        private static int lastCaptureFrame = int.MinValue;
        private static int lastWheelFrame = int.MinValue;
        private static int lastPointerFrame = int.MinValue;

        internal static bool Capturing => activeField?.CanReceiveCapture == true;
        internal static bool SuppressesHotkeys => Capturing || lastCaptureFrame == Time.frameCount;
        internal static bool HoldsKey(int key) => Gate.HeldOn(TopiaForgeInputDevice.Keyboard, key);

        internal static void Begin(TopiaForgeInputBindingField next)
        {
            if (!ReferenceEquals(activeField, next)) activeField?.CancelCapture();
            activeField = next;
            pending = null;
        }

        internal static void End(TopiaForgeInputBindingField previous)
        {
            if (!ReferenceEquals(activeField, previous)) return;
            activeField = null;
            pending = null;
            lastCaptureFrame = Time.frameCount;
        }

        internal static bool TryTake(TopiaForgeInputBindingField owner, out InputBinding binding)
        {
            if (ReferenceEquals(activeField, owner) && pending is { } next)
            {
                binding = next;
                pending = null;
                return true;
            }

            binding = default;
            return false;
        }

        internal static void Forget(int deviceId)
        {
            Gate.Forget(deviceId);
            if (deviceId == pendingDevice) pending = null;
        }

        internal static void Reset(bool failed = false)
        {
            var current = activeField;
            activeField = null;
            pending = null;
            Gate.ForgetAll();
            lastCaptureFrame = int.MinValue;
            lastWheelFrame = int.MinValue;
            lastPointerFrame = int.MinValue;
            if (failed) current?.CaptureBackendStopped();
            else current?.CancelCapture();
        }

        internal static void Filter(InputEventPtr eventPtr, InputDevice device)
        {
            var listener = Capturing ? activeField : null;
            var id = device.deviceId;
            if (listener == null && !Gate.HoldsOn(id)) return;

            switch (device)
            {
                case Keyboard keyboard:
                    FilterKeyboard(keyboard, eventPtr, listener);
                    break;
                case Gamepad gamepad:
                    FilterGamepad(gamepad, eventPtr, listener);
                    break;
                case Mouse mouse:
                    FilterMouse(mouse, eventPtr, listener);
                    break;
            }
        }

        // The SDK input sampler also reads Unity's legacy API. It uses the same physical ownership until release.
        internal static bool Took(InputBinding binding)
        {
            var control = binding.Control;
            if (string.IsNullOrEmpty(control)) return false;
            if (binding.Kind == InputBindingKind.Axis)
            {
                if (Is(control, nameof(InputAxis.Scroll))) return lastWheelFrame == Time.frameCount;
                if (Is(control, nameof(InputAxis.PointerX)) || Is(control, nameof(InputAxis.PointerY)))
                    return lastPointerFrame == Time.frameCount;
                if (!Gate.HoldsAny) return false;
                if (Is(control, nameof(InputAxis.Horizontal))) return HoldsKey((int)Key.A) || HoldsKey((int)Key.D)
                    || HoldsKey((int)Key.LeftArrow) || HoldsKey((int)Key.RightArrow) || HoldsPadAxis(InputGamepadAxis.LeftX)
                    || Gate.HeldOn(TopiaForgeInputDevice.Gamepad, Dpad);
                if (Is(control, nameof(InputAxis.Vertical))) return HoldsKey((int)Key.W) || HoldsKey((int)Key.S)
                    || HoldsKey((int)Key.UpArrow) || HoldsKey((int)Key.DownArrow) || HoldsPadAxis(InputGamepadAxis.LeftY)
                    || Gate.HeldOn(TopiaForgeInputDevice.Gamepad, Dpad);
                return false;
            }

            if (!Gate.HoldsAny) return false;

            return binding.Kind switch
            {
                InputBindingKind.Key => Is(control, "Esc") ? HoldsKey((int)Key.Escape)
                    : KeyCodes.TryGetValue(control, out var key) && HoldsKey(key),
                InputBindingKind.MouseButton => Enum.TryParse(control, true, out InputMouseButton button)
                    && Gate.HeldOn(TopiaForgeInputDevice.Other, (int)button),
                InputBindingKind.GamepadButton => Enum.TryParse(control, true, out InputGamepadButton button)
                    && Gate.HeldOn(TopiaForgeInputDevice.Gamepad, (int)button),
                InputBindingKind.GamepadAxis => Enum.TryParse(control, true, out InputGamepadAxis axis) && HoldsPadAxis(axis),
                _ => false,
            };
        }

        private static void FilterKeyboard(Keyboard keyboard, InputEventPtr eventPtr, TopiaForgeInputBindingField? listener)
        {
            var keys = keyboard.allKeys;
            for (var index = 0; index < keys.Count; index++)
            {
                var key = keys[index];
                if (key == null) continue;
                var code = (int)key.keyCode;
                var binding = code > 0 && code < Keys.Length ? Keys[code] : default;
                // Record the hold for legacy SDK sampling. The normal filter must still get the Back press.
                var back = key.keyCode == Key.Escape;
                Button(keyboard, key, eventPtr, TopiaForgeInputDevice.Keyboard, code, back ? default : binding, listener, mask: !back);
            }
        }

        private static void FilterGamepad(Gamepad pad, InputEventPtr eventPtr, TopiaForgeInputBindingField? listener)
        {
            Button(pad, pad.buttonEast, eventPtr, TopiaForgeInputDevice.Gamepad, (int)InputGamepadButton.East, default, listener, mask: false);
            PadButton(pad, pad.buttonSouth, eventPtr, InputGamepadButton.South, listener);
            PadButton(pad, pad.buttonWest, eventPtr, InputGamepadButton.West, listener);
            PadButton(pad, pad.buttonNorth, eventPtr, InputGamepadButton.North, listener);
            PadButton(pad, pad.leftShoulder, eventPtr, InputGamepadButton.LeftShoulder, listener);
            PadButton(pad, pad.rightShoulder, eventPtr, InputGamepadButton.RightShoulder, listener);
            PadButton(pad, pad.selectButton, eventPtr, InputGamepadButton.View, listener);
            PadButton(pad, pad.startButton, eventPtr, InputGamepadButton.Menu, listener);
            PadButton(pad, pad.leftStickButton, eventPtr, InputGamepadButton.LeftStick, listener);
            PadButton(pad, pad.rightStickButton, eventPtr, InputGamepadButton.RightStick, listener);
            Vector(pad, pad.leftStick, eventPtr, InputGamepadAxis.LeftX, InputGamepadAxis.LeftY, listener);
            Vector(pad, pad.rightStick, eventPtr, InputGamepadAxis.RightX, InputGamepadAxis.RightY, listener);
            FilterDpad(pad, eventPtr, listener);
            Trigger(pad, pad.leftTrigger, eventPtr, InputGamepadAxis.LeftTrigger, listener);
            Trigger(pad, pad.rightTrigger, eventPtr, InputGamepadAxis.RightTrigger, listener);
        }

        private static void FilterMouse(Mouse mouse, InputEventPtr eventPtr, TopiaForgeInputBindingField? listener)
        {
            MouseButton(mouse, mouse.leftButton, eventPtr, InputMouseButton.Primary, listener);
            MouseButton(mouse, mouse.rightButton, eventPtr, InputMouseButton.Secondary, listener);
            MouseButton(mouse, mouse.middleButton, eventPtr, InputMouseButton.Middle, listener);
            MouseButton(mouse, mouse.backButton, eventPtr, InputMouseButton.Back, listener);
            MouseButton(mouse, mouse.forwardButton, eventPtr, InputMouseButton.Forward, listener);
            if (listener == null) return;

            // Scrolling is deliberate; ordinary cursor motion does not select a new binding.
            if (mouse.scroll.y.ReadValueFromEvent(eventPtr, out float wheel) && wheel != 0f)
            {
                mouse.scroll.y.ResetToDefaultStateInEvent(eventPtr);
                lastWheelFrame = Time.frameCount;
                Publish(mouse.deviceId, listener, MouseWheel);
            }
            if (mouse.scroll.x.ReadValueFromEvent(eventPtr, out float horizontalWheel) && horizontalWheel != 0f)
                mouse.scroll.x.ResetToDefaultStateInEvent(eventPtr);
            if (mouse.delta.x.ReadValueFromEvent(eventPtr, out float dx) && dx != 0f)
            {
                mouse.delta.x.ResetToDefaultStateInEvent(eventPtr);
                lastPointerFrame = Time.frameCount;
            }
            if (mouse.delta.y.ReadValueFromEvent(eventPtr, out float dy) && dy != 0f)
            {
                mouse.delta.y.ResetToDefaultStateInEvent(eventPtr);
                lastPointerFrame = Time.frameCount;
            }
        }

        private static void PadButton(Gamepad pad, ButtonControl control, InputEventPtr eventPtr,
            InputGamepadButton button, TopiaForgeInputBindingField? listener) => Button(pad, control, eventPtr,
                TopiaForgeInputDevice.Gamepad, (int)button, PadButtons[(int)button], listener);

        private static void MouseButton(Mouse mouse, ButtonControl control, InputEventPtr eventPtr,
            InputMouseButton button, TopiaForgeInputBindingField? listener) => Button(mouse, control, eventPtr,
                TopiaForgeInputDevice.Other, (int)button, MouseButtons[(int)button], listener);

        private static void Button(InputDevice device, ButtonControl control, InputEventPtr eventPtr,
            TopiaForgeInputDevice kind, int index, InputBinding binding, TopiaForgeInputBindingField? listener, bool mask = true)
        {
            if (control == null || !control.ReadValueFromEvent(eventPtr, out float value)) return;
            var down = control.IsValueConsideredPressed(value) ? 1f : 0f;
            var before = control.isPressed || TopiaForgeInputOwnership.HoldsControl(device, control) ? 1f : 0f;
            if (Take(device, kind, index, down, before, listener, binding) && mask) control.ResetToDefaultStateInEvent(eventPtr);
        }

        private static void Trigger(Gamepad pad, AxisControl control, InputEventPtr eventPtr, InputGamepadAxis axis,
            TopiaForgeInputBindingField? listener)
        {
            if (control != null && control.ReadValueFromEvent(eventPtr, out float value) && Take(pad, TopiaForgeInputDevice.Gamepad,
                PadAxisStart + (int)axis, Math.Abs(value), Math.Abs(control.ReadValue()), listener, PadAxes[(int)axis]))
                control.ResetToDefaultStateInEvent(eventPtr);
        }

        private static void FilterDpad(Gamepad pad, InputEventPtr eventPtr, TopiaForgeInputBindingField? listener)
        {
            // Some devices encode all four directions in a single hat. Preserve ownership until the hat returns to center.
            var control = pad.dpad;
            if (control == null || !control.ReadValueFromEvent(eventPtr, out Vector2 value)) return;
            var before = TopiaForgeInputOwnership.HoldsControl(pad, control) ? 1f : control.ReadValue().magnitude;
            if (Take(pad, TopiaForgeInputDevice.Gamepad, Dpad, value.magnitude, before, listener, default))
                control.ResetToDefaultStateInEvent(eventPtr);
        }

        private static void Vector(Gamepad pad, Vector2Control control, InputEventPtr eventPtr, InputGamepadAxis horizontal,
            InputGamepadAxis vertical, TopiaForgeInputBindingField? listener)
        {
            if (control == null) return;
            // Read both components before changing the event. A DeltaStateEvent may contain only one axis.
            var hasX = control.x.ReadValueFromEvent(eventPtr, out float x);
            var hasY = control.y.ReadValueFromEvent(eventPtr, out float y);
            if (!hasX && !hasY) return;

            var normalHeld = TopiaForgeInputOwnership.HoldsControl(pad, control);
            var beforeX = normalHeld ? 1f : Math.Abs(control.x.ReadValue());
            var beforeY = normalHeld ? 1f : Math.Abs(control.y.ReadValue());
            var magnitudeX = Math.Abs(x);
            var magnitudeY = Math.Abs(y);
            var gamepad = TopiaForgeInputDevice.Gamepad;
            var xBegan = false;
            var yBegan = false;
            var xTaken = hasX && Gate.TakeForBindingCapture(pad.deviceId, gamepad, PadAxisStart + (int)horizontal,
                magnitudeX, beforeX, listener != null, out xBegan);
            var yTaken = hasY && Gate.TakeForBindingCapture(pad.deviceId, gamepad, PadAxisStart + (int)vertical,
                magnitudeY, beforeY, listener != null, out yBegan);
            if (xTaken) control.x.ResetToDefaultStateInEvent(eventPtr);
            if (yTaken) control.y.ResetToDefaultStateInEvent(eventPtr);

            if (xBegan && (!yBegan || magnitudeX >= magnitudeY)) Publish(pad.deviceId, listener!, PadAxes[(int)horizontal]);
            else if (yBegan) Publish(pad.deviceId, listener!, PadAxes[(int)vertical]);
        }

        private static bool Take(InputDevice device, TopiaForgeInputDevice kind, int index, float magnitude, float before,
            TopiaForgeInputBindingField? listener, InputBinding binding)
        {
            var taken = Gate.TakeForBindingCapture(device.deviceId, kind, index, magnitude, before, listener != null, out var began);
            if (began) Publish(device.deviceId, listener!, binding);
            return taken;
        }

        private static void Publish(int id, TopiaForgeInputBindingField listener, InputBinding binding)
        {
            if (pending != null || binding.Control == null || UiRebinding.Device(binding.Kind) != listener.Device) return;
            pending = binding;
            pendingDevice = id;
        }

        private static bool HoldsPadAxis(InputGamepadAxis axis) => Gate.HeldOn(TopiaForgeInputDevice.Gamepad, PadAxisStart + (int)axis);

        private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        private static InputBinding[] CreateKeyboardBindings()
        {
            var result = new InputBinding[TopiaForgeInputGate.ControlsPerDevice];
            // IInputAction's key names use the established SDK/legacy spelling (Return, Alpha1, KeypadEnter, ...).
            foreach (var name in Enum.GetNames(typeof(KeyCode)))
            {
                if (!Enum.TryParse(TopiaForgeNavigationBindings.InputSystemName(name), true, out Key key)) continue;
                var code = (int)key;
                if (code < 1 || code >= result.Length) continue;
                if (result[code].Control == null) result[code] = InputBinding.Key(name);
                KeyCodes[name] = code;
            }

            return result;
        }
    }
}
