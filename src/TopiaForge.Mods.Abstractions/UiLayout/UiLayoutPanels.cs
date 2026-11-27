using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    public sealed partial class UiRow
    {
        private static readonly UiFlexLayout LegacyLayout = new UiFlexLayout(crossAxisAlignment: UiCrossAxisAlignment.Center);

        /// <summary>Creates a horizontal layout with explicit spacing, padding, and alignment options.</summary>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public UiRow(IEnumerable<UiNode> children, UiFlexLayout layout, string? id = null) : base(children, id)
        {
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        }

        /// <summary>Gets the row layout. A null cross-axis alignment centers its children.</summary>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public UiFlexLayout Layout { get; } = LegacyLayout;

        internal bool HasExplicitLayout => !ReferenceEquals(Layout, LegacyLayout) || Id != null;
    }

    public sealed partial class UiColumn
    {
        private static readonly UiFlexLayout LegacyLayout = new UiFlexLayout(crossAxisAlignment: UiCrossAxisAlignment.Stretch);

        /// <summary>Creates a vertical layout with explicit spacing, padding, and alignment options.</summary>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public UiColumn(IEnumerable<UiNode> children, UiFlexLayout layout, string? id = null) : base(children, id)
        {
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        }

        /// <summary>Gets the column layout. A null cross-axis alignment stretches its children.</summary>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public UiFlexLayout Layout { get; } = LegacyLayout;

        internal bool HasExplicitLayout => !ReferenceEquals(Layout, LegacyLayout) || Id != null;
    }

    /// <summary>Places children in horizontal runs, wrapping in source order.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiWrap : UiLayoutNode
    {
        /// <summary>Creates a wrap with independent gaps between children and runs.</summary>
        public UiWrap(IEnumerable<UiNode> children, UiSpace spacing = UiSpace.Sm,
            UiSpace runSpacing = UiSpace.Sm, UiInsets padding = default,
            UiMainAxisAlignment alignment = UiMainAxisAlignment.Start, string? id = null) : base(children, id)
        {
            Spacing = UiLayoutValues.Space(spacing, nameof(spacing));
            RunSpacing = UiLayoutValues.Space(runSpacing, nameof(runSpacing));
            Padding = padding;
            Alignment = UiLayoutValues.Main(alignment, nameof(alignment));
        }

        /// <summary>Gets the minimum gap between children in a run.</summary>
        public UiSpace Spacing { get; }
        /// <summary>Gets the gap between runs.</summary>
        public UiSpace RunSpacing { get; }
        /// <summary>Gets the content padding.</summary>
        public UiInsets Padding { get; }
        /// <summary>Gets how each run distributes its spare horizontal space.</summary>
        public UiMainAxisAlignment Alignment { get; }
    }

    /// <summary>Places children in equally wide cells with responsive column count.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiGrid : UiLayoutNode
    {
        /// <summary>Creates a grid that fits at most twelve columns and lays out at least one.</summary>
        /// <param name="children">The cells in row-major order.</param>
        /// <param name="minimumCellWidth">The desired per-cell minimum from 48 to 1024 reference units.</param>
        /// <param name="maximumColumns">The upper column count from one to twelve.</param>
        /// <param name="spacing">The gap between cells in one row.</param>
        /// <param name="runSpacing">The gap between rows.</param>
        /// <param name="padding">Insets from the grid edges.</param>
        /// <param name="id">An optional stable id.</param>
        public UiGrid(IEnumerable<UiNode> children, float minimumCellWidth, int maximumColumns = 12,
            UiSpace spacing = UiSpace.Sm, UiSpace runSpacing = UiSpace.Sm, UiInsets padding = default, string? id = null)
            : base(children, id)
        {
            MinimumCellWidth = UiLayoutValues.Bounded(minimumCellWidth, 48, 1024, nameof(minimumCellWidth));
            if (maximumColumns < 1 || maximumColumns > 12) throw new ArgumentOutOfRangeException(nameof(maximumColumns));
            MaximumColumns = maximumColumns;
            Spacing = UiLayoutValues.Space(spacing, nameof(spacing));
            RunSpacing = UiLayoutValues.Space(runSpacing, nameof(runSpacing));
            Padding = padding;
        }

        /// <summary>Gets the desired minimum width used to choose a column count.</summary>
        public float MinimumCellWidth { get; }
        /// <summary>Gets the maximum column count.</summary>
        public int MaximumColumns { get; }
        /// <summary>Gets the gap between cells.</summary>
        public UiSpace Spacing { get; }
        /// <summary>Gets the gap between rows.</summary>
        public UiSpace RunSpacing { get; }
        /// <summary>Gets the content padding.</summary>
        public UiInsets Padding { get; }
    }

    /// <summary>Paints children in order inside a bounded rectangular extent.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiStack : UiLayoutNode
    {
        /// <summary>Creates a stack with preferred finite dimensions from 0 to 4096 reference units.</summary>
        public UiStack(IEnumerable<UiNode> children, float width, float height, string? id = null) : base(children, id)
        {
            Width = UiLayoutValues.Dimension(width, nameof(width));
            Height = UiLayoutValues.Dimension(height, nameof(height));
        }

        /// <summary>Gets the preferred width.</summary>
        public float Width { get; }
        /// <summary>Gets the preferred height.</summary>
        public float Height { get; }
    }
}
