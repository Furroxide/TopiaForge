namespace TopiaForge.Mods
{
    internal static class UiStatusColors
    {
        internal static UiThemePair Badge(UiThemeScheme scheme, UiBadge badge, UiPaintRole backdrop)
        {
            if (badge.Presentation == UiBadgeStyle.Filled) return scheme.GetPair(badge.Role);

            var tone = badge.Role switch
            {
                UiPaintRole.Primary => UiTone.Primary,
                UiPaintRole.Accent => UiTone.Accent,
                UiPaintRole.Success => UiTone.Success,
                UiPaintRole.Warning => UiTone.Warning,
                UiPaintRole.Danger => UiTone.Danger,
                _ => UiTone.Neutral,
            };
            return new UiThemePair(scheme.GetPair(backdrop).Fill, scheme.ResolveForeground(tone, backdrop));
        }

        internal static UiThemeColor Boundary(UiThemeScheme scheme, UiPaintRole role, UiPaintRole adjacent) =>
            UiThemeColors.RepairForeground(scheme.GetPair(role).Fill, scheme.GetPair(adjacent).Fill, 3d);

        internal static UiThemeColor PipFill(UiThemeScheme scheme, UiPaintRole role, bool highContrast) =>
            UiThemeColors.RepairForeground(scheme.GetPair(role).Fill, scheme.GetPair(UiPaintRole.Sunken).Fill,
                highContrast ? 4.5d : 3d);
    }
}
