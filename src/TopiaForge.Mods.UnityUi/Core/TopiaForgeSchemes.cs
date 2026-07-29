namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Resolves the semantic role set for each scheme from the brand palette, applying
    /// the per-host accent override and the global high-contrast transform. Pure and
    /// unit-tested; UiHost caches the result per theme version.
    /// </summary>
    public static class TopiaForgeSchemes
    {
        /// <summary>Resolves the effective value for the current theme.</summary>
        public static TopiaForgeSchemeColors Resolve(TopiaForgeScheme scheme, TopiaForgeRgba? accentOverride, bool highContrast)
        {
            return scheme == TopiaForgeScheme.Paper
                ? ResolvePaper(accentOverride, highContrast)
                : ResolveHud(accentOverride, highContrast);
        }

        /// <summary>Resolves colors for the paper tool-window scheme.</summary>
        public static TopiaForgeSchemeColors ResolvePaper(TopiaForgeRgba? accentOverride, bool highContrast)
        {
            var accent = accentOverride.HasValue
                ? TopiaForgeContrast.DarkenForPaper(accentOverride.Value, TopiaForgePalette.Surface)
                : TopiaForgePalette.AccentDark;
            var accentPressed = accentOverride.HasValue ? accent.Scale(0.85f) : TopiaForgePalette.AccentDeep;

            var colors = new TopiaForgeSchemeColors
            {
                Backdrop = TopiaForgePalette.Ink.WithAlpha(0.55f),
                Surface = TopiaForgePalette.Surface,
                SurfaceAlt = TopiaForgePalette.SurfaceAlt,
                SurfaceSunken = TopiaForgePalette.Paper,
                Tint = TopiaForgePalette.SurfaceTint,
                SelectedTint = TopiaForgePalette.SelectedTint,
                Outline = TopiaForgePalette.Border,
                OutlineStrong = TopiaForgePalette.Launch,
                Primary = TopiaForgePalette.Launch,
                PrimaryPressed = TopiaForgePalette.LaunchDark,
                OnPrimary = TopiaForgePalette.White,
                Accent = accent,
                AccentPressed = accentPressed,
                Text = TopiaForgePalette.Ink,
                TextMuted = TopiaForgePalette.MutedText,
                TextFaint = TopiaForgePalette.FaintText,
                Success = TopiaForgePalette.Good,
                Warning = TopiaForgePalette.Warning,
                Danger = TopiaForgePalette.Danger,
                OnStatus = TopiaForgePalette.White,
                Shadow = new TopiaForgeRgba(0f, 0f, 0f, 0.20f),
                ShadowStrong = TopiaForgePalette.LaunchDark.WithAlpha(0.24f),
                FocusRing = accent,
            };

            if (highContrast)
            {
                colors = colors with
                {
                    Text = TopiaForgeRgba.Hex(0x14181F),
                    TextMuted = TopiaForgeRgba.Hex(0x3D4450),
                    Outline = TopiaForgePalette.LaunchDark,
                    Backdrop = TopiaForgePalette.Ink.WithAlpha(0.72f),
                };
            }

            return colors;
        }

        /// <summary>Resolves colors for the gameplay HUD scheme.</summary>
        public static TopiaForgeSchemeColors ResolveHud(TopiaForgeRgba? accentOverride, bool highContrast)
        {
            var accent = accentOverride ?? TopiaForgePalette.Accent;
            var accentPressed = accentOverride.HasValue ? accent.Scale(0.8f) : TopiaForgePalette.AccentDark;

            var colors = new TopiaForgeSchemeColors
            {
                Backdrop = TopiaForgePalette.HudBackdrop.WithAlpha(0.66f),
                Surface = TopiaForgePalette.LogPanel.WithAlpha(0.88f),
                SurfaceAlt = TopiaForgePalette.DarkPanel.WithAlpha(0.92f),
                SurfaceSunken = TopiaForgePalette.HudSunken.WithAlpha(0.85f),
                Tint = TopiaForgePalette.HudTint,
                SelectedTint = TopiaForgePalette.HudTint.WithAlpha(0.9f),
                Outline = TopiaForgePalette.Border.WithAlpha(0.35f),
                OutlineStrong = TopiaForgePalette.Launch,
                Primary = TopiaForgePalette.Launch,
                PrimaryPressed = TopiaForgePalette.LaunchDark,
                OnPrimary = TopiaForgePalette.White,
                Accent = accent,
                AccentPressed = accentPressed,
                Text = TopiaForgePalette.Paper,
                TextMuted = TopiaForgePalette.HudMuted,
                TextFaint = TopiaForgePalette.FaintText,
                Success = TopiaForgePalette.HudGood,
                Warning = TopiaForgePalette.HudWarning,
                Danger = TopiaForgePalette.HudDanger,
                OnStatus = TopiaForgePalette.White,
                Shadow = new TopiaForgeRgba(0f, 0f, 0f, 0.45f),
                ShadowStrong = new TopiaForgeRgba(0f, 0f, 0f, 0.60f),
                FocusRing = accent,
            };

            if (highContrast)
            {
                colors = colors with
                {
                    Surface = TopiaForgePalette.LogPanel.WithAlpha(0.96f),
                    SurfaceAlt = TopiaForgePalette.DarkPanel.WithAlpha(0.98f),
                    SurfaceSunken = TopiaForgePalette.HudSunken.WithAlpha(0.95f),
                    Accent = TopiaForgeContrast.Emphasize(accent),
                    Text = TopiaForgePalette.White,
                    TextMuted = TopiaForgeContrast.Emphasize(TopiaForgePalette.HudMuted),
                    Success = TopiaForgeContrast.Emphasize(TopiaForgePalette.HudGood),
                    Warning = TopiaForgeContrast.Emphasize(TopiaForgePalette.HudWarning),
                    Danger = TopiaForgeContrast.Emphasize(TopiaForgePalette.HudDanger),
                    Outline = TopiaForgePalette.Border.WithAlpha(0.6f),
                };
            }

            return colors;
        }
    }
}
