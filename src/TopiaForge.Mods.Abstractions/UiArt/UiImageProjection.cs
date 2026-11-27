using System;

namespace TopiaForge.Mods
{
    /// <summary>Projects artwork without rebuilding unrelated trees or rebinding countdowns.</summary>
    internal static class UiImageProjection
    {
        internal static UiNode? Rewrite(UiNode? node, Func<UiImage, UiImage?> project)
        {
            if (node == null) return null;
            if (node is UiImage image) return project(image);

            var children = UiNodeChildren.Of(node);
            UiNode[]? changed = null;
            for (var index = 0; index < children.Count; index++)
            {
                var previous = children[index];
                var current = Rewrite(previous, project);
                if (current == null) return null;
                if (changed == null && !ReferenceEquals(previous, current))
                {
                    changed = new UiNode[children.Count];
                    for (var before = 0; before < index; before++) changed[before] = children[before];
                }
                if (changed != null) changed[index] = current;
            }
            return changed == null ? node : WithChildren(node, changed);
        }

        internal static UiThemeImage? Find(UiThemeDescriptor pack, string id)
        {
            for (var index = 0; index < pack.Images.Count; index++)
                if (string.Equals(pack.Images[index].Id, id, StringComparison.Ordinal)) return pack.Images[index];
            return null;
        }

        internal static UiImageFrame ResolveFrame(UiImage image, UiThemeImage metadata)
        {
            if (metadata.PixelWidth is not int width || metadata.PixelHeight is not int height || metadata.IsMask is not bool mask)
                throw new ArgumentException("$.images: The image has incomplete admitted sprite metadata.");
            return UiImageFrame.Resolve(image, width, height, mask);
        }

        internal static UiLayoutRect FixedBox(UiLayoutRect allocated, float width, float height) =>
            new UiLayoutRect(allocated.X, allocated.Y, width, height);

        private static UiNode WithChildren(UiNode node, UiNode[] children) => node switch
        {
            UiRow row => row.HasExplicitLayout ? new UiRow(children, row.Layout, row.Id) : new UiRow(children),
            UiColumn column => column.HasExplicitLayout ? new UiColumn(children, column.Layout, column.Id) : new UiColumn(children),
            UiGrid grid => new UiGrid(children, grid.MinimumCellWidth, grid.MaximumColumns, grid.Spacing, grid.RunSpacing, grid.Padding, grid.Id),
            UiWrap wrap => new UiWrap(children, wrap.Spacing, wrap.RunSpacing, wrap.Padding, wrap.Alignment, wrap.Id),
            UiStack stack => new UiStack(children, stack.Width, stack.Height, stack.Id),
            UiCard card => new UiCard(children[children.Length - 1], card.Header == null ? null : children[0],
                card.Style, card.Selected, card.Disabled, card.Padding, card.Id),
            UiPadding padding => new UiPadding(children[0], padding.Padding, padding.Id),
            UiSizedBox box => new UiSizedBox(children[0], box.Width, box.Height, box.Id),
            UiExpanded expanded => new UiExpanded(children[0], expanded.Flex, expanded.Id),
            UiPositioned positioned => new UiPositioned(children[0], positioned.Left, positioned.Top, positioned.Width, positioned.Height, positioned.Id),
            UiScroll scroll => new UiScroll(children[0], scroll.Height),
            UiSplitPane split => new UiSplitPane(children[0], children[1], split.Orientation, split.PrimaryFraction),
            _ => throw new ArgumentException("This UI container cannot be projected into a remote composition."),
        };
    }
}
