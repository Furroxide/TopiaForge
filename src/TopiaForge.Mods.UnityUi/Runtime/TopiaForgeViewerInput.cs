using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Chooses the viewing device from meaningful raw input before ownership filters the event.</summary>
    internal sealed class TopiaForgeViewerInput
    {
        private const float StickDeadZone = 0.25f;
        private const float StickMovement = 0.18f;
        private const float TriggerDeadZone = 0.25f;
        private const float TriggerMovement = 0.15f;
        private readonly Dictionary<int, DeviceSamples> samples = new Dictionary<int, DeviceSamples>();

        internal UiInputDevice LastUsed { get; private set; }

        internal void Observe(InputEventPtr input, InputDevice device)
        {
            if (input.IsA<DeviceResetEvent>())
            {
                Forget(device.deviceId);
                return;
            }
            if (!input.IsA<StateEvent>() && !input.IsA<DeltaStateEvent>()) return;
            switch (device)
            {
                case Keyboard keyboard when SampleKeyboard(keyboard, input):
                case Mouse mouse when SampleMouse(mouse, input):
                    LastUsed = UiInputDevice.KeyboardMouse;
                    break;
                case Gamepad gamepad when SampleGamepad(gamepad, input):
                    LastUsed = UiInputDevice.Gamepad;
                    break;
            }
        }

        internal void Forget(int deviceId) => samples.Remove(deviceId);

        internal void ForgetHeld() => samples.Clear();

        internal void Reset()
        {
            ForgetHeld();
            LastUsed = UiInputDevice.KeyboardMouse;
        }

        private bool SampleKeyboard(Keyboard keyboard, InputEventPtr input)
        {
            var keys = keyboard.allKeys;
            var state = State(keyboard.deviceId, keys.Count);
            var active = false;
            for (var index = 0; index < keys.Count; index++)
            {
                if (keys[index] is { } key) active |= Pressed(key, input, state.Buttons, index);
            }
            return active;
        }

        private bool SampleMouse(Mouse mouse, InputEventPtr input)
        {
            var pressed = State(mouse.deviceId, 5).Buttons;
            // Relative pointer motion and scrolling are activity. A warp of the absolute cursor is not.
            return Pressed(mouse.leftButton, input, pressed, 0) | Pressed(mouse.rightButton, input, pressed, 1) |
                Pressed(mouse.middleButton, input, pressed, 2) | Pressed(mouse.backButton, input, pressed, 3) |
                Pressed(mouse.forwardButton, input, pressed, 4) |
                RelativeMotion(mouse.delta, input) | RelativeMotion(mouse.scroll, input);
        }

        private bool SampleGamepad(Gamepad pad, InputEventPtr input)
        {
            var current = State(pad.deviceId, 10);
            var pressed = current.Buttons;
            return Pressed(pad.buttonSouth, input, pressed, 0) | Pressed(pad.buttonEast, input, pressed, 1) |
                Pressed(pad.buttonWest, input, pressed, 2) | Pressed(pad.buttonNorth, input, pressed, 3) |
                Pressed(pad.leftShoulder, input, pressed, 4) | Pressed(pad.rightShoulder, input, pressed, 5) |
                Pressed(pad.selectButton, input, pressed, 6) | Pressed(pad.startButton, input, pressed, 7) |
                Pressed(pad.leftStickButton, input, pressed, 8) | Pressed(pad.rightStickButton, input, pressed, 9) |
                current.Dpad.Observe(pad.dpad, input) | current.LeftStick.Observe(pad.leftStick, input) |
                current.RightStick.Observe(pad.rightStick, input) | current.LeftTrigger.Observe(pad.leftTrigger, input) |
                current.RightTrigger.Observe(pad.rightTrigger, input);
        }

        private DeviceSamples State(int id, int buttons)
        {
            if (samples.TryGetValue(id, out var state) && state.Buttons.Length == buttons) return state;
            state = new DeviceSamples(buttons);
            samples[id] = state;
            return state;
        }

        private static bool Pressed(ButtonControl control, InputEventPtr input, bool[] previous, int index)
        {
            if (!control.ReadValueFromEvent(input, out float value) || !float.IsFinite(value)) return false;
            var now = control.IsValueConsideredPressed(value);
            var began = now && !previous[index];
            previous[index] = now;
            return began;
        }

        private static bool RelativeMotion(Vector2Control control, InputEventPtr input) =>
            ReadVector(control, input, default, out var motion) && (motion.x != 0f || motion.y != 0f);

        private static bool ReadVector(Vector2Control control, InputEventPtr input, Vector2 previous, out Vector2 sample)
        {
            if (control.ReadValueFromEvent(input, out sample)) return float.IsFinite(sample.x) && float.IsFinite(sample.y);

            // A delta packet can cover just one axis rather than the whole vector control.
            var hasX = control.x.ReadValueFromEvent(input, out float x) && float.IsFinite(x);
            var hasY = control.y.ReadValueFromEvent(input, out float y) && float.IsFinite(y);
            sample = new Vector2(hasX ? x : previous.x, hasY ? y : previous.y);
            return hasX || hasY;
        }

        private sealed class DeviceSamples
        {
            internal DeviceSamples(int buttons) { Buttons = new bool[buttons]; }
            internal readonly bool[] Buttons;
            internal StickSample Dpad, LeftStick, RightStick;
            internal TriggerSample LeftTrigger, RightTrigger;
        }

        private struct StickSample
        {
            private Vector2 raw;
            private Vector2 anchor;

            internal bool Observe(Vector2Control control, InputEventPtr input)
            {
                if (!ReadVector(control, input, raw, out var value)) return false;
                raw = value;
                if (value.sqrMagnitude < StickDeadZone * StickDeadZone)
                {
                    anchor = default;
                    return false;
                }
                if ((value - anchor).sqrMagnitude < StickMovement * StickMovement) return false;
                anchor = value;
                return true;
            }
        }

        private struct TriggerSample
        {
            private float anchor;

            internal bool Observe(ButtonControl control, InputEventPtr input)
            {
                if (!control.ReadValueFromEvent(input, out float value) || !float.IsFinite(value)) return false;
                if (value < TriggerDeadZone)
                {
                    anchor = 0f;
                    return false;
                }
                if (Math.Abs(value - anchor) < TriggerMovement) return false;
                anchor = value;
                return true;
            }
        }
    }

    internal static partial class TopiaForgeInputOwnership
    {
        private static readonly TopiaForgeViewerInput ViewerInput = new TopiaForgeViewerInput();
        private static bool viewerObservationStopped;

        internal static UiInputDevice LastViewerDevice => ViewerInput.LastUsed;

        private static void ResetViewerInput()
        {
            viewerObservationStopped = false;
            ViewerInput.Reset();
        }

        private static void ObserveViewerInput(InputEventPtr eventPtr, InputDevice device)
        {
            if (viewerObservationStopped) return;
            try { ViewerInput.Observe(eventPtr, device); }
            catch (Exception error)
            {
                // The optional display preference must not interrupt key capture or ownership filtering.
                viewerObservationStopped = true;
                ViewerInput.ForgetHeld();
                try { TopiaForgeLog.Error("Viewer input activity stopped after " + error.GetType().Name + "."); }
                catch { }
            }
        }
    }
}
