using System;

namespace TopiaForge.Mods
{
    /// <summary>Insets one child from its allocated edges.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiPadding : UiNode
    {
        /// <summary>Creates padding in the kit's fixed spacing tokens.</summary>
        public UiPadding(UiNode child, UiInsets padding, string? id = null) : base(id)
        {
            Child = child ?? throw new ArgumentNullException(nameof(child));
            Padding = padding;
        }

        /// <summary>Gets the inset child.</summary>
        public UiNode Child { get; }
        /// <summary>Gets the four insets.</summary>
        public UiInsets Padding { get; }
    }

    /// <summary>Gives a child a bounded preferred size or contributes empty space to its parent.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiSizedBox : UiNode
    {
        /// <summary>Creates a box. At least one dimension or a child is required.</summary>
        /// <param name="child">The optional child. A box without a child contributes an empty gap.</param>
        /// <param name="width">The preferred width from 0 to 4096 reference units, or null for a loose width.</param>
        /// <param name="height">The preferred height from 0 to 4096 reference units, or null for a loose height.</param>
        /// <param name="id">An optional stable id.</param>
        public UiSizedBox(UiNode? child = null, float? width = null, float? height = null, string? id = null) : base(id)
        {
            if (child == null && !width.HasValue && !height.HasValue)
                throw new ArgumentException("A sized box requires a child or at least one dimension.");
            Width = UiLayoutValues.Dimension(width, nameof(width));
            Height = UiLayoutValues.Dimension(height, nameof(height));
            Child = child;
        }

        /// <summary>Gets the optional child.</summary>
        public UiNode? Child { get; }
        /// <summary>Gets the preferred width, or null for a loose width constraint.</summary>
        public float? Width { get; }
        /// <summary>Gets the preferred height, or null for a loose height constraint.</summary>
        public float? Height { get; }
    }

    /// <summary>Gives a child a weighted share of the remaining space in a bounded row or column.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiExpanded : UiNode
    {
        /// <summary>Creates an expanded child with a flex weight from 1 to 16.</summary>
        public UiExpanded(UiNode child, int flex = 1, string? id = null) : base(id)
        {
            Child = child ?? throw new ArgumentNullException(nameof(child));
            Flex = UiLayoutValues.Flex(flex);
        }

        /// <summary>Gets the child that receives the allocation.</summary>
        public UiNode Child { get; }
        /// <summary>Gets the allocation weight.</summary>
        public int Flex { get; }
    }

    /// <summary>Fills a weighted share of a bounded row or column without painting or taking input.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiSpacer : UiNode
    {
        /// <summary>Creates an empty flex child with a weight from 1 to 16.</summary>
        public UiSpacer(int flex = 1, string? id = null) : base(id) { Flex = UiLayoutValues.Flex(flex); }

        /// <summary>Gets the allocation weight.</summary>
        public int Flex { get; }
    }

    /// <summary>Positions one child relative to the top-left of its immediate stack.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiPositioned : UiNode
    {
        /// <summary>Positions a child. Offsets and explicit dimensions must fit the measured parent stack.</summary>
        public UiPositioned(UiNode child, float left, float top, float? width = null, float? height = null, string? id = null)
            : base(id)
        {
            Child = child ?? throw new ArgumentNullException(nameof(child));
            Left = UiLayoutValues.Dimension(left, nameof(left));
            Top = UiLayoutValues.Dimension(top, nameof(top));
            Width = UiLayoutValues.Dimension(width, nameof(width));
            Height = UiLayoutValues.Dimension(height, nameof(height));
        }

        /// <summary>Gets the positioned child.</summary>
        public UiNode Child { get; }
        /// <summary>Gets the horizontal offset to the right in reference units.</summary>
        public float Left { get; }
        /// <summary>Gets the vertical offset down in reference units.</summary>
        public float Top { get; }
        /// <summary>Gets the requested width, or null to measure within the remaining width.</summary>
        public float? Width { get; }
        /// <summary>Gets the requested height, or null to measure within the remaining height.</summary>
        public float? Height { get; }
    }

    /// <summary>Groups a child and optional header on a styled presentation surface.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiCard : UiNode
    {
        /// <summary>Creates a card. A disabled card keeps its content readable and disables descendant controls.</summary>
        public UiCard(UiNode child, UiNode? header = null, UiStyleRef? style = null,
            bool selected = false, bool disabled = false, UiInsets? padding = null, string? id = null) : base(id)
        {
            Child = child ?? throw new ArgumentNullException(nameof(child));
            Header = header;
            Style = style;
            Selected = selected;
            Disabled = disabled;
            Padding = padding;
        }

        /// <summary>Gets the card's content child.</summary>
        public UiNode Child { get; }
        /// <summary>Gets the optional header, laid out before the content.</summary>
        public UiNode? Header { get; }
        /// <summary>Gets the optional named card style from the resolved theme.</summary>
        public UiStyleRef? Style { get; }
        /// <summary>Gets whether the card shows the selection cue.</summary>
        public bool Selected { get; }
        /// <summary>Gets whether interactive descendants are disabled.</summary>
        public bool Disabled { get; }
        /// <summary>Gets the authored padding, or null to use the kit's large inset on each edge.</summary>
        public UiInsets? Padding { get; }
    }

    /// <summary>Defines the appearance of a divider.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiLineStyle
    {
        /// <summary>Draws a continuous line.</summary>
        Solid = 0,
        /// <summary>Draws dashes separated by gaps.</summary>
        Dashed = 1
    }

    /// <summary>Separates adjacent content with a horizontal semantic line.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiDivider : UiNode
    {
        /// <summary>Creates a divider with optional spacing above and below its line.</summary>
        public UiDivider(UiLineStyle lineStyle = UiLineStyle.Solid,
            UiSpace before = UiSpace.Sm, UiSpace after = UiSpace.Sm, string? id = null) : base(id)
        {
            if (lineStyle != UiLineStyle.Solid && lineStyle != UiLineStyle.Dashed)
                throw new ArgumentOutOfRangeException(nameof(lineStyle));
            LineStyle = lineStyle;
            Before = UiLayoutValues.Space(before, nameof(before));
            After = UiLayoutValues.Space(after, nameof(after));
        }

        /// <summary>Gets the line style.</summary>
        public UiLineStyle LineStyle { get; }
        /// <summary>Gets the spacing before the line.</summary>
        public UiSpace Before { get; }
        /// <summary>Gets the spacing after the line.</summary>
        public UiSpace After { get; }
    }

    /// <summary>Paints a bounded decorative rectangle with a semantic fill.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiBox : UiNode
    {
        /// <summary>Creates a box with preferred dimensions from 0 to 4096 reference units.</summary>
        public UiBox(float width, float height, UiPaintRole fill = UiPaintRole.Surface,
            UiStyleRef? style = null, string? id = null) : base(id)
        {
            Width = UiLayoutValues.Dimension(width, nameof(width));
            Height = UiLayoutValues.Dimension(height, nameof(height));
            if (fill < UiPaintRole.Surface || fill > UiPaintRole.Danger) throw new ArgumentOutOfRangeException(nameof(fill));
            Fill = fill;
            Style = style;
        }

        /// <summary>Gets the preferred width.</summary>
        public float Width { get; }
        /// <summary>Gets the preferred height.</summary>
        public float Height { get; }
        /// <summary>Gets the requested semantic fill.</summary>
        public UiPaintRole Fill { get; }
        /// <summary>Gets the optional named box style from the resolved theme.</summary>
        public UiStyleRef? Style { get; }
    }
}
