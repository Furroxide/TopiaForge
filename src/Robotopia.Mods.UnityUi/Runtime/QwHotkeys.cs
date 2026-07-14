using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using InputSystemKey = UnityEngine.InputSystem.Key;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Backend-neutral key identifiers for hotkeys and keybind capture.</summary>
    public enum QwKey
    {
        None,
        A, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Alpha0, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        Tab, Space, Enter, Backspace, Delete, Home, End, PageUp, PageDown,
        UpArrow, DownArrow, LeftArrow, RightArrow,
    }

    /// <summary>
    /// Global hotkey registry polled once per frame by QwRuntime through whichever
    /// input backend is alive. Replaces scattered Input.GetKeyDown calls; pairs with
    /// QwKeybindField for rebinding. Letter/digit hotkeys are suppressed while a text
    /// field has focus so typing never triggers mod actions (F-keys still fire).
    /// </summary>
    public static class QwHotkeys
    {
        private sealed class Registration
        {
            public Registration(string owner, QwKey key, Action action)
            {
                Owner = owner;
                Key = key;
                Action = action;
            }

            public string Owner { get; }
            public QwKey Key { get; set; }
            public Action Action { get; }
        }

        private static readonly List<Registration> Registrations = new List<Registration>();

        /// <summary>Registers a hotkey; returns a handle whose Key can be rebound.</summary>
        public static object Register(string owner, QwKey key, Action action)
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
            QwRuntime.Ensure();
            return registration;
        }

        /// <summary>Rebinds a registration returned by Register.</summary>
        public static void Rebind(object handle, QwKey key)
        {
            if (handle is Registration registration)
            {
                registration.Key = key;
            }
        }

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
                if (registration.Key == QwKey.None)
                {
                    continue;
                }

                if (typing && !IsAlwaysActive(registration.Key))
                {
                    continue;
                }

                if (WasPressedThisFrame(registration.Key))
                {
                    QwCallbacks.Invoke(registration.Action, "Hotkey " + registration.Key);
                }
            }
        }

        internal static void Reset()
        {
            Registrations.Clear();
        }

        /// <summary>Any key pressed this frame (keybind capture). None when nothing pressed.</summary>
        public static QwKey CapturePressedKey()
        {
            foreach (QwKey key in Enum.GetValues(typeof(QwKey)))
            {
                if (key != QwKey.None && WasPressedThisFrame(key))
                {
                    return key;
                }
            }

            return QwKey.None;
        }

        public static bool WasPressedThisFrame(QwKey key)
        {
            if (QwInput.LegacyAvailable)
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

        private static bool IsAlwaysActive(QwKey key)
        {
            return key >= QwKey.F1 && key <= QwKey.F12;
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

        internal static KeyCode ToKeyCode(QwKey key)
        {
            return key switch
            {
                >= QwKey.A and <= QwKey.Z => KeyCode.A + (key - QwKey.A),
                >= QwKey.Alpha0 and <= QwKey.Alpha9 => KeyCode.Alpha0 + (key - QwKey.Alpha0),
                >= QwKey.F1 and <= QwKey.F12 => KeyCode.F1 + (key - QwKey.F1),
                QwKey.Tab => KeyCode.Tab,
                QwKey.Space => KeyCode.Space,
                QwKey.Enter => KeyCode.Return,
                QwKey.Backspace => KeyCode.Backspace,
                QwKey.Delete => KeyCode.Delete,
                QwKey.Home => KeyCode.Home,
                QwKey.End => KeyCode.End,
                QwKey.PageUp => KeyCode.PageUp,
                QwKey.PageDown => KeyCode.PageDown,
                QwKey.UpArrow => KeyCode.UpArrow,
                QwKey.DownArrow => KeyCode.DownArrow,
                QwKey.LeftArrow => KeyCode.LeftArrow,
                QwKey.RightArrow => KeyCode.RightArrow,
                _ => KeyCode.None,
            };
        }

        internal static InputSystemKey ToInputSystemKey(QwKey key)
        {
            return key switch
            {
                >= QwKey.A and <= QwKey.Z => InputSystemKey.A + (key - QwKey.A),
                QwKey.Alpha0 => InputSystemKey.Digit0,
                >= QwKey.Alpha1 and <= QwKey.Alpha9 => InputSystemKey.Digit1 + (key - QwKey.Alpha1),
                >= QwKey.F1 and <= QwKey.F12 => InputSystemKey.F1 + (key - QwKey.F1),
                QwKey.Tab => InputSystemKey.Tab,
                QwKey.Space => InputSystemKey.Space,
                QwKey.Enter => InputSystemKey.Enter,
                QwKey.Backspace => InputSystemKey.Backspace,
                QwKey.Delete => InputSystemKey.Delete,
                QwKey.Home => InputSystemKey.Home,
                QwKey.End => InputSystemKey.End,
                QwKey.PageUp => InputSystemKey.PageUp,
                QwKey.PageDown => InputSystemKey.PageDown,
                QwKey.UpArrow => InputSystemKey.UpArrow,
                QwKey.DownArrow => InputSystemKey.DownArrow,
                QwKey.LeftArrow => InputSystemKey.LeftArrow,
                QwKey.RightArrow => InputSystemKey.RightArrow,
                _ => InputSystemKey.None,
            };
        }
    }
}
