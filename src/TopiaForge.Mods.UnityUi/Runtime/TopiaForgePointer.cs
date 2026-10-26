using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Keeps the mouse working on TopiaForge surfaces while one shows the cursor, once a frame as the kit's driver runs.
    /// Robotopia's pause menu turns the game's whole menu map off as it closes, as its player switches back to gameplay, so
    /// Unity's UI module tracked no pointer and pressed nothing: after any pause, no click reached a TopiaForge surface. While
    /// a surface shows the cursor, the kit turns the module's own pointer actions back on where the game left them off, the
    /// position and the buttons only, never navigation, submit or cancel, and turns them off again as the last such surface
    /// closes (TopiaForgePointerHold): where the game leaves them on, as at its menus, nothing changes. The game's module has
    /// no wheel action at all, so while a surface shows the cursor the kit scrolls the TopiaForge control under the pointer
    /// itself, by as much as the module would, and nothing of the game's ever sees the wheel. A failure stops this until the
    /// game restarts and is logged once, after the game's own state is put back. Main thread only.
    /// </summary>
    internal static class TopiaForgePointer
    {
        private const int Point = 0;
        private const int LeftClick = 1;
        private const int RightClick = 2;
        private const int MiddleClick = 3;
        private const int ScrollWheel = 4;
        private const int Slots = 5;

        private static readonly TopiaForgePointerHold[] Holds =
        {
            new TopiaForgePointerHold(), new TopiaForgePointerHold(), new TopiaForgePointerHold(),
            new TopiaForgePointerHold(), new TopiaForgePointerHold(),
        };

        private static readonly InputAction?[] Actions = new InputAction?[Slots];
        private static readonly Action<object, InputActionChange> Changed = OnActionChange;
        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();
        private static PointerEventData? wheel;
        private static EventSystem? wheelEvents;
        private static bool watching;
        private static bool changing;
        private static bool failed;

        /// <summary>Follows the module's pointer actions and the wheel, once a frame.</summary>
        internal static void Tick()
        {
            if (failed)
            {
                return;
            }

            try
            {
                var events = EventSystem.current;
                var module = events != null ? events.currentInputModule as InputSystemUIInputModule : null;
                var wanted = module != null && TopiaForgeCursor.ActiveLeases > 0;
                Follow(Point, module != null ? module.point : null, wanted);
                Follow(LeftClick, module != null ? module.leftClick : null, wanted);
                Follow(RightClick, module != null ? module.rightClick : null, wanted);
                Follow(MiddleClick, module != null ? module.middleClick : null, wanted);
                Follow(ScrollWheel, module != null ? module.scrollWheel : null, wanted);
                Watch(Holding());
                if (wanted && Actions[ScrollWheel] == null)
                {
                    Scroll(module!, events!);
                }
            }
            catch (Exception exception)
            {
                failed = true;
                Reset();
                failed = true;

                // Only the failure's type: an Input System message can carry a device's name, which can carry a person's.
                TopiaForgeLog.Warn("TopiaForge stopped keeping the mouse on its windows after a failure (" + exception.GetType().Name
                    + "); until the game restarts, clicks on TopiaForge windows after the game's pause menu may do nothing.");
            }
        }

        /// <summary>Puts back every pointer action the kit turned on and forgets them, as the kit's driver stops. Safe to call repeatedly.</summary>
        internal static void Reset()
        {
            for (var slot = 0; slot < Slots; slot++)
            {
                Release(slot);
                Actions[slot] = null;
            }

            Watch(false);
            wheel = null;
            wheelEvents = null;
            Hits.Clear();
            failed = false;
        }

        private static void Follow(int slot, InputActionReference? reference, bool wanted)
        {
            var action = reference != null ? reference.action : null;
            if (!ReferenceEquals(action, Actions[slot]))
            {
                // The module took another action, as a new scene's does: the one the kit turned on goes back as it was.
                Release(slot);
                Actions[slot] = action;
            }

            var step = Holds[slot].Tick(wanted, action != null, action != null && action.enabled);
            if (step == TopiaForgePointerStep.Enable)
            {
                Change(action!, true);
            }
            else if (step == TopiaForgePointerStep.Disable)
            {
                Change(action!, false);
            }
        }

        private static void Release(int slot)
        {
            var action = Actions[slot];
            try
            {
                if (Holds[slot].Release(action != null && action.enabled))
                {
                    Change(action!, false);
                }
            }
            catch (Exception)
            {
                // An action whose asset went with its scene has nothing left to put back.
            }
        }

        private static void Change(InputAction action, bool enable)
        {
            changing = true;
            try
            {
                if (enable)
                {
                    action.Enable();
                }
                else
                {
                    action.Disable();
                }
            }
            finally
            {
                changing = false;
            }
        }

        private static bool Holding()
        {
            for (var slot = 0; slot < Slots; slot++)
            {
                if (Holds[slot].Held)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Watch(bool on)
        {
            if (on == watching)
            {
                return;
            }

            if (on)
            {
                InputSystem.onActionChange += Changed;
            }
            else
            {
                InputSystem.onActionChange -= Changed;
            }

            watching = on;
        }

        // The game turned an action the kit holds, or its map, on or off itself: its state stands.
        private static void OnActionChange(object changed, InputActionChange change)
        {
            if (changing || (change != InputActionChange.ActionEnabled && change != InputActionChange.ActionDisabled
                && change != InputActionChange.ActionMapEnabled && change != InputActionChange.ActionMapDisabled))
            {
                return;
            }

            for (var slot = 0; slot < Slots; slot++)
            {
                var action = Actions[slot];
                if (action != null && Holds[slot].Held && (ReferenceEquals(changed, action) || ReferenceEquals(changed, action.actionMap)))
                {
                    Holds[slot].Superseded();
                }
            }
        }

        // The wheel, in notches as Unity's legacy input counts them, scrolls the TopiaForge control under the pointer by as
        // much as the module's own wheel would; a pointer over anything of the game's scrolls nothing.
        private static void Scroll(InputSystemUIInputModule module, EventSystem events)
        {
            if (!TopiaForgeInput.LegacyAvailable)
            {
                return;
            }

            var notches = Input.mouseScrollDelta;
            if (notches.x == 0f && notches.y == 0f)
            {
                return;
            }

            if (wheel == null || !ReferenceEquals(wheelEvents, events))
            {
                wheel = new PointerEventData(events);
                wheelEvents = events;
            }

            wheel.Reset();
            wheel.position = Input.mousePosition;
            Hits.Clear();
            events.RaycastAll(wheel, Hits);
            var top = Hits.Count > 0 ? Hits[0].gameObject : null;
            Hits.Clear();
            if (top == null || !TopiaForgeInputOwnership.IsKitObject(top))
            {
                return;
            }

            wheel.scrollDelta = notches * module.scrollDeltaPerTick;
            ExecuteEvents.ExecuteHierarchy(top, wheel, ExecuteEvents.scrollHandler);
        }
    }
}
