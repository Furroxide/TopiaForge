using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Contrast and accessibility color math. Unity-free and covered by parity tests
    /// against the original Zombies HudColor implementation.
    /// </summary>
    public static class TopiaForgeContrast
    {
        /// <summary>
        /// High-contrast emphasis transform. Exact port of ZombiesHudBehaviour.HudColor:
        /// normalize by the max channel, then lerp 25% toward white. Alpha is preserved.
        /// </summary>
        public static TopiaForgeRgba Emphasize(TopiaForgeRgba color)
        {
            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            if (max <= 0f)
            {
                return color;
            }

            return new TopiaForgeRgba(
                Lerp(color.R / max, 1f, 0.25f),
                Lerp(color.G / max, 1f, 0.25f),
                Lerp(color.B / max, 1f, 0.25f),
                color.A);
        }

        /// <summary>WCAG relative luminance (sRGB linearized).</summary>
        public static float Luminance(TopiaForgeRgba color)
        {
            return (0.2126f * Linearize(color.R)) + (0.7152f * Linearize(color.G)) + (0.0722f * Linearize(color.B));
        }

        /// <summary>WCAG contrast ratio between two opaque colors, in [1, 21].</summary>
        public static float Ratio(TopiaForgeRgba a, TopiaForgeRgba b)
        {
            var la = Luminance(a);
            var lb = Luminance(b);
            var lighter = Math.Max(la, lb);
            var darker = Math.Min(la, lb);
            return (lighter + 0.05f) / (darker + 0.05f);
        }

        /// <summary>
        /// Darkens an accent until it reads against a light surface (>= 4.5:1), so a mod
        /// can pick any accent without producing illegible Paper-scheme text/rings.
        /// Deterministic: multiplies RGB by 0.92 per step, up to 48 steps.
        /// </summary>
        public static TopiaForgeRgba DarkenForPaper(TopiaForgeRgba accent, TopiaForgeRgba surface, float minimumRatio = 4.5f)
        {
            var current = accent;
            for (var step = 0; step < 48; step++)
            {
                if (Ratio(current, surface) >= minimumRatio)
                {
                    return current;
                }

                current = current.Scale(0.92f);
            }

            return current;
        }

        private static float Linearize(float channel)
        {
            channel = channel < 0f ? 0f : channel > 1f ? 1f : channel;
            return channel <= 0.03928f
                ? channel / 12.92f
                : (float)Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        private static float Lerp(float from, float to, float t)
        {
            return from + ((to - from) * t);
        }
    }
}
