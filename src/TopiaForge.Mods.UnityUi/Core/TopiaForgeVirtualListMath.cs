using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Visible-range and pool math for TopiaForgeListView's fixed-row-height virtualization.
    /// Pure and unit-tested; the Unity-side list only moves pooled rows to the computed
    /// slots.
    /// </summary>
    public static class TopiaForgeVirtualListMath
    {
        public const int Overscan = 1;

        /// <summary>Total content height for itemCount rows.</summary>
        public static float ContentHeight(int itemCount, float rowHeight, float spacing)
        {
            if (itemCount <= 0)
            {
                return 0f;
            }

            return (itemCount * rowHeight) + ((itemCount - 1) * spacing);
        }

        /// <summary>
        /// First visible index and visible slot count (including overscan) for a scroll
        /// offset measured from the top of the content.
        /// </summary>
        public static (int First, int Count) VisibleRange(
            float scrollOffset,
            float viewportHeight,
            int itemCount,
            float rowHeight,
            float spacing)
        {
            if (itemCount <= 0 || rowHeight <= 0f || viewportHeight <= 0f)
            {
                return (0, 0);
            }

            var stride = rowHeight + spacing;
            var first = (int)Math.Floor(Math.Max(0f, scrollOffset) / stride) - Overscan;
            first = Math.Max(0, Math.Min(first, itemCount));

            var visible = (int)Math.Ceiling(viewportHeight / stride) + 1 + (Overscan * 2);
            var count = Math.Min(visible, itemCount - first);
            return (first, Math.Max(0, count));
        }

        /// <summary>Y offset (from content top, downward-positive) of a row.</summary>
        public static float RowOffset(int index, float rowHeight, float spacing)
        {
            return index * (rowHeight + spacing);
        }

        /// <summary>Pooled row count needed to cover a viewport.</summary>
        public static int PoolSize(float viewportHeight, float rowHeight, float spacing)
        {
            if (rowHeight <= 0f)
            {
                return 0;
            }

            var stride = rowHeight + spacing;
            return (int)Math.Ceiling(viewportHeight / stride) + 1 + (Overscan * 2);
        }

        /// <summary>Clamps a scroll offset to the valid content range.</summary>
        public static float ClampScroll(float scrollOffset, float viewportHeight, int itemCount, float rowHeight, float spacing)
        {
            var max = Math.Max(0f, ContentHeight(itemCount, rowHeight, spacing) - viewportHeight);
            return scrollOffset < 0f ? 0f : scrollOffset > max ? max : scrollOffset;
        }

        /// <summary>Scroll offset that brings a row fully into view with minimal movement.</summary>
        public static float ScrollToRow(int index, float currentOffset, float viewportHeight, int itemCount, float rowHeight, float spacing)
        {
            var top = RowOffset(index, rowHeight, spacing);
            var bottom = top + rowHeight;
            if (top < currentOffset)
            {
                return ClampScroll(top, viewportHeight, itemCount, rowHeight, spacing);
            }

            if (bottom > currentOffset + viewportHeight)
            {
                return ClampScroll(bottom - viewportHeight, viewportHeight, itemCount, rowHeight, spacing);
            }

            return currentOffset;
        }
    }
}
