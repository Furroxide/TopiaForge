using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Screen/panel docking positions.</summary>
    public enum TopiaForgeCorner
    {
        TopLeft,
        Top,
        TopRight,
        Left,
        Center,
        Right,
        BottomLeft,
        Bottom,
        BottomRight,
    }

    /// <summary>
    /// Anchoring presets replacing the hand-rolled Place() helpers that Zombies and
    /// UgcLiveSync each duplicated. HUD panels dock to corners/edges so layouts stay
    /// correct on ultrawide screens.
    /// </summary>
    public static class TopiaForgeAnchors
    {
        public static void Dock(RectTransform rect, TopiaForgeCorner corner, float margin = TopiaForgeTokens.SafeMargin)
        {
            var anchor = AnchorOf(corner);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = OffsetOf(corner, margin);
        }

        /// <summary>Top-left anchored placement (the old Place() semantics: y grows downward).</summary>
        public static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        public static void Stretch(RectTransform rect, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        private static Vector2 AnchorOf(TopiaForgeCorner corner)
        {
            return corner switch
            {
                TopiaForgeCorner.TopLeft => new Vector2(0f, 1f),
                TopiaForgeCorner.Top => new Vector2(0.5f, 1f),
                TopiaForgeCorner.TopRight => new Vector2(1f, 1f),
                TopiaForgeCorner.Left => new Vector2(0f, 0.5f),
                TopiaForgeCorner.Center => new Vector2(0.5f, 0.5f),
                TopiaForgeCorner.Right => new Vector2(1f, 0.5f),
                TopiaForgeCorner.BottomLeft => new Vector2(0f, 0f),
                TopiaForgeCorner.Bottom => new Vector2(0.5f, 0f),
                TopiaForgeCorner.BottomRight => new Vector2(1f, 0f),
                _ => new Vector2(0.5f, 0.5f),
            };
        }

        private static Vector2 OffsetOf(TopiaForgeCorner corner, float margin)
        {
            return corner switch
            {
                TopiaForgeCorner.TopLeft => new Vector2(margin, -margin),
                TopiaForgeCorner.Top => new Vector2(0f, -margin),
                TopiaForgeCorner.TopRight => new Vector2(-margin, -margin),
                TopiaForgeCorner.Left => new Vector2(margin, 0f),
                TopiaForgeCorner.Center => Vector2.zero,
                TopiaForgeCorner.Right => new Vector2(-margin, 0f),
                TopiaForgeCorner.BottomLeft => new Vector2(margin, margin),
                TopiaForgeCorner.Bottom => new Vector2(0f, margin),
                TopiaForgeCorner.BottomRight => new Vector2(-margin, margin),
                _ => Vector2.zero,
            };
        }
    }
}
