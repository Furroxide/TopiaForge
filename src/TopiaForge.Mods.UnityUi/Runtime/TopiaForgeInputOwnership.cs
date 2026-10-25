using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using InputSystemKey = UnityEngine.InputSystem.Key;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Keeps what a TopiaForge surface handles away from the game. Robotopia reads its menus, its pause and its navigation
    /// through the Input System: PlayerInput messages, and the navigate, submit and cancel actions its UI library and Unity's
    /// UI module share. So the kit looks at each keyboard and controller event before any action does. A press of a control
    /// the surfaces claim (through the dismiss stack, or a kit text field with the keyboard) that begins while the claim
    /// holds is held at rest in that event and every later one until the control is released (TopiaForgeInputGate), and the
    /// kit reads the press itself. Navigation covers the kit's own keys and whatever else the game's navigate action binds
    /// while it listens, W, A, S and D among them (TopiaForgeNavigateAction). Nothing the game owns is changed, no action
    /// map, module or binding, so there is nothing to restore and nothing to fight the game's own action-map switches: with
    /// no claim and no taken press, every event passes at once, untouched. Unity's legacy input cannot be filtered this way
    /// and still sees every key. A failure stops the filtering until the game restarts and is logged once, so the game
    /// always keeps its input. Main thread only.
    /// </summary>
    internal static class TopiaForgeInputOwnership
    {
        private const int EastButton = 0;
        private const int SouthButton = 1;
        private const int Dpad = 2;
        private const int LeftStick = 3;
        private const int RightStick = 4;

        private static readonly TopiaForgeInputGate Gate = new TopiaForgeInputGate();
        private static readonly Action<InputEventPtr, InputDevice> Filter = OnEvent;
        private static readonly Action<InputDevice, InputDeviceChange> DeviceChanged = OnDeviceChange;
        private static bool installed;
        private static bool stopped;
        private static GameObject? selection;
        private static TMP_InputField? selectionText;

        /// <summary>Gets whether a Back press TopiaForge took is still held.</summary>
        internal static bool BackHeld => Gate.BackHeld;

        /// <summary>Gets whether TopiaForge took a Back press this frame.</summary>
        internal static bool BackPressedThisFrame => Gate.BackPressedIn(Time.frameCount);

        /// <summary>Gets whether TopiaForge took a submit press (Enter, Space, or a controller's A) this frame.</summary>
        internal static bool SubmitPressedThisFrame => Gate.SubmitPressedIn(Time.frameCount);

        /// <summary>Gets whether TopiaForge took a press of <paramref name="key"/> this frame.</summary>
        internal static bool KeyPressedThisFrame(InputSystemKey key) => Gate.KeyPressedIn((int)key, Time.frameCount);

        /// <summary>Gets the direction the held presses TopiaForge took point.</summary>
        internal static void Direction(out float x, out float y) => Gate.Direction(out x, out y);

        /// <summary>Starts filtering, once, as the kit's driver starts.</summary>
        internal static void Install()
        {
            if (installed || stopped)
            {
                return;
            }

            try
            {
                InputSystem.onEvent += Filter;
                InputSystem.onDeviceChange += DeviceChanged;
                installed = true;
            }
            catch (Exception exception)
            {
                Stop(exception);
            }
        }

        /// <summary>Stops filtering and lets go of every taken press, as the kit's driver stops. Safe to call repeatedly.</summary>
        internal static void Reset()
        {
            if (installed)
            {
                installed = false;
                try
                {
                    InputSystem.onEvent -= Filter;
                    InputSystem.onDeviceChange -= DeviceChanged;
                }
                catch (Exception)
                {
                    // At quit the Input System can be gone before the kit; nothing is left listening either way.
                }
            }

            stopped = false;
            Gate.ForgetAll();
            TopiaForgeNavigateAction.Reset();
            selection = null;
            selectionText = null;
        }

        /// <summary>Lets go of every taken press, as when the game loses focus and the Input System releases every key.</summary>
        internal static void ForgetHeld()
        {
            Gate.ForgetAll();
        }

        /// <summary>Gets whether a surface's root still exists and shows.</summary>
        internal static bool IsLiveRoot(object root)
        {
            return root is GameObject go && go != null && go.activeInHierarchy;
        }

        /// <summary>Gets whether the EventSystem selection is one of a surface root's controls.</summary>
        internal static bool HoldsSelection(object root)
        {
            if (!(root is GameObject go) || go == null)
            {
                return false;
            }

            var events = EventSystem.current;
            var selected = events != null ? events.currentSelectedGameObject : null;
            return selected != null && selected.activeInHierarchy && selected.transform.IsChildOf(go.transform);
        }

        private static void OnEvent(InputEventPtr eventPtr, InputDevice device)
        {
            // A mouse, pen or touch event, and every event while nothing is claimed or held, passes at once.
            var kind = device is Keyboard ? TopiaForgeInputDevice.Keyboard
                : device is Gamepad ? TopiaForgeInputDevice.Gamepad
                : TopiaForgeInputDevice.Other;
            if (stopped || kind == TopiaForgeInputDevice.Other)
            {
                return;
            }

            try
            {
                if (eventPtr.IsA<DeviceResetEvent>())
                {
                    Gate.Forget(device.deviceId);
                    return;
                }

                if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>())
                {
                    return;
                }

                var claims = TopiaForgeDismissStack.Claims;
                if (kind == TopiaForgeInputDevice.Keyboard && KitTextFieldHasKeyboard())
                {
                    claims |= TopiaForgeInputClaim.Keyboard;
                }

                if (!Gate.Examines(kind, claims))
                {
                    return;
                }

                if (kind == TopiaForgeInputDevice.Keyboard)
                {
                    FilterKeyboard((Keyboard)device, eventPtr, claims, Time.frameCount);
                }
                else
                {
                    FilterGamepad((Gamepad)device, eventPtr, claims, Time.frameCount);
                }
            }
            catch (Exception exception)
            {
                Stop(exception);
            }
        }

        private static void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (change == InputDeviceChange.Removed || change == InputDeviceChange.Disconnected
                || change == InputDeviceChange.SoftReset || change == InputDeviceChange.HardReset)
            {
                Gate.Forget(device.deviceId);
            }
        }

        private static void FilterKeyboard(Keyboard keyboard, InputEventPtr eventPtr, TopiaForgeInputClaim claims, int frame)
        {
            if ((claims & TopiaForgeInputClaim.Keyboard) != 0 || Gate.HoldsOn(keyboard.deviceId))
            {
                var keys = keyboard.allKeys;
                for (var index = 0; index < keys.Count; index++)
                {
                    var key = keys[index];
                    if (key != null)
                    {
                        Examine(keyboard, key, eventPtr, claims, frame);
                    }
                }

                return;
            }

            Examine(keyboard, keyboard.escapeKey, eventPtr, claims, frame);
            if ((claims & TopiaForgeInputClaim.Navigation) == 0)
            {
                return;
            }

            Examine(keyboard, keyboard.upArrowKey, eventPtr, claims, frame);
            Examine(keyboard, keyboard.downArrowKey, eventPtr, claims, frame);
            Examine(keyboard, keyboard.leftArrowKey, eventPtr, claims, frame);
            Examine(keyboard, keyboard.rightArrowKey, eventPtr, claims, frame);
            Examine(keyboard, keyboard.tabKey, eventPtr, claims, frame);
            Examine(keyboard, keyboard.enterKey, eventPtr, claims, frame);
            Examine(keyboard, keyboard.numpadEnterKey, eventPtr, claims, frame);
            Examine(keyboard, keyboard.spaceKey, eventPtr, claims, frame);

            // The game's menus also navigate on whatever else its navigate action binds, W, A, S and D among them.
            var bindings = TopiaForgeNavigateAction.Bindings;
            for (var index = 0; index < bindings.KeyCount; index++)
            {
                Examine(keyboard, keyboard[(InputSystemKey)bindings.KeyAt(index)], eventPtr, claims, frame);
            }
        }

        private static void Examine(Keyboard keyboard, KeyControl key, InputEventPtr eventPtr, TopiaForgeInputClaim claims, int frame)
        {
            if (!key.ReadValueFromEvent(eventPtr, out float value))
            {
                return;
            }

            var code = (int)key.keyCode;
            var bindings = TopiaForgeNavigateAction.Bindings;
            bindings.Direction(code, out var x, out var y);
            var pressed = key.IsValueConsideredPressed(value) ? 1f : 0f;
            var before = key.isPressed ? 1f : 0f;
            if (Gate.Take(keyboard.deviceId, TopiaForgeInputDevice.Keyboard, code, bindings.RoleOf(code), pressed, before, x, y, claims, frame))
            {
                key.ResetToDefaultStateInEvent(eventPtr);
            }
        }

        private static void FilterGamepad(Gamepad gamepad, InputEventPtr eventPtr, TopiaForgeInputClaim claims, int frame)
        {
            Examine(gamepad, gamepad.buttonEast, EastButton, TopiaForgeInputRole.Back, eventPtr, claims, frame);
            Examine(gamepad, gamepad.buttonSouth, SouthButton, TopiaForgeInputRole.Submit, eventPtr, claims, frame);
            Examine(gamepad, gamepad.dpad, Dpad, eventPtr, claims, frame);
            Examine(gamepad, gamepad.leftStick, LeftStick, eventPtr, claims, frame);

            // The right stick while the game's navigation binds it, and until its release once taken, however that changes.
            if (TopiaForgeNavigateAction.Bindings.RightStickMoves || Gate.IsTaken(gamepad.deviceId, RightStick))
            {
                Examine(gamepad, gamepad.rightStick, RightStick, eventPtr, claims, frame);
            }
        }

        private static void Examine(Gamepad gamepad, ButtonControl button, int control, TopiaForgeInputRole role, InputEventPtr eventPtr, TopiaForgeInputClaim claims, int frame)
        {
            if (!button.ReadValueFromEvent(eventPtr, out float value))
            {
                return;
            }

            var pressed = button.IsValueConsideredPressed(value) ? 1f : 0f;
            var before = button.isPressed ? 1f : 0f;
            if (Gate.Take(gamepad.deviceId, TopiaForgeInputDevice.Gamepad, control, role, pressed, before, 0f, 0f, claims, frame))
            {
                button.ResetToDefaultStateInEvent(eventPtr);
            }
        }

        private static void Examine(Gamepad gamepad, Vector2Control direction, int control, InputEventPtr eventPtr, TopiaForgeInputClaim claims, int frame)
        {
            if (!direction.ReadValueFromEvent(eventPtr, out Vector2 value))
            {
                return;
            }

            var before = direction.ReadValue().magnitude;
            if (Gate.Take(gamepad.deviceId, TopiaForgeInputDevice.Gamepad, control, TopiaForgeInputRole.Direction, value.magnitude, before, value.x, value.y, claims, frame))
            {
                direction.ResetToDefaultStateInEvent(eventPtr);
            }
        }

        /// <summary>
        /// Gets whether an object is one of the kit's: its canvas sits in the sorting bands the kit keeps above the game's UI.
        /// An inactive object, such as the control a closed window left selected, is nobody's.
        /// </summary>
        internal static bool IsKitObject(GameObject target)
        {
            var canvas = target.activeInHierarchy ? target.GetComponentInParent<Canvas>() : null;
            var order = canvas != null ? canvas.rootCanvas.sortingOrder : int.MinValue;
            return order >= TopiaForgeLayerBands.DefaultHudBase && order < TopiaForgeLayerBands.DefaultCeiling;
        }

        /// <summary>
        /// Gets whether a kit text field has the keyboard. Its typing reaches it through TextMesh Pro's own key events, never
        /// through these, so taking every key leaves the typing to the field and none of it to the game.
        /// </summary>
        internal static bool KitTextFieldHasKeyboard()
        {
            var events = EventSystem.current;
            var current = events != null ? events.currentSelectedGameObject : null;
            if (current == null)
            {
                selection = null;
                selectionText = null;
                return false;
            }

            if (!ReferenceEquals(current, selection))
            {
                selection = current;
                selectionText = current.TryGetComponent<TopiaForgeTextEntry>(out _) && current.TryGetComponent(out TMP_InputField field)
                    ? field
                    : null;
            }

            return selectionText != null && selectionText.isFocused;
        }

        private static void Stop(Exception exception)
        {
            stopped = true;
            Gate.ForgetAll();

            // Only the failure's type: an Input System message can carry a device's name, which can carry a person's.
            TopiaForgeLog.Error("TopiaForge stopped keeping keys from the game after a failure (" + exception.GetType().Name
                + "); until the game restarts, the game sees every key and button, Escape included.");
        }
    }

    /// <summary>Marks a kit text field: while it has the keyboard, TopiaForge takes every key from the game.</summary>
    internal sealed class TopiaForgeTextEntry : MonoBehaviour
    {
    }
}
