using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiLayoutRenderer
        {
            private sealed partial class NativeActionSlot
            {
                private void Tick()
                {
                    if (scheme is not { } colors || theme is not { } native) return;
                    var current = UiActionSlotPresentation.Resolve(source, callbacks.Clock(), callbacks.ClockEpoch());
                    var repaint = !initialized || current.State != painted.State || current.Status != painted.Status;
                    if (repaint)
                    {
                        var role = current.Backdrop(decoration.Fill);
                        paintInk = ColorOf(colors.ResolveForeground(current.Tone, role));
                        PaintFrame(current, role, paintInk, colors, native);
                    }
                    var nextSecond = current.Cooldown.RemainingSeconds != painted.Cooldown.RemainingSeconds;
                    var nextChargeSecond = current.Charges.RemainingSeconds != painted.Charges.RemainingSeconds;
                    var reducedMotion = host.Host.EffectiveReducedMotion;
                    if (repaint || current.Cooldown.Elapsed != painted.Cooldown.Elapsed && (!reducedMotion || nextSecond))
                        PaintSweep(current, colors);
                    if (repaint || nextSecond || current.Charges.Filled != painted.Charges.Filled)
                        PaintCaption(current);
                    if (repaint || current.Charges.Filled != painted.Charges.Filled ||
                        current.Charges.Fraction != painted.Charges.Fraction && (!reducedMotion || nextChargeSecond))
                        PaintCharges(current, paintInk);
                    painted = current;
                    initialized = true;
                }

                private void PaintFrame(UiActionSlotPresentation current, UiPaintRole role, Color ink,
                    UiThemeScheme colors, TopiaForgeResolvedTheme native)
                {
                    var panel = new UiLayoutRect(0, 0, layout.Size.Width, layout.Size.Height);
                    var fill = ColorOf(colors.GetPair(role).Fill);
                    var stroke = current.Emphasized ? UiActionSlotGeometry.EmphasisStroke((float)decoration.BorderWidth, layout.Readability)
                        : (float)decoration.BorderWidth;
                    var radius = (float)decoration.CornerRadius;
                    var edge = current.Emphasized ? colors.ResolveForeground(UiTone.Primary, role) : colors.ResolveBoundary(backdrop);

                    shadow.enabled = current.State != UiActionSlotState.Unavailable &&
                        (decoration.ShadowX != 0 || decoration.ShadowY != 0);
                    if (shadow.enabled)
                    {
                        var shadowInk = ColorOf(colors.GetPair(backdrop).On);
                        shadowInk.a = native.Shadow.a;
                        Shape(shadow, panel.Translate(decoration.ShadowX, decoration.ShadowY), shadowInk, radius);
                    }
                    Shape(background, panel, fill, radius);
                    outline.enabled = stroke > 0f;
                    if (outline.enabled) Shape(outline, panel, ColorOf(edge), radius, stroke);
                    key.Apply(binding.Face, layout.Key, role, current.Tone);
                    PaintArt(ink, colors);

                    labelPlate.color = fill;
                    Place(labelPlate.rectTransform, layout.Caption);
                    caption.SetResolvedInk(ink);
                    Place(caption.Rect, layout.Caption);

                    var gap = layout.CueWidth > 0f ? layout.GapSize : 0d;
                    var left = layout.Interior.X + layout.CueWidth + gap;
                    var height = current.Status == UiActionSlotStatus.Charges ? layout.ChargeLabelHeight : layout.StatusHeight;
                    var labelBounds = new UiLayoutRect(left, layout.StatusTop, layout.StatusWidth, height);
                    var visible = current.Status != UiActionSlotStatus.None;
                    statusPlate.enabled = visible;
                    statusPlate.color = fill;
                    Place(statusPlate.rectTransform, new UiLayoutRect(layout.Interior.X, layout.StatusTop,
                        layout.Interior.Width, height));
                    status.SetShown(visible);
                    status.SetResolvedInk(ink);
                    Place(status.Rect, labelBounds);
                    statusCue.enabled = current.StatusGlyph.HasValue;
                    if (current.StatusGlyph is { } glyph)
                    {
                        statusCue.sprite = TopiaForgeSprites.Glyph(glyph);
                        statusCue.color = ink;
                        Place(statusCue.rectTransform, new UiLayoutRect(layout.Interior.X, layout.StatusTop,
                            layout.CueWidth, layout.CueWidth));
                    }
                }

                private void PaintArt(Color ink, UiThemeScheme colors)
                {
                    var image = imageResource?.Invoke();
                    var glyph = source.Glyph;
                    var sprite = glyph.HasValue ? TopiaForgeSprites.Glyph(glyph.Value) : image?.Sprite;
                    imageFallback.enabled = source.Image != null && sprite == null;
                    if (imageFallback.enabled)
                        Shape(imageFallback, layout.Art, ColorOf(colors.GetPair(UiPaintRole.Sunken).Fill),
                            4f * layout.Readability, layout.Readability);
                    art.enabled = sprite != null;
                    if (!art.enabled) return;
                    art.sprite = sprite;
                    art.preserveAspect = true;
                    art.color = glyph.HasValue || image?.IsMask == true ? ink : Color.white;
                    Place(art.rectTransform, layout.Art);
                }

                private void PaintSweep(UiActionSlotPresentation current, UiThemeScheme colors)
                {
                    var fraction = current.Cooldown.Elapsed;
                    var visible = current.Status == UiActionSlotStatus.Cooldown && fraction > 0f;
                    sweepMask.SetShown(visible);
                    if (!visible) return;
                    Place(sweepMask.Rect, new UiLayoutRect(0, 0, layout.Size.Width * fraction, layout.Size.Height));
                    Shape(sweep, new UiLayoutRect(0, 0, layout.Size.Width, layout.Size.Height),
                        ColorOf(colors.GetPair(UiPaintRole.Sunken).Fill), (float)decoration.CornerRadius);
                }

                private void PaintCaption(UiActionSlotPresentation current)
                {
                    if (current.Status == UiActionSlotStatus.Cooldown)
                    {
                        var length = UiCountdownFormatting.WriteDigits(current.Cooldown.RemainingSeconds, UiCountdownFormat.Seconds, digits);
                        digits[length++] = ' ';
                        digits[length++] = 's';
                        statusText.SetCharArray(digits, 0, length);
                    }
                    else if (current.Status == UiActionSlotStatus.Charges)
                    {
                        var length = UiCountdownFormatting.WriteDigits(current.Charges.Filled, UiCountdownFormat.Seconds, digits);
                        digits[length++] = ' ';
                        digits[length++] = '/';
                        digits[length++] = ' ';
                        length += UiCountdownFormatting.WriteDigits(current.Charges.Total, UiCountdownFormat.Seconds, digits.AsSpan(length));
                        statusText.SetCharArray(digits, 0, length);
                    }
                    else status.SetText(current.Text ?? string.Empty);
                }

                private void PaintCharges(UiActionSlotPresentation current, Color ink)
                {
                    var charges = current.Charges;
                    var shown = current.Status == UiActionSlotStatus.Charges;
                    var dot = charges.Style == UiPipStyle.Dots;
                    var gap = dot ? layout.GapSize : layout.Readability;
                    var width = Math.Min(ChargeHeight * layout.Readability,
                        (layout.Interior.Width - (marks.Length - 1) * gap) / Math.Max(1, marks.Length));
                    var top = layout.StatusTop + layout.ChargeLabelHeight + layout.GapSize;
                    var radius = dot ? (float)width * 0.5f : 0f;
                    for (var index = 0; index < marks.Length; index++)
                    {
                        var mark = marks[index];
                        mark.Border.enabled = shown;
                        var fraction = index < charges.Filled ? 1f : index == charges.Filled ? charges.Fraction : 0f;
                        mark.Fill.enabled = shown && fraction > 0f;
                        if (!shown) continue;
                        var x = layout.Interior.X + index * (width + gap);
                        Shape(mark.Border, new UiLayoutRect(x, top, width, ChargeHeight * layout.Readability),
                            ink, radius, layout.Readability);
                        if (mark.Fill.enabled)
                            Shape(mark.Fill, new UiLayoutRect(x, top, width * fraction, ChargeHeight * layout.Readability),
                                ink, Math.Min(radius, (float)width * fraction * 0.5f));
                    }
                }

                private static void Shape(TopiaForgeRoundedImage image, UiLayoutRect bounds, Color color, float radius,
                    float border = 0f)
                {
                    Place(image.rectTransform, bounds);
                    image.color = color;
                    image.SetShape(radius, border);
                }

                private static ChargeMark[] CreateChargeMarks(TopiaForgeContainer parent, int count)
                {
                    if (count == 0) return Array.Empty<ChargeMark>();
                    var marks = new ChargeMark[count];
                    var root = parent.CreateChild("Action charges");
                    TopiaForgeAnchors.Stretch(root.Rect);
                    for (var index = 0; index < count; index++)
                        marks[index] = new ChargeMark(AddGraphic<TopiaForgeRoundedImage>(root, "Charge boundary"),
                            AddGraphic<TopiaForgeRoundedImage>(root, "Charge fill"));
                    return marks;
                }

                private readonly struct ChargeMark
                {
                    internal ChargeMark(TopiaForgeRoundedImage border, TopiaForgeRoundedImage fill)
                    { Border = border; Fill = fill; }
                    internal TopiaForgeRoundedImage Border { get; }
                    internal TopiaForgeRoundedImage Fill { get; }
                }
            }
        }
    }
}
