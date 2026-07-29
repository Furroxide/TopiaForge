using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Draggable brand window: card chrome (radius 26, orange border, hard shadow),
    /// 42px title bar with an Audiowide title and close button, ESC-close via the dismiss
    /// stack, automatic cursor lease while visible, screen clamping + edge snapping,
    /// and rect persistence keyed owner+windowId in the host's state store.
    /// </summary>
    public sealed class TopiaForgeWindow : TopiaForgeContainer, ITopiaForgeThemeAware, ITopiaForgeDismissable
    {
        private readonly UImage shadow;
        private readonly GameObject canvasRoot;
        private readonly UImage fill;
        private readonly UImage ring;
        private readonly UImage titleBarFill;
        private readonly TextMeshProUGUI titleLabel;
        private readonly TopiaForgeCursorLease cursorLease = new TopiaForgeCursorLease();
        private readonly string persistKey;
        private readonly bool autoHeight;
        private bool open;
        private bool closing;
        private bool tornDown;

        internal TopiaForgeWindow(UiHost host, TopiaForgeContainer layerRoot, string id, string title, float width, float height)
            : base(host, layerRoot.Scheme, layerRoot.CreateChildGameObject("Window"))
        {
            canvasRoot = layerRoot.Go;
            persistKey = "win:" + id;
            autoHeight = height <= 0f;

            Rect.anchorMin = new Vector2(0.5f, 0.5f);
            Rect.anchorMax = new Vector2(0.5f, 0.5f);
            Rect.pivot = new Vector2(0.5f, 0.5f);
            Rect.sizeDelta = new Vector2(width, autoHeight ? 200f : height);

            shadow = CreateDecor("Shadow", TopiaForgeSprites.Fill(TopiaForgeRadius.Card), raycast: false);
            var shadowRect = shadow.rectTransform;
            shadowRect.offsetMin = new Vector2(TopiaForgeTokens.ShadowCardX, TopiaForgeTokens.ShadowCardY);
            shadowRect.offsetMax = new Vector2(TopiaForgeTokens.ShadowCardX, TopiaForgeTokens.ShadowCardY);

            fill = CreateDecor("Fill", TopiaForgeSprites.Fill(TopiaForgeRadius.Card), raycast: true);
            ring = CreateDecor("Ring", TopiaForgeSprites.Ring(TopiaForgeRadius.Card, TopiaForgeTokens.BorderStandard), raycast: false);

            TopiaForgeLayout.ApplyColumn(Go, TopiaForgeGap.None, TopiaForgeGap.None);
            if (autoHeight)
            {
                var fitter = Go.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            // Title bar (drag handle).
            var titleBarGo = CreateChildGameObject("TitleBar");
            var titleBarLayout = titleBarGo.AddComponent<LayoutElement>();
            titleBarLayout.minHeight = TopiaForgeTokens.TitleBarHeight;
            titleBarLayout.preferredHeight = TopiaForgeTokens.TitleBarHeight;
            titleBarFill = titleBarGo.AddComponent<UImage>();
            titleBarFill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Card);
            titleBarFill.type = UImage.Type.Sliced;
            titleBarFill.raycastTarget = true;
            TopiaForgeLayout.ApplyRow(titleBarGo, TopiaForgeGap.Sm, TopiaForgeGap.None);
            var drag = titleBarGo.AddComponent<TopiaForgeWindowDrag>();
            drag.Window = this;

            var titleBar = new TopiaForgeContainer(Host, Scheme, titleBarGo);
            var titleGo = titleBar.CreateChildGameObject("Title");
            titleLabel = TopiaForgeTmp.Create(titleGo);
            titleLabel.fontSize = TopiaForgeTokens.TitleSize;
            titleLabel.alignment = TextAlignmentOptions.Left;
            titleLabel.textWrappingMode = TextWrappingModes.NoWrap;
            var displayFont = TopiaForgeFonts.For(TopiaForgeTextStyle.Title);
            if (displayFont != null)
            {
                titleLabel.font = displayFont;
            }

            if (TopiaForgeFonts.UseFauxDisplay)
            {
                titleLabel.fontStyle = FontStyles.Bold;
            }

            titleLabel.text = title;
            var titleLayout = titleGo.AddComponent<LayoutElement>();
            titleLayout.flexibleWidth = 1f;
            titleLayout.minHeight = TopiaForgeTokens.TitleBarHeight;
            titleLabel.margin = new Vector4(16f, 0f, 0f, 0f);

            titleBar.IconButton(TopiaForgeIcon.Cross, Close, TopiaForgeButtonStyle.Ghost).Fixed(34f, 34f);

            // Content area.
            Content = Column(TopiaForgeGap.Md, TopiaForgeGap.Lg);
            Content.Flex(1f, autoHeight ? 0f : 1f);

            // Focus on click.
            var focus = Go.AddComponent<TopiaForgeWindowFocus>();
            focus.Window = this;

            RestoreRect();
            TopiaForgeWindowRegistry.Register(this);
            Go.SetActive(false);
            ApplyTheme(Theme);
        }

        /// <summary>Window body — add content here.</summary>
        public TopiaForgeContainer Content { get; }

        /// <summary>Gets whether the window is open.</summary>
        public bool IsOpen => open;

        /// <summary>Raised after the window closes.</summary>
        public event Action? Closed;

        TopiaForgeLayerBand ITopiaForgeDismissable.Band => TopiaForgeLayerBand.Window;

        void ITopiaForgeDismissable.Dismiss()
        {
            Close();
        }

        /// <summary>Sets the title.</summary>
        public void SetTitle(string title)
        {
            ThrowIfTornDown();
            titleLabel.text = title;
        }

        /// <summary>Shows this UI element.</summary>
        public void Show()
        {
            ThrowIfTornDown();
            if (open)
            {
                BringToFront();
                return;
            }

            open = true;
            closing = false;
            Go.SetActive(true);
            ClampToScreen();
            TopiaForgeMotion.WindowIn(this);
            cursorLease.Acquire();
            TopiaForgeDismissStack.Push(this);
            BringToFront();
        }

        /// <summary>Closes this UI element.</summary>
        public void Close()
        {
            if (tornDown || !open || closing)
            {
                return;
            }

            closing = true;
            cursorLease.Release();
            TopiaForgeDismissStack.Remove(this);
            PersistRect();
            TopiaForgeMotion.WindowOut(this, () =>
            {
                closing = false;
                open = false;
                if (Go != null)
                {
                    Go.SetActive(false);
                }

                RaiseClosed();
            });
        }

        /// <summary>Toggles this UI element between open and closed.</summary>
        public void Toggle()
        {
            if (open)
            {
                Close();
            }
            else
            {
                Show();
            }
        }

        /// <summary>Moves this window to the front of its layer band.</summary>
        public void BringToFront()
        {
            if (tornDown)
            {
                return;
            }

            TopiaForgeWindowRegistry.BringToFront(this);
        }

        /// <summary>Host teardown: release shared state without animation.</summary>
        internal void Teardown()
        {
            if (tornDown)
            {
                return;
            }

            tornDown = true;
            TopiaForgeTween.Cancel(this);
            cursorLease.Release();
            TopiaForgeDismissStack.Remove(this);
            TopiaForgeWindowRegistry.Unregister(this);
            open = false;
            closing = false;
            Closed = null;
        }

        private void ThrowIfTornDown()
        {
            if (tornDown)
            {
                throw new ObjectDisposedException(nameof(TopiaForgeWindow));
            }
        }

        private void RaiseClosed()
        {
            TopiaForgeCallbacks.Invoke(Closed, "Window Closed");
        }

        internal Canvas? OwnCanvas => Go != null ? Go.GetComponentInParent<Canvas>() : null;

        internal GameObject CanvasRoot => canvasRoot;

        internal void HandleDrag(Vector2 screenDelta)
        {
            var canvas = OwnCanvas;
            var scale = canvas != null ? canvas.scaleFactor : 1f;
            Rect.anchoredPosition += screenDelta / scale;
        }

        internal void HandleDragEnd()
        {
            var (rect, canvasSize) = CanvasSpaceRect();
            var snapped = TopiaForgeWindowMath.SnapToEdges(rect, canvasSize.x, canvasSize.y);
            var clamped = TopiaForgeWindowMath.ClampToScreen(snapped, canvasSize.x, canvasSize.y);
            ApplyCanvasSpaceRect(clamped, canvasSize);
            PersistRect();
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            fill.color = theme.Surface;
            ring.color = theme.OutlineStrong;
            shadow.color = theme.ShadowStrong;
            titleBarFill.color = theme.SurfaceAlt;
            titleLabel.color = theme.Text;
        }

        private void ClampToScreen()
        {
            var (rect, canvasSize) = CanvasSpaceRect();
            var (w, h) = TopiaForgeWindowMath.ClampSize(rect.Width, rect.Height, canvasSize.x, canvasSize.y, 240f, 120f);
            var clamped = TopiaForgeWindowMath.ClampToScreen(new TopiaForgeRect(rect.X, rect.Y, w, h), canvasSize.x, canvasSize.y);
            ApplyCanvasSpaceRect(clamped, canvasSize);
        }

        private (TopiaForgeRect Rect, Vector2 CanvasSize) CanvasSpaceRect()
        {
            var canvas = OwnCanvas;
            var canvasRect = canvas != null ? ((RectTransform)canvas.transform).rect.size : new Vector2(TopiaForgeTokens.ReferenceWidth, TopiaForgeTokens.ReferenceHeight);
            var size = Rect.rect.size;
            var position = Rect.anchoredPosition;
            var x = position.x + (canvasRect.x / 2f) - (size.x / 2f);
            var y = position.y + (canvasRect.y / 2f) - (size.y / 2f);
            return (new TopiaForgeRect(x, y, size.x, size.y), canvasRect);
        }

        private void ApplyCanvasSpaceRect(TopiaForgeRect rect, Vector2 canvasSize)
        {
            Rect.anchoredPosition = new Vector2(
                rect.X - (canvasSize.x / 2f) + (rect.Width / 2f),
                rect.Y - (canvasSize.y / 2f) + (rect.Height / 2f));
            if (!autoHeight)
            {
                Rect.sizeDelta = new Vector2(rect.Width, rect.Height);
            }
        }

        private void PersistRect()
        {
            var position = Rect.anchoredPosition;
            var size = Rect.rect.size;
            var value = string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.#};{1:0.#};{2:0.#};{3:0.#}",
                position.x,
                position.y,
                size.x,
                size.y);
            Host.StateStore.Write(persistKey, value);
        }

        private void RestoreRect()
        {
            if (!Host.StateStore.TryRead(persistKey, out var value))
            {
                return;
            }

            var parts = value.Split(';');
            if (parts.Length != 4
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            {
                return;
            }

            Rect.anchoredPosition = new Vector2(x, y);
            if (!autoHeight && w > 0f && h > 0f)
            {
                Rect.sizeDelta = new Vector2(w, h);
            }
        }

        private UImage CreateDecor(string name, Sprite sprite, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = UImage.Type.Sliced;
            image.raycastTarget = raycast;
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            var layout = go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            return image;
        }
    }

}
