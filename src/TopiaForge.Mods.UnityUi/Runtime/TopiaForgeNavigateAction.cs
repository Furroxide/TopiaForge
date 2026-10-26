using System;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Follows the game's navigate action, the one Unity's UI module moves its selection with and Robotopia's menus read,
    /// once a frame as the kit's driver runs (TopiaForgeNavigationBindings): whether it is enabled, the keys it binds with
    /// the way each points, whether it binds the right stick, and whether the game's gameplay input is on next to it, as in
    /// a level, so those keys walk too. Robotopia keeps its menu map enabled next to its gameplay map from a level's start
    /// until its first pause, so the gameplay map is what tells a level from a menu, never the menu map. Reading it changes
    /// nothing of the game's, and allocates nothing unless the action, its controls or their binding directions changed. A
    /// failure stops the following until the game restarts, logged once, and surfaces take only the kit's own navigation
    /// keys from the game again. Main thread only.
    /// </summary>
    internal static class TopiaForgeNavigateAction
    {
        private static readonly List<InputControl> Followed = new List<InputControl>();
        private static readonly List<string?> Parts = new List<string?>();
        private static InputAction? action;
        private static bool failed;

        /// <summary>Gets what the game's navigate action binds now.</summary>
        internal static TopiaForgeNavigationBindings Bindings { get; } = new TopiaForgeNavigationBindings();

        /// <summary>Reads the navigate action of the active event system's UI module, once a frame.</summary>
        internal static void Follow()
        {
            if (failed)
            {
                return;
            }

            try
            {
                var events = EventSystem.current;
                var module = events != null ? events.currentInputModule as InputSystemUIInputModule : null;
                var reference = module != null ? module.move : null;
                var current = reference != null ? reference.action : null;
                Bindings.Listening = current != null && current.enabled;
                Bindings.Gameplay = current != null && GameplayListens(current);
                if (current != null && !(ReferenceEquals(current, action) && Unchanged(current)))
                {
                    Read(current);
                }
            }
            catch (Exception exception)
            {
                failed = true;
                Bindings.Listening = false;
                Bindings.Gameplay = false;

                // Only the failure's type: an Input System message can carry a device's name, which can carry a person's.
                TopiaForgeLog.Warn("TopiaForge stopped following the game's menu navigation keys after a failure ("
                    + exception.GetType().Name + "); until the game restarts, its surfaces take only the arrows, Tab, Enter, "
                    + "Space and Escape from the game's menus.");
            }
        }

        /// <summary>Forgets the action and what it binds, as the kit's driver stops.</summary>
        internal static void Reset()
        {
            action = null;
            failed = false;
            Followed.Clear();
            Parts.Clear();
            Bindings.Clear();
            Bindings.Listening = false;
            Bindings.Gameplay = false;
        }

        /// <summary>
        /// Gets whether the game's gameplay input is on next to its navigate action: another action map of the action's own
        /// input asset, or a player's current map, is enabled. At the game's menus only the menu map is. An action outside any
        /// asset tells nothing, so it counts as gameplay: the keys it shares then stay the player's under a window.
        /// </summary>
        private static bool GameplayListens(InputAction navigate)
        {
            var own = navigate.actionMap;
            var asset = own != null ? own.asset : null;
            if (asset == null)
            {
                return true;
            }

            var maps = asset.actionMaps;
            for (var index = 0; index < maps.Count; index++)
            {
                if (!ReferenceEquals(maps[index], own) && maps[index].enabled)
                {
                    return true;
                }
            }

            var players = PlayerInput.all;
            for (var index = 0; index < players.Count; index++)
            {
                var map = players[index] != null ? players[index].currentActionMap : null;
                if (map != null && !ReferenceEquals(map, own) && map.enabled)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Read(InputAction navigate)
        {
            action = navigate;
            Followed.Clear();
            Parts.Clear();
            Bindings.Clear();
            var controls = navigate.controls;
            for (var index = 0; index < controls.Count; index++)
            {
                var control = controls[index];
                Followed.Add(control);
                var part = control is KeyControl ? Part(navigate, control) : null;
                Parts.Add(part);
                if (control is KeyControl key)
                {
                    Bindings.BindKey((int)key.keyCode, part, key.keyCode.ToString());
                }
                else if (control.device is Gamepad gamepad && Within(control, gamepad.rightStick))
                {
                    Bindings.BindRightStick();
                }
            }
        }

        private static bool Unchanged(InputAction navigate)
        {
            // A binding can change its composite part without changing any resolved control's identity.
            var controls = navigate.controls;
            if (controls.Count != Followed.Count)
            {
                return false;
            }

            for (var index = 0; index < controls.Count; index++)
            {
                if (!ReferenceEquals(controls[index], Followed[index])
                    || (controls[index] is KeyControl && !string.Equals(Part(navigate, controls[index]), Parts[index], StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return true;
        }

        private static string? Part(InputAction navigate, InputControl control)
        {
            var binding = navigate.GetBindingIndexForControl(control);
            var part = binding >= 0 ? navigate.bindings[binding] : default;
            return part.isPartOfComposite ? part.name : null;
        }

        private static bool Within(InputControl control, InputControl stick)
        {
            for (var at = control; at != null; at = at.parent)
            {
                if (ReferenceEquals(at, stick))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
