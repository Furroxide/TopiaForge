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
        public TopiaForgeScheme Scheme { get; }
        public int ThemeVersion { get; }
        public bool HighContrast { get; }

        public Color Backdrop { get; }
        public Color Surface { get; }
        public Color SurfaceAlt { get; }
        public Color SurfaceSunken { get; }
        public Color Tint { get; }
        public Color SelectedTint { get; }
        public Color Outline { get; }
        public Color OutlineStrong { get; }
        public Color Primary { get; }
        public Color PrimaryPressed { get; }
        public Color OnPrimary { get; }
        public Color Accent { get; }
        public Color AccentPressed { get; }
        public Color Text { get; }
        public Color TextMuted { get; }
        public Color TextFaint { get; }
        public Color Success { get; }
        public Color Warning { get; }
        public Color Danger { get; }
        public Color OnStatus { get; }
        public Color Shadow { get; }
        public Color ShadowStrong { get; }
        public Color FocusRing { get; }

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
        Neutral,
        Muted,
        Faint,
        Primary,
        Accent,
        Success,
        Warning,
        Danger,
    }
}
