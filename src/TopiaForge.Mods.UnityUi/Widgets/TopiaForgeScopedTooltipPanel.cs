using TMPro;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    internal sealed class TopiaForgeScopedTooltipPanel
    {
        private const float MaxWidth = 360f;
        private const float HorizontalPadding = 12f;
        private const float VerticalPadding = 8f;
        private const float ScreenMargin = 4f;
        private const float AnchorGap = 8f;

        private readonly TopiaForgeRoundedImage fill;
        private readonly TopiaForgeRoundedImage border;
        private readonly TopiaForgeRoundedImage shadow;
        private readonly TextMeshProUGUI label;
        private readonly TMP_FontAsset? fallbackFont;
        private TopiaForgeScopedTooltipTrigger? paintedFor;
        private TopiaForgeResolvedTheme? paintedTheme;
        private Vector2 paintedViewport;
        private float paintedScale;
        private Vector2 shadowOffset;

        internal TopiaForgeScopedTooltipPanel(RectTransform canvas)
        {
            // The popup is a sibling of the content that owns its trigger. It escapes the
            // content's viewport masks without escaping this canvas's sorting order.
            Go = NewChild(canvas, "Tooltip popup");
            Go.SetActive(false);
            Rect = (RectTransform)Go.transform;
            Rect.anchorMin = canvas.pivot;
            Rect.anchorMax = canvas.pivot;
            Rect.pivot = new Vector2(0f, 1f);

            shadow = CreateImage(Rect, "Shadow");
            fill = CreateImage(Rect, "Fill");
            border = CreateImage(Rect, "Ring");
            label = TopiaForgeTmp.Create(NewChild(Rect, "Label"));
            fallbackFont = TopiaForgeFonts.For(TopiaForgeTextStyle.Label) ?? label.font;
            label.richText = false;
            label.parseCtrlCharacters = false;
            label.raycastTarget = false;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            if (TopiaForgeFonts.UseFauxBold) label.fontStyle = FontStyles.Bold;
            TopiaForgeAnchors.Stretch(label.rectTransform, HorizontalPadding, VerticalPadding,
                HorizontalPadding, VerticalPadding);
        }

        internal GameObject Go { get; }
        internal RectTransform Rect { get; }

        internal bool Show(TopiaForgeScopedTooltipTrigger source, Rect screen, Rect anchor)
        {
            var theme = source.Widget.ResolvedTheme;
            var scale = source.Widget.Host.EffectiveUiScale;
            if (paintedFor != source || !ReferenceEquals(paintedTheme, theme) || paintedScale != scale || paintedViewport != screen.size)
            {
                if (!Paint(source, theme, scale, screen.size)) return false;
            }

            var position = Place(screen, anchor, Rect.sizeDelta, shadowOffset);
            if (Rect.anchoredPosition != position) Rect.anchoredPosition = position;
            if (Rect.parent is Transform parent && Rect.GetSiblingIndex() != parent.childCount - 1)
                Rect.SetAsLastSibling();
            if (!Go.activeSelf) Go.SetActive(true);
            return true;
        }

        internal void Hide()
        {
            if (Go != null && Go.activeSelf) Go.SetActive(false);
            if (!ReferenceEquals(paintedFor, null) && label != null)
            {
                label.text = string.Empty;
                label.font = fallbackFont;
            }
            paintedFor = null;
            paintedTheme = null;
        }

        private bool Paint(TopiaForgeScopedTooltipTrigger source, TopiaForgeResolvedTheme theme, float scale, Vector2 screen)
        {
            var descriptor = theme.Descriptor ?? UiThemePackParser.Default;
            var style = descriptor.Components[UiThemeTarget.Tooltip];
            var shadowStyle = theme.HighContrast ? UiThemeShadow.None : style.Shadow;
            shadowOffset = shadowStyle switch
            {
                UiThemeShadow.Small => new Vector2(-3f, -4f),
                UiThemeShadow.Card => new Vector2(-4f, -8f),
                _ => Vector2.zero,
            };
            var maxWidth = screen.x - 2f * ScreenMargin - Mathf.Abs(shadowOffset.x);
            var maxHeight = screen.y - 2f * ScreenMargin - Mathf.Abs(shadowOffset.y);
            if (maxWidth <= 2f * HorizontalPadding || maxHeight <= 2f * VerticalPadding) return false;

            label.text = source.Message;
            label.font = fallbackFont;
            label.fontSize = TopiaForgeTokens.SizeOf(TopiaForgeTextStyle.Label);
            label.characterSpacing = 0f;
            TopiaForgeTypography.ApplyControlText(label, theme, TopiaForgeTextStyle.Label, scale);
            label.color = theme.ForegroundOn(UiPaintRole.Raised);
            var preferredWidth = Mathf.Min(maxWidth, MaxWidth);
            var size = PreferredSize(preferredWidth);
            if (size.y > maxHeight && preferredWidth < maxWidth) size = PreferredSize(maxWidth);
            size.y = Mathf.Min(size.y, maxHeight);
            Rect.sizeDelta = size;

            var radius = descriptor.Shape.Resolve(style.Radius, size.x, size.y);
            var stroke = (float)style.Border;
            if (theme.HighContrast && stroke > 0f) stroke = Mathf.Max(3f, stroke);
            fill.SetShape(radius);
            fill.color = theme.SurfaceAlt;
            border.gameObject.SetActive(stroke > 0f);
            border.SetShape(radius, stroke);
            border.color = theme.BoundaryOn(source.Backdrop);
            shadow.gameObject.SetActive(shadowStyle != UiThemeShadow.None);
            shadow.rectTransform.anchoredPosition = shadowOffset;
            shadow.SetShape(radius);
            shadow.color = shadowStyle == UiThemeShadow.Card ? theme.ShadowStrong : theme.Shadow;

            paintedFor = source;
            paintedTheme = theme;
            paintedViewport = screen;
            paintedScale = scale;
            return true;
        }

        private Vector2 PreferredSize(float width)
        {
            var measure = label.GetPreferredValues(label.text, width - 2f * HorizontalPadding, 0f);
            return new Vector2(Mathf.Min(width, measure.x + 2f * HorizontalPadding), measure.y + 2f * VerticalPadding);
        }

        private static Vector2 Place(Rect screen, Rect anchor, Vector2 size, Vector2 shadow)
        {
            var left = screen.xMin + ScreenMargin - Mathf.Min(shadow.x, 0f);
            var right = screen.xMax - ScreenMargin - Mathf.Max(shadow.x, 0f) - size.x;
            var bottom = screen.yMin + ScreenMargin - Mathf.Min(shadow.y, 0f) + size.y;
            var top = screen.yMax - ScreenMargin - Mathf.Max(shadow.y, 0f);
            var y = anchor.yMin - AnchorGap;
            if (y < bottom) y = anchor.yMax + AnchorGap + size.y;
            return new Vector2(Mathf.Clamp(anchor.xMin, left, right), Mathf.Clamp(y, bottom, top));
        }

        private static TopiaForgeRoundedImage CreateImage(RectTransform parent, string name)
        {
            var go = NewChild(parent, name);
            var image = go.AddComponent<TopiaForgeRoundedImage>();
            image.raycastTarget = false;
            TopiaForgeAnchors.Stretch(image.rectTransform);
            return image;
        }

        private static GameObject NewChild(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
