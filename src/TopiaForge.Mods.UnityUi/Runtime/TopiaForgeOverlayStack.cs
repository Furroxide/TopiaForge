using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Coordinates paint and input for overlays belonging to different scopes.</summary>
    internal static class TopiaForgeOverlayStack
    {
        private static readonly List<TopiaForgeOverlay> CreationOrder = new List<TopiaForgeOverlay>();
        private static TopiaForgeOverlay? inputOwner;
        private static GameObject? previousSelection;
        private static bool gamePaused;

        internal static bool OwnsInput => inputOwner != null;

        internal static bool AllowsKeyboardPause =>
            inputOwner is { Dismissible: false } && ReferenceEquals(inputOwner, TopiaForgeDismissStack.Top());

        internal static void RefreshCoveredFocus() => inputOwner?.RefreshFocus();

        internal static bool IsCovered(TopiaForgeOverlay overlay)
        {
            var order = overlay.Canvas.sortingOrder;
            foreach (var candidate in CreationOrder)
                if (candidate.IsPainted && candidate.Go.activeInHierarchy && candidate.Canvas.sortingOrder > order)
                    return true;
            return false;
        }

        internal static void Add(TopiaForgeOverlay overlay)
        {
            // The allocator can reuse a released low slot. Move that slot through the existing owners so the
            // new overlay receives the highest allocated order, even after out-of-order destruction.
            TopiaForgeLayers.TryGetOrder(overlay.CanvasRoot, out var slot);
            foreach (var earlier in CreationOrder)
            {
                if (TopiaForgeLayers.TryGetOrder(earlier.CanvasRoot, out var earlierOrder) && earlierOrder > slot)
                {
                    TopiaForgeLayers.AssignAllocatedOrder(earlier.Canvas, slot);
                    slot = earlierOrder;
                }
            }
            TopiaForgeLayers.AssignAllocatedOrder(overlay.Canvas, slot);
            CreationOrder.Add(overlay);
        }

        internal static void Remove(TopiaForgeOverlay overlay)
        {
            if (CreationOrder.Remove(overlay)) ReconcileInput();
        }

        internal static void SetGamePaused(bool paused)
        {
            if (gamePaused == paused) return;
            gamePaused = paused;
            if (paused) ReconcileInput();
            foreach (var overlay in CreationOrder) overlay.ApplyPauseVisibility(paused);
            if (!paused) ReconcileInput();
        }

        internal static void Refresh(TopiaForgeOverlay overlay)
        {
            overlay.ApplyPauseVisibility(gamePaused);
            ReconcileInput();
        }

        private static void ReconcileInput()
        {
            TopiaForgeOverlay? next = null;
            if (!gamePaused)
            {
                for (var index = CreationOrder.Count - 1; index >= 0; index--)
                {
                    var overlay = CreationOrder[index];
                    if (!overlay.IsPainted) continue;
                    if (overlay.AcceptsInput) next = overlay;
                    break;
                }
            }

            if (ReferenceEquals(inputOwner, next)) return;
            var events = EventSystem.current;
            if (inputOwner == null && events != null) previousSelection = events.currentSelectedGameObject;
            inputOwner?.SetInputOwner(false);
            inputOwner = next;
            if (next != null)
            {
                next.SetInputOwner(true);
            }
            else
            {
                if (!gamePaused && events != null && events.currentSelectedGameObject == null &&
                    previousSelection != null && previousSelection.activeInHierarchy &&
                    CanRestoreSelection(previousSelection))
                    events.SetSelectedGameObject(previousSelection);

                previousSelection = null;
            }
        }

        private static bool CanRestoreSelection(GameObject selection)
        {
            var top = TopiaForgeDismissStack.Top();
            if (top == null || TopiaForgeLayerPriority.Rank(top.Band) <= TopiaForgeLayerPriority.Rank(TopiaForgeLayerPriority.Overlay))
                return true;

            var root = TopiaForgeDismissStack.FocusRoot;
            return root != null && selection.transform.IsChildOf(root.transform);
        }
    }
}
