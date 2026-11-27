using System;
using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    internal sealed class TopiaForgeStinger : TopiaForgeContainer, ITopiaForgeThemeAware
    {
        private readonly UiStingerRequest request;
        private readonly UiThemeDescriptor? declared;
        private readonly TopiaForgeLabel title;
        private readonly TopiaForgeLabel? subtitle;
        private readonly Image? glyph;
        private readonly TopiaForgeRoundedImage fill;
        private readonly TopiaForgeRoundedImage edge;
        private readonly TopiaForgeRoundedImage shadow;
        private UiLayoutExtent size;
        private Rect lastSafeArea;
        private int lastWidth;
        private int lastHeight;
        private float lastScale;
        private bool shown;

        private TopiaForgeStinger(TopiaForgeContainer layer, GameObject root, UiStingerRequest request,
            UiThemeDescriptor? declared) : base(layer.Host, layer.Scheme, root)
        {
            this.request = request;
            this.declared = declared;
            shadow = Plate("Shadow");
            fill = Plate("Surface");
            edge = Plate("Boundary");
            if (request.Glyph is UiGlyph icon)
            {
                glyph = CreateChildGameObject("Glyph").AddComponent<Image>();
                glyph.sprite = TopiaForgeSprites.Glyph(icon);
                glyph.raycastTarget = false;
            }
            title = Label(request.Title, TopiaForgeTextStyle.Banner).PlainText().AlignCenter();
            title.Go.name = "Title";
            if (request.Subtitle.Length != 0)
            {
                subtitle = Label(request.Subtitle, TopiaForgeTextStyle.Title).PlainText().AlignCenter();
                subtitle.Go.name = "Subtitle";
            }
            Announcement = subtitle == null ? request.Title : request.Title + ". " + request.Subtitle;
        }

        internal string Announcement { get; }
        internal UiThemeDescriptor Typography { get; private set; } = UiThemePackParser.Default;
        internal bool UsesDeclaredFaces { get; private set; }

        internal static TopiaForgeStinger Stage(TopiaForgeContainer layer, UiStingerRequest request,
            UiThemeDescriptor? declared, UiThemeDescriptor effective, TMP_FontAsset? bannerFace, TMP_FontAsset? subtitleFace,
            bool checkCurrentViewport, bool measureDelivery)
        {
            var root = layer.CreateChildGameObject("Stinger");
            root.SetActive(false);
            try
            {
                var slot = root.AddComponent<TopiaForgeLayerSlot>();
                slot.SelectTheme(effective);
                var view = new TopiaForgeStinger(layer, root, request, declared);
                var typography = declared ?? UiThemePackParser.Default;
                if (measureDelivery && !ReferenceEquals(typography, UiThemePackParser.Default))
                    view.MeasureTypography(UiThemePackParser.Default, null, null);
                if (measureDelivery && (bannerFace != null || subtitleFace != null))
                    view.MeasureTypography(typography, null, null);
                view.MeasureTypography(typography, bannerFace, subtitleFace);
                if (checkCurrentViewport && !view.RefreshViewport())
                    throw new ArgumentException("The stinger cannot fit the safe area.", nameof(request));
                return view;
            }
            catch
            {
                layer.Host.DestroyLayer(root);
                throw;
            }
        }

        private void MeasureTypography(UiThemeDescriptor typography, TMP_FontAsset? bannerFace, TMP_FontAsset? subtitleFace)
        {
            Typography = typography;
            UsesDeclaredFaces = bannerFace != null || subtitleFace != null;
            title.FreezeTypeface(typography.GetText(UiTextStyle.Banner), bannerFace);
            subtitle?.FreezeTypeface(typography.GetText(UiTextStyle.Title), subtitleFace);
            ValidateSupportedViewport();
        }

        internal void Show()
        {
            shown = true;
            SetShown(RefreshViewport());
            EnsureCanvasGroup().alpha = 1f;
            TopiaForgeMotion.StingerIn(this);
            TopiaForgeStingerAnnouncements.Record(Announcement);
        }

        internal void Exit(Action complete)
        {
            TopiaForgeTween.Cancel(this);
            TopiaForgeMotion.StingerOut(this, complete);
        }

        internal bool RefreshViewport()
        {
            var width = Screen.width;
            var height = Screen.height;
            var safe = Screen.safeArea;
            var uiScale = Host.EffectiveUiScale;
            if (width == lastWidth && height == lastHeight && safe == lastSafeArea && uiScale == lastScale)
                return size.Width > 0;

            lastWidth = width;
            lastHeight = height;
            lastSafeArea = safe;
            lastScale = uiScale;
            size = default;
            if (width <= 0 || height <= 0 || safe.width <= 0 || safe.height <= 0)
            {
                SetShown(false);
                return false;
            }

            var factor = uiScale * Mathf.Sqrt(width / TopiaForgeTokens.ReferenceWidth * (height / TopiaForgeTokens.ReferenceHeight));
            var panelWidth = Math.Min(MaximumReferenceWidth, safe.width / factor - 2f * TopiaForgeTokens.SafeMargin);
            var safeY = safe.yMax - safe.height * UiStingerLayout.HeightFraction;
            // The OutBack entrance overshoots slightly. Keep the whole plate and its shadow clear of the crosshair.
            var topHeight = 2f * (safe.height / factor * UiStingerLayout.HeightFraction - TopiaForgeTokens.SafeMargin) /
                UiStingerLayout.EntranceOvershoot;
            var crosshairHeight = UiStingerLayout.HeightBeforeCrosshair((safeY - height / 2f) / factor);
            var maximumHeight = Math.Min(topHeight, crosshairHeight);
            if (!Arrange(panelWidth, maximumHeight))
            {
                SetShown(false);
                return false;
            }

            var anchor = new Vector2(safe.center.x / width, safeY / height);
            Rect.anchorMin = Rect.anchorMax = anchor;
            Rect.anchoredPosition = Vector2.zero;
            Rect.sizeDelta = new Vector2((float)size.Width, (float)size.Height);
            ApplyTheme(ResolvedTheme);
            SetShown(shown);
            return true;
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            var descriptor = theme.Descriptor ?? UiThemePackParser.Default;
            var pair = descriptor.SelectScheme(hud: true, theme.HighContrast).GetPair(request.Role);
            var ink = ToColor(pair.On);
            title.SetResolvedInk(ink);
            subtitle?.SetResolvedInk(ink);
            if (glyph != null) glyph.color = ink;
            fill.color = ToColor(pair.Fill);
            edge.color = theme.BoundaryOn(request.Role);
            shadow.color = theme.ShadowStrong;
            var paint = UiLayoutDecoration.ResolveStinger(request, descriptor, declared, size, theme.HighContrast);
            fill.SetShape((float)paint.CornerRadius);
            edge.SetShape((float)paint.CornerRadius, (float)paint.BorderWidth);
            edge.enabled = paint.BorderWidth > 0;
            shadow.SetShape((float)paint.CornerRadius);
            shadow.rectTransform.anchoredPosition = new Vector2((float)paint.ShadowX, (float)-paint.ShadowY);
            shadow.enabled = paint.ShadowX != 0 || paint.ShadowY != 0;
        }

        private float ReadableRatio => Math.Max(1f, Host.EffectiveUiScale) / Host.EffectiveUiScale;
        private float MaximumReferenceWidth => UiStingerLayout.MaximumWidth * ReadableRatio;

        private void ValidateSupportedViewport()
        {
            var smallestHeight = TopiaForgeTokens.ReferenceHeight / 2f;
            var centreToCrosshair = smallestHeight * (0.5f - UiStingerLayout.HeightFraction);
            var supportedHeight = UiStingerLayout.HeightBeforeCrosshair(centreToCrosshair) * ReadableRatio;
            if (!Arrange(MaximumReferenceWidth, supportedHeight))
                throw new ArgumentException("A stinger title and subtitle must each fit within two lines and clear the crosshair.");
        }

        private bool Arrange(float maxWidth, float maxHeight)
        {
            var ratio = ReadableRatio;
            var padding = UiStingerLayout.PaddingX * ratio;
            var glyphWidth = glyph == null ? 0 : (UiStingerLayout.GlyphSize + UiStingerLayout.GlyphGap) * ratio;
            var titleText = title.Go.GetComponent<TMP_Text>();
            var subtitleText = subtitle?.Go.GetComponent<TMP_Text>();
            var desired = Math.Max(titleText.GetPreferredValues().x + glyphWidth, subtitleText?.GetPreferredValues().x ?? 0f);
            var width = Math.Min(maxWidth, MathF.Ceiling(desired) + 2 * padding + 2f);
            var titleWidth = UiStingerLayout.TitleWidth(width / ratio, glyph != null) * ratio;
            var titleHeight = Measure(titleText, titleWidth);
            var subtitleHeight = subtitleText == null ? 0f : Measure(subtitleText, width - 2 * padding);
            if (titleHeight <= 0 || subtitleHeight < 0) return false;

            var rowHeight = Math.Max(titleHeight, glyph == null ? 0 : UiStingerLayout.GlyphSize * ratio);
            var top = UiStingerLayout.PaddingY * ratio;
            var gap = subtitle == null ? 0f : UiStingerLayout.LineGap * ratio;
            var panelHeight = 2 * top + rowHeight + gap + subtitleHeight;
            if (panelHeight > maxHeight) return false;
            TopiaForgeAnchors.Place(title.Rect, padding + glyphWidth, top, titleWidth, rowHeight);
            if (glyph != null)
            {
                var glyphSize = UiStingerLayout.GlyphSize * ratio;
                TopiaForgeAnchors.Place(glyph.rectTransform, padding, top + (rowHeight - glyphSize) / 2f, glyphSize, glyphSize);
            }
            if (subtitle != null)
                TopiaForgeAnchors.Place(subtitle.Rect, padding, top + rowHeight + gap, width - 2 * padding, subtitleHeight);
            size = new UiLayoutExtent(width, panelHeight);
            return true;
        }

        private static float Measure(TMP_Text label, float width)
        {
            if (width <= 0 || label.font == null) return -1f;
            var preferred = label.GetPreferredValues(label.text, width, 0f);
            TopiaForgeAnchors.Place(label.rectTransform, 0, 0, width, preferred.y);
            return label.GetTextInfo(label.text).lineCount <= UiStingerLayout.MaximumLines && preferred.x <= width + 0.1f
                ? Mathf.Ceil(preferred.y) : -1f;
        }

        private TopiaForgeRoundedImage Plate(string name)
        {
            var image = Stack(name).Go.AddComponent<TopiaForgeRoundedImage>();
            image.raycastTarget = false;
            return image;
        }

        private static Color ToColor(UiThemeColor color) => new Color32(color.R, color.G, color.B, 255);
    }
}
