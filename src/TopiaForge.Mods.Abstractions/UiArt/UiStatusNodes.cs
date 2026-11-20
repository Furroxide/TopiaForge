using System;
using System.Globalization;

namespace TopiaForge.Mods
{
    /// <summary>Specifies how a status badge shows its semantic color role.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiBadgeStyle
    {
        /// <summary>Uses the role's fill and its paired foreground.</summary>
        Filled = 0,
        /// <summary>Uses a readable ink and boundary over the surrounding surface.</summary>
        Outlined = 1
    }

    /// <summary>Draws a plain-text semantic badge with an optional redundant glyph.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiBadge : UiNode
    {
        /// <summary>Creates a badge with 1 to 256 plain-text units.</summary>
        /// <remarks>
        /// The host supplies readable colors and replaces an incompatible status glyph in high contrast.
        /// A named style may decorate the badge and must preserve its requested role.
        /// </remarks>
        public UiBadge(string text, UiPaintRole role = UiPaintRole.Surface,
            UiBadgeStyle presentation = UiBadgeStyle.Filled, UiGlyph? icon = null,
            UiStyleRef? style = null, string? id = null)
            : base(id)
        {
            UiArtValues.Label(text, false, nameof(text));
            UiArtValues.PaintRole(role, nameof(role));
            if (!Enum.IsDefined(typeof(UiBadgeStyle), presentation))
                throw new ArgumentOutOfRangeException(nameof(presentation));
            if (icon is UiGlyph glyph)
                UiGlyphLabels.Validate(glyph, nameof(icon));

            Text = text;
            Role = role;
            Presentation = presentation;
            Icon = icon;
            Style = style;
        }

        /// <summary>Gets the plain-text status shown to the player.</summary>
        public string Text { get; }
        /// <summary>Gets the requested semantic role.</summary>
        public UiPaintRole Role { get; }
        /// <summary>Gets the filled or outlined presentation.</summary>
        public UiBadgeStyle Presentation { get; }
        /// <summary>Gets the requested redundant icon, if any.</summary>
        public UiGlyph? Icon { get; }
        /// <summary>Gets the named badge style, if supplied.</summary>
        public UiStyleRef? Style { get; }
    }

    /// <summary>Specifies the shape of a pip indicator.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiPipStyle
    {
        /// <summary>Uses round pips.</summary>
        Dots = 0,
        /// <summary>Uses segments that fill the available row width.</summary>
        Segments = 1
    }

    /// <summary>Shows a labelled count with an optional partial fill in the next pip.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiPips : UiNode
    {
        /// <summary>Creates a static indicator for 1 to 32 pips.</summary>
        /// <remarks>
        /// Fraction is in the range [0, 1) and is zero when all pips are filled. To finish a partial pip,
        /// increase the filled count. The kit supplies the visible count and refill percentage.
        /// </remarks>
        public UiPips(int total, int filled, string label, float fraction = 0,
            UiPipStyle presentation = UiPipStyle.Dots, UiPaintRole role = UiPaintRole.Accent,
            UiStyleRef? style = null, string? id = null)
            : base(id)
        {
            if (total < 1 || total > 32) throw new ArgumentOutOfRangeException(nameof(total));
            if (filled < 0 || filled > total) throw new ArgumentOutOfRangeException(nameof(filled));
            if (!(fraction >= 0f && fraction < 1f) || (filled == total && fraction != 0f))
                throw new ArgumentOutOfRangeException(nameof(fraction));
            UiArtValues.Label(label, false, nameof(label));
            if (!Enum.IsDefined(typeof(UiPipStyle), presentation))
                throw new ArgumentOutOfRangeException(nameof(presentation));
            UiArtValues.PaintRole(role, nameof(role));

            Total = total;
            Filled = filled;
            Label = label;
            Fraction = fraction;
            Presentation = presentation;
            Role = role;
            Style = style;
            DisplayLabel = FormatLabel();
        }

        /// <summary>Gets the total number of pips.</summary>
        public int Total { get; }
        /// <summary>Gets the number of fully filled pips.</summary>
        public int Filled { get; }
        /// <summary>Gets the label without the count that the kit adds to it.</summary>
        public string Label { get; }
        /// <summary>Gets the fractional fill of the next pip, or zero.</summary>
        public float Fraction { get; }
        /// <summary>Gets the requested dot or segment presentation.</summary>
        public UiPipStyle Presentation { get; }
        /// <summary>Gets the requested semantic role.</summary>
        public UiPaintRole Role { get; }
        /// <summary>Gets the named pip style, if supplied.</summary>
        public UiStyleRef? Style { get; }

        internal string DisplayLabel { get; }

        private string FormatLabel()
        {
            var count = Label + ": " + Filled.ToString(CultureInfo.InvariantCulture)
                + " / " + Total.ToString(CultureInfo.InvariantCulture);
            if (Fraction == 0f) return count;

            // A partial pip cannot display a completed refill.
            var percent = Math.Min(99, (int)(Fraction * 100f));
            return count + " (" + percent.ToString(CultureInfo.InvariantCulture) + "% refilling)";
        }
    }

    internal static class UiBadgePresentation
    {
        internal static UiGlyph? Glyph(UiBadge badge, bool highContrast) =>
            highContrast ? badge.Role switch
            {
                UiPaintRole.Success => UiGlyph.Check,
                UiPaintRole.Warning => UiGlyph.Warning,
                UiPaintRole.Danger => UiGlyph.Cross,
                _ => badge.Icon
            } : badge.Icon;
    }
}
