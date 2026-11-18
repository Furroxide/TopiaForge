namespace TopiaForge.Mods
{
    internal static class UiControlPaint
    {
        internal static UiPaintRole ListRowBackground(bool enabled, bool selected) =>
            enabled && selected ? UiPaintRole.Selected : UiPaintRole.Sunken;

        internal static UiPaintRole GraphNodeBackground(bool enabled, bool selected) =>
            !enabled ? UiPaintRole.Sunken : selected ? UiPaintRole.Selected : UiPaintRole.Raised;

        internal static UiThemePair SliderTrack(UiThemeScheme scheme, bool hud, bool enabled, double minimum)
        {
            var background = scheme.GetPair(UiPaintRole.Sunken).Fill;
            var accent = scheme.GetPair(UiPaintRole.Accent).Fill;
            var thumb = enabled ? hud ? new UiThemeColor(255, 255, 255) : accent : scheme.Muted;
            return new UiThemePair(background, UiThemeColors.RepairForeground(thumb, background, minimum));
        }

        internal static UiThemeColor SliderFill(UiThemeScheme scheme, UiThemeColor track, double minimum) =>
            UiThemeColors.RepairForeground(scheme.GetPair(UiPaintRole.Accent).Fill, track, minimum);
    }
}
