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

        /// <summary>
        /// Gets a color drawn at an opacity over an opaque one, as an opaque color. Unity blends the stored sRGB values in
        /// a gamma project and linear light in a linear one, so the caller says which.
        /// </summary>
        internal static TopiaForgeRgba Over(TopiaForgeRgba top, float opacity, TopiaForgeRgba bottom, bool linear)
        {
            return new TopiaForgeRgba(
                Blend(top.R, bottom.R, opacity, linear),
                Blend(top.G, bottom.G, opacity, linear),
                Blend(top.B, bottom.B, opacity, linear),
                1f);
        }

        /// <summary>
        /// Gets a color as uGUI draws it: every channel, opacity included, rounded to one of 256 steps, as a Color32 vertex
        /// color and an 8-bit target hold it. A contrast that must hold on screen is checked on these values.
        /// </summary>
        internal static TopiaForgeRgba Quantize(TopiaForgeRgba color)
        {
            return new TopiaForgeRgba(QuantizeChannel(color.R), QuantizeChannel(color.G), QuantizeChannel(color.B), QuantizeChannel(color.A));
        }

        private static float QuantizeChannel(float channel)
        {
            // Rounds half to even, as Unity's Color to Color32 conversion does.
            channel = channel < 0f ? 0f : channel > 1f ? 1f : channel;
            return (float)Math.Round(channel * 255f) / 255f;
        }

        private static float Blend(float top, float bottom, float opacity, bool linear)
        {
            return linear
                ? Delinearize(Lerp(Linearize(bottom), Linearize(top), opacity))
                : Lerp(bottom, top, opacity);
        }

        private static float Linearize(float channel)
        {
            channel = channel < 0f ? 0f : channel > 1f ? 1f : channel;
            return channel <= 0.03928f
                ? channel / 12.92f
                : (float)Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        private static float Delinearize(float channel)
        {
            channel = channel < 0f ? 0f : channel > 1f ? 1f : channel;
            return channel <= 0.0031308f
                ? channel * 12.92f
                : (float)((1.055 * Math.Pow(channel, 1.0 / 2.4)) - 0.055);
        }

        private static float Lerp(float from, float to, float t)
        {
            return from + ((to - from) * t);
        }
    }
}
