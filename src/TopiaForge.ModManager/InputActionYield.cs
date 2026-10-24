using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>What the UI holds in a frame, as a mod's input actions see it.</summary>
    internal readonly struct UiInputFocus
    {
        /// <summary>Creates a frame's focus.</summary>
        public UiInputFocus(TopiaForgeInputClaim claims, bool surfaceHoldsFocus, bool gameUiSelected, bool pointerOverUi)
        {
            Claims = claims;
            SurfaceHoldsFocus = surfaceHoldsFocus;
            GameUiSelected = gameUiSelected;
            PointerOverUi = pointerOverUi;
        }

        /// <summary>Gets what the open TopiaForge surfaces take from the game.</summary>
        public TopiaForgeInputClaim Claims { get; }

        /// <summary>
        /// Gets whether a TopiaForge surface holds the focus outright: the manager overlay, a modal, a fullscreen tool, a key
        /// capture, or a kit text field with the keyboard.
        /// </summary>
        public bool SurfaceHoldsFocus { get; }

        /// <summary>Gets whether the game's own UI holds the EventSystem selection, where it may be typing.</summary>
        public bool GameUiSelected { get; }

        /// <summary>Gets whether the pointer is over UI.</summary>
        public bool PointerOverUi { get; }
    }

    /// <summary>
    /// Which of a mod's bindings yield to the UI in a frame, Unity-free. A binding yields only to what holds its control:
    /// every binding while a TopiaForge surface holds the focus outright; a key or controller control the open windows claim
    /// (Escape and a controller's B, and the navigation controls while one of a window's own controls is selected); every key
    /// and controller control while the game's own UI holds the selection, since it may be typing; and a mouse button, the
    /// pointer's motion or the wheel while the pointer is over UI. Anything else reaches the action, wherever the pointer is
    /// and whichever kit control is selected: a mod's own key opens its window at the main menu and closes it while it shows.
    /// </summary>
    internal static class InputActionYield
    {
        /// <summary>Gets whether <paramref name="binding"/> yields to the UI under <paramref name="focus"/>.</summary>
        public static bool Yields(InputBinding binding, UiInputFocus focus)
        {
            if (focus.SurfaceHoldsFocus)
            {
                return true;
            }

            switch (binding.Kind)
            {
                case InputBindingKind.MouseButton:
                    return focus.PointerOverUi;
                case InputBindingKind.Axis:
                    return IsPointerAxis(binding.Control)
                        ? focus.PointerOverUi
                        : focus.GameUiSelected || Covers(focus, TopiaForgeInputRole.Direction, TopiaForgeInputDevice.Keyboard);
                case InputBindingKind.Key:
                    return focus.GameUiSelected || Covers(focus, KeyRole(binding.Control), TopiaForgeInputDevice.Keyboard);
                case InputBindingKind.GamepadButton:
                    return focus.GameUiSelected || Covers(focus, ButtonRole(binding.Control), TopiaForgeInputDevice.Gamepad);
                case InputBindingKind.GamepadAxis:
                    return focus.GameUiSelected
                        || (IsLeftStick(binding.Control) && Covers(focus, TopiaForgeInputRole.Direction, TopiaForgeInputDevice.Gamepad));
                default:
                    return focus.GameUiSelected;
            }
        }

        private static bool Covers(UiInputFocus focus, TopiaForgeInputRole role, TopiaForgeInputDevice device)
        {
            return TopiaForgeInputClaims.Cover(focus.Claims, role, device);
        }

        private static TopiaForgeInputRole KeyRole(string control)
        {
            if (Is(control, "Escape"))
            {
                return TopiaForgeInputRole.Back;
            }

            if (Is(control, "UpArrow") || Is(control, "DownArrow") || Is(control, "LeftArrow") || Is(control, "RightArrow"))
            {
                return TopiaForgeInputRole.Direction;
            }

            if (Is(control, "Return") || Is(control, "KeypadEnter") || Is(control, "Space"))
            {
                return TopiaForgeInputRole.Submit;
            }

            return Is(control, "Tab") ? TopiaForgeInputRole.Tab : TopiaForgeInputRole.Key;
        }

        private static TopiaForgeInputRole ButtonRole(string control)
        {
            if (Is(control, nameof(InputGamepadButton.East)))
            {
                return TopiaForgeInputRole.Back;
            }

            return Is(control, nameof(InputGamepadButton.South)) ? TopiaForgeInputRole.Submit : TopiaForgeInputRole.Key;
        }

        private static bool IsPointerAxis(string control)
        {
            return Is(control, nameof(InputAxis.PointerX)) || Is(control, nameof(InputAxis.PointerY)) || Is(control, nameof(InputAxis.Scroll));
        }

        private static bool IsLeftStick(string control)
        {
            return Is(control, nameof(InputGamepadAxis.LeftX)) || Is(control, nameof(InputGamepadAxis.LeftY));
        }

        private static bool Is(string control, string name)
        {
            return string.Equals(control, name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
