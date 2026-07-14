using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Scheme colors resolved once per (scheme, accent, theme version) and converted to
    /// UnityEngine.Color so widgets read plain fields in ApplyTheme with no per-widget
    /// math. Owned and cached by UiHost.
    /// </summary>
    public sealed class QwResolvedTheme
    {
        public QwScheme Scheme { get; }
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

        public QwResolvedTheme(QwScheme scheme, QwRgba? accentOverride)
            : this(scheme, accentOverride, QwTheme.HighContrast, QwTheme.Version)
        {
        }

        internal QwResolvedTheme(
            QwScheme scheme,
            QwRgba? accentOverride,
            bool highContrast,
            int themeVersion)
        {
            Scheme = scheme;
            ThemeVersion = themeVersion;
            HighContrast = highContrast;

            var colors = QwSchemes.Resolve(scheme, accentOverride, HighContrast);
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
        public Color ToneColor(QwTone tone)
        {
            return tone switch
            {
                QwTone.Neutral => Text,
                QwTone.Muted => TextMuted,
                QwTone.Faint => TextFaint,
                QwTone.Primary => Primary,
                QwTone.Accent => Accent,
                QwTone.Success => Success,
                QwTone.Warning => Warning,
                QwTone.Danger => Danger,
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

            var emphasized = QwContrast.Emphasize(new QwRgba(color.r, color.g, color.b, color.a));
            return new Color(emphasized.R, emphasized.G, emphasized.B, emphasized.A);
        }

        private static Color ToColor(QwRgba value)
        {
            return new Color(value.R, value.G, value.B, value.A);
        }
    }

    /// <summary>Semantic color tones widgets accept instead of raw colors.</summary>
    public enum QwTone
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
