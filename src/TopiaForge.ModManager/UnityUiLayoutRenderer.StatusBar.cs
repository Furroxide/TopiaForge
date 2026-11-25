using System;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiLayoutRenderer
        {
            private sealed class NativeStatBar
            {
                private const float Padding = UiStatBarLayout.Padding;
                private const float GlyphFloor = 18f;

                private readonly TopiaForgeContainer host;
                private readonly UiStatBar node;
                private readonly UiStatusPresentation status;
                private readonly TopiaForgeRoundedImage shadow;
                private readonly TopiaForgeRoundedImage track;
                private readonly TopiaForgeRoundedImage fill;
                private readonly TopiaForgeRoundedImage border;
                private readonly Side filled;
                private readonly Side empty;
                private UiLayoutExtent contentSize;

                internal NativeStatBar(TopiaForgeContainer host, UiStatBar node)
                {
                    this.host = host;
                    this.node = node;
                    status = UiStatusPresentation.For(node);
                    shadow = AddGraphic<TopiaForgeRoundedImage>(host, "Stat shadow");
                    track = AddGraphic<TopiaForgeRoundedImage>(host, "Stat track");
                    filled = new Side(host, "Stat filled clip");
                    fill = AddGraphic<TopiaForgeRoundedImage>(filled.Root, "Stat fill");
                    filled.AddContent(node.DisplayLabel, node.Thresholds != null);
                    empty = new Side(host, "Stat empty clip");
                    empty.AddContent(node.DisplayLabel, node.Thresholds != null);
                    border = AddGraphic<TopiaForgeRoundedImage>(host, "Stat boundary");
                }

                internal UiLayoutLeafMeasurement Measure(UiLayoutConstraints constraints, UiThemeDescriptor effective,
                    UiThemeDescriptor? declared, bool highContrast)
                {
                    var caption = filled.Label.GetPreferredValues(filled.Label.text);
                    var cue = CueExtent;
                    contentSize = new UiLayoutExtent(caption.x + (cue > 0 ? cue + Padding : 0), Math.Max(caption.y, cue));
                    var height = Math.Max(UiStatBarLayout.MinimumHeight, contentSize.Height + 2 * Padding);
                    var fitted = new UiLayoutExtent(constraints.MaxWidth, Math.Clamp(height, constraints.MinHeight, constraints.MaxHeight));
                    var chrome = UiLayoutDecoration.Resolve(node, effective, declared, fitted, highContrast);
                    var width = contentSize.Width + 2 * (UiStatBarLayout.ContentInset(chrome, fitted, contentSize) + Padding);
                    return new UiLayoutLeafMeasurement(new UiLayoutExtent(width, height), UiLayoutPaintKind.Protected, chrome.ShadowOutsets);
                }

                internal void Apply(UiLayoutRect bounds, UiPaintRole behind, UiThemeScheme scheme, TopiaForgeResolvedTheme theme,
                    UiThemeDescriptor effective, UiThemeDescriptor? declared)
                {
                    var size = new UiLayoutExtent(bounds.Width, bounds.Height);
                    var chrome = UiLayoutDecoration.Resolve(node, effective, declared, size, theme.HighContrast);
                    ApplyGeometry(size, chrome);
                    Paint(scheme, theme, behind, chrome);
                }

                private void ApplyGeometry(UiLayoutExtent size, UiLayoutDecoration chrome)
                {
                    var whole = new UiLayoutRect(0, 0, size.Width, size.Height);
                    var split = size.Width * node.Fraction;
                    Place(track.rectTransform, whole);
                    Place(fill.rectTransform, whole);
                    Place(border.rectTransform, whole);
                    Place(shadow.rectTransform, whole.Translate(chrome.ShadowX, chrome.ShadowY));

                    // Clip a single rounded paint projection at the same boundary as the two copies of the content.
                    // Rounding the fractional fill itself would expose the wrong ink at its rounded end.
                    filled.SetClip(new UiLayoutRect(0, 0, split, size.Height));
                    empty.SetClip(new UiLayoutRect(split, 0, size.Width - split, size.Height));
                    var cue = CueExtent;
                    var inset = UiStatBarLayout.ContentInset(chrome, size, contentSize) + Padding;
                    var textInset = inset + (cue > 0 ? cue + Padding : 0);
                    var label = new UiLayoutRect(textInset, 0, Math.Max(0, size.Width - textInset - inset), size.Height);
                    var glyph = new UiLayoutRect(inset, (size.Height - cue) / 2, cue, cue);
                    filled.PlaceContent(label, glyph, offset: 0);
                    empty.PlaceContent(label, glyph, offset: -split);
                }

                private void Paint(UiThemeScheme scheme, TopiaForgeResolvedTheme theme, UiPaintRole behind, UiLayoutDecoration chrome)
                {
                    var colors = UiStatusMeterPaint.Resolve(scheme, status.Tone, theme.HighContrast);
                    var radius = (float)chrome.CornerRadius;
                    track.SetShape(radius);
                    track.color = ColorOf(colors.Empty.Fill);
                    fill.SetShape(radius);
                    fill.color = ColorOf(colors.Filled.Fill);
                    filled.SetInk(colors.Filled.On, status.Glyph);
                    empty.SetInk(colors.Empty.On, status.Glyph);

                    border.enabled = chrome.BorderWidth > 0;
                    border.SetShape(radius, (float)chrome.BorderWidth);
                    border.color = ColorOf(scheme.ResolveBoundary(behind));
                    shadow.enabled = chrome.ShadowX != 0 || chrome.ShadowY != 0;
                    shadow.SetShape(radius);
                    var shadowInk = ColorOf(scheme.GetPair(behind).On);
                    shadowInk.a = theme.Shadow.a;
                    shadow.color = shadowInk;
                }

                private float CueExtent => node.Thresholds == null ? 0f
                    : GlyphFloor * Math.Max(1f, host.Host.EffectiveUiScale) / host.Host.EffectiveUiScale;

                private sealed class Side
                {
                    private Image? cue;

                    internal Side(TopiaForgeContainer parent, string name)
                    {
                        Root = parent.CreateChild(name);
                        Root.Go.AddComponent<RectMask2D>();
                    }

                    internal TopiaForgeContainer Root { get; }
                    internal TMP_Text Label { get; private set; } = null!;

                    internal void AddContent(string title, bool reserveCue)
                    {
                        Label = Root.Label(title, TopiaForgeTextStyle.Label).PlainText().NoWrap().AlignCenter().Go.GetComponent<TMP_Text>();
                        if (reserveCue) cue = AddGraphic<Image>(Root, "Status cue");
                    }

                    internal void SetClip(UiLayoutRect clip)
                    {
                        Place(Root.Rect, clip);
                        Root.SetShown(clip.Width > 0);
                    }

                    internal void PlaceContent(UiLayoutRect label, UiLayoutRect glyph, double offset)
                    {
                        Place(Label.rectTransform, label.Translate(offset, 0));
                        if (cue != null) Place(cue.rectTransform, glyph.Translate(offset, 0));
                    }

                    internal void SetInk(UiThemeColor ink, UiGlyph? glyph)
                    {
                        var color = ColorOf(ink);
                        Label.color = color;
                        if (cue != null) NativeStatusLeaf.PaintCue(cue, glyph, color);
                    }
                }
            }
        }
    }
}
