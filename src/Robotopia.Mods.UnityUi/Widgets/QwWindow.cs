using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Draggable brand window: card chrome (radius 26, orange border, hard shadow),
    /// 42px title bar with an Arista title and close button, ESC-close via the dismiss
    /// stack, automatic cursor lease while visible, screen clamping + edge snapping,
    /// and rect persistence keyed owner+windowId in the host's state store.
    /// </summary>
    public sealed class QwWindow : QwContainer, IQwThemeAware, IQwDismissable
    {
        private readonly UImage shadow;
        private readonly UImage fill;
        private readonly UImage ring;
        private readonly UImage titleBarFill;
        private readonly TextMeshProUGUI titleLabel;
        private readonly QwCursorLease cursorLease = new QwCursorLease();
        private readonly string persistKey;
        private readonly bool autoHeight;
        private bool open;
        private bool closing;

        internal QwWindow(UiHost host, QwContainer layerRoot, string id, string title, float width, float height)
            : base(host, layerRoot.Scheme, layerRoot.CreateChildGameObject("Window"))
        {
            persistKey = "win:" + id;
            autoHeight = height <= 0f;

            Rect.anchorMin = new Vector2(0.5f, 0.5f);
            Rect.anchorMax = new Vector2(0.5f, 0.5f);
            Rect.pivot = new Vector2(0.5f, 0.5f);
            Rect.sizeDelta = new Vector2(width, autoHeight ? 200f : height);

            shadow = CreateDecor("Shadow", QwSprites.Fill(QwRadius.Card), raycast: false);
            var shadowRect = shadow.rectTransform;
            shadowRect.offsetMin = new Vector2(QwTokens.ShadowCardX, QwTokens.ShadowCardY);
            shadowRect.offsetMax = new Vector2(QwTokens.ShadowCardX, QwTokens.ShadowCardY);

            fill = CreateDecor("Fill", QwSprites.Fill(QwRadius.Card), raycast: true);
            ring = CreateDecor("Ring", QwSprites.Ring(QwRadius.Card, QwTokens.BorderStandard), raycast: false);

            QwLayout.ApplyColumn(Go, QwGap.None, QwGap.None);
            if (autoHeight)
            {
                var fitter = Go.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            // Title bar (drag handle).
            var titleBarGo = CreateChildGameObject("TitleBar");
            var titleBarLayout = titleBarGo.AddComponent<LayoutElement>();
            titleBarLayout.minHeight = QwTokens.TitleBarHeight;
            titleBarLayout.preferredHeight = QwTokens.TitleBarHeight;
            titleBarFill = titleBarGo.AddComponent<UImage>();
            titleBarFill.sprite = QwSprites.Fill(QwRadius.Card);
            titleBarFill.type = UImage.Type.Sliced;
            titleBarFill.raycastTarget = true;
            QwLayout.ApplyRow(titleBarGo, QwGap.Sm, QwGap.None);
            var drag = titleBarGo.AddComponent<QwWindowDrag>();
            drag.Window = this;

            var titleBar = new QwContainer(Host, Scheme, titleBarGo);
            var titleGo = titleBar.CreateChildGameObject("Title");
            titleLabel = titleGo.AddComponent<TextMeshProUGUI>();
            titleLabel.raycastTarget = false;
            titleLabel.fontSize = QwTokens.TitleSize;
            titleLabel.alignment = TextAlignmentOptions.Left;
            titleLabel.textWrappingMode = TextWrappingModes.NoWrap;
            var displayFont = QwFonts.For(QwTextStyle.Title);
            if (displayFont != null)
            {
                titleLabel.font = displayFont;
            }

            if (QwFonts.UseFauxDisplay)
            {
                titleLabel.fontStyle = FontStyles.Bold;
            }

            titleLabel.text = title;
            var titleLayout = titleGo.AddComponent<LayoutElement>();
            titleLayout.flexibleWidth = 1f;
            titleLayout.minHeight = QwTokens.TitleBarHeight;
            titleLabel.margin = new Vector4(16f, 0f, 0f, 0f);

            titleBar.IconButton(QwIcon.Cross, Close, QwButtonStyle.Ghost).Fixed(34f, 34f);

            // Content area.
            Content = Column(QwGap.Md, QwGap.Lg);
            Content.Flex(1f, autoHeight ? 0f : 1f);

            // Focus on click.
            var focus = Go.AddComponent<QwWindowFocus>();
            focus.Window = this;

            RestoreRect();
            QwWindowRegistry.Register(this);
            Go.SetActive(false);
            ApplyTheme(Theme);
        }

        /// <summary>Window body — add content here.</summary>
        public QwContainer Content { get; }

        public bool IsOpen => open;

        public event Action? Closed;

        QwLayerBand IQwDismissable.Band => QwLayerBand.Window;

        void IQwDismissable.Dismiss()
        {
            Close();
        }

        public void SetTitle(string title)
        {
            titleLabel.text = title;
        }

        public void Show()
        {
            if (open)
            {
                BringToFront();
                return;
            }

            open = true;
            closing = false;
            Go.SetActive(true);
            ClampToScreen();
            QwMotion.WindowIn(this);
            cursorLease.Acquire();
            QwDismissStack.Push(this);
            BringToFront();
        }

        public void Close()
        {
            if (!open || closing)
            {
                return;
            }

            closing = true;
            cursorLease.Release();
            QwDismissStack.Remove(this);
            PersistRect();
            QwMotion.WindowOut(this, () =>
            {
                closing = false;
                open = false;
                if (Go != null)
                {
                    Go.SetActive(false);
                }

                Closed?.Invoke();
            });
        }

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

        public void BringToFront()
        {
            QwWindowRegistry.BringToFront(this);
        }

        /// <summary>Host teardown: release shared state without animation.</summary>
        internal void Teardown()
        {
            cursorLease.Release();
            QwDismissStack.Remove(this);
            QwWindowRegistry.Unregister(this);
        }

        internal Canvas? OwnCanvas => Go != null ? Go.GetComponentInParent<Canvas>() : null;

        internal void HandleDrag(Vector2 screenDelta)
        {
            var canvas = OwnCanvas;
            var scale = canvas != null ? canvas.scaleFactor : 1f;
            Rect.anchoredPosition += screenDelta / scale;
        }

        internal void HandleDragEnd()
        {
            var (rect, canvasSize) = CanvasSpaceRect();
            var snapped = QwWindowMath.SnapToEdges(rect, canvasSize.x, canvasSize.y);
            var clamped = QwWindowMath.ClampToScreen(snapped, canvasSize.x, canvasSize.y);
            ApplyCanvasSpaceRect(clamped, canvasSize);
            PersistRect();
        }

        public void ApplyTheme(QwResolvedTheme theme)
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
            var (w, h) = QwWindowMath.ClampSize(rect.Width, rect.Height, canvasSize.x, canvasSize.y, 240f, 120f);
            var clamped = QwWindowMath.ClampToScreen(new QwRect(rect.X, rect.Y, w, h), canvasSize.x, canvasSize.y);
            ApplyCanvasSpaceRect(clamped, canvasSize);
        }

        private (QwRect Rect, Vector2 CanvasSize) CanvasSpaceRect()
        {
            var canvas = OwnCanvas;
            var canvasRect = canvas != null ? ((RectTransform)canvas.transform).rect.size : new Vector2(QwTokens.ReferenceWidth, QwTokens.ReferenceHeight);
            var size = Rect.rect.size;
            var position = Rect.anchoredPosition;
            var x = position.x + (canvasRect.x / 2f) - (size.x / 2f);
            var y = position.y + (canvasRect.y / 2f) - (size.y / 2f);
            return (new QwRect(x, y, size.x, size.y), canvasRect);
        }

        private void ApplyCanvasSpaceRect(QwRect rect, Vector2 canvasSize)
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
            QwAnchors.Stretch((RectTransform)go.transform);
            var layout = go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            return image;
        }
    }

    /// <summary>Title-bar drag behavior.</summary>
    internal sealed class QwWindowDrag : MonoBehaviour, IDragHandler, IEndDragHandler, IPointerDownHandler
    {
        public QwWindow? Window;

        public void OnPointerDown(PointerEventData eventData)
        {
            Window?.BringToFront();
        }

        public void OnDrag(PointerEventData eventData)
        {
            Window?.HandleDrag(eventData.delta);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            Window?.HandleDragEnd();
        }
    }

    /// <summary>Click-anywhere-to-front behavior on the window body.</summary>
    internal sealed class QwWindowFocus : MonoBehaviour, IPointerDownHandler
    {
        public QwWindow? Window;

        public void OnPointerDown(PointerEventData eventData)
        {
            Window?.BringToFront();
        }
    }

    /// <summary>
    /// Process-wide window z-order: focus reassigns canvas sorting orders sequentially
    /// from the window band base, so click-to-front never exhausts the band.
    /// </summary>
    internal static class QwWindowRegistry
    {
        private static readonly System.Collections.Generic.List<QwWindow> Order = new System.Collections.Generic.List<QwWindow>();

        public static void Register(QwWindow window)
        {
            if (!Order.Contains(window))
            {
                Order.Add(window);
                Reassign();
            }
        }

        public static void Unregister(QwWindow window)
        {
            if (Order.Remove(window))
            {
                Reassign();
            }
        }

        public static void BringToFront(QwWindow window)
        {
            var index = Order.IndexOf(window);
            if (index < 0 || index == Order.Count - 1)
            {
                return;
            }

            Order.RemoveAt(index);
            Order.Add(window);
            Reassign();
        }

        private static void Reassign()
        {
            for (var index = 0; index < Order.Count; index++)
            {
                var canvas = Order[index].OwnCanvas;
                if (canvas != null)
                {
                    canvas.sortingOrder = QwLayerBands.DefaultWindowBase + index;
                }
            }
        }
    }
}
