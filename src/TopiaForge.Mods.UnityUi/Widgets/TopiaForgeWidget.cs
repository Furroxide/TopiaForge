using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Implemented by widgets that re-tint when the theme changes.</summary>
    public interface ITopiaForgeThemeAware
    {
        /// <summary>Applies the resolved theme to this UI element.</summary>
        void ApplyTheme(TopiaForgeResolvedTheme theme);
    }

    /// <summary>
    /// Base retained handle wrapping a GameObject. Build-time chainers return the
    /// widget (see TopiaForgeWidgetChainers); runtime setters return void and dirty-check so
    /// per-frame HUD updates allocate nothing when values are unchanged.
    /// </summary>
    public abstract class TopiaForgeWidget
    {
        private CanvasGroup? visibilityGroup;
        private bool visible = true;

        /// <summary>Creates a widget.</summary>
        protected TopiaForgeWidget(UiHost host, TopiaForgeScheme scheme, GameObject go)
        {
            Host = host;
            Scheme = scheme;
            Go = go;
            Rect = TopiaForgeComponents.GetOrAdd<RectTransform>(go);
            host.RegisterWidget(this);
            if (this is ITopiaForgeThemeAware aware)
            {
                host.RegisterThemeAware(aware);
            }
        }

        /// <summary>Gets host.</summary>
        public UiHost Host { get; }
        /// <summary>Gets scheme.</summary>
        public TopiaForgeScheme Scheme { get; }
        /// <summary>Gets go.</summary>
        public GameObject Go { get; }
        /// <summary>Gets rect.</summary>
        public RectTransform Rect { get; }

        /// <summary>Gets visible.</summary>
        public bool Visible => visible;

        /// <summary>Gets theme.</summary>
        protected TopiaForgeResolvedTheme Theme => Host.Theme(Scheme);

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
                visibilityGroup = TopiaForgeComponents.GetOrAdd<CanvasGroup>(Go);
            }

            visibilityGroup.alpha = value ? 1f : 0f;
            visibilityGroup.interactable = value;
            visibilityGroup.blocksRaycasts = value;
        }

        /// <summary>Destroys the widget's GameObject and unregisters it from theming.</summary>
        public void Destroy()
        {
            Host.DestroyWidget(this);
        }

        internal LayoutElement EnsureLayoutElement()
        {
            return TopiaForgeComponents.GetOrAdd<LayoutElement>(Go);
        }

        internal CanvasGroup EnsureCanvasGroup()
        {
            if (visibilityGroup == null)
            {
                visibilityGroup = TopiaForgeComponents.GetOrAdd<CanvasGroup>(Go);
            }

            return visibilityGroup;
        }
    }

    /// <summary>
    /// Build-time sizing/placement chainers. Extension methods so every widget type
    /// keeps its concrete type through a chain.
    /// </summary>
    public static class TopiaForgeWidgetChainers
    {
        /// <summary>Configures fixed for this widget.</summary>
        public static T Fixed<T>(this T widget, float width, float height) where T : TopiaForgeWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.minHeight = height;
            layout.preferredHeight = height;
            return widget;
        }

        /// <summary>Configures fixed width for this widget.</summary>
        public static T FixedWidth<T>(this T widget, float width) where T : TopiaForgeWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.minWidth = width;
            layout.preferredWidth = width;
            return widget;
        }

        /// <summary>Configures fixed height for this widget.</summary>
        public static T FixedHeight<T>(this T widget, float height) where T : TopiaForgeWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.minHeight = height;
            layout.preferredHeight = height;
            return widget;
        }

        /// <summary>Configures flex for this widget.</summary>
        public static T Flex<T>(this T widget, float width = 1f, float height = 1f) where T : TopiaForgeWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.flexibleWidth = width;
            layout.flexibleHeight = height;
            return widget;
        }

        /// <summary>Configures fill width for this widget.</summary>
        public static T FillWidth<T>(this T widget) where T : TopiaForgeWidget
        {
            var layout = widget.EnsureLayoutElement();
            layout.flexibleWidth = 1f;
            return widget;
        }

        /// <summary>Excludes the widget from its parent's layout group (free placement).</summary>
        public static T Free<T>(this T widget) where T : TopiaForgeWidget
        {
            widget.EnsureLayoutElement().ignoreLayout = true;
            return widget;
        }

        /// <summary>Docks to a screen/panel corner or edge with the brand safe margin.</summary>
        public static T Dock<T>(this T widget, TopiaForgeCorner corner, float margin = TopiaForgeTokens.SafeMargin) where T : TopiaForgeWidget
        {
            TopiaForgeAnchors.Dock(widget.Rect, corner, margin);
            return widget;
        }

        /// <summary>Sets an explicit rect size (with Dock, positions a fixed-size panel).</summary>
        public static T Size<T>(this T widget, float width, float height) where T : TopiaForgeWidget
        {
            widget.Rect.sizeDelta = new Vector2(width, height);
            return widget;
        }

        /// <summary>Stretches to fill the parent with optional edge insets.</summary>
        public static T Stretch<T>(this T widget, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f) where T : TopiaForgeWidget
        {
            TopiaForgeAnchors.Stretch(widget.Rect, left, top, right, bottom);
            return widget;
        }

        /// <summary>
        /// Isolates this subtree on a nested canvas so its per-frame geometry churn
        /// (bars, reticles, floaters) never dirties the parent canvas's static chrome.
        /// </summary>
        public static T Dynamic<T>(this T widget) where T : TopiaForgeWidget
        {
            if (widget.Go.GetComponent<Canvas>() == null)
            {
                widget.Go.AddComponent<Canvas>();
            }

            return widget;
        }
    }
}
