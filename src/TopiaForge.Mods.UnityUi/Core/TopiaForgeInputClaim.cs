using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// What a TopiaForge surface takes from the game while it is on the dismiss stack. A press of a control a claim covers
    /// that begins while the claim holds never reaches the game, and stays TopiaForge's until it is released.
    /// </summary>
    [Flags]
    internal enum TopiaForgeInputClaim
    {
        /// <summary>Nothing: every press reaches the game.</summary>
        None = 0,

        /// <summary>Back: Escape, and a controller's B. Every surface on the dismiss stack claims it.</summary>
        Back = 1,

        /// <summary>
        /// Menu navigation: the arrows, Tab, Enter and Space, and a controller's left stick, D-pad, A and B, and the other keys
        /// the game's navigate action binds while only its menus listen to them (TopiaForgeNavigationBindings).
        /// </summary>
        Navigation = 2,

        /// <summary>
        /// Navigation only while the EventSystem selection is on one of the surface's own controls, as a window over gameplay
        /// takes it: the window's focused control handles those keys, and gameplay keeps them otherwise.
        /// </summary>
        NavigationWhileSelected = 4,

        /// <summary>Every keyboard key: a key capture, or typing in a kit text field.</summary>
        Keyboard = 8,

        /// <summary>
        /// The keys and the stick the game's menu navigation shares with its gameplay (W, A, S and D in a level), as a
        /// surface that takes navigation whatever is selected does: the manager overlay, a kit modal and a fullscreen tool.
        /// Resolved claims only: the dismiss order adds it for a surface pushed with <see cref="Navigation"/>, never for a
        /// window, so over gameplay a window leaves those keys to the player.
        /// </summary>
        SharedNavigation = 16,
    }

    /// <summary>The part a control plays in what TopiaForge can take from the game.</summary>
    internal enum TopiaForgeInputRole : byte
    {
        /// <summary>Any other keyboard key: only the keyboard claim covers it.</summary>
        Key,

        /// <summary>Escape, or a controller's B.</summary>
        Back,

        /// <summary>An arrow key, a controller's D-pad or its left stick.</summary>
        Direction,

        /// <summary>Enter, Space, or a controller's A: the kit's submit.</summary>
        Submit,

        /// <summary>Tab.</summary>
        Tab,

        /// <summary>
        /// A key or stick the game's navigate action binds besides the kit's own while its gameplay input is on as well, as
        /// W, A, S and D are in a level: it walks or turns as well as moving the game's menus. Only a surface that takes
        /// navigation whatever is selected takes it (<see cref="TopiaForgeInputClaim.SharedNavigation"/>).
        /// </summary>
        SharedDirection,
    }

    /// <summary>The kind of device an input event comes from, as far as TopiaForge's claims go.</summary>
    internal enum TopiaForgeInputDevice : byte
    {
        /// <summary>A mouse, a pen, a touchscreen or anything else: TopiaForge never takes its input.</summary>
        Other,

        /// <summary>A keyboard.</summary>
        Keyboard,

        /// <summary>A controller (an Input System gamepad).</summary>
        Gamepad,
    }

    /// <summary>Which controls a set of claims covers. Unity-free.</summary>
    internal static class TopiaForgeInputClaims
    {
        /// <summary>
        /// Gets whether <paramref name="claims"/> cover a control playing <paramref name="role"/> on <paramref name="device"/>.
        /// The claims are resolved ones: a surface's <see cref="TopiaForgeInputClaim.NavigationWhileSelected"/> has already
        /// become <see cref="TopiaForgeInputClaim.Navigation"/> or nothing.
        /// </summary>
        public static bool Cover(TopiaForgeInputClaim claims, TopiaForgeInputRole role, TopiaForgeInputDevice device)
        {
            if (device == TopiaForgeInputDevice.Other)
            {
                return false;
            }

            if (device == TopiaForgeInputDevice.Keyboard && (claims & TopiaForgeInputClaim.Keyboard) != 0)
            {
                return true;
            }

            switch (role)
            {
                case TopiaForgeInputRole.Back:
                    return (claims & (TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Navigation)) != 0;
                case TopiaForgeInputRole.Direction:
                case TopiaForgeInputRole.Submit:
                case TopiaForgeInputRole.Tab:
                    return (claims & TopiaForgeInputClaim.Navigation) != 0;
                case TopiaForgeInputRole.SharedDirection:
                    return (claims & TopiaForgeInputClaim.SharedNavigation) != 0;
                default:
                    return false;
            }
        }

        /// <summary>Gets whether a control playing <paramref name="role"/> steers the kit's navigation once it is taken.</summary>
        public static bool IsDirection(TopiaForgeInputRole role)
        {
            return role == TopiaForgeInputRole.Direction || role == TopiaForgeInputRole.SharedDirection;
        }
    }
}
