using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Keeps the control the keyboard or a controller moves to in view, once a frame as the kit's driver runs. A move could
    /// select a control a kit scroll view had scrolled out of sight, and nothing showed. When the EventSystem selection moves
    /// to a TopiaForge control inside a scroll view's content that does not show in full, the content moves just enough to
    /// show it (TopiaForgeScrollReveal), in each scroll view around it, innermost first. A control the mouse clicks already
    /// shows, so the mouse never scrolls anything this way. Nothing here allocates. Main thread only.
    /// </summary>
    internal static class TopiaForgeScrollFollow
    {
        private static readonly Vector3[] Corners = new Vector3[4];
        private static GameObject? followed;

        /// <summary>Shows the selection if it moved since the last frame.</summary>
        internal static void Tick()
        {
            var events = EventSystem.current;
            var selected = events != null ? events.currentSelectedGameObject : null;
            if (ReferenceEquals(selected, followed))
            {
                return;
            }

            followed = selected;
            if (selected != null && selected.activeInHierarchy && TopiaForgeInputOwnership.IsKitObject(selected)
                && selected.transform is RectTransform item)
            {
                Reveal(item);
            }
        }

        /// <summary>Forgets the selection followed, as the kit's driver stops.</summary>
        internal static void Reset()
        {
            followed = null;
        }

        private static void Reveal(RectTransform item)
        {
            for (var at = item.parent; at != null; at = at.parent)
            {
                if (!at.TryGetComponent(out ScrollRect scroll) || scroll.content == null || !item.IsChildOf(scroll.content))
                {
                    continue;
                }

                var view = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
                var bounds = view.rect;
                item.GetWorldCorners(Corners);
                var low = view.InverseTransformPoint(Corners[0]);
                var high = view.InverseTransformPoint(Corners[2]);
                var x = scroll.horizontal ? TopiaForgeScrollReveal.Horizontal(bounds.xMin, bounds.xMax, low.x, high.x) : 0f;
                var y = scroll.vertical ? TopiaForgeScrollReveal.Vertical(bounds.yMin, bounds.yMax, low.y, high.y) : 0f;
                if (x != 0f || y != 0f)
                {
                    scroll.StopMovement();
                    var position = scroll.content.anchoredPosition;
                    scroll.content.anchoredPosition = new Vector2(position.x + x, position.y + y);
                }
            }
        }
    }
}
