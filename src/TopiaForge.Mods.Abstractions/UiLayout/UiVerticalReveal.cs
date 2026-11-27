using System;

namespace TopiaForge.Mods
{
    // Tracks the top positions from which a protected rectangle can remain wholly visible. Multiline text
    // can reveal a complete line at a time; its first and last bands must each be reachable.
    internal struct UiVerticalReveal
    {
        private readonly double height;
        private double firstMinimum;
        private double firstMaximum;
        private double lastMinimum;
        private double lastMaximum;

        internal UiVerticalReveal(double top, double extent, double? lineHeight = null)
        {
            height = Math.Min(extent, lineHeight ?? extent);
            firstMinimum = firstMaximum = top;
            lastMinimum = lastMaximum = top + extent - height;
        }

        internal bool Project(double top, double bottom, double minimumShift = 0, double maximumShift = 0,
            double tolerance = 0.000000001d)
        {
            if (firstMinimum > firstMaximum + tolerance || lastMinimum > lastMaximum + tolerance) return false;
            var ceiling = bottom - height;
            firstMinimum = Math.Max(top, firstMinimum + minimumShift);
            firstMaximum = Math.Min(ceiling, firstMaximum + maximumShift);
            lastMinimum = Math.Max(top, lastMinimum + minimumShift);
            lastMaximum = Math.Min(ceiling, lastMaximum + maximumShift);
            return firstMinimum <= firstMaximum + tolerance && lastMinimum <= lastMaximum + tolerance;
        }
    }
}
