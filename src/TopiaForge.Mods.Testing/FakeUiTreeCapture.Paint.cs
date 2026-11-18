using System;
using System.Globalization;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private float TextMinimum => preferences.HighContrast ? 7f : 4.5f;
        private float MarkMinimum => preferences.HighContrast ? 4.5f : 3f;

        private void Text(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect clip, string value,
            UiTextStyle style, UiTone tone, UiCaptureNodeFlags flags, UiPaintRole background = UiPaintRole.Surface)
        {
            var paint = new UiCaptureNodePaint(scheme.ResolveForeground(tone, background), scheme.GetPair(background).Fill);
            Add(path, id, kind, bounds, clip, TextContent(value, style, flags), paint);
        }

        private UiCaptureNodeContent TextContent(string text, UiTextStyle style, UiCaptureNodeFlags flags, string? accessibleLabel = null)
        {
            var role = theme.Descriptor.GetText(style);
            var visible = role.IsUppercase ? text.ToUpperInvariant() : text;
            var expectedHeight = text.Length > 0 ? TextSize(style) : 0f;
            var metrics = service.ResolveTextMetrics(theme.Descriptor, role.Font);
            var actualHeight = metrics.PredictHeight(expectedHeight);
            return new UiCaptureNodeContent(visible, accessibleLabel ?? text, actualHeight, expectedHeight, flags);
        }

        private void PaintControl(UiNode node, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            switch (node)
            {
                case UiButton button: PaintButton(button, path, bounds, clip); return;
                case UiToggle toggle: PaintToggle(toggle, path, bounds, clip); return;
                case UiSlider slider: PaintSlider(slider, path, bounds, clip); return;
                case UiTextInput input:
                    surface.TryGetTextValue(input.Id!, out var value);
                    PaintField(path, input.Id, nameof(UiTextInput), bounds, clip, input.Label,
                        string.IsNullOrEmpty(value) ? input.Placeholder : value, input.Enabled, string.IsNullOrEmpty(value));
                    return;
                case UiDropdown dropdown:
                    surface.TryGetDropdownValue(dropdown.Id!, out var selection);
                    var label = string.Empty;
                    foreach (var choice in dropdown.Choices)
                    {
                        if (choice.Value == selection) { label = choice.Label; break; }
                    }
                    PaintField(path, dropdown.Id, nameof(UiDropdown), bounds, clip, dropdown.Label, label, dropdown.Enabled, muted: false);
                    return;
                case UiVirtualList list: PaintList(list, path, bounds, clip); return;
                case UiGraphCanvas graph: PaintGraph(graph, path, bounds, clip); return;
                default:
                    Add(path, node.Id, node.GetType().Name, bounds, clip,
                        new UiCaptureNodeContent(string.Empty, string.Empty, 0, 0, UiCaptureNodeFlags.InvalidLayout), default);
                    return;
            }
        }

        private void PaintButton(UiButton button, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var state = State(button.Enabled);
            var flags = ControlFlags(button.Enabled);
            var role = !button.Enabled ? UiPaintRole.Sunken : button.Style switch
            {
                UiButtonStyle.Primary => UiPaintRole.Primary,
                UiButtonStyle.Danger => UiPaintRole.Danger,
                _ => UiPaintRole.Surface,
            };
            var original = PairFor(role, button.Enabled ? button.Style == UiButtonStyle.Ghost ? UiTone.Primary : UiTone.Neutral : UiTone.Muted);
            var fill = StatefulFill(original, state, TextMinimum, switchControl: false);
            var hasShadow = button.Enabled && button.Style != UiButtonStyle.Ghost && !preferences.HighContrast;
            var shadowX = hasShadow ? -3f * layoutScale : 0f;
            var shadowY = hasShadow ? 4f * layoutScale : 0f;
            if (hasShadow && state == UiCaptureControlState.Pressed && !preferences.ReducedMotion && preferences.MotionIntensity > 0f)
            {
                bounds = new UiCaptureRect(bounds.X + shadowX, bounds.Y + shadowY, bounds.Width, bounds.Height);
                shadowX = 0f;
                shadowY = 0f;
            }

            var border = button.Style == UiButtonStyle.Secondary ? BorderWidth(true) : 0f;
            var paint = new UiCaptureNodePaint(original.On, fill, theme.Descriptor.Shape.Control * layoutScale, border, shadowX, shadowY);
            Add(path, button.Id, nameof(UiButton), bounds, clip,
                TextContent(button.Label, UiTextStyle.Label, flags), paint);
            PaintFocus(path, state, bounds, clip, UiPaintRole.Surface);
        }

        private void PaintToggle(UiToggle toggle, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            surface.TryGetToggleValue(toggle.Id!, out var enabled);
            var labelBounds = bounds.Inset(0, 0, 58f * layoutScale, 0);
            var controlStateNow = State(toggle.Enabled);
            var flags = ControlFlags(toggle.Enabled);
            Text(path, toggle.Id, nameof(UiToggle), labelBounds, clip, toggle.Label, UiTextStyle.Body,
                toggle.Enabled ? UiTone.Neutral : UiTone.Muted, flags);
            var trackWidth = 50f * layoutScale;
            var trackHeight = 26f * layoutScale;
            var track = new UiCaptureRect(bounds.Right - trackWidth, bounds.Y + (bounds.Height - trackHeight) / 2f, trackWidth, trackHeight);
            var role = toggle.Enabled && enabled ? UiPaintRole.Primary : UiPaintRole.Sunken;
            var pair = PairFor(role, toggle.Enabled && enabled ? UiTone.Neutral : UiTone.Muted);
            var draw = StatefulFill(pair, controlStateNow, MarkMinimum, switchControl: true);
            Add(path + "/track", null, "ToggleTrack", track, clip, EmptyContent,
                new UiCaptureNodePaint(null, draw, track.Height / 2f, 0f, 0f, 0f));

            var thumbSize = 18f * layoutScale;
            var inset = (trackHeight - thumbSize) / 2f;
            var thumb = new UiCaptureRect(enabled ? track.Right - thumbSize - inset : track.X + inset,
                track.Y + inset, thumbSize, thumbSize);
            var markFlags = flags | UiCaptureNodeFlags.SemanticMark | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            if (enabled) markFlags |= UiCaptureNodeFlags.Selected;
            var mark = new UiCaptureNodeContent(string.Empty, enabled ? "On" : "Off", 0, 0, markFlags);
            Add(path + "/thumb", null, "ToggleThumb", thumb, clip, mark, new UiCaptureNodePaint(pair.On, draw,
                thumbSize / 2f, 0f, 0f, 0f));
            PaintFocus(path, controlStateNow, track, clip, UiPaintRole.Surface);
        }

        private void PaintSlider(UiSlider slider, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            surface.TryGetSliderValue(slider.Id!, out var value);
            var state = State(slider.Enabled);
            var flags = ControlFlags(slider.Enabled);
            var labelSize = bounds.Width * 0.42f;
            var valueSize = bounds.Width * 0.16f;
            var text = slider.Enabled ? UiTone.Neutral : UiTone.Muted;
            Text(path, slider.Id, nameof(UiSlider), new UiCaptureRect(bounds.X, bounds.Y, labelSize, bounds.Height), clip,
                slider.Label, UiTextStyle.Body, text, flags);
            Text(path + "/value", null, "SliderValue", new UiCaptureRect(bounds.Right - valueSize, bounds.Y, valueSize, bounds.Height), clip,
                value.ToString("0.##", CultureInfo.InvariantCulture), UiTextStyle.Caption, UiTone.Muted, flags);

            var track = new UiCaptureRect(bounds.X + labelSize, bounds.Y + bounds.Height / 2f - (4f * layoutScale),
                Math.Max(0, bounds.Width - labelSize - valueSize), 8f * layoutScale);
            var fraction = (value - slider.Minimum) / (slider.Maximum - slider.Minimum);
            PaintTrack(path, track, clip, fraction, slider.Enabled ? UiPaintRole.Accent : UiPaintRole.Selected);
            var size = Math.Min(track.Width, 18f * layoutScale);
            var thumb = new UiCaptureRect(track.X + fraction * (track.Width - size), track.Y + (track.Height - size) / 2f, size, size);
            var background = scheme.GetPair(UiPaintRole.Sunken).Fill;
            var color = slider.Enabled ? (useHudScheme ? new UiThemeColor(255, 255, 255) : scheme.GetPair(UiPaintRole.Accent).Fill) : scheme.Muted;
            color = UiThemeColors.RepairForeground(color, background, MarkMinimum);
            Add(path + "/thumb", null, "SliderThumb", thumb, clip,
                new UiCaptureNodeContent(string.Empty, slider.Label, 0, 0, flags | UiCaptureNodeFlags.SemanticMark),
                new UiCaptureNodePaint(color, background, size / 2f, 0f, 0f, 0f));
            PaintFocus(path, state, bounds, clip, UiPaintRole.Surface);
        }

        private void PaintField(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect clip,
            string label, string value, bool enabled, bool muted)
        {
            var captionHeight = MeasureText(label, UiTextStyle.Caption, bounds.Width).Height;
            var caption = new UiCaptureRect(bounds.X, bounds.Y, bounds.Width, captionHeight);
            var state = State(enabled);
            var flags = ControlFlags(enabled);
            Text(path, id, kind, caption, clip, label, UiTextStyle.Caption, UiTone.Muted, flags);
            var field = bounds.Inset(0, captionHeight + LabelGap, 0, 0);
            var pair = PairFor(UiPaintRole.Sunken, muted || !enabled ? UiTone.Muted : UiTone.Neutral);
            var fill = StatefulFill(pair, state, TextMinimum, switchControl: false);
            Add(path + "/value", null, "FieldValue", field, clip, TextContent(value, UiTextStyle.Body, flags, label),
                new UiCaptureNodePaint(pair.On, fill, theme.Descriptor.Shape.Control * layoutScale, BorderWidth(true), 0f, 0f));
            PaintFocus(path, state, field, clip, UiPaintRole.Surface);
        }

        private void PaintProgress(UiProgress progress, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var height = MeasureText(progress.Label, UiTextStyle.Body, bounds.Width).Height;
            var label = new UiCaptureRect(bounds.X, bounds.Y, bounds.Width, height);
            Text(path, progress.Id, nameof(UiProgress), label, clip, progress.Label, UiTextStyle.Body, progress.Tone,
                UiCaptureNodeFlags.ScrollableClip);
            var track = new UiCaptureRect(bounds.X, bounds.Bottom - 14f * layoutScale, bounds.Width, 14f * layoutScale);
            var role = progress.Tone == UiTone.Neutral || progress.Tone == UiTone.Muted
                ? UiPaintRole.Primary
                : UiThemeScheme.RoleForTone(progress.Tone);
            PaintTrack(path, track, clip, progress.Fraction, role);
        }

        private void PaintTrack(string path, UiCaptureRect track, UiCaptureRect clip, float fraction, UiPaintRole role)
        {
            var pair = scheme.GetPair(UiPaintRole.Sunken);
            Add(path + "/track", null, "Track", track, clip, EmptyContent, new UiCaptureNodePaint(null, pair.Fill,
                track.Height / 2f, 0f, 0f, 0f));
            var fillColor = UiThemeColors.RepairForeground(scheme.GetPair(role).Fill, pair.Fill, MarkMinimum);
            var fill = new UiCaptureRect(track.X, track.Y, track.Width * fraction, track.Height);
            Add(path + "/fill", null, "TrackFill", fill, clip, EmptyContent, new UiCaptureNodePaint(null, fillColor,
                track.Height / 2f, 0f, 0f, 0f));
        }

        private void PaintFocus(string path, UiCaptureControlState state, UiCaptureRect bounds, UiCaptureRect clip,
            UiPaintRole background)
        {
            if (state != UiCaptureControlState.Focused) return;
            var width = BorderWidth(true);
            var offset = 2f * layoutScale + width;
            var ring = bounds.Inset(-offset, -offset, -offset, -offset);
            var paint = new UiCaptureNodePaint(scheme.ResolveFocus(background), scheme.GetPair(background).Fill,
                theme.Descriptor.Shape.Control * layoutScale, width, 0f, 0f);
            Add(path + "/focus", null, "FocusRing", ring, clip, EmptyContent, paint);
        }

        private UiThemeColor StatefulFill(UiThemePair pair, UiCaptureControlState state, float minimum, bool switchControl)
        {
            var tints = switchControl
                ? UiThemeColors.ResolveTints(pair, minimum, UiThemeColors.ControlHighlighted, UiThemeColors.ControlHighlighted,
                    UiThemeColors.ControlPressed)
                : UiThemeColors.ResolveTints(pair, minimum, UiThemeColors.ButtonHovered, UiThemeColors.ButtonSelected,
                    UiThemeColors.ButtonPressed);
            var tint = state switch
            {
                UiCaptureControlState.Hovered => tints.Hovered,
                UiCaptureControlState.Selected => tints.Selected,
                UiCaptureControlState.Pressed => tints.Pressed,
                _ => 1f,
            };
            return pair.Fill.Tinted(tint);
        }

        private UiThemePair PairFor(UiPaintRole role, UiTone tone) =>
            new UiThemePair(scheme.GetPair(role).Fill, scheme.ResolveForeground(tone, role));

        private float BorderWidth(bool hasBorder) => hasBorder ? (preferences.HighContrast ? 3f : 2f) * layoutScale : 0f;

        private UiCaptureControlState State(bool enabled) => enabled ? controlState : UiCaptureControlState.Rest;

        private static UiCaptureNodeFlags ControlFlags(bool enabled) =>
            UiCaptureNodeFlags.RequiresLabel | UiCaptureNodeFlags.ScrollableClip |
                (enabled ? UiCaptureNodeFlags.None : UiCaptureNodeFlags.Disabled);
    }
}
