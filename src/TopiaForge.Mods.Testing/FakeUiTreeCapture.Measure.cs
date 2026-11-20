using System;
using System.Globalization;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private float ControlHeight => 40f * layoutScale;
        private float LabelGap => 4f * layoutScale;
        private float ToggleLabelInset => (48f + UiControlPaint.CompactCueGutter) * layoutScale;

        private Size MeasureLeaf(UiNode node, float width)
        {
            switch (node)
            {
                case UiText text:
                    return MeasureText(text.Text, text.Style, width);
                case UiCountdown countdown:
                    return MeasureText(CountdownText(countdown), UiTextStyle.Body, width);
                case UiProgress progress:
                    var label = MeasureText(progress.Label, UiTextStyle.Body, width);
                    return new Size(Math.Max(width, label.Width), label.Height + (progress.Label.Length == 0 ? 14f * layoutScale : 18f * layoutScale));
                case UiButton button:
                    var buttonLabel = MeasureText(button.Label, UiTextStyle.Label, width, wrap: false);
                    return new Size(Math.Max(ControlHeight, buttonLabel.Width + (28f * layoutScale)),
                        Math.Max(ControlHeight, buttonLabel.Height + 8f * layoutScale));
                case UiTextInput input:
                    return MeasureLabeledControl(input.Label, width);
                case UiDropdown dropdown:
                    return MeasureLabeledControl(dropdown.Label, width);
                case UiToggle toggle:
                    var toggleLabel = MeasureText(toggle.Label, UiTextStyle.Body, Math.Max(0, width - ToggleLabelInset));
                    return new Size(Math.Max(width, toggleLabel.Width + ToggleLabelInset), Math.Max(ControlHeight, toggleLabel.Height));
                case UiSlider slider:
                    var sliderLabel = MeasureText(slider.Label, UiTextStyle.Body, width * 0.42f);
                    return new Size(Math.Max(Math.Max(width, sliderLabel.Width / 0.42f), ControlHeight / 0.42f),
                        Math.Max(ControlHeight, sliderLabel.Height));
                case UiVirtualList list:
                    return MeasureVirtualList(list, width);
                default:
                    return new Size(width, 0);
            }
        }

        private Size MeasureLabeledControl(string label, float width)
        {
            var caption = MeasureText(label, UiTextStyle.Caption, width);
            return new Size(Math.Max(ControlHeight, Math.Max(width, caption.Width)), caption.Height + LabelGap + ControlHeight);
        }

        private Size MeasureText(string text, UiTextStyle style, float availableWidth, bool wrap = true,
            UiThemeDescriptor? descriptor = null)
        {
            if (text.Length == 0) return default;
            var typography = (descriptor ?? theme.Descriptor).GetText(style);
            var height = typography.ReferenceSize * readableScale;
            var visual = typography.IsUppercase ? text.ToUpperInvariant() : text;
            var width = 0f;
            var line = 0f;
            var lines = 1;
            for (var index = 0; index < visual.Length; index++)
            {
                var ch = visual[index];
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    width = Math.Max(width, line);
                    line = 0f;
                    lines++;
                    continue;
                }

                var category = CharUnicodeInfo.GetUnicodeCategory(visual, index);
                if (char.IsSurrogatePair(visual, index)) index++;
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark) continue;
                var advance = height * (typography.TrackingEm + EstimatedAdvance(ch, style));
                if (wrap && line > 0f && line + advance > availableWidth)
                {
                    width = Math.Max(width, line);
                    line = 0f;
                    lines++;
                }

                line += advance;
            }

            return new Size(Math.Max(width, line), Height(height, lines));
        }

        // Advance estimates are deterministic fixture geometry, independent of native font rasterization.
        private static float EstimatedAdvance(char character, UiTextStyle style)
        {
            if (style == UiTextStyle.Mono || (style == UiTextStyle.Numeral && char.IsDigit(character))) return 0.6f;
            if (character >= '\u2E80') return 1f;
            if (character == 'i' || character == 'l' || character == 'I' || char.IsWhiteSpace(character)) return 0.32f;
            return char.IsUpper(character) ? 0.65f : 0.55f;
        }

        private float TextSize(UiTextStyle style) => theme.Descriptor.GetText(style).ReferenceSize * readableScale;

        private static float Height(float height, int lines) => height + ((lines - 1) * height * 1.2f);

        private readonly struct Size
        {
            internal Size(float width, float height) { Width = width; Height = height; }
            internal float Width { get; }
            internal float Height { get; }
        }
    }
}
