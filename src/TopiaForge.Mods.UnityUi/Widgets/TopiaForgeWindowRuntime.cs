using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Title-bar drag behavior.</summary>
    internal sealed class TopiaForgeWindowDrag : MonoBehaviour, IDragHandler, IEndDragHandler, IPointerDownHandler
    {
        public TopiaForgeWindow? Window;

        public void OnPointerDown(PointerEventData eventData)
        {
            Window?.BringToFront();
        }

        public void OnDrag(PointerEventData eventData)
        {
            Window?.HandleDrag(eventData.delta);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            Window?.HandleDragEnd();
        }
    }

    /// <summary>Click-anywhere-to-front behavior on the window body.</summary>
    internal sealed class TopiaForgeWindowFocus : MonoBehaviour, IPointerDownHandler
    {
        public TopiaForgeWindow? Window;

        public void OnPointerDown(PointerEventData eventData)
        {
            Window?.BringToFront();
        }
    }

    /// <summary>
    /// Finishes a show-time clamp on the first frame the canvas has a size. Enabled only while
    /// a clamp is pending, so it costs nothing per frame otherwise.
    /// </summary>
    internal sealed class TopiaForgeWindowDeferredClamp : MonoBehaviour
    {
        public TopiaForgeWindow? Window;

        private void Update()
        {
            if (Window == null || Window.FinishDeferredClamp())
            {
                enabled = false;
            }
        }
    }

    /// <summary>
    /// Sits on a window's canvas and re-places the open window when the canvas changes size, as a new resolution or UI
    /// scale does: the deferred clamp then runs on the next frame, so a title bar never stays off screen until the window
    /// is reopened. Unity calls it only when the size changes, so it costs nothing per frame.
    /// </summary>
    internal sealed class TopiaForgeWindowCanvasResize : MonoBehaviour
    {
        public TopiaForgeWindow? Window;

        private void OnRectTransformDimensionsChange()
        {
            Window?.ArmClamp();
        }
    }

    /// <summary>
    /// Process-wide window z-order. Focus permutes only the canvas orders allocated to
    /// registered windows, preserving custom layers in the same band and avoiding new allocations.
    /// </summary>
    internal static class TopiaForgeWindowRegistry
    {
        private static readonly List<TopiaForgeWindow> Order = new List<TopiaForgeWindow>();
        private static readonly List<int> AllocatedSlots = new List<int>();

        public static void Register(TopiaForgeWindow window)
        {
            if (Order.Contains(window))
            {
                return;
            }

            Order.Add(window);

            // A window's slot comes from the allocator's record, never from its canvas, which a scene or another mod may
            // already have destroyed by the time the window is unregistered.
            if (TopiaForgeLayers.TryGetOrder(window.CanvasRoot, out var slot))
            {
                AllocatedSlots.Add(slot);
                AllocatedSlots.Sort();
            }

            Reassign();
        }

        public static void Unregister(TopiaForgeWindow window)
        {
            if (!Order.Remove(window))
            {
                return;
            }

            if (TopiaForgeLayers.TryGetOrder(window.CanvasRoot, out var slot))
            {
                AllocatedSlots.Remove(slot);
            }

            Reassign();
        }

        public static void BringToFront(TopiaForgeWindow window)
        {
            var index = Order.IndexOf(window);
            if (index < 0 || index == Order.Count - 1)
            {
                return;
            }

            Order.RemoveAt(index);
            Order.Add(window);
            Reassign();
        }

        private static void Reassign()
        {
            var count = Math.Min(Order.Count, AllocatedSlots.Count);
            for (var index = 0; index < count; index++)
            {
                var canvas = Order[index].OwnCanvas;
                if (canvas != null)
                {
                    TopiaForgeLayers.AssignAllocatedOrder(canvas, AllocatedSlots[index]);
                }
            }
        }
    }
}
