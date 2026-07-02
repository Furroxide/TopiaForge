using System;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Analytic anti-aliased coverage math for the procedural sprite atlas. Pure and
    /// unit-tested (corner symmetry, straight-edge exactness) — the Unity-side
    /// QwSprites rasterizer only loops pixels and calls into this.
    /// </summary>
    public static class QwRoundedRectMath
    {
        /// <summary>
        /// Signed distance from a pixel-center point to the edge of a rounded rect that
        /// fills a w×h tile with corner radius r. Negative inside, positive outside.
        /// </summary>
        public static float SignedDistance(float x, float y, float width, float height, float radius)
        {
            var halfW = width * 0.5f;
            var halfH = height * 0.5f;
            radius = Math.Min(radius, Math.Min(halfW, halfH));

            var qx = Math.Abs(x - halfW) - (halfW - radius);
            var qy = Math.Abs(y - halfH) - (halfH - radius);
            var ax = Math.Max(qx, 0f);
            var ay = Math.Max(qy, 0f);
            var outside = (float)Math.Sqrt((ax * ax) + (ay * ay));
            var inside = Math.Min(Math.Max(qx, qy), 0f);
            return outside + inside - radius;
        }

        /// <summary>Fill coverage in [0, 1] for the pixel whose center is (x, y).</summary>
        public static float FillCoverage(float x, float y, float width, float height, float radius)
        {
            return Clamp01(0.5f - SignedDistance(x, y, width, height, radius));
        }

        /// <summary>
        /// Ring (border) coverage: filled between the outer edge and an edge inset by
        /// thickness. Inner radius shrinks with the inset so borders stay concentric.
        /// </summary>
        public static float RingCoverage(float x, float y, float width, float height, float radius, float thickness)
        {
            var outer = FillCoverage(x, y, width, height, radius);
            var innerRadius = Math.Max(0f, radius - thickness);
            var inner = InsetFillCoverage(x, y, width, height, innerRadius, thickness);
            return Clamp01(outer - inner);
        }

        /// <summary>Circle coverage for a w×w tile (used for toggle thumbs and pips).</summary>
        public static float CircleCoverage(float x, float y, float diameter)
        {
            var r = diameter * 0.5f;
            var dx = x - r;
            var dy = y - r;
            var dist = (float)Math.Sqrt((dx * dx) + (dy * dy)) - r;
            return Clamp01(0.5f - dist);
        }

        private static float InsetFillCoverage(float x, float y, float width, float height, float radius, float inset)
        {
            var w = width - (inset * 2f);
            var h = height - (inset * 2f);
            if (w <= 0f || h <= 0f)
            {
                return 0f;
            }

            return FillCoverage(x - inset, y - inset, w, h, radius);
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }
    }
}
