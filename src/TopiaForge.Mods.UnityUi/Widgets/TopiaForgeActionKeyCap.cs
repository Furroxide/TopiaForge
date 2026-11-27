using System;
using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>A noninteractive key cap drawn with the viewer's Label type and readable scale.</summary>
    internal sealed class TopiaForgeActionKeyCap
    {
        private const float Gap = 4f;
        private const float CueSize = 18f;
        private const float MinimumHeight = 24f;
        private readonly TopiaForgeContainer root;
        private readonly TopiaForgeRoundedImage shadow;
        private readonly TopiaForgeRoundedImage fill;
        private readonly TopiaForgeRoundedImage border;
        private readonly Image cue;
        private readonly TopiaForgeLabel label;
        private readonly TMP_Text text;
        private UiActionBindingPresentation displayed;
        private UiLayoutRect lastAllocation;
        private TopiaForgeResolvedTheme? lastTheme;
        private UiPaintRole lastBackdrop;
        private UiTone lastTone;
        private float lastReadability;

        internal TopiaForgeActionKeyCap(TopiaForgeContainer parent)
        {
            root = parent.CreateChild("Key cap");
            shadow = Graphic<TopiaForgeRoundedImage>("Key cap shadow");
            fill = Graphic<TopiaForgeRoundedImage>("Key cap fill");
            border = Graphic<TopiaForgeRoundedImage>("Key cap border");
            cue = Graphic<Image>("Key cap cue");
            label = root.Label(string.Empty, TopiaForgeTextStyle.Label).PlainText().AlignTopLeft();
            text = label.Go.GetComponent<TMP_Text>();
            text.raycastTarget = false;
        }

        internal string SemanticLabel { get; private set; } = string.Empty;
        internal RectTransform Bounds => root.Rect;

        internal UiLayoutLeafMeasurement Measure(UiActionBindingPresentation binding, double maxWidth)
        {
            var theme = root.ResolvedTheme;
            var layout = ReadLayout(binding, maxWidth, theme, Readability);
            var decoration = UiLayoutDecoration.ResolveKeyGlyph(theme.Descriptor ?? UiThemePackParser.Default, layout.Size, theme.HighContrast);
            return new UiLayoutLeafMeasurement(layout.Size, UiLayoutPaintKind.Protected, decoration.ShadowOutsets,
                localPaintBounds: new UiLayoutRect(0, 0, layout.Size.Width, layout.Size.Height));
        }

        internal void Apply(UiActionBindingPresentation binding, UiLayoutRect allocation, UiPaintRole backdrop, UiTone tone = UiTone.Neutral)
        {
            var theme = root.ResolvedTheme;
            var readability = Readability;
            var sameBinding = string.Equals(displayed.Label, binding.Label, StringComparison.Ordinal) &&
                string.Equals(displayed.SpokenControl, binding.SpokenControl, StringComparison.Ordinal) && displayed.Cue == binding.Cue;
            if (sameBinding && ReferenceEquals(lastTheme, theme) && lastReadability == readability && lastTone == tone &&
                lastBackdrop == backdrop && SameBounds(lastAllocation, allocation)) return;

            var layout = ReadLayout(binding, allocation.Width, theme, readability);
            var descriptor = theme.Descriptor ?? UiThemePackParser.Default;
            var style = UiLayoutDecoration.ResolveKeyGlyph(descriptor, layout.Size, theme.HighContrast);
            var scheme = descriptor.SelectScheme(theme.Scheme == TopiaForgeScheme.Hud, theme.HighContrast);
            var ink = ColorOf(scheme.ResolveForeground(tone, style.Fill));
            var edge = ColorOf(scheme.ResolveBoundary(backdrop));
            var width = (float)layout.Size.Width;
            var height = (float)layout.Size.Height;
            var radius = (float)style.CornerRadius;
            Place(root.Rect, (float)allocation.X, (float)allocation.Y, width, height);
            ApplyShape(fill, ColorOf(scheme.GetPair(style.Fill).Fill), 0f, 0f, width, height, radius);
            border.enabled = style.BorderWidth > 0;
            if (border.enabled)
                ApplyShape(border, edge, 0f, 0f, width, height, radius, (float)style.BorderWidth);
            shadow.enabled = style.ShadowX != 0 || style.ShadowY != 0;
            if (shadow.enabled)
            {
                var color = ColorOf(scheme.GetPair(backdrop).On);
                color.a = theme.Shadow.a;
                ApplyShape(shadow, color, (float)style.ShadowX, (float)style.ShadowY, width, height, radius);
            }
            cue.enabled = binding.Cue.HasValue;
            if (binding.Cue is { } mark)
            {
                cue.sprite = TopiaForgeSprites.Glyph(mark);
                cue.color = ink;
                cue.preserveAspect = true;
                Place(cue.rectTransform, layout.Padding, (height - layout.Cue) / 2f, layout.Cue, layout.Cue);
            }
            label.SetText(binding.Label);
            label.SetResolvedInk(ink);
            Place(label.Rect, layout.Padding + layout.Leading, (height - layout.Label.y) / 2f, layout.Label.x, layout.Label.y);
            if (!sameBinding)
                SemanticLabel = binding.Cue.HasValue ? binding.SpokenControl : "key " + binding.SpokenControl;
            displayed = binding;
            lastAllocation = allocation;
            lastTheme = theme;
            lastReadability = readability;
            lastBackdrop = backdrop;
            lastTone = tone;
        }

        private KeyLayout ReadLayout(UiActionBindingPresentation binding, double maxWidth, TopiaForgeResolvedTheme theme, float scale)
        {
            var padding = Gap * scale;
            var glyph = binding.Cue.HasValue ? CueSize * scale : 0f;
            var leading = glyph > 0f ? glyph + padding : 0f;
            var visual = (theme.Descriptor ?? UiThemePackParser.Default).GetText(UiTextStyle.Label).IsUppercase
                ? binding.Label.ToUpperInvariant() : binding.Label;
            var chromeWidth = 2d * padding + leading;
            var textLimit = double.IsPositiveInfinity(maxWidth) ? float.MaxValue : (float)Math.Max(1d, maxWidth - chromeWidth);
            var measure = text.GetPreferredValues(visual, textLimit, 0f);
            return new KeyLayout(new UiLayoutExtent(measure.x + chromeWidth,
                Math.Max(MinimumHeight * scale, Math.Max(glyph, measure.y) + padding)), measure, padding, glyph, leading);
        }

        private float Readability => Math.Max(1f, root.Host.EffectiveUiScale) / root.Host.EffectiveUiScale;

        private T Graphic<T>(string name) where T : Graphic
        {
            var graphic = root.CreateChild(name).Go.AddComponent<T>();
            graphic.raycastTarget = false;
            return graphic;
        }

        private static void ApplyShape(TopiaForgeRoundedImage image, Color color,
            float x, float y, float width, float height, float radius, float line = 0f)
        {
            Place(image.rectTransform, x, y, width, height);
            image.color = color;
            image.SetShape(radius, line);
        }

        private static Color ColorOf(UiThemeColor color) => new Color32(color.R, color.G, color.B, byte.MaxValue);
        private static void Place(RectTransform rect, float x, float y, float w, float h) => TopiaForgeAnchors.Place(rect, x, y, w, h);
        private static bool SameBounds(UiLayoutRect first, UiLayoutRect second) => first.X == second.X && first.Y == second.Y &&
            first.Width == second.Width && first.Height == second.Height;

        private readonly struct KeyLayout
        {
            internal KeyLayout(UiLayoutExtent size, Vector2 label, float padding, float cue, float leading)
            { Size = size; Label = label; Padding = padding; Cue = cue; Leading = leading; }
            internal UiLayoutExtent Size { get; }
            internal Vector2 Label { get; }
            internal float Padding { get; }
            internal float Cue { get; }
            internal float Leading { get; }
        }
    }
}
