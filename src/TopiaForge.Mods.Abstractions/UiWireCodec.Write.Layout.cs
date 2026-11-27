using System;

namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        private const byte RowLayoutTag = 16;
        private const byte ColumnLayoutTag = 17;
        private const byte PaddingTag = 18;
        private const byte ExpandedTag = 19;
        private const byte WrapTag = 20;
        private const byte GridTag = 21;
        private const byte StackTag = 22;
        private const byte PositionedTag = 23;
        private const byte CardTag = 24;
        private const byte DividerTag = 25;
        private const byte BoxTag = 26;
        private const byte SizedBoxTag = 31;
        private const byte SpacerTag = 32;

        private static bool IsEnhancedLayout(UiNode node) => node is UiRow { HasExplicitLayout: true } or
            UiColumn { HasExplicitLayout: true } or UiPadding or UiExpanded or UiWrap or UiGrid or UiStack or
            UiPositioned or UiCard or UiDivider or UiBox or UiSizedBox or UiSpacer;

        private static void WriteLayoutNode(IUiWireWriter writer, UiNode node, string clockEpoch, bool retained, ref bool hasCountdown)
        {
            switch (node)
            {
                case UiRow row:
                    LayoutHeader(writer, RowLayoutTag, row);
                    WriteFlexLayout(writer, row.Layout);
                    Children(writer, row, clockEpoch, retained, ref hasCountdown);
                    break;
                case UiColumn column:
                    LayoutHeader(writer, ColumnLayoutTag, column);
                    WriteFlexLayout(writer, column.Layout);
                    Children(writer, column, clockEpoch, retained, ref hasCountdown);
                    break;
                case UiPadding padding:
                    LayoutHeader(writer, PaddingTag, padding);
                    WriteInsets(writer, padding.Padding);
                    WriteLayoutChild(writer, padding, clockEpoch, retained, ref hasCountdown);
                    break;
                case UiExpanded expanded:
                    LayoutHeader(writer, ExpandedTag, expanded);
                    writer.WriteByte((byte)expanded.Flex);
                    WriteLayoutChild(writer, expanded, clockEpoch, retained, ref hasCountdown);
                    break;
                case UiWrap wrap:
                    LayoutHeader(writer, WrapTag, wrap);
                    writer.WriteByte((byte)wrap.Spacing);
                    writer.WriteByte((byte)wrap.RunSpacing);
                    WriteInsets(writer, wrap.Padding);
                    writer.WriteByte((byte)wrap.Alignment);
                    Children(writer, wrap, clockEpoch, retained, ref hasCountdown);
                    break;
                case UiGrid grid:
                    LayoutHeader(writer, GridTag, grid);
                    writer.WriteSingle(grid.MinimumCellWidth);
                    writer.WriteByte((byte)grid.MaximumColumns);
                    writer.WriteByte((byte)grid.Spacing);
                    writer.WriteByte((byte)grid.RunSpacing);
                    WriteInsets(writer, grid.Padding);
                    Children(writer, grid, clockEpoch, retained, ref hasCountdown);
                    break;
                case UiStack stack:
                    LayoutHeader(writer, StackTag, stack);
                    writer.WriteSingle(stack.Width);
                    writer.WriteSingle(stack.Height);
                    Children(writer, stack, clockEpoch, retained, ref hasCountdown);
                    break;
                case UiPositioned positioned:
                    LayoutHeader(writer, PositionedTag, positioned);
                    writer.WriteSingle(positioned.Left);
                    writer.WriteSingle(positioned.Top);
                    OptionalNumber(writer, positioned.Width);
                    OptionalNumber(writer, positioned.Height);
                    WriteLayoutChild(writer, positioned, clockEpoch, retained, ref hasCountdown);
                    break;
                case UiCard card:
                    LayoutHeader(writer, CardTag, card);
                    Text(writer, card.Style?.Id ?? string.Empty);
                    Flag(writer, card.Selected);
                    Flag(writer, card.Disabled);
                    Flag(writer, card.Padding.HasValue);
                    if (card.Padding is UiInsets cardInsets) WriteInsets(writer, cardInsets);
                    Flag(writer, card.Header != null);
                    var cardChildren = UiNodeChildren.Of(card);
                    for (var index = 0; index < cardChildren.Count; index++)
                        WriteNode(writer, cardChildren[index], clockEpoch, retained, ref hasCountdown);
                    break;
                case UiDivider divider:
                    LayoutHeader(writer, DividerTag, divider);
                    writer.WriteByte((byte)divider.LineStyle);
                    writer.WriteByte((byte)divider.Before);
                    writer.WriteByte((byte)divider.After);
                    break;
                case UiBox box:
                    LayoutHeader(writer, BoxTag, box);
                    writer.WriteSingle(box.Width);
                    writer.WriteSingle(box.Height);
                    writer.WriteByte((byte)box.Fill);
                    Text(writer, box.Style?.Id ?? string.Empty);
                    break;
                case UiSizedBox sized:
                    LayoutHeader(writer, SizedBoxTag, sized);
                    OptionalNumber(writer, sized.Width);
                    OptionalNumber(writer, sized.Height);
                    Flag(writer, sized.Child != null);
                    if (sized.Child != null) WriteLayoutChild(writer, sized, clockEpoch, retained, ref hasCountdown);
                    break;
                case UiSpacer spacer:
                    LayoutHeader(writer, SpacerTag, spacer);
                    writer.WriteByte((byte)spacer.Flex);
                    break;
                default:
                    throw new ArgumentException("The enhanced layout node is not supported.", nameof(node));
            }
        }

        private static void WriteLayoutChild(IUiWireWriter writer, UiNode node, string clockEpoch, bool retained, ref bool hasCountdown) =>
            WriteNode(writer, UiNodeChildren.Of(node)[0], clockEpoch, retained, ref hasCountdown);

        private static void LayoutHeader(IUiWireWriter writer, byte tag, UiNode node)
        {
            writer.WriteByte(tag);
            Text(writer, node.Id ?? string.Empty);
        }

        private static void WriteFlexLayout(IUiWireWriter writer, UiFlexLayout layout)
        {
            writer.WriteByte((byte)layout.Spacing);
            WriteInsets(writer, layout.Padding);
            writer.WriteByte((byte)layout.MainAxisAlignment);
            Flag(writer, layout.CrossAxisAlignment.HasValue);
            if (layout.CrossAxisAlignment is UiCrossAxisAlignment cross) writer.WriteByte((byte)cross);
        }

        private static void WriteInsets(IUiWireWriter writer, UiInsets value)
        {
            writer.WriteByte((byte)value.Left);
            writer.WriteByte((byte)value.Top);
            writer.WriteByte((byte)value.Right);
            writer.WriteByte((byte)value.Bottom);
        }

        private static void OptionalNumber(IUiWireWriter writer, float? value)
        {
            Flag(writer, value.HasValue);
            if (value.HasValue) writer.WriteSingle(value.Value);
        }
    }
}
