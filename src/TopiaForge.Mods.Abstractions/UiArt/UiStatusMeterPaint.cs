namespace TopiaForge.Mods
{
    internal readonly struct UiStatusMeterPaint
    {
        private UiStatusMeterPaint(UiThemePair empty, UiThemePair filled)
        {
            Empty = empty;
            Filled = filled;
        }

        internal UiThemePair Empty { get; }
        internal UiThemePair Filled { get; }

        internal static UiStatusMeterPaint Resolve(UiThemeScheme scheme, UiTone tone, bool highContrast)
        {
            var track = scheme.GetPair(UiPaintRole.Sunken);
            var role = tone is UiTone.Neutral or UiTone.Muted ? UiPaintRole.Primary : UiThemeScheme.RoleForTone(tone);
            var paint = scheme.GetPair(role);
            var fill = UiThemeColors.RepairForeground(paint.Fill, track.Fill, highContrast ? 4.5d : 3d);
            var minimum = highContrast ? 7d : 4.5d;
            var fillInk = UiThemeColors.RepairForeground(paint.On, fill, minimum);
            if (UiThemeColors.Contrast(fillInk, fill) < minimum)
            {
                // Repairing the fill against the track can otherwise leave a midtone with no valid 7:1 ink.
                // Move the fill farther from the track until its opposite ink reads.
                var black = new UiThemeColor(0, 0, 0);
                var white = new UiThemeColor(255, 255, 255);
                var inkExtreme = UiThemeColors.Contrast(track.Fill, white) >= UiThemeColors.Contrast(track.Fill, black) ? black : white;
                fill = UiThemeColors.RepairForeground(fill, inkExtreme, minimum);
                fillInk = UiThemeColors.RepairForeground(paint.On, fill, minimum);
            }
            var trackInk = scheme.ResolveForeground(UiTone.Neutral, UiPaintRole.Sunken);
            return new UiStatusMeterPaint(new UiThemePair(track.Fill, trackInk), new UiThemePair(fill, fillInk));
        }
    }
}
