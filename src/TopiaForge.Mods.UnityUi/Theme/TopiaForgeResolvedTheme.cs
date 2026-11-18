using System;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Scheme colors resolved once per (scheme, accent, theme version) and converted to
    /// UnityEngine.Color so widgets read plain fields in ApplyTheme with no per-widget
    /// math. Owned and cached by UiHost.
    /// </summary>
    public sealed partial class TopiaForgeResolvedTheme
    {
        private readonly TopiaForgeFillPaint[] fills;
        private readonly Color primaryText;
        private readonly Color accentText;
        private readonly Color successText;
        private readonly Color warningText;
        private readonly Color dangerText;
        private readonly (Color Fill, Color FlatFill, Color Ring, Color Label)[] chips;

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
        /// <summary>Gets the color of a label on <see cref="Primary"/>, readable in every state a filled button is drawn in.</summary>
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
        /// <summary>Gets the color of a label on <see cref="Danger"/>, readable in every state a danger button is drawn in.</summary>
        public Color OnDanger { get; }
        /// <summary>Gets the shadow color.</summary>
        public Color Shadow { get; }
        /// <summary>Gets the shadow strong color.</summary>
        public Color ShadowStrong { get; }
        /// <summary>Gets the focus ring color.</summary>
        public Color FocusRing { get; }

        /// <summary>
        /// Gets a slider handle's color: on Paper the accent, opaque and darkened if it must be, so the handle shows on its
        /// light track and surface; on the HUD white, which shows on the HUD's dark ones.
        /// </summary>
        internal Color SliderHandle { get; }

        /// <summary>Creates a resolved theme.</summary>
        public TopiaForgeResolvedTheme(TopiaForgeScheme scheme, TopiaForgeRgba? accentOverride)
            : this(scheme, accentOverride, TopiaForgeTheme.HighContrast, TopiaForgeTheme.Version)
        {
        }

        internal TopiaForgeResolvedTheme(
            TopiaForgeScheme scheme,
            TopiaForgeRgba? accentOverride,
            bool highContrast,
            int themeVersion,
            UiThemeDescriptor? descriptor = null)
        {
            Scheme = scheme;
            ThemeVersion = themeVersion;
            HighContrast = highContrast;

            descriptor ??= accentOverride.HasValue ? null : UiThemePackParser.Default;
            Descriptor = descriptor;
            var authoredScheme = descriptor?.SelectScheme(scheme == TopiaForgeScheme.Hud, HighContrast);
            packScheme = authoredScheme;
            var colors = TopiaForgeSchemes.Resolve(scheme, accentOverride, HighContrast);
            if (authoredScheme != null) colors = ApplyPack(colors, authoredScheme, HighContrast);
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
            OnDanger = ToColor(colors.OnDanger);
            Shadow = ToColor(colors.Shadow);
            ShadowStrong = ToColor(colors.ShadowStrong);
            FocusRing = ToColor(colors.FocusRing);

            // What the kit draws on a fill is resolved against that fill, and each state's tint keeps it readable.
            fills = authoredScheme == null
                ? NativeFills(scheme, colors, HighContrast)
                : PackFills(authoredScheme, HighContrast);

            SliderHandle = ToColor(TopiaForgeOnColors.SliderHandle(scheme, colors, HighContrast));

            // Status and brand colors are made for fills and rings. On Paper their text is darkened until it reads on every
            // surface; the HUD's are bright on dark already.
            var paper = scheme == TopiaForgeScheme.Paper;
            primaryText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Primary, HighContrast)) : Primary;
            accentText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Accent, HighContrast)) : Accent;
            successText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Success, HighContrast)) : Success;
            warningText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Warning, HighContrast)) : Warning;
            dangerText = paper ? ToColor(TopiaForgeSchemes.PaperText(colors.Danger, HighContrast)) : Danger;
            if (authoredScheme != null)
            {
                primaryText = ToColor(PackInk(authoredScheme, UiTone.Primary));
                accentText = ToColor(PackInk(authoredScheme, UiTone.Accent));
                successText = ToColor(PackInk(authoredScheme, UiTone.Success));
                warningText = ToColor(PackInk(authoredScheme, UiTone.Warning));
                dangerText = ToColor(PackInk(authoredScheme, UiTone.Danger));
            }

            // A chip's label is drawn on the chip, so it is resolved against the chip's own fill.
            var tones = (TopiaForgeTone[])Enum.GetValues(typeof(TopiaForgeTone));
            chips = new (Color Fill, Color FlatFill, Color Ring, Color Label)[tones.Length];
            foreach (var tone in tones)
            {
                var chip = tone == TopiaForgeTone.Neutral
                    ? TopiaForgeSchemes.NeutralChip(scheme, colors, HighContrast)
                    : TopiaForgeSchemes.Chip(scheme, colors, ToRgba(ToneColor(tone)), HighContrast);
                chips[(int)tone] = (
                    ToColor(chip.Fill),
                    ToColor(chip.Flattened(colors.SurfaceSunken).Fill),
                    ToColor(chip.Ring),
                    ToColor(chip.Label));
            }
        }

        /// <summary>
        /// Gets a status chip's colors in a tone, its label reading on the chip at WCAG AA, or AAA in high contrast. On
        /// Paper the chip is a wash of its tone with a ring in it; on the HUD it is solid, so the world behind it cannot
        /// change it. Over a picture, as on a card's preview, a Paper chip's wash is flattened onto sunken paper instead,
        /// so the picture cannot show through.
        /// </summary>
        internal void ChipColors(TopiaForgeTone tone, bool overPicture, out Color fill, out Color ring, out Color label)
        {
            var index = (int)tone;
            var chip = chips[index >= 0 && index < chips.Length ? index : (int)TopiaForgeTone.Neutral];
            fill = overPicture ? chip.FlatFill : chip.Fill;
            ring = chip.Ring;
            label = chip.Label;
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

        /// <summary>
        /// Gets a fill, the color of the label, icon, thumb or check the kit draws on it, and the tints its control's color
        /// block uses while hovered, selected or pressed. The content reads on the fill in every one of those states: a label
        /// at WCAG AA and a mark at 3:1, or 7:1 and 4.5:1 in high contrast. The fill is the brand's own unless nothing reads
        /// on it.
        /// </summary>
        internal TopiaForgeFillPaint Fill(TopiaForgeFillRole role) => fills[(int)role];

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

        private static TopiaForgeRgba ToRgba(Color value)
        {
            return new TopiaForgeRgba(value.r, value.g, value.b, value.a);
        }

        private static Color StateTint(float value)
        {
            return new Color(value, value, value, 1f);
        }

        private static TopiaForgeFillPaint[] NativeFills(TopiaForgeScheme scheme, TopiaForgeSchemeColors colors, bool highContrast)
        {
            var resolved = TopiaForgeOnColors.ResolveAll(scheme, colors, highContrast);
            var result = new TopiaForgeFillPaint[resolved.Length];
            for (var index = 0; index < resolved.Length; index++)
            {
                var state = resolved[index];
                result[index] = new TopiaForgeFillPaint(ToColor(state.Fill), ToColor(state.Content),
                    StateTint(state.Hovered), StateTint(state.Selected), StateTint(state.Pressed));
            }

            return result;
        }
    }

    /// <summary>
    /// A fill, the color drawn on it, and the color block tints that keep that color readable while the control is
    /// hovered, selected or pressed.
    /// </summary>
    internal readonly struct TopiaForgeFillPaint
    {
        internal TopiaForgeFillPaint(Color fill, Color content, Color hovered, Color selected, Color pressed)
        {
            Fill = fill;
            Content = content;
            Hovered = hovered;
            Selected = selected;
            Pressed = pressed;
        }

        /// <summary>Gets the fill.</summary>
        internal Color Fill { get; }

        /// <summary>Gets the color of the label, icon, thumb or check drawn on the fill.</summary>
        internal Color Content { get; }

        /// <summary>Gets the color block's hovered tint.</summary>
        internal Color Hovered { get; }

        /// <summary>Gets the color block's selected tint.</summary>
        internal Color Selected { get; }

        /// <summary>Gets the color block's pressed tint.</summary>
        internal Color Pressed { get; }

        /// <summary>Gets a color block with these state tints and the rest of <paramref name="block"/> unchanged.</summary>
        internal ColorBlock Tinted(ColorBlock block)
        {
            block.highlightedColor = Hovered;
            block.selectedColor = Selected;
            block.pressedColor = Pressed;
            return block;
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
