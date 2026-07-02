namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Resolves the semantic role set for each scheme from the brand palette, applying
    /// the per-host accent override and the global high-contrast transform. Pure and
    /// unit-tested; UiHost caches the result per theme version.
    /// </summary>
    public static class QwSchemes
    {
        public static QwSchemeColors Resolve(QwScheme scheme, QwRgba? accentOverride, bool highContrast)
        {
            return scheme == QwScheme.Paper
                ? ResolvePaper(accentOverride, highContrast)
                : ResolveHud(accentOverride, highContrast);
        }

        public static QwSchemeColors ResolvePaper(QwRgba? accentOverride, bool highContrast)
        {
            var accent = accentOverride.HasValue
                ? QwContrast.DarkenForPaper(accentOverride.Value, QwPalette.Surface)
                : QwPalette.AccentDark;
            var accentPressed = accentOverride.HasValue ? accent.Scale(0.85f) : QwPalette.AccentDeep;

            var colors = new QwSchemeColors
            {
                Backdrop = QwPalette.Ink.WithAlpha(0.55f),
                Surface = QwPalette.Surface,
                SurfaceAlt = QwPalette.SurfaceAlt,
                SurfaceSunken = QwPalette.Paper,
                Tint = QwPalette.SurfaceTint,
                SelectedTint = QwPalette.SelectedTint,
                Outline = QwPalette.Border,
                OutlineStrong = QwPalette.Launch,
                Primary = QwPalette.Launch,
                PrimaryPressed = QwPalette.LaunchDark,
                OnPrimary = QwPalette.White,
                Accent = accent,
                AccentPressed = accentPressed,
                Text = QwPalette.Ink,
                TextMuted = QwPalette.MutedText,
                TextFaint = QwPalette.FaintText,
                Success = QwPalette.Good,
                Warning = QwPalette.Warning,
                Danger = QwPalette.Danger,
                OnStatus = QwPalette.White,
                Shadow = new QwRgba(0f, 0f, 0f, 0.20f),
                ShadowStrong = QwPalette.LaunchDark.WithAlpha(0.24f),
                FocusRing = accent,
            };

            if (highContrast)
            {
                colors = colors with
                {
                    Text = QwRgba.Hex(0x14181F),
                    TextMuted = QwRgba.Hex(0x3D4450),
                    Outline = QwPalette.LaunchDark,
                    Backdrop = QwPalette.Ink.WithAlpha(0.72f),
                };
            }

            return colors;
        }

        public static QwSchemeColors ResolveHud(QwRgba? accentOverride, bool highContrast)
        {
            var accent = accentOverride ?? QwPalette.Accent;
            var accentPressed = accentOverride.HasValue ? accent.Scale(0.8f) : QwPalette.AccentDark;

            var colors = new QwSchemeColors
            {
                Backdrop = QwPalette.HudBackdrop.WithAlpha(0.66f),
                Surface = QwPalette.LogPanel.WithAlpha(0.88f),
                SurfaceAlt = QwPalette.DarkPanel.WithAlpha(0.92f),
                SurfaceSunken = QwPalette.HudSunken.WithAlpha(0.85f),
                Tint = QwPalette.HudTint,
                SelectedTint = QwPalette.HudTint.WithAlpha(0.9f),
                Outline = QwPalette.Border.WithAlpha(0.35f),
                OutlineStrong = QwPalette.Launch,
                Primary = QwPalette.Launch,
                PrimaryPressed = QwPalette.LaunchDark,
                OnPrimary = QwPalette.White,
                Accent = accent,
                AccentPressed = accentPressed,
                Text = QwPalette.Paper,
                TextMuted = QwPalette.HudMuted,
                TextFaint = QwPalette.FaintText,
                Success = QwPalette.HudGood,
                Warning = QwPalette.HudWarning,
                Danger = QwPalette.HudDanger,
                OnStatus = QwPalette.White,
                Shadow = new QwRgba(0f, 0f, 0f, 0.45f),
                ShadowStrong = new QwRgba(0f, 0f, 0f, 0.60f),
                FocusRing = accent,
            };

            if (highContrast)
            {
                colors = colors with
                {
                    Surface = QwPalette.LogPanel.WithAlpha(0.96f),
                    SurfaceAlt = QwPalette.DarkPanel.WithAlpha(0.98f),
                    SurfaceSunken = QwPalette.HudSunken.WithAlpha(0.95f),
                    Accent = QwContrast.Emphasize(accent),
                    Text = QwPalette.White,
                    TextMuted = QwContrast.Emphasize(QwPalette.HudMuted),
                    Success = QwContrast.Emphasize(QwPalette.HudGood),
                    Warning = QwContrast.Emphasize(QwPalette.HudWarning),
                    Danger = QwContrast.Emphasize(QwPalette.HudDanger),
                    Outline = QwPalette.Border.WithAlpha(0.6f),
                };
            }

            return colors;
        }
    }
}
