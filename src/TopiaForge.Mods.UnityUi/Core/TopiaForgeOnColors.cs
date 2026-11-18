using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>A fill the kit draws a label, an icon, a thumb or a check on.</summary>
    internal enum TopiaForgeFillRole
    {
        /// <summary>A filled button: its label or icon on the brand orange.</summary>
        FilledButton,

        /// <summary>A danger button: its label or icon on the danger fill.</summary>
        DangerButton,

        /// <summary>A switch that is on, or a ticked checkbox: the thumb or the check on the brand orange.</summary>
        ControlOn,

        /// <summary>A switch that is off: its faint thumb on the tint.</summary>
        SwitchOff,

        /// <summary>A stepper's done step: the check on the success fill.</summary>
        StepDone,

        /// <summary>A stepper's failed step: the cross on the danger fill.</summary>
        StepFailed,
    }

    /// <summary>
    /// A fill, the color drawn on it, and the tints uGUI gives the fill while it is hovered, selected or pressed, resolved by
    /// <see cref="TopiaForgeOnColors.Resolve"/>.
    /// </summary>
    internal readonly struct TopiaForgeFillColors
    {
        internal TopiaForgeFillColors(TopiaForgeRgba fill, TopiaForgeRgba content, float hovered, float selected, float pressed)
        {
            Fill = fill;
            Content = content;
            Hovered = hovered;
            Selected = selected;
            Pressed = pressed;
        }

        /// <summary>Gets the fill: the brand's own color, unless nothing reads on it.</summary>
        internal TopiaForgeRgba Fill { get; }

        /// <summary>Gets the color of the label, icon, thumb or check drawn on the fill.</summary>
        internal TopiaForgeRgba Content { get; }

        /// <summary>Gets the tint uGUI multiplies the fill by while the pointer is over it.</summary>
        internal float Hovered { get; }

        /// <summary>Gets the tint uGUI multiplies the fill by while the control is selected.</summary>
        internal float Selected { get; }

        /// <summary>Gets the tint uGUI multiplies the fill by while the control is pressed.</summary>
        internal float Pressed { get; }
    }

    /// <summary>
    /// Resolves what the kit draws on a fill against that fill: a button's label or icon, a switch's thumb, a checkbox's
    /// check and a stepper's marks. A label keeps WCAG AA (4.5:1), and a mark the 3:1 a control's parts need; high contrast
    /// raises them to 7:1 and 4.5:1. The brand's white stays wherever it reads. On the brand orange, where white read at
    /// 2.6:1, and on the HUD's bright status fills, the content is dark.
    /// <para>
    /// One rule keeps every interaction state readable: the content is resolved against the fill at rest, and each state
    /// keeps the kit's usual tint only while the content still reads on the tinted fill. Where it would not, the state takes
    /// the mirror tint instead, which moves the fill away from the content, so the pair reads at least as well as at rest.
    /// The brand's fills therefore keep their colors in every state, and every state still looks different from rest. Pure
    /// and unit-tested; the resolved theme caches the result per theme version.
    /// </para>
    /// </summary>
    internal static class TopiaForgeOnColors
    {
        /// <summary>The tint uGUI multiplies a kit button's fill by while the pointer is over it, unless that stops its label reading.</summary>
        internal const float ButtonHoveredTint = 1.06f;

        /// <summary>The tint uGUI multiplies a kit button's fill by while the button is selected, unless that stops its label reading.</summary>
        internal const float ButtonSelectedTint = 1.04f;

        /// <summary>The tint uGUI multiplies a kit button's fill by while it is pressed, unless that stops its label reading.</summary>
        internal const float ButtonPressedTint = TopiaForgeSchemes.PressedTint;

        /// <summary>The hovered and selected tint in uGUI's default color block, which kit switches and checkboxes start from.</summary>
        internal const float ControlHighlightedTint = 245f / 255f;

        /// <summary>The pressed tint in uGUI's default color block, which kit switches and checkboxes start from.</summary>
        internal const float ControlPressedTint = 200f / 255f;

        /// <summary>The dark content the kit draws where white does not read: the HUD's backdrop, as on a HUD badge.</summary>
        internal static readonly TopiaForgeRgba DarkContent = TopiaForgePalette.HudBackdrop;

        private const int Steps = 48;
        private const float DarkenStep = 0.92f;
        private const float LightenStep = 0.08f;

        /// <summary>The contrast a label keeps on its fill: WCAG AA, or AAA in high contrast.</summary>
        internal static float LabelRatio(bool highContrast) => highContrast ? 7f : 4.5f;

        /// <summary>The contrast a thumb, a check or a stepper's mark keeps on its fill: the 3:1 of WCAG AA, or 4.5:1 in high contrast.</summary>
        internal static float MarkRatio(bool highContrast) => highContrast ? 4.5f : 3f;

        /// <summary>Gets every fill role's colors in a scheme, indexed by role.</summary>
        internal static TopiaForgeFillColors[] ResolveAll(TopiaForgeScheme scheme, TopiaForgeSchemeColors colors, bool highContrast)
        {
            var roles = (TopiaForgeFillRole[])Enum.GetValues(typeof(TopiaForgeFillRole));
            var all = new TopiaForgeFillColors[roles.Length];
            foreach (var role in roles)
            {
                all[(int)role] = Resolve(role, scheme, colors, highContrast);
            }

            return all;
        }

        /// <summary>Gets a fill role's fill, the color drawn on it and its state tints, in a scheme.</summary>
        internal static TopiaForgeFillColors Resolve(TopiaForgeFillRole role, TopiaForgeScheme scheme, TopiaForgeSchemeColors colors, bool highContrast)
        {
            var white = TopiaForgePalette.White;
            var onOrange = scheme == TopiaForgeScheme.Paper ? TopiaForgePalette.Ink : DarkContent;
            return role switch
            {
                TopiaForgeFillRole.FilledButton => Button(colors.Primary, onOrange, LabelRatio(highContrast)),
                TopiaForgeFillRole.DangerButton => Button(colors.Danger, white, LabelRatio(highContrast)),
                TopiaForgeFillRole.ControlOn => Control(colors.Primary, onOrange, MarkRatio(highContrast)),
                TopiaForgeFillRole.SwitchOff => Control(colors.Tint, colors.TextFaint, MarkRatio(highContrast)),
                TopiaForgeFillRole.StepDone => Still(colors.Success, white, MarkRatio(highContrast)),
                TopiaForgeFillRole.StepFailed => Still(colors.Danger, white, MarkRatio(highContrast)),
                _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown fill role."),
            };
        }

        /// <summary>
        /// Gets a slider handle's color. On Paper a white handle vanished, at 1.02:1 on the surface and 1.13:1 on its
        /// track, so the handle takes the accent, darkened if it must be until it reads at the mark ratio on the darkest
        /// surface text sits on; pressing it only darkens it. The handle is opaque whatever alpha a mod gives its accent,
        /// since the ratio is measured on opaque colors and a transparent handle would not show at all. On the HUD it
        /// stays white, which reads on the HUD's dark track and surfaces, pressed too.
        /// </summary>
        internal static TopiaForgeRgba SliderHandle(TopiaForgeScheme scheme, TopiaForgeSchemeColors colors, bool highContrast)
        {
            return scheme == TopiaForgeScheme.Paper
                ? MoveUntilReads(colors.Accent.WithAlpha(1f), lighter: false, TopiaForgeSchemes.DarkestTextSurface, MarkRatio(highContrast))
                : TopiaForgePalette.White;
        }

        /// <summary>
        /// Gets the content for a fill at rest, and the fill. The preferred content is moved away from the fill until it
        /// reads: lighter on a darker fill, darker on a lighter one. Content that cannot move far enough, such as white on the
        /// brand orange, gives way to the other side: the kit's dark content, darkened if it must be, or white. A mid-tone
        /// fill that neither reads on moves away from whichever comes closer until it reads. Interaction states are made
        /// readable by <see cref="ReadableTint"/>, not by changing the pair.
        /// </summary>
        internal static TopiaForgeFillColors OnFill(TopiaForgeRgba fill, TopiaForgeRgba preferred, float minimumRatio)
        {
            var lighter = TopiaForgeContrast.Luminance(preferred) > TopiaForgeContrast.Luminance(fill);
            var content = MoveUntilReads(preferred, lighter, fill, minimumRatio);
            if (RatioAsDrawn(content, fill, 1f) >= minimumRatio)
            {
                return AtRest(fill, content);
            }

            var other = MoveUntilReads(lighter ? DarkContent : TopiaForgePalette.White, !lighter, fill, minimumRatio);
            if (RatioAsDrawn(other, fill, 1f) >= minimumRatio)
            {
                return AtRest(fill, other);
            }

            var keepContent = RatioAsDrawn(content, fill, 1f) >= RatioAsDrawn(other, fill, 1f);
            var label = keepContent ? content : other;
            var labelIsLighter = keepContent == lighter;
            for (var step = 0; step < Steps && RatioAsDrawn(label, fill, 1f) < minimumRatio; step++)
            {
                fill = labelIsLighter ? fill.Scale(DarkenStep) : Lighten(fill);
            }

            return AtRest(fill, label);
        }

        /// <summary>
        /// Gets the tint a state gives a fill: the kit's usual <paramref name="tint"/> while the content still reads on the
        /// tinted fill, or else its mirror (<c>2 - tint</c>), which moves the fill away from the content. Contrast only
        /// falls as a fill nears its content's luminance, so the mirror reads at least as well as the fill at rest. A gamma
        /// project multiplies the stored values by the tint and a linear one multiplies linear light, which lands between the
        /// fill and the stored-value result, so checking the stored values covers both. If even the mirror fell short, which
        /// only a pair that does not read at rest could do, the state keeps the fill as it is at rest.
        /// </summary>
        internal static float ReadableTint(TopiaForgeRgba fill, TopiaForgeRgba content, float minimumRatio, float tint)
        {
            if (RatioAsDrawn(content, fill, tint) >= minimumRatio)
            {
                return tint;
            }

            var mirror = Mirror(tint);
            return RatioAsDrawn(content, fill, mirror) >= minimumRatio ? mirror : 1f;
        }

        /// <summary>
        /// Gets <c>2 - tint</c>, rounded in double precision to four decimals so the mirror of a kit tint is exactly the
        /// other kit tint (the mirror of 1.06 is 0.94) on every runtime, whatever precision it keeps float locals in.
        /// </summary>
        internal static float Mirror(float tint) => (float)Math.Round(2.0 - tint, 4, MidpointRounding.ToEven);

        /// <summary>Gets the contrast between content and its fill tinted by a state, as computed and as drawn in 256 steps a channel.</summary>
        internal static float RatioAsDrawn(TopiaForgeRgba content, TopiaForgeRgba fill, float tint)
        {
            var tinted = Clamp(fill.Scale(tint));
            var drawnContent = TopiaForgeContrast.Quantize(content);
            var drawnFill = TopiaForgeContrast.Quantize(Clamp(TopiaForgeContrast.Quantize(fill).Scale(tint)));
            return Math.Min(TopiaForgeContrast.Ratio(content, tinted), TopiaForgeContrast.Ratio(drawnContent, drawnFill));
        }

        private static TopiaForgeFillColors Button(TopiaForgeRgba fill, TopiaForgeRgba preferred, float minimumRatio)
        {
            var pair = OnFill(fill, preferred, minimumRatio);
            return WithTints(pair, minimumRatio, ButtonHoveredTint, ButtonSelectedTint, ButtonPressedTint);
        }

        private static TopiaForgeFillColors Control(TopiaForgeRgba fill, TopiaForgeRgba preferred, float minimumRatio)
        {
            var pair = OnFill(fill, preferred, minimumRatio);
            return WithTints(pair, minimumRatio, ControlHighlightedTint, ControlHighlightedTint, ControlPressedTint);
        }

        private static TopiaForgeFillColors Still(TopiaForgeRgba fill, TopiaForgeRgba preferred, float minimumRatio) =>
            OnFill(fill, preferred, minimumRatio);

        private static TopiaForgeFillColors WithTints(TopiaForgeFillColors pair, float minimumRatio, float hovered, float selected, float pressed)
        {
            var hover = ReadableTint(pair.Fill, pair.Content, minimumRatio, hovered);
            var press = ReadableTint(pair.Fill, pair.Content, minimumRatio, pressed);

            // Selection is hover's quieter sibling, so it moves the same way: mirrored with hover, if that still reads.
            var select = ReadableTint(pair.Fill, pair.Content, minimumRatio, hover == hovered ? selected : Mirror(selected));
            return new TopiaForgeFillColors(pair.Fill, pair.Content, hover, select, Beyond(pair, minimumRatio, hover, press));
        }

        // A pressed fill must look different from a hovered one even when reduced motion turns off the press's sink into its
        // shadow. When both tints land on the same side of rest and press goes no further than hover, it goes twice as far
        // as hover, if that reads. Distances are compared with a tolerance, so a one-ulp difference never counts as further.
        private static float Beyond(TopiaForgeFillColors pair, float minimumRatio, float hover, float press)
        {
            const float Tolerance = 1e-4f;
            var sameSide = (hover - 1f) * (press - 1f) > 0f;
            if (!sameSide || Math.Abs(press - 1f) > Math.Abs(hover - 1f) + Tolerance)
            {
                return press;
            }

            var further = (float)Math.Round(1.0 + (2.0 * (hover - 1.0)), 4, MidpointRounding.ToEven);
            return RatioAsDrawn(pair.Content, pair.Fill, further) >= minimumRatio ? further : press;
        }

        private static TopiaForgeFillColors AtRest(TopiaForgeRgba fill, TopiaForgeRgba content) =>
            new TopiaForgeFillColors(fill, content, 1f, 1f, 1f);

        // Moves content toward white or black until it reads at rest. The steps only approach the end, so content that still
        // falls short takes white or black itself.
        private static TopiaForgeRgba MoveUntilReads(TopiaForgeRgba content, bool lighter, TopiaForgeRgba fill, float minimumRatio)
        {
            for (var step = 0; step < Steps && RatioAsDrawn(content, fill, 1f) < minimumRatio; step++)
            {
                content = lighter ? Lighten(content) : content.Scale(DarkenStep);
            }

            if (RatioAsDrawn(content, fill, 1f) >= minimumRatio)
            {
                return content;
            }

            return lighter ? TopiaForgePalette.White.WithAlpha(content.A) : new TopiaForgeRgba(0f, 0f, 0f, content.A);
        }

        // uGUI stores vertex colors in 8 bits a channel, so a tint above one saturates at white.
        private static TopiaForgeRgba Clamp(TopiaForgeRgba color) =>
            new TopiaForgeRgba(Math.Min(1f, color.R), Math.Min(1f, color.G), Math.Min(1f, color.B), color.A);

        private static TopiaForgeRgba Lighten(TopiaForgeRgba color)
        {
            return TopiaForgeRgba.Lerp(color, TopiaForgePalette.White.WithAlpha(color.A), LightenStep);
        }
    }
}
