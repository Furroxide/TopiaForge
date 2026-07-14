using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Raw image handle with dirty-checked setters — the building block for reticles,
    /// hit markers, vignettes, and flashes that reposition/retint every frame.
    /// </summary>
    public sealed class TopiaForgeImage : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly Image image;
        private TopiaForgeTone tone = TopiaForgeTone.Neutral;
        private bool hasCustomColor;
        private Color customColor = Color.white;
        private float alpha = 1f;
        private float lastX = float.NaN;
        private float lastY = float.NaN;
        private float lastWidth = float.NaN;
        private float lastHeight = float.NaN;
        private float lastRotation;

        internal TopiaForgeImage(TopiaForgeContainer parent, string name, bool free)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject(name))
        {
            image = Go.AddComponent<Image>();
            image.sprite = TopiaForgeSprites.White;
            image.raycastTarget = false;
            if (free)
            {
                this.Free();
            }

            ApplyTheme(Theme);
        }

        public Image Image => image;

        /// <summary>Uses an atlas sprite (rounded fill/ring/icon) instead of the flat white fill.</summary>
        public TopiaForgeImage Sprite(Sprite sprite, bool sliced = false)
        {
            image.sprite = sprite;
            image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
            return this;
        }

        public TopiaForgeImage Icon(TopiaForgeIcon icon)
        {
            return Sprite(TopiaForgeSprites.Icon(icon));
        }

        /// <summary>Top-left anchored placement (old Place() semantics), dirty-checked.</summary>
        public void SetRect(float x, float y, float width, float height)
        {
            if (x == lastX && y == lastY && width == lastWidth && height == lastHeight)
            {
                return;
            }

            lastX = x;
            lastY = y;
            lastWidth = width;
            lastHeight = height;
            TopiaForgeAnchors.Place(Rect, x, y, width, height);
        }

        /// <summary>Anchored position update (dirty-checked) without touching size.</summary>
        public void SetPosition(float x, float y)
        {
            if (x == lastX && y == lastY)
            {
                return;
            }

            lastX = x;
            lastY = y;
            Rect.anchoredPosition = new Vector2(x, y);
        }

        public void SetSize(float width, float height)
        {
            if (width == lastWidth && height == lastHeight)
            {
                return;
            }

            lastWidth = width;
            lastHeight = height;
            Rect.sizeDelta = new Vector2(width, height);
        }

        public void SetColor(Color color)
        {
            hasCustomColor = true;
            customColor = color;
            alpha = color.a;
            ApplyTheme(Theme);
        }

        /// <summary>Uses a theme semantic tone and follows accessibility theme changes.</summary>
        public void SetTone(TopiaForgeTone value)
        {
            if (!hasCustomColor && tone == value)
            {
                return;
            }

            tone = value;
            hasCustomColor = false;
            ApplyTheme(Theme);
        }

        public void SetAlpha(float alpha)
        {
            this.alpha = alpha;
            var color = image.color;
            if (color.a != alpha)
            {
                color.a = alpha;
                image.color = color;
            }
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            var color = hasCustomColor ? theme.Emphasize(customColor) : theme.ToneColor(tone);
            color.a = alpha;
            if (image.color != color)
            {
                image.color = color;
            }
        }

        public void SetRotation(float degrees)
        {
            if (degrees == lastRotation)
            {
                return;
            }

            lastRotation = degrees;
            Rect.localEulerAngles = new Vector3(0f, 0f, degrees);
        }
    }
}
