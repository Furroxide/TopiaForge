using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// How far a scroll view's content moves along one axis so a control shows in full, Unity-free. Spans are in the
    /// viewport's own space, growing up or right: the view from <c>viewMin</c> to <c>viewMax</c>, the control from
    /// <c>itemMin</c> to <c>itemMax</c>. A control already shown in full needs no move. One cut off below or to the left moves
    /// in until its edge is a margin inside the view, as one cut off above or to the right does, and one larger than the
    /// view shows its top or left edge, where its label reads. The content moves by the same amount.
    /// </summary>
    internal static class TopiaForgeScrollReveal
    {
        /// <summary>The space the kit keeps between a control it scrolls into view and the view's edge, in the view's units.</summary>
        public const float Margin = 8f;

        /// <summary>
        /// Gets how far to move the content along a vertical axis, growing up, so the control shows: positive moves it up.
        /// A control larger than the view shows its top edge.
        /// </summary>
        public static float Vertical(float viewMin, float viewMax, float itemMin, float itemMax, float margin = Margin)
        {
            return Delta(viewMin, viewMax, itemMin, itemMax, margin, startAtMax: true);
        }

        /// <summary>
        /// Gets how far to move the content along a horizontal axis, growing right, so the control shows: positive moves it
        /// right. A control larger than the view shows its left edge.
        /// </summary>
        public static float Horizontal(float viewMin, float viewMax, float itemMin, float itemMax, float margin = Margin)
        {
            return Delta(viewMin, viewMax, itemMin, itemMax, margin, startAtMax: false);
        }

        private static float Delta(float viewMin, float viewMax, float itemMin, float itemMax, float margin, bool startAtMax)
        {
            if (!(viewMax > viewMin) || !(itemMax >= itemMin) || float.IsNaN(margin) || float.IsInfinity(viewMin + viewMax + itemMin + itemMax))
            {
                return 0f;
            }

            if (itemMin >= viewMin && itemMax <= viewMax)
            {
                return 0f;
            }

            // The margin shrinks so a control that nearly fills the view still fits inside it.
            var spare = Math.Max(0f, Math.Min(margin, ((viewMax - viewMin) - (itemMax - itemMin)) / 2f));
            if (itemMax - itemMin > viewMax - viewMin)
            {
                return startAtMax ? (viewMax - spare) - itemMax : (viewMin + spare) - itemMin;
            }

            return itemMax > viewMax ? (viewMax - spare) - itemMax : (viewMin + spare) - itemMin;
        }
    }
}
