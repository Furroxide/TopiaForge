using System;

namespace TopiaForge.Mods
{
    [Flags]
    internal enum UiButtonCue
    {
        None = 0,
        Selection = 1,
        Press = 2
    }

    internal static class UiControlPaint
    {
        internal const float ButtonSelectionWidth = 3f;
        internal const float ButtonSelectionLeft = 8f;
        internal const float ButtonSelectionTop = 12f;
        internal const float ButtonPressInset = 3f;
        internal const float CompactCueGutter = 12f;
        internal const float CompactCueLeading = 4.5f;
        internal const float CompactCueSideInset = 1f;
        internal const float CompactCueRadius = 3f;

        internal static float ButtonPressStroke(bool highContrast) => highContrast ? 3f : 2f;

        internal static UiButtonCue ButtonCues(UiThemeColor rest, UiThemeStateTints tints)
        {
            var hovered = rest.Tinted(tints.Hovered);
            var selected = rest.Tinted(tints.Selected);
            var pressed = rest.Tinted(tints.Pressed);
            var cues = selected.Equals(rest) ? UiButtonCue.Selection : UiButtonCue.None;
            if (pressed.Equals(hovered) || pressed.Equals(rest)) cues |= UiButtonCue.Press;
            return cues;
        }

        internal static UiPaintRole ListRowBackground(bool enabled, bool selected) =>
            enabled && selected ? UiPaintRole.Selected : UiPaintRole.Sunken;

        internal static UiPaintRole GraphNodeBackground(bool enabled, bool selected) =>
            !enabled ? UiPaintRole.Sunken : selected ? UiPaintRole.Selected : UiPaintRole.Raised;

        internal static UiThemePair SliderTrack(UiThemeScheme scheme, bool hud, bool enabled, double minimum)
        {
            var background = scheme.GetPair(UiPaintRole.Sunken).Fill;
            var accent = scheme.GetPair(UiPaintRole.Accent).Fill;
            var thumb = enabled ? UiThemeColors.RepairForeground(hud ? new UiThemeColor(255, 255, 255) : accent, background, minimum)
                : scheme.ResolveForeground(UiTone.Muted, UiPaintRole.Sunken);
            return new UiThemePair(background, thumb);
        }

        internal static UiThemeColor SliderFill(UiThemeScheme scheme, UiThemeColor track, double minimum) =>
            UiThemeColors.RepairForeground(scheme.GetPair(UiPaintRole.Accent).Fill, track, minimum);

        internal static UiThemeColor ToggleBoundary(UiThemeScheme scheme, UiPaintRole backdrop, bool value, bool enabled)
        {
            var ink = enabled && value ? scheme.GetPair(UiPaintRole.Primary).On : scheme.Muted;
            return UiThemeColors.RepairForeground(ink, scheme.GetPair(backdrop).Fill, 3d);
        }
    }
}
