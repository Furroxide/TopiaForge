using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Title-bar drag behavior.</summary>
    internal sealed class QwWindowDrag : MonoBehaviour, IDragHandler, IEndDragHandler, IPointerDownHandler
    {
        public QwWindow? Window;

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
    internal sealed class QwWindowFocus : MonoBehaviour, IPointerDownHandler
    {
        public QwWindow? Window;

        public void OnPointerDown(PointerEventData eventData)
        {
            Window?.BringToFront();
        }
    }

    /// <summary>
    /// Process-wide window z-order. Focus permutes only the canvas orders allocated to
    /// registered windows, preserving custom layers in the same band and avoiding new allocations.
    /// </summary>
    internal static class QwWindowRegistry
    {
        private static readonly List<QwWindow> Order = new List<QwWindow>();
        private static readonly List<int> AllocatedSlots = new List<int>();

        public static void Register(QwWindow window)
        {
            if (Order.Contains(window))
            {
                return;
            }

            Order.Add(window);
            var canvas = window.OwnCanvas;
            if (canvas != null)
            {
                AllocatedSlots.Add(canvas.sortingOrder);
                AllocatedSlots.Sort();
            }

            Reassign();
        }

        public static void Unregister(QwWindow window)
        {
            var canvas = window.OwnCanvas;
            if (!Order.Remove(window))
            {
                return;
            }

            if (canvas != null)
            {
                AllocatedSlots.Remove(canvas.sortingOrder);
            }

            Reassign();
        }

        public static void BringToFront(QwWindow window)
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
                    QwLayers.AssignAllocatedOrder(canvas, AllocatedSlots[index]);
                }
            }
        }
    }
}
