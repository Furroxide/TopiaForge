using System;

namespace TopiaForge.Mods
{
    /// <summary>Controls the distribution of spare space along a layout's main axis.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiMainAxisAlignment
    {
        /// <summary>Places children at the start of the axis.</summary>
        Start = 0,
        /// <summary>Centers the group of children.</summary>
        Center = 1,
        /// <summary>Places children at the end of the axis.</summary>
        End = 2,
        /// <summary>Distributes spare space between children.</summary>
        SpaceBetween = 3,
        /// <summary>Distributes spare space between children with half a share at each end.</summary>
        SpaceAround = 4,
        /// <summary>Distributes equal shares of spare space between children and at both ends.</summary>
        SpaceEvenly = 5
    }

    /// <summary>Controls alignment or sizing perpendicular to a layout's main axis.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiCrossAxisAlignment
    {
        /// <summary>Aligns each measured child to the start.</summary>
        Start = 0,
        /// <summary>Centers each measured child.</summary>
        Center = 1,
        /// <summary>Aligns each measured child to the end.</summary>
        End = 2,
        /// <summary>Gives each child the available finite cross-axis extent.</summary>
        Stretch = 3
    }

    /// <summary>Names the kit's fixed spacing values in reference units.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiSpace
    {
        /// <summary>Zero reference units.</summary>
        None = 0,
        /// <summary>Four reference units.</summary>
        Xs = 4,
        /// <summary>Eight reference units.</summary>
        Sm = 8,
        /// <summary>Twelve reference units.</summary>
        Md = 12,
        /// <summary>Sixteen reference units.</summary>
        Lg = 16,
        /// <summary>Twenty-four reference units.</summary>
        Xl = 24,
        /// <summary>Thirty-two reference units.</summary>
        Xxl = 32,
        /// <summary>Forty-eight reference units.</summary>
        Xxxl = 48
    }

    /// <summary>Insets from the four edges of an allocated region.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public readonly struct UiInsets
    {
        /// <summary>Applies the same inset to all edges.</summary>
        public UiInsets(UiSpace all) : this(all, all, all, all) { }

        /// <summary>Applies symmetric horizontal and vertical insets.</summary>
        public UiInsets(UiSpace horizontal, UiSpace vertical) : this(horizontal, vertical, horizontal, vertical) { }

        /// <summary>Applies explicit insets in left, top, right and bottom order.</summary>
        public UiInsets(UiSpace left, UiSpace top, UiSpace right, UiSpace bottom)
        {
            Left = UiLayoutValues.Space(left, nameof(left));
            Top = UiLayoutValues.Space(top, nameof(top));
            Right = UiLayoutValues.Space(right, nameof(right));
            Bottom = UiLayoutValues.Space(bottom, nameof(bottom));
        }

        /// <summary>Gets the left inset.</summary>
        public UiSpace Left { get; }
        /// <summary>Gets the top inset.</summary>
        public UiSpace Top { get; }
        /// <summary>Gets the right inset.</summary>
        public UiSpace Right { get; }
        /// <summary>Gets the bottom inset.</summary>
        public UiSpace Bottom { get; }

        internal float Horizontal => (int)Left + (int)Right;
        internal float Vertical => (int)Top + (int)Bottom;
    }

    /// <summary>Spacing and alignment options for a row or column.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiFlexLayout
    {
        /// <summary>Creates options. A null cross-axis alignment uses Center for a row and Stretch for a column.</summary>
        public UiFlexLayout(UiSpace spacing = UiSpace.Sm, UiInsets padding = default,
            UiMainAxisAlignment mainAxisAlignment = UiMainAxisAlignment.Start,
            UiCrossAxisAlignment? crossAxisAlignment = null)
        {
            Spacing = UiLayoutValues.Space(spacing, nameof(spacing));
            Padding = padding;
            MainAxisAlignment = UiLayoutValues.Main(mainAxisAlignment, nameof(mainAxisAlignment));
            if (crossAxisAlignment.HasValue &&
                (crossAxisAlignment.Value < UiCrossAxisAlignment.Start || crossAxisAlignment.Value > UiCrossAxisAlignment.Stretch))
                throw new ArgumentOutOfRangeException(nameof(crossAxisAlignment));
            CrossAxisAlignment = crossAxisAlignment;
        }

        /// <summary>Gets the minimum gap between children.</summary>
        public UiSpace Spacing { get; }
        /// <summary>Gets the content padding.</summary>
        public UiInsets Padding { get; }
        /// <summary>Gets the main-axis alignment.</summary>
        public UiMainAxisAlignment MainAxisAlignment { get; }
        /// <summary>Gets the authored cross-axis alignment, or null to use the axis default.</summary>
        public UiCrossAxisAlignment? CrossAxisAlignment { get; }
    }

    internal static class UiLayoutValues
    {
        internal static UiSpace Space(UiSpace value, string parameterName) => value switch
        {
            UiSpace.None or UiSpace.Xs or UiSpace.Sm or UiSpace.Md or UiSpace.Lg or UiSpace.Xl or UiSpace.Xxl or UiSpace.Xxxl => value,
            _ => throw new ArgumentOutOfRangeException(parameterName),
        };

        internal static UiMainAxisAlignment Main(UiMainAxisAlignment value, string parameterName) =>
            value >= UiMainAxisAlignment.Start && value <= UiMainAxisAlignment.SpaceEvenly
                ? value : throw new ArgumentOutOfRangeException(parameterName);

        internal static float Dimension(float value, string parameterName) => Bounded(value, 0, 4096, parameterName);

        internal static float? Dimension(float? value, string parameterName) =>
            value.HasValue ? Dimension(value.Value, parameterName) : null;

        internal static float Bounded(float value, float minimum, float maximum, string parameterName) =>
            value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(parameterName);

        internal static int Flex(int value) => value >= 1 && value <= 16 ? value : throw new ArgumentOutOfRangeException("flex");
    }
}
