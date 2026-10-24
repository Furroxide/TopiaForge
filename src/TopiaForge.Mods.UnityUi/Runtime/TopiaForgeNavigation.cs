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
    /// repeat delay and rate). Back stays the dismiss stack's. A legacy UI module reads its own keys, so it is left alone.
    /// Nothing here allocates per frame. Main thread only.
    /// </summary>
    internal static class TopiaForgeNavigation
    {
        private static readonly TopiaForgeNavigationDriver Driver = new TopiaForgeNavigationDriver();
        private static readonly List<Selectable> Controls = new List<Selectable>();
        private static EventSystem? events;
        private static AxisEventData? move;
        private static BaseEventData? submit;

        internal static void Tick(float deltaSeconds)
        {
            var root = TopiaForgeDismissStack.FocusRoot;
            var system = EventSystem.current;
            var module = system != null ? system.currentInputModule as InputSystemUIInputModule : null;
            TopiaForgeInputOwnership.Direction(out var x, out var y);
            if (root == null || system == null || module == null)
            {
                Driver.Tick(false, false, false, x, y, false, deltaSeconds, 0f, 0f);
                return;
            }

            var selected = system.currentSelectedGameObject;
            var inSurface = selected != null && selected.activeInHierarchy && selected.transform.IsChildOf(root.transform);
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
            Controls.Clear();
            Driver.Tick(false, false, false, 0f, 0f, false, 0f, 0f, 0f);
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
}
