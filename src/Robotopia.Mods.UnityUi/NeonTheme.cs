using System;
using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    [Obsolete("Replaced by QwPalette/QwSchemes (brand tokens) - see docs/UiKit.md. NeonTheme will be removed once all consumers migrate.")]
    public static class NeonTheme
    {
        public static readonly Color Ink = new Color(0.015f, 0.025f, 0.04f, 0.96f);
        public static readonly Color Panel = new Color(0.035f, 0.055f, 0.085f, 0.92f);
        public static readonly Color PanelAlt = new Color(0.055f, 0.075f, 0.105f, 0.96f);
        public static readonly Color PanelSoft = new Color(0.045f, 0.065f, 0.09f, 0.72f);
        public static readonly Color Cyan = new Color(0.20f, 0.92f, 1f, 1f);
        public static readonly Color CyanDim = new Color(0.12f, 0.55f, 0.68f, 1f);
        public static readonly Color Acid = new Color(0.52f, 1f, 0.28f, 1f);
        public static readonly Color Amber = new Color(1f, 0.74f, 0.20f, 1f);
        public static readonly Color Violet = new Color(0.66f, 0.50f, 1f, 1f);
        public static readonly Color Danger = new Color(1f, 0.24f, 0.20f, 1f);
        public static readonly Color Text = new Color(0.92f, 0.98f, 1f, 1f);
        public static readonly Color TextMuted = new Color(0.58f, 0.72f, 0.82f, 1f);
        public static readonly Color Line = new Color(0.18f, 0.82f, 1f, 0.72f);
        public static readonly Color Dim = new Color(0f, 0f, 0f, 0.62f);

        public static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }
    }
}
