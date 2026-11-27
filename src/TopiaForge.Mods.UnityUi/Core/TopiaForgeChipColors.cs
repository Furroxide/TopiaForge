namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A status chip's colors in one tone of one scheme, resolved by <see cref="TopiaForgeSchemes.Chip"/> and
    /// <see cref="TopiaForgeSchemes.NeutralChip"/> so that the label reads on the chip.
    /// </summary>
    internal readonly struct TopiaForgeChipColors
    {
        internal TopiaForgeChipColors(TopiaForgeRgba fill, TopiaForgeRgba ring, TopiaForgeRgba label)
        {
            Fill = fill;
            Ring = ring;
            Label = label;
        }

        /// <summary>Gets the fill, with its own opacity: a wash of the tone on Paper, solid on the HUD.</summary>
        internal TopiaForgeRgba Fill { get; }

        /// <summary>Gets the ring drawn around the fill.</summary>
        internal TopiaForgeRgba Ring { get; }

        /// <summary>Gets the label's color.</summary>
        internal TopiaForgeRgba Label { get; }

        /// <summary>
        /// Gets the chip with a translucent fill flattened onto the color it sits on, as drawn, so that it looks the same
        /// there and a picture under it, such as a card's preview, cannot show through. The label reads on the flattened
        /// fill on any Paper surface, the darkest included.
        /// </summary>
        internal TopiaForgeChipColors Flattened(TopiaForgeRgba behind)
        {
            return Fill.A >= 1f
                ? this
                : new TopiaForgeChipColors(TopiaForgeContrast.Quantize(TopiaForgeContrast.Over(Fill, Fill.A, behind, linear: false)), Ring, Label);
        }
    }
}
