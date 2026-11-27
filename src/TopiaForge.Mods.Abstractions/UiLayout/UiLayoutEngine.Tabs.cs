using System;

namespace TopiaForge.Mods
{
    internal sealed partial class UiLayoutEngine
    {
        private void MeasureTabs(Slot slot, UiTabs tabs, UiLayoutConstraints constraints)
        {
            const double gap = (int)UiSpace.Sm;
            const double minimumPageWidth = 40d;
            var rail = tabs.Style == UiTabsStyle.Rail;
            var tabCount = tabs.Tabs.Count;
            var children = new Slot[tabCount * 2];
            var inner = constraints.Inset(gap * 2, gap * 2);
            var stripWidthLimit = rail ? Math.Max(0, inner.MaxWidth - minimumPageWidth - gap) : inner.MaxWidth;
            var stripLimits = UiLayoutConstraints.Loose(stripWidthLimit, inner.MaxHeight);
            var stripWidth = 0d;
            var stripHeight = 0d;
            var x = 0d;
            var y = 0d;
            var rowHeight = 0d;
            for (var index = 0; index < tabCount; index++)
            {
                var tab = Measure(tabs.Buttons[index], stripLimits, slot.Placement, tabCount + index);
                if (!rail && x > 0d && x + tab.Size.Width > stripWidthLimit)
                {
                    x = 0d;
                    y += rowHeight + gap;
                    rowHeight = 0d;
                }
                tab.X = gap + x;
                tab.Y = gap + y;
                stripWidth = Math.Max(stripWidth, x + tab.Size.Width);
                stripHeight = Math.Max(stripHeight, y + tab.Size.Height);
                if (rail) y += tab.Size.Height + gap;
                else
                {
                    x += tab.Size.Width + gap;
                    rowHeight = Math.Max(rowHeight, tab.Size.Height);
                }
                children[tabCount + index] = tab;
            }

            var pageX = rail ? stripWidth + gap : 0d;
            var pageY = rail ? 0d : stripHeight + gap;
            var pageLimits = inner.Inset(pageX, pageY);
            var size = new UiLayoutExtent(stripWidth, stripHeight);
            for (var index = 0; index < tabCount; index++)
            {
                var page = Measure(tabs.Tabs[index].Child, UiLayoutConstraints.Loose(pageLimits.MaxWidth, pageLimits.MaxHeight),
                    slot.Placement, index);
                page.X = gap + pageX;
                page.Y = gap + pageY;
                size = new UiLayoutExtent(Math.Max(size.Width, pageX + page.Size.Width), Math.Max(size.Height, pageY + page.Size.Height));
                children[index] = page;
            }
            slot.Children = children;
            slot.Size = constraints.Fit(new UiLayoutExtent(size.Width + gap * 2, size.Height + gap * 2));
        }

        private static bool VisibleTogether(UiLayoutPlacement first, UiLayoutPlacement second)
        {
            if (first.TabPage == null || second.TabPage == null || ReferenceEquals(first.TabPage, second.TabPage)) return true;
            for (var left = first.TabPage; left != null; left = left.Parent)
            {
                for (var right = second.TabPage; right != null; right = right.Parent)
                {
                    if (!ReferenceEquals(left.Group, right.Group)) continue;
                    if (right.PageIndex != left.PageIndex) return false;
                    break;
                }
            }
            return true;
        }
    }
}
