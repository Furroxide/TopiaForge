using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// The kit's own keyboard and controller navigation while the topmost surface takes it from the game. Unity's UI module
    /// reads the Input System actions the game's menus read, so it sees none of the presses TopiaForge takes: this sends the
    /// kit's selection what the module would have, a move and a submit (TopiaForgeNavigationDriver, with the module's own
    /// repeat delay and rate). Back stays the dismiss stack's. A legacy UI module reads its own keys, so it is left alone,
    /// but no move leaves the surface, whoever sends it: the control selected inside it carries a guard that undoes a move
    /// out of it, such as one Unity's module makes on a key the game still holds from before the surface opened.
    /// Nothing here allocates per frame. Main thread only.
    /// </summary>
    internal static class TopiaForgeNavigation
    {
        private static readonly TopiaForgeNavigationDriver Driver = new TopiaForgeNavigationDriver();
        private static readonly List<Selectable> Controls = new List<Selectable>();
        private static EventSystem? events;
        private static AxisEventData? move;
        private static BaseEventData? submit;
        private static GameObject? guarded;
        private static GameObject? guardedRoot;

        internal static void Tick(float deltaSeconds)
        {
            var root = TopiaForgeDismissStack.FocusRoot;
            var system = EventSystem.current;
            Navigate(root, system, deltaSeconds);
            Guard(system != null ? system.currentSelectedGameObject : null, root);
        }

        /// <summary>
        /// Undoes a move that took the selection out of the surface taking navigation, whoever sent it: the guard on the
        /// control that had the selection calls it after that control's own move handler picked the next one. The surface is
        /// the one that control was selected in at the last frame: a window takes navigation only while one of its controls
        /// is selected, so once the move has taken the selection out, it claims nothing any more.
        /// </summary>
        internal static void Contain(GameObject moved)
        {
            var root = ReferenceEquals(moved, guarded) && guardedRoot != null ? guardedRoot : TopiaForgeDismissStack.FocusRoot;
            var system = EventSystem.current;
            if (root != null && system != null && IsInside(moved, root) && !IsInside(system.currentSelectedGameObject, root))
            {
                system.SetSelectedGameObject(moved);
            }
        }

        private static void Navigate(GameObject? root, EventSystem? system, float deltaSeconds)
        {
            var module = system != null ? system.currentInputModule as InputSystemUIInputModule : null;
            TopiaForgeInputOwnership.Direction(out var x, out var y);
            if (root == null || system == null || module == null)
            {
                Driver.Tick(false, false, false, x, y, false, deltaSeconds, 0f, 0f);
                return;
            }

            var selected = system.currentSelectedGameObject;
            var inSurface = IsInside(selected, root);
            var step = Driver.Tick(
                true,
                inSurface,
                inSurface && IsTyping(selected!),
                x,
                y,
                TopiaForgeInputOwnership.SubmitPressedThisFrame,
                deltaSeconds,
                module.moveRepeatDelay,
                module.moveRepeatRate,
                root);
            if (step.IsEmpty)
            {
                return;
            }

            if (step.SelectFirst)
            {
                SelectFirst(system, root);
                return;
            }

            Prepare(system);
            if (step.Move != TopiaForgeMoveDirection.None && selected != null)
            {
                move!.Reset();
                move.moveDir = ToUnity(step.Move);
                move.moveVector = new Vector2(x, y);
                ExecuteEvents.Execute(selected, move, ExecuteEvents.moveHandler);
                if (!IsInside(system.currentSelectedGameObject, root))
                {
                    // Unity's automatic navigation picks the nearest control on any canvas. Past the surface's last control the
                    // selection stays where it was, so neither the move nor a submit with it reaches a surface beneath.
                    system.SetSelectedGameObject(selected);
                }
            }

            var target = system.currentSelectedGameObject;
            if (step.Submit && target != null)
            {
                submit!.Reset();
                ExecuteEvents.Execute(target, submit, ExecuteEvents.submitHandler);
            }
        }

        internal static void Reset()
        {
            events = null;
            move = null;
            submit = null;
            guarded = null;
            guardedRoot = null;
            Controls.Clear();
            Driver.Tick(false, false, false, 0f, 0f, false, 0f, 0f, 0f);
        }

        private static void Guard(GameObject? selected, GameObject? root)
        {
            // Remembers the control selected inside the surface taking navigation, and that surface; the control carries the
            // guard, added once, so a move any sender makes from it stays inside.
            if (root == null || selected == null || !IsInside(selected, root))
            {
                guarded = null;
                guardedRoot = null;
                return;
            }

            guardedRoot = root;
            if (ReferenceEquals(selected, guarded))
            {
                return;
            }

            guarded = selected;
            if (!selected.TryGetComponent(out TopiaForgeMoveGuard _))
            {
                selected.AddComponent<TopiaForgeMoveGuard>();
            }
        }

        private static void Prepare(EventSystem system)
        {
            if (!ReferenceEquals(events, system) || move == null || submit == null)
            {
                events = system;
                move = new AxisEventData(system);
                submit = new BaseEventData(system);
            }
        }

        private static void SelectFirst(EventSystem system, GameObject root)
        {
            root.GetComponentsInChildren(false, Controls);
            for (var index = 0; index < Controls.Count; index++)
            {
                var control = Controls[index];
                if (control != null && control.IsInteractable() && control.navigation.mode != Navigation.Mode.None)
                {
                    system.SetSelectedGameObject(control.gameObject);
                    break;
                }
            }

            Controls.Clear();
        }

        private static bool IsInside(GameObject? target, GameObject root)
        {
            return target != null && target.activeInHierarchy && target.transform.IsChildOf(root.transform);
        }

        private static bool IsTyping(GameObject selected)
        {
            return (selected.TryGetComponent(out TMP_InputField text) && text.isFocused)
                || (selected.TryGetComponent(out InputField legacy) && legacy.isFocused);
        }

        private static MoveDirection ToUnity(TopiaForgeMoveDirection direction)
        {
            switch (direction)
            {
                case TopiaForgeMoveDirection.Left:
                    return MoveDirection.Left;
                case TopiaForgeMoveDirection.Up:
                    return MoveDirection.Up;
                case TopiaForgeMoveDirection.Right:
                    return MoveDirection.Right;
                case TopiaForgeMoveDirection.Down:
                    return MoveDirection.Down;
                default:
                    return MoveDirection.None;
            }
        }
    }

    /// <summary>
    /// Keeps a move from a control inside the surface taking navigation inside it, whoever sends it: Unity's UI module on a
    /// key the game still holds, the kit's legacy module, or the kit's own navigation. Unity sends a move to each handler on
    /// the selected control in turn, and this one is added after the control's own, so it sees where that move went.
    /// </summary>
    internal sealed class TopiaForgeMoveGuard : MonoBehaviour, IMoveHandler
    {
        /// <summary>Undoes the move if it left the surface.</summary>
        public void OnMove(AxisEventData eventData)
        {
            TopiaForgeNavigation.Contain(gameObject);
        }
    }
}
