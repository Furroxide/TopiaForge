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
            private sealed class NativeStatusLeaf
            {
                private const float Gap = 4f;
                private const float GlyphFloor = 18f;
                private const float ProgressHeight = 14f;

                private readonly TopiaForgeContainer host;
                private readonly UiNode node;
                private readonly NativeStatBar? stat;
                private readonly NativeStatusCountdown? countdown;
                private readonly TMP_Text? caption;
                private readonly Image? cue;
                private readonly TopiaForgeRoundedImage? track;
                private readonly TopiaForgeRoundedImage? fill;
                private readonly bool reservesCue;
                private UiThemeScheme? scheme;
                private UiPaintRole backdrop;
                private UiStatusPresentation status;

                internal NativeStatusLeaf(TopiaForgeContainer host, UiNode node, UiCallbackGate callbacks)
                {
                    this.host = host;
                    this.node = node;
                    if (node is UiStatBar bar)
                    {
                        stat = new NativeStatBar(host, bar);
                        return;
                    }

                    if (node is UiCountdown timer)
                    {
                        reservesCue = timer.Thresholds != null;
                        caption = Caption(host, timer.Label, TopiaForgeTextStyle.Label);
                        countdown = new NativeStatusCountdown(host, timer, callbacks);
                        status = countdown.Status;
                        var driver = host.Go.AddComponent<UnityUiStatusCountdownDriver>();
                        driver.Initialize(TickCountdown);
                    }
                    else if (node is UiProgress progress)
                    {
                        reservesCue = true;
                        caption = Caption(host, progress.DisplayLabel, TopiaForgeTextStyle.Body);
                        status = UiStatusPresentation.For(progress);
                        track = AddGraphic<TopiaForgeRoundedImage>(host, "Progress track");
                        fill = AddGraphic<TopiaForgeRoundedImage>(host, "Progress fill");
                    }
                    else
                    {
                        throw new ArgumentException("Expected a status widget.", nameof(node));
                    }

                    if (reservesCue)
                    {
                        cue = AddGraphic<Image>(host, "Status cue");
                        cue.sprite = TopiaForgeSprites.Glyph(UiGlyph.Warning);
                        cue.enabled = false;
                    }
                }

                internal static bool Handles(UiNode node) =>
                    node is UiStatBar or UiCountdown { UsesStatusFormat: true } or UiProgress { Thresholds: not null };

                internal UiLayoutLeafMeasurement Measure(UiLayoutConstraints constraints, UiThemeDescriptor effective,
                    UiThemeDescriptor? declared, bool highContrast)
                {
                    if (stat != null) return stat.Measure(constraints, effective, declared, highContrast);

                    countdown?.UseTypography(host.ResolvedTheme, host.Host.EffectiveUiScale);
                    var row = RowSize(constraints.MaxWidth);
                    if (node is UiProgress && double.IsFinite(constraints.MaxWidth))
                        row = new UiLayoutExtent(Math.Max(row.Width, constraints.MaxWidth), row.Height);
                    var height = node is UiProgress ? row.Height + Gap + ProgressHeight : row.Height;
                    return new UiLayoutLeafMeasurement(new UiLayoutExtent(row.Width, height), UiLayoutPaintKind.Protected);
                }

                internal void Apply(UiLayoutRect bounds, UiPaintRole behind, UiThemeScheme colors,
                    TopiaForgeResolvedTheme theme, UiThemeDescriptor effective, UiThemeDescriptor? declared)
                {
                    if (stat != null)
                    {
                        stat.Apply(bounds, behind, colors, theme, effective, declared);
                        return;
                    }

                    scheme = colors;
                    backdrop = behind;
                    countdown?.UseTypography(theme, host.Host.EffectiveUiScale);
                    RefreshCountdown();
                    var row = RowSize(bounds.Width);
                    var cueExtent = CueExtent;
                    var captionStart = reservesCue ? cueExtent + Gap : 0f;
                    var digits = countdown?.Extent ?? default;
                    var digitsLeft = row.Width - digits.Width;
                    var captionWidth = Math.Max(0, (digits.Width > 0 && caption != null ? digitsLeft - Gap : digitsLeft) - captionStart);
                    if (caption != null)
                        Place(caption.rectTransform, new UiLayoutRect(captionStart, 0, captionWidth, row.Height));
                    if (countdown != null)
                        Place(countdown.Text.rectTransform, new UiLayoutRect(digitsLeft, 0, digits.Width, row.Height));
                    if (cue != null)
                        Place(cue.rectTransform, new UiLayoutRect(0, (row.Height - cueExtent) / 2, cueExtent, cueExtent));
                    PaintStatus();

                    if (node is UiProgress progress)
                    {
                        var meter = UiStatusMeterPaint.Resolve(colors, status.Tone, theme.HighContrast);
                        var trackBounds = new UiLayoutRect(0, bounds.Height - ProgressHeight, bounds.Width, ProgressHeight);
                        Place(track!.rectTransform, trackBounds);
                        track.SetShape(ProgressHeight / 2);
                        track.color = ColorOf(meter.Empty.Fill);
                        Place(fill!.rectTransform, new UiLayoutRect(0, trackBounds.Y, trackBounds.Width * progress.Fraction, ProgressHeight));
                        fill.SetShape(ProgressHeight / 2);
                        fill.color = ColorOf(meter.Filled.Fill);
                    }
                }

                private float CueExtent => reservesCue
                    ? GlyphFloor * Math.Max(1f, host.Host.EffectiveUiScale) / host.Host.EffectiveUiScale : 0f;

                private UiLayoutExtent RowSize(double available)
                {
                    var glyph = CueExtent;
                    var digits = countdown?.Extent ?? default;
                    var extra = (glyph > 0 ? glyph + Gap : 0) + digits.Width + (caption != null && digits.Width > 0 ? Gap : 0);
                    var preferred = caption == null ? default : MeasureCaption(caption, available - extra);
                    return new UiLayoutExtent(preferred.x + extra, Math.Max(preferred.y, Math.Max(glyph, digits.Height)));
                }

                private static Vector2 MeasureCaption(TMP_Text text, double available)
                {
                    var natural = text.GetPreferredValues(text.text);
                    if (available <= 0 || natural.x <= available) return natural;
                    var wrapped = text.GetPreferredValues(text.text, (float)available, 0);
                    return new Vector2(Mathf.Max((float)available, wrapped.x), wrapped.y);
                }

                private void TickCountdown()
                {
                    if (RefreshCountdown()) PaintStatus();
                }

                private bool RefreshCountdown()
                {
                    if (countdown == null || !countdown.Refresh()) return false;
                    status = countdown.Status;
                    return true;
                }

                private void PaintStatus()
                {
                    if (scheme == null) return;
                    var ink = ColorOf(scheme.ResolveForeground(status.Tone, backdrop));
                    if (caption != null) caption.color = ink;
                    if (countdown != null) countdown.Text.color = ink;
                    if (cue != null) PaintCue(cue, status.Glyph, ink);
                }

                private static TMP_Text? Caption(TopiaForgeContainer parent, string content, TopiaForgeTextStyle style) =>
                    content.Length == 0 ? null : parent.Label(content, style).PlainText().Go.GetComponent<TMP_Text>();

                internal static void PaintCue(Image image, UiGlyph? glyph, Color ink)
                {
                    image.enabled = glyph.HasValue;
                    if (glyph is not { } symbol) return;
                    image.sprite = TopiaForgeSprites.Glyph(symbol);
                    image.color = ink;
                }
            }
        }
    }

    internal sealed class UnityUiStatusCountdownDriver : MonoBehaviour
    {
        private Action? refresh;
        internal void Initialize(Action tick) => refresh = tick;
        private void Update() => refresh?.Invoke();
        private void OnDestroy() => refresh = null;
    }
}
