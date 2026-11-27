using System;
using System.Globalization;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private float TextMinimum => preferences.HighContrast ? 7f : 4.5f;
        private float MarkMinimum => preferences.HighContrast ? 4.5f : 3f;

        private void Text(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect clip, string value,
            UiTextStyle style, UiTone tone, UiCaptureNodeFlags flags, UiPaintRole? background = null)
        {
            var role = background ?? backdrop;
            var paint = new UiCaptureNodePaint(scheme.ResolveForeground(tone, role), scheme.GetPair(role).Fill);
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
                case UiCheckbox checkbox: PaintCheckbox(checkbox, path, bounds, clip); return;
                case UiKeyBindingField field: PaintBindingField(field, path, bounds, clip); return;
                case UiSlider slider: PaintSlider(slider, path, bounds, clip); return;
                case UiTextInput input:
                    surface.TryGetTextValue(input.Id!, out var value);
                    PaintField(path, input.Id, nameof(UiTextInput), bounds, clip, input.Label,
                        string.IsNullOrEmpty(value) ? input.Placeholder : value, Enabled(input.Enabled), string.IsNullOrEmpty(value), input.Error);
                    return;
                case UiDropdown dropdown:
                    surface.TryGetDropdownValue(dropdown.Id!, out var selection);
                    var label = string.Empty;
                    foreach (var choice in dropdown.Choices)
                    {
                        if (choice.Value == selection) { label = choice.Label; break; }
                    }
                    PaintField(path, dropdown.Id, nameof(UiDropdown), bounds, clip, dropdown.Label, label, Enabled(dropdown.Enabled), muted: false);
                    if (Enabled(dropdown.Enabled) && surface.DropdownIsOpen(dropdown.Id) && bounds.Overlaps(clip))
                        PaintDropdownPopup(dropdown, selection, path, bounds);
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
            var enabled = Enabled(button.Enabled);
            PaintHitTarget(path, bounds, clip, enabled);
            var state = State(enabled);
            var flags = ControlFlags(enabled);
            var role = !enabled ? UiPaintRole.Sunken : button.Style switch
            {
                UiButtonStyle.Primary => UiPaintRole.Primary,
                UiButtonStyle.Danger => UiPaintRole.Danger,
                UiButtonStyle.Ghost => backdrop,
                _ => UiPaintRole.Surface,
            };
            var original = PairFor(role, enabled ? button.Style == UiButtonStyle.Ghost ? UiTone.Primary : UiTone.Neutral : UiTone.Muted);
            var ghost = button.Style == UiButtonStyle.Ghost && enabled;
            var tints = ghost ? new UiThemeStateTints(1f, 1f, 1f)
                : UiThemeColors.ResolveTints(original, TextMinimum, UiThemeColors.ButtonHovered, UiThemeColors.ButtonSelected,
                    UiThemeColors.ButtonPressed);
            var fill = StateFill(original.Fill, state, tints);
            var selected = state is UiCaptureControlState.Selected or UiCaptureControlState.SelectedPressed;
            var pressed = state is UiCaptureControlState.Pressed or UiCaptureControlState.SelectedPressed;
            if (selected) flags |= UiCaptureNodeFlags.Selected;
            var component = theme.Descriptor.Components[UiThemeTarget.Button];
            var shadowStyle = enabled && (button.Style is UiButtonStyle.Primary or UiButtonStyle.Danger) && !preferences.HighContrast
                ? component.Shadow : UiThemeShadow.None;
            var (x, y) = UiLayoutDecoration.ShadowOffset(shadowStyle);
            var shadowX = (float)x * layoutScale;
            var shadowY = (float)y * layoutScale;
            var strength = shadowStyle == UiThemeShadow.None || preferences.ReducedMotion ? 0f : Math.Min(1f, preferences.MotionIntensity);
            if (pressed && strength > 0f)
            {
                bounds = new UiCaptureRect(bounds.X + shadowX * strength, bounds.Y + shadowY * strength, bounds.Width, bounds.Height);
                shadowX = 0f;
                shadowY = 0f;
            }
            else if (state == UiCaptureControlState.Hovered && strength > 0f)
            {
                var lift = strength * layoutScale;
                bounds = new UiCaptureRect(bounds.X + lift, bounds.Y - 2f * lift, bounds.Width, bounds.Height);
                shadowX -= 2f * lift;
                shadowY += lift;
            }

            var stroke = button.Style == UiButtonStyle.Secondary ? (float)component.Border : 0f;
            if (preferences.HighContrast && stroke > 0f) stroke = Math.Max(3f, stroke);
            var radius = theme.Descriptor.Shape.Resolve(component.Radius, bounds.Width / layoutScale, bounds.Height / layoutScale);
            var paint = new UiCaptureNodePaint(original.On, fill, radius * layoutScale, stroke * layoutScale, shadowX, shadowY);
            Add(path, button.Id, nameof(UiButton), bounds, clip,
                TextContent(button.Label, UiTextStyle.Label, flags), paint);
            var cues = UiControlPaint.ButtonCues(original.Fill, tints);
            var mark = new UiCaptureNodeContent(string.Empty, button.Label, 0f, 0f,
                UiCaptureNodeFlags.SemanticMark | UiCaptureNodeFlags.HasNonColorCue |
                (selected ? UiCaptureNodeFlags.Selected : UiCaptureNodeFlags.None));
            if (selected && (cues & UiButtonCue.Selection) != 0)
            {
                var top = UiControlPaint.ButtonSelectionTop * layoutScale;
                var leading = new UiCaptureRect(bounds.X + UiControlPaint.ButtonSelectionLeft * layoutScale,
                    bounds.Y + top, UiControlPaint.ButtonSelectionWidth * layoutScale, bounds.Height - 2f * top);
                Add(path + "/selection", null, "ButtonSelectionBar", leading, clip, mark,
                    new UiCaptureNodePaint(original.On, fill));
            }
            if (pressed && (cues & UiButtonCue.Press) != 0)
            {
                var inset = UiControlPaint.ButtonPressInset * layoutScale;
                var outline = bounds.Inset(inset, inset, inset, inset);
                Add(path + "/press", null, "ButtonPressOutline", outline, clip, mark,
                    new UiCaptureNodePaint(original.On, fill, Math.Max(0f, theme.Descriptor.Shape.Control * layoutScale - inset),
                        UiControlPaint.ButtonPressStroke(preferences.HighContrast) * layoutScale));
            }
            PaintFocus(path, state, bounds, clip, backdrop);
        }

        private void PaintToggle(UiToggle toggle, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var enabled = Enabled(toggle.Enabled);
            PaintHitTarget(path, bounds, clip, enabled);
            surface.TryGetToggleValue(toggle.Id!, out var value);
            var labelBounds = bounds.Inset(ToggleLabelInset, 0, 0, 0);
            var controlStateNow = State(enabled);
            var flags = ControlFlags(enabled);
            Text(path, toggle.Id, nameof(UiToggle), labelBounds, clip, toggle.Label, UiTextStyle.Body,
                enabled ? UiTone.Neutral : UiTone.Muted, flags);
            var trackWidth = 40f * layoutScale;
            var trackHeight = 22f * layoutScale;
            var track = new UiCaptureRect(bounds.X + UiControlPaint.CompactCueGutter * layoutScale,
                bounds.Y + (bounds.Height - trackHeight) / 2f, trackWidth, trackHeight);
            var role = !enabled ? UiPaintRole.Sunken : value ? UiPaintRole.Primary : UiPaintRole.Selected;
            var pair = PairFor(role, enabled && value ? UiTone.Neutral : UiTone.Muted);
            var tints = MarkTints(pair, enabled);
            var draw = StateFill(pair.Fill, controlStateNow, tints);
            Add(path + "/track", null, "ToggleTrack", track, clip, EmptyContent,
                new UiCaptureNodePaint(null, draw, track.Height / 2f, 0f, 0f, 0f));
            PaintToggleBoundary(path + "/track/edge", track, clip, track.Height / 2f, value, enabled);

            var thumbSize = 16f * layoutScale;
            var inset = (trackHeight - thumbSize) / 2f;
            var thumb = new UiCaptureRect(value ? track.Right - thumbSize - inset : track.X + inset,
                track.Y + inset, thumbSize, thumbSize);
            var markFlags = flags | UiCaptureNodeFlags.SemanticMark | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            if (value) markFlags |= UiCaptureNodeFlags.Selected;
            var mark = new UiCaptureNodeContent(string.Empty, value ? "On" : "Off", 0, 0, markFlags);
            Add(path + "/thumb", null, "ToggleThumb", thumb, clip, mark, new UiCaptureNodePaint(pair.On, draw,
                thumbSize / 2f, 0f, 0f, 0f));
            PaintToggleBoundary(path + "/thumb/edge", thumb, clip, thumbSize / 2f, value, enabled);
            var backplate = scheme.GetPair(backdrop);
            PaintStateCues(path, toggle.Label, controlStateNow, bounds, clip, pair.Fill, tints, backplate.On, backplate.Fill,
                compact: true);
            PaintFocus(path, controlStateNow, bounds, clip, backdrop);
        }

        private void PaintCheckbox(UiCheckbox checkbox, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var enabled = Enabled(checkbox.Enabled);
            surface.TryGetCheckboxValue(checkbox.Id!, out var checkedValue);
            var flags = ControlFlags(enabled);
            if (checkedValue) flags |= UiCaptureNodeFlags.Selected;
            PaintHitTarget(path, bounds, clip, enabled);
            var gutter = UiControlPaint.CompactCueGutter * layoutScale;
            Text(path, checkbox.Id, nameof(UiCheckbox), bounds.Inset(32f * layoutScale + gutter, 0f, 0f, 0f), clip,
                checkbox.Label, UiTextStyle.Body, enabled ? UiTone.Neutral : UiTone.Muted, flags);
            var size = 24f * layoutScale;
            var box = new UiCaptureRect(bounds.X + gutter, bounds.Y + (bounds.Height - size) / 2f, size, size);
            var role = enabled && checkedValue ? UiPaintRole.Primary : UiPaintRole.Sunken;
            var pair = PairFor(role, enabled ? UiTone.Neutral : UiTone.Muted);
            var state = State(enabled);
            var tints = enabled && !checkedValue
                ? new UiThemeStateTints(UiThemeColors.ControlHighlighted, UiThemeColors.ControlHighlighted, UiThemeColors.ControlPressed)
                : MarkTints(pair, enabled);
            var fill = StateFill(pair.Fill, state, tints);
            var radius = theme.Descriptor.Shape.Small * layoutScale;
            Add(path + "/box", null, "CheckboxBox", box, clip, EmptyContent, new UiCaptureNodePaint(null, fill, radius));
            PaintToggleBoundary(path + "/box/edge", box, clip, radius, checkedValue, enabled);
            if (checkedValue)
            {
                var mark = flags | UiCaptureNodeFlags.SemanticMark | UiCaptureNodeFlags.RequiresNonColorCue |
                    UiCaptureNodeFlags.HasNonColorCue;
                var inset = 3f * layoutScale;
                Add(path + "/check", null, "CheckboxCheck", box.Inset(inset, inset, inset, inset), clip,
                    new UiCaptureNodeContent(string.Empty, checkbox.Label, 0f, 0f, mark), new UiCaptureNodePaint(pair.On, fill));
            }
            var backplate = scheme.GetPair(backdrop);
            PaintStateCues(path, checkbox.Label, state, bounds, clip, pair.Fill, tints, backplate.On, backplate.Fill, compact: true);
            PaintFocus(path, state, bounds, clip, backdrop, checkbox.Label);
        }

        private void PaintToggleBoundary(string path, UiCaptureRect rect, UiCaptureRect clip, float radius, bool value, bool enabled)
        {
            var ink = UiControlPaint.ToggleBoundary(scheme, backdrop, value, enabled);
            Add(path, null, "ToggleBoundary", rect, clip,
                new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.BoundaryMark),
                new UiCaptureNodePaint(ink, scheme.GetPair(backdrop).Fill, radius, (preferences.HighContrast ? 3f : 2f) * layoutScale));
        }

        private void PaintSlider(UiSlider slider, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            surface.TryGetSliderValue(slider.Id!, out var value);
            var enabled = Enabled(slider.Enabled);
            var state = State(enabled);
            var flags = ControlFlags(enabled);
            var labelSize = bounds.Width * 0.42f;
            var valueSize = bounds.Width * 0.16f;
            var text = enabled ? UiTone.Neutral : UiTone.Muted;
            var body = bounds.Inset(labelSize, 0f, valueSize, 0f);
            PaintHitTarget(path, body, clip, enabled);
            Text(path, slider.Id, nameof(UiSlider), new UiCaptureRect(bounds.X, bounds.Y, labelSize, bounds.Height), clip,
                slider.Label, UiTextStyle.Body, text, flags);
            Text(path + "/value", null, "SliderValue", new UiCaptureRect(bounds.Right - valueSize, bounds.Y, valueSize, bounds.Height), clip,
                slider.UsesExplicitFormat ? slider.Format.Readout(value, slider.Minimum, slider.Maximum) :
                    value.ToString("0.##", CultureInfo.InvariantCulture), UiTextStyle.Caption, UiTone.Muted, flags);

            var gutter = UiControlPaint.CompactCueGutter * layoutScale;
            var track = new UiCaptureRect(body.X + gutter, body.Y + body.Height / 2f - (4f * layoutScale),
                Math.Max(0f, body.Width - gutter), 8f * layoutScale);
            var fraction = (value - slider.Minimum) / (slider.Maximum - slider.Minimum);
            var pair = UiControlPaint.SliderTrack(scheme, useHudScheme, enabled, MarkMinimum);
            var tints = MarkTints(pair, enabled);
            var background = StateFill(pair.Fill, state, tints);
            Add(path + "/track", null, "Track", track, clip, EmptyContent,
                new UiCaptureNodePaint(null, background, track.Height / 2f, 0f, 0f, 0f));
            var size = Math.Min(track.Width, 16f * layoutScale);
            var thumb = new UiCaptureRect(track.X + fraction * (track.Width - size), track.Y + (track.Height - size) / 2f, size, size);
            if (enabled)
            {
                var fill = new UiCaptureRect(track.X, track.Y, thumb.X - track.X, track.Height);
                var fillColor = UiControlPaint.SliderFill(scheme, pair.Fill, MarkMinimum);
                Add(path + "/fill", null, "TrackFill", fill, clip, EmptyContent,
                    new UiCaptureNodePaint(null, fillColor, track.Height / 2f, 0f, 0f, 0f));
            }

            Add(path + "/thumb", null, "SliderThumb", thumb, clip,
                new UiCaptureNodeContent(string.Empty, slider.Label, 0, 0, flags | UiCaptureNodeFlags.SemanticMark),
                new UiCaptureNodePaint(pair.On, background, size / 2f, 0f, 0f, 0f));
            var backplate = scheme.GetPair(backdrop);
            PaintStateCues(path, slider.Label, state, body, clip, pair.Fill, tints, backplate.On, backplate.Fill, compact: true);
            PaintFocus(path, state, body, clip, backdrop);
        }

        private UiThemeStateTints MarkTints(UiThemePair pair, bool enabled) => enabled
            ? UiThemeColors.ResolveTints(pair, MarkMinimum, UiThemeColors.ControlHighlighted,
                UiThemeColors.ControlHighlighted, UiThemeColors.ControlPressed)
            : new UiThemeStateTints(1f, 1f, 1f);

        private void PaintField(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect clip,
            string label, string value, bool enabled, bool muted, string? error = null)
        {
            var captionHeight = MeasureText(label, UiTextStyle.Caption, bounds.Width).Height;
            var caption = new UiCaptureRect(bounds.X, bounds.Y, bounds.Width, captionHeight);
            var state = State(enabled);
            var flags = ControlFlags(enabled);
            if (error == null) Text(path, id, kind, caption, clip, label, UiTextStyle.Caption, UiTone.Muted, flags);
            else
            {
                var captionText = TextContent(label, UiTextStyle.Caption, flags, label + ". " + error);
                Add(path, id, kind, caption, clip, new UiCaptureNodeContent(captionText.Text, captionText.SemanticLabel,
                    captionText.TextHeight, captionText.ExpectedTextHeight, flags, "error"),
                    new UiCaptureNodePaint(scheme.ResolveForeground(UiTone.Muted, backdrop), scheme.GetPair(backdrop).Fill));
            }
            var field = error == null ? bounds.Inset(0f, captionHeight + LabelGap, 0f, 0f) :
                new UiCaptureRect(bounds.X, caption.Bottom + LabelGap, bounds.Width, ControlHeight);
            PaintHitTarget(path, field, clip, enabled);
            var pair = PairFor(UiPaintRole.Sunken, muted || !enabled ? UiTone.Muted : UiTone.Neutral);
            var tints = UiThemeColors.ResolveTints(pair, TextMinimum, UiThemeColors.ButtonHovered,
                UiThemeColors.ButtonSelected, UiThemeColors.ButtonPressed);
            var fill = StateFill(pair.Fill, state, tints);
            var component = theme.Descriptor.Components[UiThemeTarget.Input];
            var radius = theme.Descriptor.Shape.Resolve(component.Radius, field.Width / layoutScale, field.Height / layoutScale);
            var stroke = error != null ? 3f : (float)component.Border;
            if (preferences.HighContrast && stroke > 0f) stroke = Math.Max(3f, stroke);
            var (shadowX, shadowY) = UiLayoutDecoration.ShadowOffset(enabled && !preferences.HighContrast
                ? component.Shadow : UiThemeShadow.None);
            Add(path + "/value", null, "FieldValue", field, clip, TextContent(value, UiTextStyle.Body, flags, label),
                new UiCaptureNodePaint(pair.On, fill, radius * layoutScale, stroke * layoutScale,
                    (float)shadowX * layoutScale, (float)shadowY * layoutScale));
            PaintStateCues(path, label, state, field, clip, pair.Fill, tints, pair.On, fill);
            PaintFocus(path, state, field, clip, backdrop);
            if (error != null) PaintValidation(path, error, field, bounds, clip, radius * layoutScale);
        }

        private void PaintValidation(string path, string error, UiCaptureRect field, UiCaptureRect bounds, UiCaptureRect clip, float radius)
        {
            var background = scheme.GetPair(backdrop).Fill;
            var danger = scheme.ResolveForeground(UiTone.Danger, backdrop);
            var border = new UiCaptureNodePaint(danger, background, radius, 3f * layoutScale);
            Add(path + "/error-outline", null, "FieldErrorOutline", field, clip,
                new UiCaptureNodeContent(string.Empty, error, 0f, 0f, UiCaptureNodeFlags.BoundaryMark), border);
            var message = bounds.Inset(0f, field.Bottom - bounds.Y + LabelGap, 0f, 0f);
            var size = 18f * layoutScale;
            Add(path + "/error-glyph", null, "FieldErrorGlyph", new UiCaptureRect(message.X, message.Y, size, size), clip,
                new UiCaptureNodeContent(string.Empty, "Error", 0f, 0f,
                    UiCaptureNodeFlags.SemanticMark | UiCaptureNodeFlags.HasNonColorCue), new UiCaptureNodePaint(danger, background));
            Text(path + "/error", null, "FieldErrorMessage", message.Inset(24f * layoutScale, 0f, 0f, 0f), clip,
                error, UiTextStyle.Caption, UiTone.Danger, UiCaptureNodeFlags.None);
        }

        private void PaintHitTarget(string path, UiCaptureRect bounds, UiCaptureRect clip, bool enabled, bool popup = false)
        {
            if (!popup && surface.HasOpenDropdown) return;
            if (surface.OverlayKind.HasValue && !service.AllowsOverlayInput(surface)) return;
            var flags = UiCaptureNodeFlags.HitTarget | (enabled ? UiCaptureNodeFlags.None : UiCaptureNodeFlags.Disabled);
            Add(path + "/hit", null, "HitTarget", bounds, clip,
                new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, flags), default);
        }

        private void PaintProgress(UiProgress progress, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var height = MeasureText(progress.Label, UiTextStyle.Body, bounds.Width).Height;
            var label = new UiCaptureRect(bounds.X, bounds.Y, bounds.Width, height);
            var colors = PairFor(backdrop, progress.Tone);
            var content = TextContent(progress.Label, UiTextStyle.Body, UiCaptureNodeFlags.ScrollableClip)
                .WithStatus("normal", fraction: progress.Fraction);
            Add(path, progress.Id, nameof(UiProgress), label, clip, content, new UiCaptureNodePaint(colors.On, colors.Fill));
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
            UiPaintRole background, string label = "")
        {
            if (state != UiCaptureControlState.Focused) return;
            var width = UiControlFocusGeometry.Stroke(preferences.HighContrast) * layoutScale;
            var offset = UiControlFocusGeometry.Gap * layoutScale + width;
            var ring = bounds.Inset(-offset, -offset, -offset, -offset);
            var paint = new UiCaptureNodePaint(scheme.ResolveFocus(background), scheme.GetPair(background).Fill,
                theme.Descriptor.Shape.Control * layoutScale + offset, width, 0f, 0f);
            var content = new UiCaptureNodeContent(string.Empty, label, 0, 0,
                UiCaptureNodeFlags.SemanticMark | UiCaptureNodeFlags.FocusIndicator);
            Add(path + "/focus", null, "FocusRing", ring, clip, content, paint);
        }

        private UiThemeColor StatefulFill(UiThemePair pair, UiCaptureControlState state, float minimum, bool switchControl)
        {
            var tints = switchControl
                ? UiThemeColors.ResolveTints(pair, minimum, UiThemeColors.ControlHighlighted, UiThemeColors.ControlHighlighted,
                    UiThemeColors.ControlPressed)
                : UiThemeColors.ResolveTints(pair, minimum, UiThemeColors.ButtonHovered, UiThemeColors.ButtonSelected,
                    UiThemeColors.ButtonPressed);
            return StateFill(pair.Fill, state, tints);
        }

        private static UiThemeColor StateFill(UiThemeColor fill, UiCaptureControlState state, UiThemeStateTints tints)
        {
            var tint = state switch
            {
                UiCaptureControlState.Hovered => tints.Hovered,
                UiCaptureControlState.Selected => tints.Selected,
                UiCaptureControlState.Pressed or UiCaptureControlState.SelectedPressed => tints.Pressed,
                _ => 1f,
            };
            return fill.Tinted(tint);
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
