using System;

namespace TopiaForge.Mods
{
    /// <summary>Rewrites presentation metadata and retains every unaffected node, including its clock identity.</summary>
    internal static class UiTreeProjection
    {
        internal static UiNode? Rewrite(UiNode? node, Func<UiNode, UiNode?> project)
        {
            if (node == null) return null;
            var projected = project(node);
            if (projected == null) return null;

            var children = UiNodeChildren.Of(projected);
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
            return changed == null ? projected : WithChildren(projected, changed);
        }

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
            UiTabs tabs => WithTabChildren(tabs, children),
            UiTooltip tooltip => new UiTooltip(children[0], tooltip.Message, tooltip.Id),
            _ => throw new ArgumentException("This UI container cannot be projected into a remote composition."),
        };

        private static UiTabs WithTabChildren(UiTabs tabs, UiNode[] children)
        {
            var pages = new UiTab[children.Length];
            for (var index = 0; index < children.Length; index++)
            {
                var tab = tabs.Tabs[index];
                pages[index] = ReferenceEquals(tab.Child, children[index]) ? tab : new UiTab(tab.Id, tab.Label, children[index]);
            }
            return new UiTabs(tabs.Id!, pages, tabs.SelectedTabId, tabs.Selected, tabs.Style, tabs.Enabled);
        }
    }
}
