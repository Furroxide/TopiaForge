using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using InputSystemKey = UnityEngine.InputSystem.Key;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Global hotkey registry polled once per frame by TopiaForgeRuntime through whichever
    /// input backend is alive. Replaces scattered Input.GetKeyDown calls; pairs with
    /// TopiaForgeKeybindField for rebinding. Letter/digit hotkeys are suppressed while a text
    /// field has focus so typing never triggers mod actions (F-keys still fire).
    /// </summary>
    public static class TopiaForgeHotkeys
    {
        private static readonly HotkeyRegistrationStore Registrations = new HotkeyRegistrationStore();

        /// <summary>Registers a hotkey; returns a handle whose Key can be rebound.</summary>
        public static object Register(string owner, TopiaForgeKey key, Action action)
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                throw new ArgumentException("A stable hotkey owner is required.", nameof(owner));
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            var registration = Registrations.Add(owner, key, action);
            try { TopiaForgeRuntime.Ensure(); }
            catch { Registrations.Remove(registration); throw; }
            return registration;
        }

        /// <summary>Rebinds a registration returned by Register.</summary>
        public static void Rebind(object handle, TopiaForgeKey key)
        {
            if (handle is HotkeyRegistration registration)
            {
                registration.Key = key;
            }
        }

        /// <summary>Removes every hotkey registered by the specified owner.</summary>
        public static void UnregisterOwner(string owner)
        {
            Registrations.RemoveOwner(owner);
        }

        internal static void Unregister(object handle) => Registrations.Remove(handle);

        internal static void Tick()
        {
            var snapshot = Registrations.Snapshot;
            if (snapshot.Length == 0)
            {
                return;
            }

            var typing = IsTextFieldFocused();
            for (var index = 0; index < snapshot.Length; index++)
            {
                var registration = snapshot[index];
                if (!registration.IsActive || registration.Key == TopiaForgeKey.None)
                {
                    continue;
                }

                if (typing && !IsAlwaysActive(registration.Key))
                {
                    continue;
                }

                if (WasPressedThisFrame(registration.Key))
                {
                    TopiaForgeCallbacks.Invoke(registration.Action, "Hotkey " + registration.Key);
                }
            }
        }

        internal static void Reset()
        {
            Registrations.Clear();
        }

        /// <summary>Any key pressed this frame (keybind capture). None when nothing pressed.</summary>
        public static TopiaForgeKey CapturePressedKey()
        {
            foreach (TopiaForgeKey key in Enum.GetValues(typeof(TopiaForgeKey)))
            {
                if (key != TopiaForgeKey.None && WasPressedThisFrame(key))
                {
                    return key;
                }
            }

            return TopiaForgeKey.None;
        }

        /// <summary>Gets whether the specified key was pressed during the current frame.</summary>
        public static bool WasPressedThisFrame(TopiaForgeKey key)
        {
            if (TopiaForgeInput.LegacyAvailable)
            {
                return Input.GetKeyDown(ToKeyCode(key));
            }

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null)
            {
                return false;
            }

            var mapped = ToInputSystemKey(key);
            return mapped != InputSystemKey.None && keyboard[mapped].wasPressedThisFrame;
        }

        private static bool IsAlwaysActive(TopiaForgeKey key)
        {
            return key >= TopiaForgeKey.F1 && key <= TopiaForgeKey.F12;
        }

        private static bool IsTextFieldFocused()
        {
            var eventSystem = EventSystem.current;
            var selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (selected == null)
            {
                return false;
            }

            var input = selected.GetComponent<TMP_InputField>();
            return input != null && input.isFocused;
        }

        internal static KeyCode ToKeyCode(TopiaForgeKey key)
        {
            return key switch
            {
                >= TopiaForgeKey.A and <= TopiaForgeKey.Z => KeyCode.A + (key - TopiaForgeKey.A),
                >= TopiaForgeKey.Alpha0 and <= TopiaForgeKey.Alpha9 => KeyCode.Alpha0 + (key - TopiaForgeKey.Alpha0),
                >= TopiaForgeKey.F1 and <= TopiaForgeKey.F12 => KeyCode.F1 + (key - TopiaForgeKey.F1),
                TopiaForgeKey.Tab => KeyCode.Tab,
                TopiaForgeKey.Space => KeyCode.Space,
                TopiaForgeKey.Enter => KeyCode.Return,
                TopiaForgeKey.Backspace => KeyCode.Backspace,
                TopiaForgeKey.Delete => KeyCode.Delete,
                TopiaForgeKey.Home => KeyCode.Home,
                TopiaForgeKey.End => KeyCode.End,
                TopiaForgeKey.PageUp => KeyCode.PageUp,
                TopiaForgeKey.PageDown => KeyCode.PageDown,
                TopiaForgeKey.UpArrow => KeyCode.UpArrow,
                TopiaForgeKey.DownArrow => KeyCode.DownArrow,
                TopiaForgeKey.LeftArrow => KeyCode.LeftArrow,
                TopiaForgeKey.RightArrow => KeyCode.RightArrow,
                _ => KeyCode.None,
            };
        }

        internal static InputSystemKey ToInputSystemKey(TopiaForgeKey key)
        {
            return key switch
            {
                >= TopiaForgeKey.A and <= TopiaForgeKey.Z => InputSystemKey.A + (key - TopiaForgeKey.A),
                TopiaForgeKey.Alpha0 => InputSystemKey.Digit0,
                >= TopiaForgeKey.Alpha1 and <= TopiaForgeKey.Alpha9 => InputSystemKey.Digit1 + (key - TopiaForgeKey.Alpha1),
                >= TopiaForgeKey.F1 and <= TopiaForgeKey.F12 => InputSystemKey.F1 + (key - TopiaForgeKey.F1),
                TopiaForgeKey.Tab => InputSystemKey.Tab,
                TopiaForgeKey.Space => InputSystemKey.Space,
                TopiaForgeKey.Enter => InputSystemKey.Enter,
                TopiaForgeKey.Backspace => InputSystemKey.Backspace,
                TopiaForgeKey.Delete => InputSystemKey.Delete,
                TopiaForgeKey.Home => InputSystemKey.Home,
                TopiaForgeKey.End => InputSystemKey.End,
                TopiaForgeKey.PageUp => InputSystemKey.PageUp,
                TopiaForgeKey.PageDown => InputSystemKey.PageDown,
                TopiaForgeKey.UpArrow => InputSystemKey.UpArrow,
                TopiaForgeKey.DownArrow => InputSystemKey.DownArrow,
                TopiaForgeKey.LeftArrow => InputSystemKey.LeftArrow,
                TopiaForgeKey.RightArrow => InputSystemKey.RightArrow,
                _ => InputSystemKey.None,
            };
        }
    }
}
