using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Scheme colors resolved once per (scheme, accent, theme version) and converted to
    /// UnityEngine.Color so widgets read plain fields in ApplyTheme with no per-widget
    /// math. Owned and cached by UiHost.
    /// </summary>
    public sealed class TopiaForgeResolvedTheme
    {
        private readonly Color primaryText;
        private readonly Color accentText;
        private readonly Color successText;
        private readonly Color warningText;
        private readonly Color dangerText;

        /// <summary>Gets scheme.</summary>
        public TopiaForgeScheme Scheme { get; }
        /// <summary>Gets theme version.</summary>
        public int ThemeVersion { get; }
        /// <summary>Gets whether high-contrast colors were used to resolve this theme.</summary>
        public bool HighContrast { get; }

        /// <summary>Gets the backdrop color.</summary>
        public Color Backdrop { get; }
        /// <summary>Gets the surface color.</summary>
        public Color Surface { get; }
        /// <summary>Gets the surface alt color.</summary>
        public Color SurfaceAlt { get; }
        /// <summary>Gets the surface sunken color.</summary>
        public Color SurfaceSunken { get; }
        /// <summary>Gets the tint color.</summary>
        public Color Tint { get; }
        /// <summary>Gets the selected tint color.</summary>
        public Color SelectedTint { get; }
        /// <summary>Gets the outline color.</summary>
        public Color Outline { get; }
        /// <summary>Gets the outline strong color.</summary>
        public Color OutlineStrong { get; }
        /// <summary>Gets the primary color.</summary>
        public Color Primary { get; }
        /// <summary>Gets the primary pressed color.</summary>
        public Color PrimaryPressed { get; }
        /// <summary>Gets the on primary color.</summary>
        public Color OnPrimary { get; }
        /// <summary>Gets the accent color.</summary>
        public Color Accent { get; }
        /// <summary>Gets the accent pressed color.</summary>
        public Color AccentPressed { get; }
        /// <summary>Gets the text color.</summary>
        public Color Text { get; }
        /// <summary>Gets the text muted color.</summary>
        public Color TextMuted { get; }
        /// <summary>Gets the text faint color.</summary>
        public Color TextFaint { get; }
        /// <summary>Gets the success color.</summary>
        public Color Success { get; }
        /// <summary>Gets the warning color.</summary>
        public Color Warning { get; }
        /// <summary>Gets the danger color.</summary>
        public Color Danger { get; }
        /// <summary>Gets the on status color.</summary>
        public Color OnStatus { get; }
        /// <summary>Gets the shadow color.</summary>
        public Color Shadow { get; }
        /// <summary>Gets the shadow strong color.</summary>
        public Color ShadowStrong { get; }
        /// <summary>Gets the focus ring color.</summary>
        public Color FocusRing { get; }

        /// <summary>Creates a resolved theme.</summary>
        public TopiaForgeResolvedTheme(TopiaForgeScheme scheme, TopiaForgeRgba? accentOverride)
            : this(scheme, accentOverride, TopiaForgeTheme.HighContrast, TopiaForgeTheme.Version)
        {
        }

        internal TopiaForgeResolvedTheme(
            TopiaForgeScheme scheme,
            TopiaForgeRgba? accentOverride,
            bool highContrast,
            int themeVersion)
        {
            Scheme = scheme;
            ThemeVersion = themeVersion;
            HighContrast = highContrast;

            var colors = TopiaForgeSchemes.Resolve(scheme, accentOverride, HighContrast);
            Backdrop = ToColor(colors.Backdrop);
            Surface = ToColor(colors.Surface);
            SurfaceAlt = ToColor(colors.SurfaceAlt);
            SurfaceSunken = ToColor(colors.SurfaceSunken);
            Tint = ToColor(colors.Tint);
            SelectedTint = ToColor(colors.SelectedTint);
            Outline = ToColor(colors.Outline);
            OutlineStrong = ToColor(colors.OutlineStrong);
            Primary = ToColor(colors.Primary);
            PrimaryPressed = ToColor(colors.PrimaryPressed);
            OnPrimary = ToColor(colors.OnPrimary);
            Accent = ToColor(colors.Accent);
            AccentPressed = ToColor(colors.AccentPressed);
            Text = ToColor(colors.Text);
            TextMuted = ToColor(colors.TextMuted);
            TextFaint = ToColor(colors.TextFaint);
            Success = ToColor(colors.Success);
            Warning = ToColor(colors.Warning);
            Danger = ToColor(colors.Danger);
            OnStatus = ToColor(colors.OnStatus);
            Shadow = ToColor(colors.Shadow);
            ShadowStrong = ToColor(colors.ShadowStrong);
            FocusRing = ToColor(colors.FocusRing);

            // Status and brand colors are made for fills and rings. On Paper their text is darkened until it reads on every
            // surface; the HUD's are bright on dark already.
            var paper = scheme == TopiaForgeScheme.Paper;
            primaryText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Primary, HighContrast)) : Primary;
            accentText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Accent, HighContrast)) : Accent;
            successText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Success, HighContrast)) : Success;
            warningText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Warning, HighContrast)) : Warning;
            dangerText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Danger, HighContrast)) : Danger;
        }

        /// <summary>
        /// Gets a tone's color for body text: as <see cref="ToneColor"/>, except that on Paper the status and brand tones
        /// are darkened until they read at WCAG AA on every surface, or AAA in high contrast.
        /// </summary>
        internal Color TextToneColor(TopiaForgeTone tone)
        {
            return tone switch
            {
                TopiaForgeTone.Primary => primaryText,
                TopiaForgeTone.Accent => accentText,
                TopiaForgeTone.Success => successText,
                TopiaForgeTone.Warning => warningText,
                TopiaForgeTone.Danger => dangerText,
                _ => ToneColor(tone),
            };
        }

        /// <summary>Semantic tone lookup used by widgets with a Tone chainer.</summary>
        public Color ToneColor(TopiaForgeTone tone)
        {
            return tone switch
            {
                TopiaForgeTone.Neutral => Text,
                TopiaForgeTone.Muted => TextMuted,
                TopiaForgeTone.Faint => TextFaint,
                TopiaForgeTone.Primary => Primary,
                TopiaForgeTone.Accent => Accent,
                TopiaForgeTone.Success => Success,
                TopiaForgeTone.Warning => Warning,
                TopiaForgeTone.Danger => Danger,
                _ => Text,
            };
        }

        /// <summary>High-contrast emphasis for consumer-supplied custom colors.</summary>
        public Color Emphasize(Color color)
        {
            if (!HighContrast)
            {
                return color;
            }

            var emphasized = TopiaForgeContrast.Emphasize(new TopiaForgeRgba(color.r, color.g, color.b, color.a));
            return new Color(emphasized.R, emphasized.G, emphasized.B, emphasized.A);
        }

        private static Color ToColor(TopiaForgeRgba value)
        {
            return new Color(value.R, value.G, value.B, value.A);
        }
    }

    /// <summary>Semantic color tones widgets accept instead of raw colors.</summary>
    public enum TopiaForgeTone
    {
        /// <summary>Selects the neutral option.</summary>
        Neutral,
        /// <summary>Selects the muted option.</summary>
        Muted,
        /// <summary>Selects the faint option.</summary>
        Faint,
        /// <summary>Selects the primary option.</summary>
        Primary,
        /// <summary>Selects the accent option.</summary>
        Accent,
        /// <summary>Selects the success option.</summary>
        Success,
        /// <summary>Selects the warning option.</summary>
        Warning,
        /// <summary>Selects the danger option.</summary>
        Danger,
    }
}
