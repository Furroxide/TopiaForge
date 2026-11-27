using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    internal sealed partial class UiLayoutEngine
    {
        private void MeasureWrap(Slot slot, UiWrap wrap, UiLayoutConstraints constraints)
        {
            var padding = wrap.Padding;
            var inner = constraints.Inset(padding.Horizontal, padding.Vertical);
            if (!double.IsFinite(inner.MaxWidth)) throw LayoutError(slot, "Wrap requires a finite content width.");

            var children = new Slot[wrap.Children.Count];
            var runs = new List<Run>();
            var spacing = (int)wrap.Spacing;
            var start = 0;
            var width = 0d;
            var height = 0d;
            var childConstraints = UiLayoutConstraints.Loose(inner.MaxWidth, inner.MaxHeight);
            for (var index = 0; index < children.Length; index++)
            {
                // Each child is measured under the full line width, so overwide text wraps before run placement.
                var child = Measure(wrap.Children[index], childConstraints, slot.Placement, index);
                if (index > start && width + spacing + child.Size.Width > inner.MaxWidth)
                {
                    runs.Add(new Run(start, index - start, width, height));
                    start = index;
                    width = 0;
                    height = 0;
                }

                if (index > start) width += spacing;
                width += child.Size.Width;
                height = Math.Max(height, child.Size.Height);
                children[index] = child;
            }

            if (children.Length > start) runs.Add(new Run(start, children.Length - start, width, height));
            var naturalWidth = 0d;
            var naturalHeight = Math.Max(0, runs.Count - 1) * (double)(int)wrap.RunSpacing;
            foreach (var run in runs)
            {
                naturalWidth = Math.Max(naturalWidth, run.Width);
                naturalHeight += run.Height;
            }

            slot.Size = constraints.Fit(new UiLayoutExtent(naturalWidth + padding.Horizontal, naturalHeight + padding.Vertical));
            slot.Children = children;
            var runWidth = Math.Max(0, slot.Size.Width - padding.Horizontal);
            var y = (double)(int)padding.Top;
            foreach (var run in runs)
            {
                var distribution = Distribute(wrap.Alignment, run.Count, Math.Max(0, runWidth - run.Width));
                var x = (int)padding.Left + distribution.Start;
                for (var index = run.Start; index < run.Start + run.Count; index++)
                {
                    var child = children[index];
                    child.X = x;
                    child.Y = y;
                    x += child.Size.Width + spacing + distribution.Gap;
                }

                y += run.Height + (int)wrap.RunSpacing;
            }
        }

        private void MeasureGrid(Slot slot, UiGrid grid, UiLayoutConstraints constraints)
        {
            var padding = grid.Padding;
            var inner = constraints.Inset(padding.Horizontal, padding.Vertical);
            if (!double.IsFinite(inner.MaxWidth)) throw LayoutError(slot, "Grid requires a finite content width.");
            var spacing = (int)grid.Spacing;
            var columns = (int)Math.Clamp(Math.Floor((inner.MaxWidth + spacing) / (grid.MinimumCellWidth + spacing)),
                1, grid.MaximumColumns);
            var cellWidth = Math.Max(0, (inner.MaxWidth - (columns - 1) * spacing) / columns);
            var cellConstraints = new UiLayoutConstraints(cellWidth, cellWidth, 0, inner.MaxHeight);
            var children = new Slot[grid.Children.Count];
            var y = (double)(int)padding.Top;
            var rowHeight = 0d;
            for (var index = 0; index < children.Length; index++)
            {
                var column = index % columns;
                if (index > 0 && column == 0)
                {
                    y += rowHeight + (int)grid.RunSpacing;
                    rowHeight = 0;
                }
                var child = Measure(grid.Children[index], cellConstraints, slot.Placement, index);
                child.X = (int)padding.Left + column * (cellWidth + spacing);
                child.Y = y;
                children[index] = child;
                rowHeight = Math.Max(rowHeight, child.Size.Height);
            }

            slot.Size = constraints.Fit(new UiLayoutExtent(inner.MaxWidth + padding.Horizontal,
                y + rowHeight + (int)padding.Bottom));
            slot.Children = children;
        }

        private readonly struct Run
        {
            internal Run(int start, int count, double width, double height)
            {
                Start = start;
                Count = count;
                Width = width;
                Height = height;
            }

            internal int Start { get; }
            internal int Count { get; }
            internal double Width { get; }
            internal double Height { get; }
        }
    }
}
