using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Vertical scroll view with a thin brand scrollbar. Content is a Column container
    /// that grows with its children; ScrollToTop/End for log views.
    /// </summary>
    public sealed class QwScrollView : QwWidget, IQwThemeAware
    {
        private readonly ScrollRect scrollRect;
        private readonly UImage scrollbarTrack;
        private readonly UImage scrollbarHandle;

        internal QwScrollView(QwContainer parent, QwGap contentGap, QwGap contentPadding)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Scroll"))
        {
            // Viewport with mask.
            var viewportGo = new GameObject("Viewport", typeof(RectTransform));
            viewportGo.transform.SetParent(Go.transform, false);
            viewportGo.AddComponent<RectMask2D>();
            var viewportImage = viewportGo.AddComponent<UImage>();
            viewportImage.color = Color.clear;
            viewportImage.raycastTarget = true; // catches scroll wheel over empty space
            var viewportRect = (RectTransform)viewportGo.transform;
            QwAnchors.Stretch(viewportRect, 0f, 0f, 12f, 0f);

            // Content column.
            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            var contentRect = (RectTransform)contentGo.transform;
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            QwLayout.ApplyColumn(contentGo, contentGap, contentPadding);
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            Content = new QwContainer(Host, Scheme, contentGo);

            // Scrollbar.
            var scrollbarGo = new GameObject("Scrollbar", typeof(RectTransform));
            scrollbarGo.transform.SetParent(Go.transform, false);
            scrollbarTrack = scrollbarGo.AddComponent<UImage>();
            scrollbarTrack.sprite = QwSprites.Fill(QwRadius.Bar);
            scrollbarTrack.type = UImage.Type.Sliced;
            var scrollbarRect = (RectTransform)scrollbarGo.transform;
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 0.5f);
            scrollbarRect.anchoredPosition = Vector2.zero;
            scrollbarRect.sizeDelta = new Vector2(8f, 0f);
            var scrollbar = scrollbarGo.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            var handleAreaGo = new GameObject("HandleArea", typeof(RectTransform));
            handleAreaGo.transform.SetParent(scrollbarGo.transform, false);
            var handleArea = (RectTransform)handleAreaGo.transform;
            QwAnchors.Stretch(handleArea, 1f, 1f, 1f, 1f);

            var handleGo = new GameObject("Handle", typeof(RectTransform));
            handleGo.transform.SetParent(handleAreaGo.transform, false);
            scrollbarHandle = handleGo.AddComponent<UImage>();
            scrollbarHandle.sprite = QwSprites.Fill(QwRadius.Bar);
            scrollbarHandle.type = UImage.Type.Sliced;
            scrollbar.handleRect = (RectTransform)handleGo.transform;
            scrollbar.targetGraphic = scrollbarHandle;

            scrollRect = Go.AddComponent<ScrollRect>();
            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 28f;
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            this.Flex(1f, 1f);
            ApplyTheme(Theme);
        }

        /// <summary>Add children here.</summary>
        public QwContainer Content { get; }

        public void ScrollToTop()
        {
            scrollRect.verticalNormalizedPosition = 1f;
        }

        public void ScrollToEnd()
        {
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
        }

        public void ApplyTheme(QwResolvedTheme theme)
        {
            scrollbarTrack.color = theme.SurfaceSunken;
            scrollbarHandle.color = theme.Tint;
        }
    }
}
