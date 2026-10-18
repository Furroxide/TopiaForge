namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Resolves the semantic role set for each scheme from the brand palette, applying
    /// the per-host accent override and the global high-contrast transform. Pure and
    /// unit-tested; UiHost caches the result per theme version.
    /// </summary>
    public static class TopiaForgeSchemes
    {
        /// <summary>The contrast Paper text keeps on every Paper surface it sits on, the tint the darkest: WCAG AA.</summary>
        internal const float PaperTextRatio = 4.5f;

        /// <summary>
        /// The contrast muted text keeps on sunken paper, where a tool's content sits: WCAG AAA. Muted text is drawn in the
        /// regular face at 12 to 14 points, whose thin strokes render lighter than their color.
        /// </summary>
        internal const float PaperMutedTextRatio = 7f;

        /// <summary>The contrast every Paper text role keeps on every Paper surface in high contrast: WCAG AAA.</summary>
        internal const float PaperHighContrastTextRatio = 7f;

        /// <summary>The kit's pressed tint, which darkens a row of sunken paper while it is pressed.</summary>
        internal const float PressedTint = 0.94f;

        /// <summary>The darkest Paper surface text sits on: the tint, or a row of sunken paper while pressed, whichever is darker.</summary>
        internal static readonly TopiaForgeRgba DarkestTextSurface = Darker(TopiaForgePalette.SurfaceTint, TopiaForgePalette.Paper.Scale(PressedTint));

        /// <summary>
        /// Gets a color as Paper text: darkened, if it must be, until it reads at <see cref="PaperTextRatio"/> on the
        /// darkest Paper surface text sits on, or at <see cref="PaperHighContrastTextRatio"/> in high contrast. The status
        /// and brand colors are made for fills and rings; text in their tones takes this instead.
        /// </summary>
        internal static TopiaForgeRgba PaperText(TopiaForgeRgba color, bool highContrast)
        {
            return TopiaForgeContrast.DarkenForPaper(
                color,
                DarkestTextSurface,
                highContrast ? PaperHighContrastTextRatio : PaperTextRatio);
        }

        private static TopiaForgeRgba Darker(TopiaForgeRgba first, TopiaForgeRgba second) =>
            TopiaForgeContrast.Luminance(first) <= TopiaForgeContrast.Luminance(second) ? first : second;

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

                // The launcher's muted and faint text, darkened for the game's light face: muted text reads at AAA on
                // sunken paper, and faint text, placeholders and disabled labels included, at AA on every surface.
                TextMuted = TopiaForgeContrast.DarkenForPaper(TopiaForgePalette.MutedText, TopiaForgePalette.Paper, PaperMutedTextRatio),
                TextFaint = PaperText(TopiaForgePalette.FaintText, highContrast: false),
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
                    TextFaint = PaperText(TopiaForgePalette.FaintText, highContrast: true),
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
