using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.Mods
{
    internal delegate UiLayoutLeafMeasurement UiLayoutMeasureLeaf(UiNode node, UiLayoutConstraints constraints, bool disabled);

    internal enum UiLayoutPaintKind { None, Painter, Image, Protected }

    internal readonly struct UiLayoutLeafMeasurement
    {
        internal UiLayoutLeafMeasurement(UiLayoutExtent size, UiLayoutPaintKind paintKind, UiLayoutOutsets paintOutsets = default,
            UiLayoutOutsets focusOutsets = default, double? scrollLineHeight = null)
        {
            Size = size;
            PaintKind = paintKind;
            PaintOutsets = paintOutsets;
            FocusOutsets = focusOutsets;
            ScrollLineHeight = scrollLineHeight;
        }

        internal UiLayoutExtent Size { get; }
        internal UiLayoutPaintKind PaintKind { get; }
        internal UiLayoutOutsets PaintOutsets { get; }
        internal UiLayoutOutsets FocusOutsets { get; }
        internal double? ScrollLineHeight { get; }
    }

    internal sealed class UiLayoutResult
    {
        internal static readonly UiLayoutResult Empty = new UiLayoutResult(default, System.Array.Empty<UiLayoutPlacement>());

        internal UiLayoutResult(UiLayoutExtent size, IReadOnlyList<UiLayoutPlacement> placements)
        {
            Size = size;
            Placements = placements;
        }

        internal UiLayoutExtent Size { get; }

        // The backing collection is constructed once per layout and stays stable while the layout is displayed.
        internal IReadOnlyList<UiLayoutPlacement> Placements { get; }
    }

    internal sealed class UiLayoutPlacement
    {
        internal UiLayoutPlacement(UiNode node, UiLayoutPlacement? parent, int index, bool disabled)
        {
            Node = node;
            Parent = parent;
            ChildIndex = index;
            Disabled = disabled;
        }

        internal UiNode Node { get; }
        internal UiLayoutPlacement? Parent { get; }
        internal int ChildIndex { get; }
        internal bool Disabled { get; }
        internal UiLayoutRect Bounds { get; set; }
        internal UiLayoutRect Clip { get; set; }
        internal UiLayoutRect PaintBounds { get; set; }
        internal bool RequiresBackplate { get; set; }

        internal string Path
        {
            get
            {
                var path = new StringBuilder();
                AppendPath(path);
                return path.ToString();
            }
        }

        private void AppendPath(StringBuilder path)
        {
            Parent?.AppendPath(path);
            path.Append('/').Append(ChildIndex.ToString(CultureInfo.InvariantCulture));
        }
    }
}
