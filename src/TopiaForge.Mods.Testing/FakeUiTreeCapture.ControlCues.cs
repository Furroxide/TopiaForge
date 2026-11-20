using System;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private void PaintStateCues(string path, string label, UiCaptureControlState state, UiCaptureRect bounds,
            UiCaptureRect clip, UiThemeColor stateFill, UiThemeStateTints tints, UiThemeColor ink, UiThemeColor backdrop,
            bool compact = false, float? radius = null)
        {
            var selected = state is UiCaptureControlState.Selected or UiCaptureControlState.SelectedPressed;
            var pressed = state is UiCaptureControlState.Pressed or UiCaptureControlState.SelectedPressed;
            if (!selected && !pressed) return;
            var cues = UiControlPaint.ButtonCues(stateFill, tints);
            var flags = UiCaptureNodeFlags.SemanticMark | UiCaptureNodeFlags.HasNonColorCue;
            if (selected) flags |= UiCaptureNodeFlags.Selected;
            var mark = new UiCaptureNodeContent(string.Empty, label, 0f, 0f, flags);
            if (selected && (cues & UiButtonCue.Selection) != 0)
            {
                var top = UiControlPaint.ButtonSelectionTop * layoutScale;
                var left = compact ? UiControlPaint.CompactCueLeading : UiControlPaint.ButtonSelectionLeft;
                var leading = new UiCaptureRect(bounds.X + left * layoutScale, bounds.Y + top,
                    UiControlPaint.ButtonSelectionWidth * layoutScale, bounds.Height - 2f * top);
                Add(path + "/selection", null, "ControlSelectionBar", leading, clip, mark, new UiCaptureNodePaint(ink, backdrop));
            }
            if (pressed && (cues & UiButtonCue.Press) != 0)
            {
                var inset = UiControlPaint.ButtonPressInset * layoutScale;
                var side = UiControlPaint.CompactCueSideInset * layoutScale;
                var outline = compact
                    ? new UiCaptureRect(bounds.X + side, bounds.Y + inset, UiControlPaint.CompactCueGutter * layoutScale - 2f * side,
                        bounds.Height - 2f * inset)
                    : bounds.Inset(inset, inset, inset, inset);
                var corner = compact ? UiControlPaint.CompactCueRadius * layoutScale
                    : Math.Max(0f, (radius ?? theme.Descriptor.Shape.Control) * layoutScale - inset);
                Add(path + "/press", null, "ControlPressOutline", outline, clip, mark,
                    new UiCaptureNodePaint(ink, backdrop, corner, UiControlPaint.ButtonPressStroke(preferences.HighContrast) * layoutScale));
            }
        }
    }
}
