using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>What the UI holds in a frame, as a mod's input actions see it.</summary>
    internal readonly struct UiInputFocus
    {
        /// <summary>Creates a frame's focus.</summary>
        public UiInputFocus(TopiaForgeInputClaim claims, bool surfaceHoldsFocus, bool gameTextFieldHasKeyboard, bool pointerOverUi,
            TopiaForgeNavigationBindings? navigation = null)
        {
            Claims = claims;
            SurfaceHoldsFocus = surfaceHoldsFocus;
            GameTextFieldHasKeyboard = gameTextFieldHasKeyboard;
            PointerOverUi = pointerOverUi;
            Navigation = navigation;
        }

        /// <summary>Gets what the open TopiaForge surfaces take from the game.</summary>
        public TopiaForgeInputClaim Claims { get; }

        /// <summary>
        /// Gets whether a TopiaForge surface holds the focus outright: the manager overlay, a modal, a fullscreen tool, a key
        /// capture, or a kit text field with the keyboard.
        /// </summary>
        public bool SurfaceHoldsFocus { get; }

        /// <summary>
        /// Gets whether one of the game's own text fields has the keyboard. A game control that is merely selected does not
        /// count: the game's menu panel stays selected after a click into it.
        /// </summary>
        public bool GameTextFieldHasKeyboard { get; }

        /// <summary>Gets whether the pointer is over UI.</summary>
        public bool PointerOverUi { get; }

        /// <summary>Gets the game's currently followed navigation bindings, also used by the event filter.</summary>
        public TopiaForgeNavigationBindings? Navigation { get; }
    }

    /// <summary>Who a binding's press belongs to: decided as the press begins, kept until it is released.</summary>
    internal enum InputPressOwner : byte
    {
        /// <summary>The binding is released.</summary>
        None,

        /// <summary>The press began while the binding did not yield: the mod's action reads it.</summary>
        Action,

        /// <summary>The press began while the binding yielded: the UI that held its control keeps it until its release.</summary>
        Ui,
    }

    /// <summary>
    /// Which of a mod's bindings yield to the UI in a frame, Unity-free. A binding yields only to what holds its control:
    /// every binding while a TopiaForge surface holds the focus outright; a key or controller control the open windows claim
    /// (Escape and a controller's B, and the navigation controls while one of a window's own controls is selected, the keys
    /// the game's menus share with its gameplay only while its gameplay does not listen); every key and controller control
    /// while one of the game's own text fields has the keyboard; and a mouse button, the pointer's motion or the wheel while
    /// the pointer is over UI. Anything else reaches the action, wherever the pointer is and whichever control is selected,
    /// the game's included: a mod's own key opens its window at the main menu, closes it while it shows, and still works in
    /// a level after a click on the game's own menu. A press keeps the owner it began with until it is released (Hold).
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
                        : focus.GameTextFieldHasKeyboard || Covers(focus, TopiaForgeInputRole.Direction, TopiaForgeInputDevice.Keyboard);
                case InputBindingKind.Key:
                    return focus.GameTextFieldHasKeyboard || Covers(focus, KeyRole(binding.Control, focus.Navigation), TopiaForgeInputDevice.Keyboard);
                case InputBindingKind.GamepadButton:
                    return focus.GameTextFieldHasKeyboard || Covers(focus, ButtonRole(binding.Control), TopiaForgeInputDevice.Gamepad);
                case InputBindingKind.GamepadAxis:
                    return focus.GameTextFieldHasKeyboard
                        || (IsLeftStick(binding.Control) && Covers(focus, TopiaForgeInputRole.Direction, TopiaForgeInputDevice.Gamepad))
                        || (IsRightStick(binding.Control) && RightStickYields(focus));
                default:
                    return focus.GameTextFieldHasKeyboard;
            }
        }

        /// <summary>
        /// One frame of one binding of an action: <paramref name="value"/> is what the binding reads now,
        /// <paramref name="yielding"/> whether its action yields to the UI at all, and <paramref name="takenByTopiaForge"/>
        /// whether TopiaForge took the binding's press from the game and still holds it, which it keeps from mods too until
        /// its release, whatever the claims say by then: a key capture ends in the frame it takes its key. Returns what the
        /// binding adds to its action now (Hold).
        /// </summary>
        public static float Read(ref InputPressOwner owner, InputBinding binding, float value, UiInputFocus focus, bool yielding, bool takenByTopiaForge)
        {
            var yields = yielding && owner == InputPressOwner.None && Math.Abs(value) > ReleasedBelow
                && (takenByTopiaForge || Yields(binding, focus));
            return Hold(ref owner, value, yields);
        }

        /// <summary>
        /// One frame of one binding: <paramref name="value"/> is what it reads now, and <paramref name="yields"/> whether it
        /// yields now, which only matters as a press begins. A press belongs to whoever held its control as it began, and
        /// stays theirs until it is released, as a press TopiaForge takes from the game does: one that began while the binding
        /// yielded adds nothing to the action until its release, though the key capture that took it ended, the surface that
        /// held it closed or its claim dropped meanwhile, so the next frame reports no fresh press; one that began while it
        /// did not still reaches the action after UI takes the control. Returns what the binding adds to its action now.
        /// </summary>
        public static float Hold(ref InputPressOwner owner, float value, bool yields)
        {
            if (Math.Abs(value) <= ReleasedBelow)
            {
                owner = InputPressOwner.None;
                return 0f;
            }

            if (owner == InputPressOwner.None)
            {
                owner = yields ? InputPressOwner.Ui : InputPressOwner.Action;
            }

            return owner == InputPressOwner.Action ? value : 0f;
        }

        /// <summary>A binding reading at most this much is released, as an action reading at most this much is not held.</summary>
        public const float ReleasedBelow = 0.0001f;

        private static bool Covers(UiInputFocus focus, TopiaForgeInputRole role, TopiaForgeInputDevice device)
        {
            return TopiaForgeInputClaims.Cover(focus.Claims, role, device);
        }

        // The right stick yields only while the game's navigate action binds it, as the event filter takes it.
        private static bool RightStickYields(UiInputFocus focus)
        {
            var navigation = focus.Navigation;
            return navigation != null && navigation.RightStickMoves
                && Covers(focus, navigation.FollowedRole, TopiaForgeInputDevice.Gamepad);
        }

        private static TopiaForgeInputRole KeyRole(string control, TopiaForgeNavigationBindings? navigation)
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

            return Is(control, "Tab") ? TopiaForgeInputRole.Tab : navigation?.RoleOf(control) ?? TopiaForgeInputRole.Key;
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

        private static bool IsRightStick(string control)
        {
            return Is(control, nameof(InputGamepadAxis.RightX)) || Is(control, nameof(InputGamepadAxis.RightY));
        }

        private static bool Is(string control, string name)
        {
            return string.Equals(control, name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
