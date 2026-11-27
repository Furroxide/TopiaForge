using System;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private void PaintTab(UiTabs tabs, UiButton tab, int index, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var enabled = Enabled(tabs.Enabled);
            var selected = surface.ActiveTabIndex(tabs) == index;
            var flags = ControlFlags(enabled);
            if (selected) flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            PaintHitTarget(path, bounds, clip, enabled);
            var role = !enabled ? UiPaintRole.Sunken : selected ? UiPaintRole.Selected : backdrop;
            var component = theme.Descriptor.Components[UiThemeTarget.Tabs];
            var radius = theme.Descriptor.Shape.Resolve(component.Radius, bounds.Width / layoutScale, bounds.Height / layoutScale);
            var border = selected ? (float)component.Border : 0f;
            if (preferences.HighContrast && border > 0f) border = Math.Max(3f, border);
            var shadow = selected && enabled && !preferences.HighContrast ? component.Shadow : UiThemeShadow.None;
            var (shadowX, shadowY) = UiLayoutDecoration.ShadowOffset(shadow);
            Add(path, null, "Tab", bounds, clip, TextContent(tab.Label, UiTextStyle.Label, flags),
                new UiCaptureNodePaint(scheme.ResolveForeground(enabled ? UiTone.Neutral : UiTone.Muted, role), scheme.GetPair(role).Fill,
                    radius * layoutScale, border * layoutScale, (float)shadowX * layoutScale, (float)shadowY * layoutScale));
            if (selected)
            {
                var stroke = (preferences.HighContrast ? 3f : 2f) * layoutScale;
                var left = bounds.X + 8f * layoutScale;
                var line = new UiCaptureRect(left, bounds.Bottom - stroke, Math.Max(0f, bounds.Width - 16f * layoutScale), stroke);
                Add(path + "/selection", null, "TabSelection", line, clip, EmptyContent,
                    new UiCaptureNodePaint(scheme.ResolveFocus(role), scheme.GetPair(role).Fill));
            }
            PaintFocus(path, State(enabled), bounds, clip, backdrop);
        }

        private (string Label, string Binding, bool Available) BindingReadout(UiKeyBindingField field)
        {
            var action = service.FindInputAction(field.ActionName, surface.Receiver);
            var label = field.Label ?? action?.Definition.DisplayName ?? field.ActionName;
            if (action != null)
            {
                foreach (var binding in action.Bindings)
                    if (UiRebinding.Device(binding.Kind) == field.Device)
                        return (label, UiRebinding.KeyLabel(binding.Kind, binding.Control), true);
                return (label, "Unbound", true);
            }

            if (!field.HasWireDefaults && UiLayoutTree.TryFind(images.Presentation, field.Id!, out var projected, out _) &&
                projected is UiKeyBindingField received) field = received;
            foreach (var hint in field.WireDefaults)
                if (UiRebinding.Device(hint.Kind) == field.Device)
                    return (label, UiRebinding.KeyLabel(hint.Kind, hint.Control), false);
            return (label, "Unbound", false);
        }

        private void PaintBindingField(UiKeyBindingField field, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var readout = BindingReadout(field);
            var enabled = Enabled(field.Enabled && readout.Available);
            var warning = surface.BindingWarning(field);
            var listening = enabled && surface.IsBindingListening(field.Id);
            var message = !readout.Available ? "Not available here" : listening ? "Listening: press a key, Esc cancels" : warning.Message;
            var captionHeight = 22f * layoutScale;
            var caption = new UiCaptureRect(bounds.X, bounds.Y, bounds.Width, captionHeight);
            var flags = ControlFlags(enabled);
            var state = !readout.Available ? "unavailable" : listening ? "listening" : warning.Conflict ? "conflict" : "idle";
            var text = TextContent(readout.Label, UiTextStyle.Caption, flags);
            var content = new UiCaptureNodeContent(text.Text, text.SemanticLabel, text.TextHeight, text.ExpectedTextHeight,
                text.Flags, state, readout.Binding);
            Add(path, field.Id, nameof(UiKeyBindingField), caption, clip, content,
                new UiCaptureNodePaint(scheme.ResolveForeground(UiTone.Muted, backdrop), scheme.GetPair(backdrop).Fill));
            var row = new UiCaptureRect(bounds.X, bounds.Y + captionHeight + LabelGap, bounds.Width, ControlHeight);
            var rebindWidth = Math.Max(ControlHeight, MeasureText("Rebind", UiTextStyle.Label, row.Width, wrap: false).Width +
                28f * layoutScale);
            var rebind = new UiCaptureRect(row.Right - rebindWidth, row.Y, rebindWidth, row.Height);
            var cap = row.Inset(0f, 0f, rebindWidth + LabelGap, 0f);
            PaintHitTarget(path + "/rebind", rebind, clip, enabled);
            Text(path + "/key", null, "KeyBindingValue", cap, clip, readout.Binding, UiTextStyle.Label,
                enabled ? UiTone.Neutral : UiTone.Muted, flags, UiPaintRole.Sunken);
            Text(path + "/rebind", null, "RebindAction", rebind, clip, "Rebind", UiTextStyle.Label,
                enabled ? UiTone.Neutral : UiTone.Muted, flags, enabled ? UiPaintRole.Surface : UiPaintRole.Sunken);
            PaintFocus(path + "/rebind", State(enabled), rebind, clip, backdrop);
            if (message == null) return;
            var warned = !listening && warning.Message != null;
            var statusBounds = bounds.Inset(warned ? 22f * layoutScale : 0f, captionHeight + 2f * LabelGap + ControlHeight, 0f, 0f);
            if (warned)
                Add(path + "/warning-glyph", null, "BindingWarningGlyph",
                    new UiCaptureRect(bounds.X, statusBounds.Y, 18f * layoutScale, 18f * layoutScale), clip,
                    new UiCaptureNodeContent(string.Empty, "Warning", 0f, 0f, UiCaptureNodeFlags.SemanticMark),
                    new UiCaptureNodePaint(scheme.ResolveForeground(UiTone.Warning, backdrop), scheme.GetPair(backdrop).Fill));
            Text(path + "/status", null, "BindingStatus", statusBounds, clip, message, UiTextStyle.Caption,
                warned ? UiTone.Warning : UiTone.Muted, flags);
        }

        private void PaintDropdownPopup(UiDropdown dropdown, string? selected, string path, UiCaptureRect anchor)
        {
            var margin = 4f * layoutScale;
            var screen = new UiCaptureRect(margin, margin, viewport.Width - 2f * margin, viewport.Height - 2f * margin);
            var width = Math.Min(anchor.Width, screen.Width);
            var rows = Math.Min(dropdown.Choices.Count, (int)Math.Floor(Math.Min(screen.Height, 190f * layoutScale) / ControlHeight));
            if (rows <= 0) return;
            var height = rows * ControlHeight;
            var below = anchor.Bottom + margin;
            var left = Math.Clamp(anchor.X, screen.X, screen.Right - width);
            var top = Math.Clamp(below + height <= screen.Bottom ? below : anchor.Y - margin - height, screen.Y, screen.Bottom - height);
            var popup = new UiCaptureRect(left, top, width, height);
            Add(path + "/choices", null, "DropdownPopup", popup, screen, EmptyContent,
                new UiCaptureNodePaint(null, scheme.GetPair(UiPaintRole.Surface).Fill));
            for (var index = 0; index < rows; index++)
            {
                var choice = dropdown.Choices[index];
                var choicePath = ChildPath(path + "/choices", index);
                var row = new UiCaptureRect(left, top + index * ControlHeight, width, ControlHeight);
                var chosen = choice.Value == selected;
                var flags = ControlFlags(true);
                if (chosen) flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                PaintHitTarget(choicePath, row, popup, enabled: true, popup: true);
                Text(choicePath, null, "DropdownChoice", row, popup, choice.Label, UiTextStyle.Body,
                    UiTone.Neutral, flags, UiPaintRole.Selected);
            }
        }

        private void PaintTooltip(UiTooltip tooltip, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var enabled = !inheritedDisabled;
            var shown = enabled && bounds.Overlaps(clip) &&
                (State(enabled) == UiCaptureControlState.Focused || surface.TooltipHoverElapsed(tooltip.Id));
            Add(path, tooltip.Id, nameof(UiTooltip), bounds, clip,
                new UiCaptureNodeContent(string.Empty, tooltip.Message, 0f, 0f, ControlFlags(enabled), shown ? "shown" : "hidden"), default);
            if (!UiLayoutTree.ContainsInteractive(tooltip.Child))
            {
                PaintHitTarget(path, bounds, clip, enabled);
                PaintFocus(path, State(enabled), bounds, clip, backdrop);
            }
            if (!shown) return;
            var gap = 8f * layoutScale;
            var screen = new UiCaptureRect(gap, gap, Math.Max(0f, viewport.Width - 2f * gap), Math.Max(0f, viewport.Height - 2f * gap));
            var measure = MeasureText(tooltip.Message, UiTextStyle.Label, Math.Max(0f, Math.Min(screen.Width, 280f * layoutScale) - 2f * gap));
            var width = Math.Min(screen.Width, measure.Width + 2f * gap);
            var height = Math.Min(screen.Height, measure.Height + 2f * gap);
            var targetX = Math.Clamp(bounds.X, screen.X, screen.Right - width);
            var below = bounds.Bottom + gap;
            var targetY = Math.Clamp(below + height <= screen.Bottom ? below : bounds.Y - height - gap, screen.Y, screen.Bottom - height);
            var popup = new UiCaptureRect(targetX, targetY, width, height).Inset(gap, gap, gap, gap);
            Text(path + "/popup", null, "TooltipPopup", popup, screen, tooltip.Message, UiTextStyle.Label,
                UiTone.Neutral, UiCaptureNodeFlags.None, UiPaintRole.Raised);
        }
    }
}
