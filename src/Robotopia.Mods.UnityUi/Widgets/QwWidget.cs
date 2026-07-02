using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Implemented by widgets that re-tint when the theme changes.</summary>
    public interface IQwThemeAware
    {
        void ApplyTheme(QwResolvedTheme theme);
    }

    /// <summary>
    /// Base retained handle wrapping a GameObject. Build-time chainers return the
    /// widget (see QwWidgetChainers); runtime setters return void and dirty-check so
    /// per-frame HUD updates allocate nothing when values are unchanged.
    /// </summary>
    public abstract class QwWidget
    {
        private CanvasGroup? visibilityGroup;
        private bool visible = true;

        protected QwWidget(UiHost host, QwScheme scheme, GameObject go)
        {
            Host = host;
            Scheme = scheme;
            Go = go;
            Rect = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            if (this is IQwThemeAware aware)
            {
                host.RegisterThemeAware(aware);
            }
        }

        public UiHost Host { get; }
        public QwScheme Scheme { get; }
        public GameObject Go { get; }
        public RectTransform Rect { get; }

        public bool Visible => visible;

        protected QwResolvedTheme Theme => Host.Theme(Scheme);

        /// <summary>
        /// Shows/hides via CanvasGroup (alpha + interactable + raycasts) — no layout
        /// rebuild storm, safe to call every frame. Dirty-checked.
        /// </summary>
        public void SetVisible(bool value)
        {
            if (visible == value)
            {
                return;
            }

            visible = value;
            if (visibilityGroup == null)
            {
                visibilityGroup = Go.GetComponent<CanvasGroup>() ?? Go.AddComponent<CanvasGroup>();
            }

            visibilityGroup.alpha = value ? 1f : 0f;
            visibilityGroup.interactable = value;
            visibilityGroup.blocksRaycasts = value;
        }

        /// <summary>Destroys the widget's GameObject and unregisters it from theming.</summary>
        public void Destroy()
        {
            if (this is IQwThemeAware aware)
            {
                Host.UnregisterThemeAware(aware);
            }

            if (Go != null)
            {
                Object.Destroy(Go);
            }
        }

        internal LayoutElement EnsureLayoutElement()
        {
            return Go.GetComponent<LayoutElement>() ?? Go.AddComponent<LayoutElement>();
        }
    }

    /// <summary>
    /// Build-time sizing/placement chainers. Extension methods so every widget type
    /// keeps its concrete type through a chain.
    /// </summary>
    public static class QwWidgetChainers
    {
        public static T Fixed<T>(this T widget, float width, float height) where T : QwWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.minHeight = height;
            layout.preferredHeight = height;
            return widget;
        }

        public static T FixedWidth<T>(this T widget, float width) where T : QwWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.minWidth = width;
            layout.preferredWidth = width;
            return widget;
        }

        public static T FixedHeight<T>(this T widget, float height) where T : QwWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.minHeight = height;
            layout.preferredHeight = height;
            return widget;
        }

        public static T Flex<T>(this T widget, float width = 1f, float height = 1f) where T : QwWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.flexibleWidth = width;
            layout.flexibleHeight = height;
            return widget;
        }

        public static T FillWidth<T>(this T widget) where T : QwWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.flexibleWidth = 1f;
            return widget;
        }

        /// <summary>Excludes the widget from its parent's layout group (free placement).</summary>
        public static T Free<T>(this T widget) where T : QwWidget
        {
            widget.EnsureLayoutElement().ignoreLayout = true;
            return widget;
        }

        /// <summary>Docks to a screen/panel corner or edge with the brand safe margin.</summary>
        public static T Dock<T>(this T widget, QwCorner corner, float margin = QwTokens.SafeMargin) where T : QwWidget
        {
            QwAnchors.Dock(widget.Rect, corner, margin);
            return widget;
        }

        /// <summary>Sets an explicit rect size (with Dock, positions a fixed-size panel).</summary>
        public static T Size<T>(this T widget, float width, float height) where T : QwWidget
        {
            widget.Rect.sizeDelta = new Vector2(width, height);
            return widget;
        }

        /// <summary>Stretches to fill the parent with optional edge insets.</summary>
        public static T Stretch<T>(this T widget, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f) where T : QwWidget
        {
            QwAnchors.Stretch(widget.Rect, left, top, right, bottom);
            return widget;
        }
    }
}
