using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Screen/panel docking positions.</summary>
    public enum QwCorner
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
    public static class QwAnchors
    {
        public static void Dock(RectTransform rect, QwCorner corner, float margin = QwTokens.SafeMargin)
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

        private static Vector2 AnchorOf(QwCorner corner)
        {
            return corner switch
            {
                QwCorner.TopLeft => new Vector2(0f, 1f),
                QwCorner.Top => new Vector2(0.5f, 1f),
                QwCorner.TopRight => new Vector2(1f, 1f),
                QwCorner.Left => new Vector2(0f, 0.5f),
                QwCorner.Center => new Vector2(0.5f, 0.5f),
                QwCorner.Right => new Vector2(1f, 0.5f),
                QwCorner.BottomLeft => new Vector2(0f, 0f),
                QwCorner.Bottom => new Vector2(0.5f, 0f),
                QwCorner.BottomRight => new Vector2(1f, 0f),
                _ => new Vector2(0.5f, 0.5f),
            };
        }

        private static Vector2 OffsetOf(QwCorner corner, float margin)
        {
            return corner switch
            {
                QwCorner.TopLeft => new Vector2(margin, -margin),
                QwCorner.Top => new Vector2(0f, -margin),
                QwCorner.TopRight => new Vector2(-margin, -margin),
                QwCorner.Left => new Vector2(margin, 0f),
                QwCorner.Center => Vector2.zero,
                QwCorner.Right => new Vector2(-margin, 0f),
                QwCorner.BottomLeft => new Vector2(margin, margin),
                QwCorner.Bottom => new Vector2(0f, margin),
                QwCorner.BottomRight => new Vector2(-margin, margin),
                _ => Vector2.zero,
            };
        }
    }
}
