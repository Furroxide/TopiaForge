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

    /// <summary>A fill and the color drawn on it, resolved by <see cref="TopiaForgeOnColors.Resolve"/>.</summary>
    internal readonly struct TopiaForgeFillColors
    {
        internal TopiaForgeFillColors(TopiaForgeRgba fill, TopiaForgeRgba content)
        {
            Fill = fill;
            Content = content;
        }

        /// <summary>Gets the fill: the brand's own color, unless nothing reads on it.</summary>
        internal TopiaForgeRgba Fill { get; }

        /// <summary>Gets the color of the label, icon, thumb or check drawn on the fill.</summary>
        internal TopiaForgeRgba Content { get; }
    }

    /// <summary>
    /// Resolves what the kit draws on a fill against that fill: a button's label or icon, a switch's thumb, a checkbox's
    /// check and a stepper's marks. A label keeps WCAG AA (4.5:1), and a mark the 3:1 a control's parts need, in every
    /// state uGUI tints the fill to; high contrast raises them to 7:1 and 4.5:1. The brand's white stays wherever it reads.
    /// On the brand orange, where white read at 2.6:1, and on the HUD's bright status fills, the content is dark. Pure and
    /// unit-tested; the resolved theme caches the result per theme version.
    /// </summary>
    internal static class TopiaForgeOnColors
    {
        /// <summary>The tint uGUI multiplies a kit button's fill by while the pointer is over it, the lightest it goes.</summary>
        internal const float ButtonHoveredTint = 1.06f;

        /// <summary>The tint uGUI multiplies a kit button's fill by while the button is selected.</summary>
        internal const float ButtonSelectedTint = 1.04f;

        /// <summary>The tint uGUI multiplies a kit button's fill by while it is pressed, the darkest it goes.</summary>
        internal const float ButtonPressedTint = TopiaForgeSchemes.PressedTint;

        /// <summary>
        /// The tint uGUI's default color block multiplies a control's target by while it is pressed, the darkest it goes.
        /// A switch's track, a checkbox's box and a slider's handle keep that block, whose other states are lighter.
        /// </summary>
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
        internal static TopiaForgeFillColors[] ResolveAll(TopiaForgeSchemeColors colors, bool highContrast)
        {
            var roles = (TopiaForgeFillRole[])Enum.GetValues(typeof(TopiaForgeFillRole));
            var all = new TopiaForgeFillColors[roles.Length];
            foreach (var role in roles)
            {
                all[(int)role] = Resolve(role, colors, highContrast);
            }

            return all;
        }

        /// <summary>Gets a fill role's fill, and the color drawn on it, in a scheme.</summary>
        internal static TopiaForgeFillColors Resolve(TopiaForgeFillRole role, TopiaForgeSchemeColors colors, bool highContrast)
        {
            var white = TopiaForgePalette.White;
            return role switch
            {
                TopiaForgeFillRole.FilledButton => OnFill(colors.Primary, white, LabelRatio(highContrast), ButtonPressedTint, ButtonHoveredTint),
                TopiaForgeFillRole.DangerButton => OnFill(colors.Danger, white, LabelRatio(highContrast), ButtonPressedTint, ButtonHoveredTint),
                TopiaForgeFillRole.ControlOn => OnFill(colors.Primary, white, MarkRatio(highContrast), ControlPressedTint, 1f),
                TopiaForgeFillRole.SwitchOff => OnFill(colors.Tint, colors.TextFaint, MarkRatio(highContrast), ControlPressedTint, 1f),
                TopiaForgeFillRole.StepDone => OnFill(colors.Success, white, MarkRatio(highContrast), 1f, 1f),
                TopiaForgeFillRole.StepFailed => OnFill(colors.Danger, white, MarkRatio(highContrast), 1f, 1f),
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
                ? TopiaForgeContrast.DarkenForPaper(colors.Accent.WithAlpha(1f), TopiaForgeSchemes.DarkestTextSurface, MarkRatio(highContrast))
                : TopiaForgePalette.White;
        }

        /// <summary>
        /// Gets the content for a fill, and the fill, so that the content reads on the fill in every state from the darkest
        /// tint uGUI gives it to the lightest. The preferred content is moved away from the fill until it reads: lighter on
        /// a darker fill, darker on a lighter one. Content that cannot move far enough, such as white on the brand orange,
        /// gives way to the other side: the kit's dark content, darkened if it must be, or white. A mid-tone fill that
        /// neither reads on, such as Paper's danger red while hovered, moves away from whichever comes closer until it
        /// reads. A gamma project multiplies the stored values by the tint and a linear one multiplies linear light, which
        /// lands between the fill and the stored-value result, so checking the stored-value extremes covers both.
        /// </summary>
        internal static TopiaForgeFillColors OnFill(TopiaForgeRgba fill, TopiaForgeRgba preferred, float minimumRatio, float darkestTint, float lightestTint)
        {
            var lighter = TopiaForgeContrast.Luminance(preferred) > TopiaForgeContrast.Luminance(fill);
            var content = MoveUntilReads(preferred, lighter, fill, minimumRatio, darkestTint, lightestTint);
            if (Worst(content, fill, darkestTint, lightestTint) >= minimumRatio)
            {
                return new TopiaForgeFillColors(fill, content);
            }

            var other = MoveUntilReads(lighter ? DarkContent : TopiaForgePalette.White, !lighter, fill, minimumRatio, darkestTint, lightestTint);
            if (Worst(other, fill, darkestTint, lightestTint) >= minimumRatio)
            {
                return new TopiaForgeFillColors(fill, other);
            }

            var keepContent = Worst(content, fill, darkestTint, lightestTint) >= Worst(other, fill, darkestTint, lightestTint);
            var label = keepContent ? content : other;
            var labelIsLighter = keepContent == lighter;
            for (var step = 0; step < Steps && Worst(label, fill, darkestTint, lightestTint) < minimumRatio; step++)
            {
                fill = labelIsLighter ? fill.Scale(DarkenStep) : Lighten(fill);
            }

            return new TopiaForgeFillColors(fill, label);
        }

        /// <summary>
        /// Gets the lowest contrast between content and its fill over the fill's states. A ratio against one color falls
        /// and then rises as the other's luminance climbs, so over a range of tints it is lowest at one end of the range.
        /// The exception is content whose luminance lies inside the range, and over a range as narrow as the kit's tints
        /// one end then reads below 3:1 as well, so the content still fails.
        /// </summary>
        internal static float Worst(TopiaForgeRgba content, TopiaForgeRgba fill, float darkestTint, float lightestTint)
        {
            return Math.Min(
                TopiaForgeContrast.Ratio(content, fill.Scale(darkestTint)),
                TopiaForgeContrast.Ratio(content, fill.Scale(lightestTint)));
        }

        // Moves content toward white or black until it reads. The steps only approach the end, so content that still
        // falls short takes white or black itself.
        private static TopiaForgeRgba MoveUntilReads(TopiaForgeRgba content, bool lighter, TopiaForgeRgba fill, float minimumRatio, float darkestTint, float lightestTint)
        {
            for (var step = 0; step < Steps && Worst(content, fill, darkestTint, lightestTint) < minimumRatio; step++)
            {
                content = lighter ? Lighten(content) : content.Scale(DarkenStep);
            }

            if (Worst(content, fill, darkestTint, lightestTint) >= minimumRatio)
            {
                return content;
            }

            return lighter ? TopiaForgePalette.White.WithAlpha(content.A) : new TopiaForgeRgba(0f, 0f, 0f, content.A);
        }

        private static TopiaForgeRgba Lighten(TopiaForgeRgba color)
        {
            return TopiaForgeRgba.Lerp(color, TopiaForgePalette.White.WithAlpha(color.A), LightenStep);
        }
    }
}
