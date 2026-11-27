using System;

namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        private sealed partial class Decoder
        {
            private UiNode LayoutNode(byte type, int depth)
            {
                switch (type)
                {
                    case RowLayoutTag:
                    case ColumnLayoutTag:
                        return FlexNode(type, depth);
                    case PaddingTag:
                        var paddingId = LayoutId();
                        var insets = Insets();
                        return new UiPadding(LayoutChild(depth), insets, paddingId);
                    case ExpandedTag:
                        var expandedId = LayoutId();
                        var flex = Byte(r, 16);
                        if (flex == 0) throw Bad();
                        return new UiExpanded(LayoutChild(depth), flex, expandedId);
                    case WrapTag:
                        return Wrap(depth);
                    case GridTag:
                        return Grid(depth);
                    case StackTag:
                        var stackId = LayoutId();
                        var stackWidth = Number(r);
                        var stackHeight = Number(r);
                        return new UiStack(LayoutChildren(depth), stackWidth, stackHeight, stackId);
                    case PositionedTag:
                        return Positioned(depth);
                    case CardTag:
                        return Card(depth);
                    case DividerTag:
                        var dividerId = LayoutId();
                        return new UiDivider((UiLineStyle)Byte(r, 1), Space(), Space(), dividerId);
                    case BoxTag:
                        var boxId = LayoutId();
                        return new UiBox(Number(r), Number(r), (UiPaintRole)Byte(r, (byte)UiPaintRole.Danger), StyleId(), boxId);
                    case SizedBoxTag:
                        var sizedId = LayoutId();
                        var width = OptionalNumber();
                        var height = OptionalNumber();
                        return new UiSizedBox(Present() ? LayoutChild(depth) : null, width, height, sizedId);
                    case SpacerTag:
                        var spacerId = LayoutId();
                        return new UiSpacer(Byte(r, 16), spacerId);
                    default:
                        throw Bad();
                }
            }

            private UiNode FlexNode(byte type, int depth)
            {
                var id = LayoutId();
                var spacing = Space();
                var padding = Insets();
                var main = (UiMainAxisAlignment)Byte(r, (byte)UiMainAxisAlignment.SpaceEvenly);
                var cross = Present() ? (UiCrossAxisAlignment?)Byte(r, (byte)UiCrossAxisAlignment.Stretch) : null;
                var layout = new UiFlexLayout(spacing, padding, main, cross);
                var children = LayoutChildren(depth);
                return type == RowLayoutTag ? new UiRow(children, layout, id) : new UiColumn(children, layout, id);
            }

            private UiWrap Wrap(int depth)
            {
                var id = LayoutId();
                var spacing = Space();
                var runSpacing = Space();
                var padding = Insets();
                var alignment = (UiMainAxisAlignment)Byte(r, (byte)UiMainAxisAlignment.SpaceEvenly);
                return new UiWrap(LayoutChildren(depth), spacing, runSpacing, padding, alignment, id);
            }

            private UiGrid Grid(int depth)
            {
                var id = LayoutId();
                var width = Number(r);
                var columns = Byte(r, 12);
                var spacing = Space();
                var runSpacing = Space();
                var padding = Insets();
                return new UiGrid(LayoutChildren(depth), width, columns, spacing, runSpacing, padding, id);
            }

            private UiPositioned Positioned(int depth)
            {
                var id = LayoutId();
                var left = Number(r);
                var top = Number(r);
                var width = OptionalNumber();
                var height = OptionalNumber();
                return new UiPositioned(LayoutChild(depth), left, top, width, height, id);
            }

            private UiCard Card(int depth)
            {
                var id = LayoutId();
                var style = StyleId();
                var selected = Present();
                var disabled = Present();
                var padding = Present() ? (UiInsets?)Insets() : null;
                var header = Present() ? LayoutChild(depth) : null;
                return new UiCard(LayoutChild(depth), header, style, selected, disabled, padding, id);
            }

            private string? LayoutId()
            {
                var id = Text(r, 128);
                return id.Length == 0 ? null : id;
            }

            private UiStyleRef? StyleId()
            {
                if (!r.TryReadString(64, out var id)) throw Bad();
                return id.Length == 0 ? null : new UiStyleRef(id);
            }

            private bool Present() => Byte(r, 1) != 0;
            private float? OptionalNumber() => Present() ? Number(r) : null;
            private UiSpace Space() => UiLayoutValues.Space((UiSpace)Byte(r), "spacing");
            private UiInsets Insets() => new UiInsets(Space(), Space(), Space(), Space());

            private UiNode LayoutChild(int depth) => Node(depth + 1) ?? throw Bad();

            private UiNode[] LayoutChildren(int depth)
            {
                var maximum = depth < UiLayoutTree.MaximumDepth
                    ? Math.Min(256, UiLayoutTree.MaximumNodes - count) : 0;
                var length = Count(maximum);
                if (length == 0) return Array.Empty<UiNode>();

                var children = new UiNode[length];
                for (var index = 0; index < children.Length; index++) children[index] = LayoutChild(depth);
                return children;
            }
        }
    }
}
