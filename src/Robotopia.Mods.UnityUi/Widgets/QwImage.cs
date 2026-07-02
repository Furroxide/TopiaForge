using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Raw image handle with dirty-checked setters — the building block for reticles,
    /// hit markers, vignettes, and flashes that reposition/retint every frame.
    /// </summary>
    public sealed class QwImage : QwWidget
    {
        private readonly Image image;
        private float lastX = float.NaN;
        private float lastY = float.NaN;
        private float lastWidth = float.NaN;
        private float lastHeight = float.NaN;
        private float lastRotation;

        internal QwImage(QwContainer parent, string name, bool free)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject(name))
        {
            image = Go.AddComponent<Image>();
            image.sprite = QwSprites.White;
            image.raycastTarget = false;
            if (free)
            {
                this.Free();
            }
        }

        public Image Image => image;

        /// <summary>Uses an atlas sprite (rounded fill/ring/icon) instead of the flat white fill.</summary>
        public QwImage Sprite(Sprite sprite, bool sliced = false)
        {
            image.sprite = sprite;
            image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
            return this;
        }

        public QwImage Icon(QwIcon icon)
        {
            return Sprite(QwSprites.Icon(icon));
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
            QwAnchors.Place(Rect, x, y, width, height);
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
            var emphasized = Theme.Emphasize(color);
            if (image.color != emphasized)
            {
                image.color = emphasized;
            }
        }

        public void SetAlpha(float alpha)
        {
            var color = image.color;
            if (color.a != alpha)
            {
                color.a = alpha;
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
