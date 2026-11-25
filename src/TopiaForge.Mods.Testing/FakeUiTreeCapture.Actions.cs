using System;
using System.Collections.Generic;
using System.Globalization;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private static readonly UiInsets ActionSlotInsets = new UiInsets(UiSpace.Sm);
        private float ActionGap => 4f * readableScale;
        private float ActionIconSize => UiActionSlotGeometry.IconSize * readableScale;

        private UiActionBindingPresentation ActionBinding(string name, IReadOnlyList<UiInputBindingHint> defaults)
        {
            var live = service.FindInputAction(name, surface.Receiver);
            return UiActionBindingPresentation.Resolve(live?.Bindings, defaults, service.LastInputDevice);
        }

        private UiLayoutLeafMeasurement MeasureAction(UiNode node, UiLayoutConstraints limits)
        {
            UiLayoutExtent size;
            if (node is UiKeyGlyph glyph)
            {
                var binding = ActionBinding(glyph.ActionName, glyph.WireDefaults);
                var (wanted, _) = MeasureKeyCap(binding, (float)(limits.MaxWidth * layoutScale));
                size = new UiLayoutExtent(ToReferenceUnits(wanted.Width, limits.MaxWidth), ToReferenceUnits(wanted.Height, limits.MaxHeight));
            }
            else
            {
                var slot = (UiActionSlot)node;
                var body = UiActionSlotGeometry.ContentBounds(preferences.UiScale, ActionSlotInsets);
                var label = MeasureText(slot.Label, UiTextStyle.Label, (float)(body.Width * layoutScale));
                if (label.Width > body.Width * layoutScale + 0.001f ||
                    label.Height > Height(TextSize(UiTextStyle.Label), UiActionSlotGeometry.MaximumLabelLines) + 0.001f)
                    throw new UiLayoutException(UiLayoutFailure.ClippedContent, slot.Id ?? nameof(UiActionSlot),
                        "The action slot label exceeds two readable lines.");
                var panel = UiActionSlotGeometry.ContentBounds(preferences.UiScale);
                size = new UiLayoutExtent(panel.Width, panel.Height);
            }
            var decoration = ResolveDecoration(node, size);
            return new UiLayoutLeafMeasurement(size, UiLayoutPaintKind.Protected, decoration.ShadowOutsets,
                localPaintBounds: new UiLayoutRect(0d, 0d, size.Width, size.Height));
        }

        private (Size Cap, Size Text) MeasureKeyCap(UiActionBindingPresentation binding, float availableWidth)
        {
            var cue = binding.Cue.HasValue ? ArtGlyphSize + ActionGap : 0f;
            var text = MeasureText(binding.Label, UiTextStyle.Label, Math.Max(1f, availableWidth - cue - 2f * ActionGap));
            return (new Size(text.Width + cue + 2f * ActionGap, Math.Max(text.Height + ActionGap, ActionIconSize)), text);
        }

        private void PaintKeyGlyph(UiKeyGlyph glyph, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var binding = ActionBinding(glyph.ActionName, glyph.WireDefaults);
            var (size, text) = MeasureKeyCap(binding, bounds.Width);
            PaintKeyCap(path, glyph.Id, nameof(UiKeyGlyph), binding,
                new UiCaptureRect(bounds.X, bounds.Y, size.Width, size.Height), clip, text,
                inheritedDisabled ? UiTone.Muted : UiTone.Neutral);
        }

        private void PaintKeyCap(string path, string? id, string kind, UiActionBindingPresentation binding,
            UiCaptureRect bounds, UiCaptureRect clip, Size text, UiTone tone)
        {
            var style = UiLayoutDecoration.ResolveKeyGlyph(theme.Descriptor,
                new UiLayoutExtent(bounds.Width / layoutScale, bounds.Height / layoutScale), preferences.HighContrast);
            var pair = PairFor(style.Fill, tone);
            var flags = ArtFlags(false);
            if (binding.Cue.HasValue) flags |= UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            var cueInset = binding.Cue.HasValue ? ArtGlyphSize + ActionGap : 0f;
            var spoken = binding.Cue.HasValue ? binding.SpokenControl : "key " + binding.SpokenControl;
            Add(path, id, kind, bounds, clip, new UiCaptureNodeContent(string.Empty, spoken, 0f, 0f, flags,
                keyLabel: binding.Label), DecorationPaint(style, null, pair.Fill, (float)style.BorderWidth * layoutScale));
            var label = new UiCaptureRect(bounds.X + ActionGap + cueInset, bounds.Y + (bounds.Height - text.Height) / 2f, text.Width, text.Height);
            Add(path + "/label", null, "KeyCapLabel", label, clip, TextContent(binding.Label, UiTextStyle.Label, flags),
                new UiCaptureNodePaint(pair.On, pair.Fill));
            if (binding.Cue is { } cue)
            {
                var box = new UiCaptureRect(bounds.X + ActionGap, bounds.Y + (bounds.Height - ArtGlyphSize) / 2f, ArtGlyphSize, ArtGlyphSize);
                Add(path + "/cue:" + cue, null, "KeyCapCue", box, clip, ReadableMark, new UiCaptureNodePaint(pair.On, pair.Fill));
            }
        }

        private void PaintActionSlot(UiActionSlot slot, string path, UiCaptureRect layout, UiCaptureRect clip)
        {
            var size = UiActionSlotGeometry.ContentBounds(preferences.UiScale);
            var bounds = new UiCaptureRect(layout.X, layout.Y, (float)size.Width * layoutScale, (float)size.Height * layoutScale);
            var content = UiActionSlotGeometry.ContentBounds(preferences.UiScale, ActionSlotInsets);
            var inner = new UiCaptureRect(bounds.X + (float)content.X * layoutScale, bounds.Y + (float)content.Y * layoutScale,
                (float)content.Width * layoutScale, (float)content.Height * layoutScale);
            var (now, epoch) = surface.CaptureClock();
            var state = UiActionSlotPresentation.Resolve(slot, now, epoch);
            var binding = ActionBinding(slot.ActionName, slot.WireDefaults);
            var decoration = ResolveDecoration(slot, new UiLayoutExtent(size.Width, size.Height));
            var role = state.Backdrop(decoration.Fill);
            var color = PairFor(role, inheritedDisabled ? UiTone.Muted : state.Tone);
            var (visible, spoken, seconds, fraction) = SlotStatus(state, slot.Charges?.Refill != null);
            var flags = ArtFlags(false);
            if (state.State == UiActionSlotState.Unavailable) flags |= UiCaptureNodeFlags.Disabled;
            if (state.Emphasized) flags |= UiCaptureNodeFlags.Selected;
            if (state.Emphasized || state.StatusGlyph.HasValue || state.Status == UiActionSlotStatus.Charges)
                flags |= UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            var semantic = slot.Label + ", " + (binding.Cue.HasValue ? binding.SpokenControl : "key " + binding.SpokenControl) + ", " + spoken;
            Add(path, slot.Id, nameof(UiActionSlot), bounds, clip, new UiCaptureNodeContent(string.Empty, semantic, 0f, 0f, flags,
                state.StateName, binding.Label, seconds, fraction), DecorationPaint(decoration, null, color.Fill, (float)decoration.BorderWidth * layoutScale));

            if (state.Emphasized)
                Add(path + "/emphasis", null, "ActionEmphasis", bounds, clip, MeaningfulEdge, new UiCaptureNodePaint(
                    scheme.ResolveForeground(UiTone.Primary, role), color.Fill, borderWidth: UiActionSlotGeometry.EmphasisBorder * readableScale));
            if (state.Status == UiActionSlotStatus.Cooldown && state.Cooldown.Elapsed > 0f)
            {
                var sweep = new UiCaptureRect(bounds.X, bounds.Y, bounds.Width * state.Cooldown.Elapsed, bounds.Height);
                Add(path + "/sweep", null, "ActionCooldownSweep", sweep, clip, DecorativeArt,
                    new UiCaptureNodePaint(null, scheme.GetPair(UiPaintRole.Sunken).Fill));
            }

            var art = slot.Glyph.HasValue || slot.Image != null;
            var (key, keyText) = MeasureKeyCap(binding, inner.Width - (art ? ActionIconSize + ActionGap : 0f));
            var labelSize = MeasureText(slot.Label, UiTextStyle.Label, inner.Width);
            var statusInset = state.StatusGlyph.HasValue ? ArtGlyphSize + ActionGap : 0f;
            var status = MeasureText(visible, UiTextStyle.Caption, inner.Width - statusInset);
            var statusHeight = Math.Max(status.Height, state.StatusGlyph.HasValue ? ArtGlyphSize : 0f);
            if (state.Status == UiActionSlotStatus.Charges) statusHeight += ActionGap + 8f * readableScale;
            var contentHeight = key.Height + labelSize.Height + statusHeight;
            var (top, rowGap) = UiActionSlotGeometry.FitVertical(bounds.Height, contentHeight,
                inner.Y - bounds.Y, ActionGap, statusHeight > 0f ? 2 : 1);
            inner = new UiCaptureRect(inner.X, bounds.Y + (float)top, inner.Width, bounds.Height - (float)(2d * top));
            PaintKeyCap(path + "/key", null, "ActionKey", binding, new UiCaptureRect(inner.X, inner.Y, key.Width, key.Height),
                clip.Intersect(inner), keyText, (flags & UiCaptureNodeFlags.Disabled) != 0 ? UiTone.Muted : UiTone.Neutral);
            if (art)
                PaintSlotIcon(slot, path, new UiCaptureRect(inner.Right - ActionIconSize, inner.Y, ActionIconSize, ActionIconSize), clip, color);
            var label = new UiCaptureRect(inner.X, inner.Y + key.Height + (float)rowGap, labelSize.Width, labelSize.Height);
            Add(path + "/label", null, "ActionLabel", label, clip.Intersect(inner), TextContent(slot.Label, UiTextStyle.Label, flags),
                new UiCaptureNodePaint(color.On, color.Fill));
            PaintSlotStatus(path, state, visible, status, statusInset, label.Bottom + (float)rowGap, inner, clip, color, flags);
        }

        private void PaintSlotIcon(UiActionSlot slot, string path, UiCaptureRect box, UiCaptureRect clip, UiThemePair pair)
        {
            if (slot.Glyph is { } glyph)
            {
                Add(path + "/glyph:" + glyph, null, "ActionGlyph", box, clip, ReadableMark, new UiCaptureNodePaint(pair.On, pair.Fill));
                return;
            }

            var asset = images.SlotIcon(slot);
            if (asset.IsReady && asset.Frame is { } frame)
            {
                var fit = frame.Place(box.Width, box.Height);
                var sprite = new UiCaptureRect(box.X + fit.X, box.Y + fit.Y, fit.Width, fit.Height);
                Add(path + "/image", null, "ActionImage", sprite, clip, DecorativeArt,
                    asset.IsMask ? new UiCaptureNodePaint(pair.On, pair.Fill) : default);
                return;
            }
            Add(path + "/image", null, "ActionImage", box, clip,
                DecorativeArt.WithFlags(DecorativeArt.Flags | UiCaptureNodeFlags.MissingAsset),
                new UiCaptureNodePaint(null, pair.Fill), usedFallback: true);
        }

        private void PaintSlotStatus(string path, UiActionSlotPresentation state, string visible, Size measured, float inset,
            float top, UiCaptureRect inner, UiCaptureRect clip, UiThemePair pair, UiCaptureNodeFlags flags)
        {
            if (visible.Length == 0) return;
            var caption = new UiCaptureRect(inner.X + inset, top, measured.Width, measured.Height);
            var contentClip = clip.Intersect(inner);
            Add(path + "/status", null, "ActionStatus", caption, contentClip, TextContent(visible, UiTextStyle.Caption, flags),
                new UiCaptureNodePaint(pair.On, pair.Fill));
            if (state.StatusGlyph is { } cue)
                Add(path + "/status/cue:" + cue, null, "ActionStatusGlyph", new UiCaptureRect(inner.X, top, ArtGlyphSize, ArtGlyphSize),
                    contentClip, ReadableMark, new UiCaptureNodePaint(pair.On, pair.Fill));
            if (state.Status == UiActionSlotStatus.Charges)
                PaintActionCharges(path + "/charges", state.Charges, inner, caption.Bottom + ActionGap, contentClip, pair);
        }

        private void PaintActionCharges(string path, UiRefillPresentation charges, UiCaptureRect inner, float top,
            UiCaptureRect clip, UiThemePair pair)
        {
            var gap = charges.Style == UiPipStyle.Dots ? ActionGap : readableScale;
            var width = Math.Min(8f * readableScale, (inner.Width - (charges.Total - 1) * gap) / charges.Total);
            for (var index = 0; index < charges.Total; index++)
            {
                var bounds = new UiCaptureRect(inner.X + index * (width + gap), top, width, width);
                var round = charges.Style == UiPipStyle.Dots ? width / 2f : 0f;
                var marker = ChildPath(path, index);
                Add(marker, null, "ActionChargeBoundary", bounds, clip, MeaningfulEdge,
                    new UiCaptureNodePaint(pair.On, pair.Fill, round, borderWidth: readableScale));
                var amount = index < charges.Filled ? 1f : index == charges.Filled ? charges.Fraction : 0f;
                if (amount > 0f)
                    Add(marker + "/fill", null, "ActionChargeFill", new UiCaptureRect(bounds.X, top, width * amount, width),
                        clip, ReadableMark, new UiCaptureNodePaint(pair.On, pair.Fill, Math.Min(round, width * amount / 2f)));
            }
        }

        private static (string Visible, string Spoken, double? Seconds, float? Fraction) SlotStatus(
            UiActionSlotPresentation state, bool refilling) =>
            state.Status switch
            {
                UiActionSlotStatus.Unavailable => (state.Text!, state.Text!, null, null),
                UiActionSlotStatus.Cooldown => (Invariant(state.Cooldown.RemainingSeconds) + " s",
                    Invariant(state.Cooldown.RemainingSeconds) + " seconds", state.Cooldown.RemainingSeconds, state.Cooldown.Elapsed),
                UiActionSlotStatus.Charges => (CountText(state.Charges.Filled, state.Charges.Total),
                    Invariant(state.Charges.Filled) + " of " + Invariant(state.Charges.Total) + " charges",
                    refilling ? state.Charges.RemainingSeconds : null, refilling ? state.Charges.Fraction : null),
                _ => (state.Text ?? string.Empty, "ready", null, null),
            };

        private static string CountText(int filled, int total) => Invariant(filled) + " / " + Invariant(total);
        private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
