using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using InputSystemKey = UnityEngine.InputSystem.Key;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Backend-neutral key identifiers for hotkeys and keybind capture.</summary>
    public enum TopiaForgeKey
    {
        /// <summary>No key is assigned.</summary>
        None,

        /// <summary>The A key.</summary>
        A,
        /// <summary>The B key.</summary>
        B,
        /// <summary>The C key.</summary>
        C,
        /// <summary>The D key.</summary>
        D,
        /// <summary>The E key.</summary>
        E,
        /// <summary>The F key.</summary>
        F,
        /// <summary>The G key.</summary>
        G,
        /// <summary>The H key.</summary>
        H,
        /// <summary>The I key.</summary>
        I,
        /// <summary>The J key.</summary>
        J,
        /// <summary>The K key.</summary>
        K,
        /// <summary>The L key.</summary>
        L,
        /// <summary>The M key.</summary>
        M,
        /// <summary>The N key.</summary>
        N,
        /// <summary>The O key.</summary>
        O,
        /// <summary>The P key.</summary>
        P,
        /// <summary>The Q key.</summary>
        Q,
        /// <summary>The R key.</summary>
        R,
        /// <summary>The S key.</summary>
        S,
        /// <summary>The T key.</summary>
        T,
        /// <summary>The U key.</summary>
        U,
        /// <summary>The V key.</summary>
        V,
        /// <summary>The W key.</summary>
        W,
        /// <summary>The X key.</summary>
        X,
        /// <summary>The Y key.</summary>
        Y,
        /// <summary>The Z key.</summary>
        Z,

        /// <summary>The top-row 0 key.</summary>
        Alpha0,
        /// <summary>The top-row 1 key.</summary>
        Alpha1,
        /// <summary>The top-row 2 key.</summary>
        Alpha2,
        /// <summary>The top-row 3 key.</summary>
        Alpha3,
        /// <summary>The top-row 4 key.</summary>
        Alpha4,
        /// <summary>The top-row 5 key.</summary>
        Alpha5,
        /// <summary>The top-row 6 key.</summary>
        Alpha6,
        /// <summary>The top-row 7 key.</summary>
        Alpha7,
        /// <summary>The top-row 8 key.</summary>
        Alpha8,
        /// <summary>The top-row 9 key.</summary>
        Alpha9,

        /// <summary>The F1 function key.</summary>
        F1,
        /// <summary>The F2 function key.</summary>
        F2,
        /// <summary>The F3 function key.</summary>
        F3,
        /// <summary>The F4 function key.</summary>
        F4,
        /// <summary>The F5 function key.</summary>
        F5,
        /// <summary>The F6 function key.</summary>
        F6,
        /// <summary>The F7 function key.</summary>
        F7,
        /// <summary>The F8 function key.</summary>
        F8,
        /// <summary>The F9 function key.</summary>
        F9,
        /// <summary>The F10 function key.</summary>
        F10,
        /// <summary>The F11 function key.</summary>
        F11,
        /// <summary>The F12 function key.</summary>
        F12,

        /// <summary>The Tab key.</summary>
        Tab,
        /// <summary>The Space key.</summary>
        Space,
        /// <summary>The Enter key.</summary>
        Enter,
        /// <summary>The Backspace key.</summary>
        Backspace,
        /// <summary>The Delete key.</summary>
        Delete,
        /// <summary>The Home key.</summary>
        Home,
        /// <summary>The End key.</summary>
        End,
        /// <summary>The Page Up key.</summary>
        PageUp,
        /// <summary>The Page Down key.</summary>
        PageDown,

        /// <summary>The up-arrow key.</summary>
        UpArrow,
        /// <summary>The down-arrow key.</summary>
        DownArrow,
        /// <summary>The left-arrow key.</summary>
        LeftArrow,
        /// <summary>The right-arrow key.</summary>
        RightArrow,
    }

    /// <summary>
    /// Global hotkey registry polled once per frame by TopiaForgeRuntime through whichever
    /// input backend is alive. Replaces scattered Input.GetKeyDown calls; pairs with
    /// TopiaForgeKeybindField for rebinding. Letter/digit hotkeys are suppressed while a text
    /// field has focus so typing never triggers mod actions (F-keys still fire).
    /// </summary>
    public static class TopiaForgeHotkeys
    {
        private sealed class Registration
        {
            public Registration(string owner, TopiaForgeKey key, Action action)
            {
                Owner = owner;
                Key = key;
                Action = action;
            }

            public string Owner { get; }
            public TopiaForgeKey Key { get; set; }
            public Action Action { get; }
        }

        private static readonly List<Registration> Registrations = new List<Registration>();

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

            var registration = new Registration(owner, key, action);
            Registrations.Add(registration);
            TopiaForgeRuntime.Ensure();
            return registration;
        }

        /// <summary>Rebinds a registration returned by Register.</summary>
        public static void Rebind(object handle, TopiaForgeKey key)
        {
            if (handle is Registration registration)
            {
                registration.Key = key;
            }
        }

        /// <summary>Removes every hotkey registered by the specified owner.</summary>
        public static void UnregisterOwner(string owner)
        {
            Registrations.RemoveAll(r => string.Equals(r.Owner, owner, StringComparison.Ordinal));
        }

        internal static void Tick()
        {
            if (Registrations.Count == 0)
            {
                return;
            }

            var typing = IsTextFieldFocused();
            for (var index = 0; index < Registrations.Count; index++)
            {
                var registration = Registrations[index];
                if (registration.Key == TopiaForgeKey.None)
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
