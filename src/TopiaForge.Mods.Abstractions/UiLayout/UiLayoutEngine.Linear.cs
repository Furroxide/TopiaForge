using System;

namespace TopiaForge.Mods
{
    internal sealed partial class UiLayoutEngine
    {
        private bool measuringIntrinsicCross;

        private void MeasureFlex(Slot slot, UiFlexLayout layout, UiLayoutConstraints constraints, bool horizontal)
        {
            var axis = new Axis(horizontal);
            var padding = layout.Padding;
            var inner = constraints.Inset(padding.Horizontal, padding.Vertical);
            var mainLimit = axis.MainLimit(inner);
            var crossLimit = axis.CrossLimit(inner);
            var alignment = layout.CrossAxisAlignment ?? (horizontal ? UiCrossAxisAlignment.Center : UiCrossAxisAlignment.Stretch);
            var boundedStretch = alignment == UiCrossAxisAlignment.Stretch && double.IsFinite(crossLimit);
            var legacyColumn = slot.Node is UiColumn { HasExplicitLayout: false };
            var stretch = boundedStretch && (!legacyColumn || axis.CrossMinimum(inner) == crossLimit);
            if (boundedStretch && !stretch && !measuringIntrinsicCross)
            {
                // Legacy columns take their content width when the parent leaves width loose. Suppress nested
                // refinements during this probe, then measure at the selected width with the original height budget.
                measuringIntrinsicCross = true;
                try { MeasureFlex(slot, layout, constraints, horizontal); }
                finally { measuringIntrinsicCross = false; }
                var allocatedCross = axis.Cross(slot.Size);
                var tightened = axis.CreateConstraints(axis.MainMinimum(constraints), axis.MainLimit(constraints),
                    allocatedCross, allocatedCross);
                MeasureFlex(slot, layout, tightened, horizontal);
                return;
            }
            var children = UiNodeChildren.Of(slot.Node);
            var measured = new Slot[children.Count];
            var minimumGap = (int)layout.Spacing;
            var occupied = Math.Max(0, measured.Length - 1) * (double)minimumGap;
            var maxCross = 0d;
            var totalFlex = 0;

            for (var index = 0; index < measured.Length; index++)
            {
                var child = children[index];
                var flex = FlexOf(child);
                if (flex > 0)
                {
                    if (!double.IsFinite(mainLimit))
                        throw LayoutError(new Slot(child, slot.Placement, index), "A flex child requires a finite main-axis extent.");
                    totalFlex += flex;
                    continue;
                }

                var available = Math.Max(0, mainLimit - occupied);
                var childConstraints = axis.CreateConstraints(0, available, stretch ? crossLimit : 0, crossLimit);
                var fixedChild = Measure(child, childConstraints, slot.Placement, index);
                measured[index] = fixedChild;
                occupied += axis.Main(fixedChild.Size);
                maxCross = Math.Max(maxCross, axis.Cross(fixedChild.Size));
            }

            if (totalFlex > 0)
            {
                var remaining = Math.Max(0, mainLimit - occupied);
                var childPixels = Math.Floor(remaining * pixelsPerUnit);
                if (!double.IsFinite(childPixels) || childPixels > int.MaxValue)
                    throw LayoutError(slot, "The main-axis allocation exceeds the pixel range.");
                var remainingPixels = childPixels;
                for (var index = 0; index < measured.Length; index++)
                {
                    remainingPixels -= Math.Floor(childPixels * FlexOf(children[index]) / totalFlex);
                }

                for (var index = 0; index < measured.Length; index++)
                {
                    var flex = FlexOf(children[index]);
                    if (flex == 0) continue;

                    var allocation = Math.Floor(childPixels * flex / totalFlex);
                    if (remainingPixels > 0)
                    {
                        allocation++;
                        remainingPixels--;
                    }
                    var units = allocation / pixelsPerUnit;
                    var childConstraints = axis.CreateConstraints(units, units, stretch ? crossLimit : 0, crossLimit);
                    var expanded = Measure(children[index], childConstraints, slot.Placement, index);
                    measured[index] = expanded;
                    occupied += axis.Main(expanded.Size);
                    maxCross = Math.Max(maxCross, axis.Cross(expanded.Size));
                }
            }

            var mainMargin = horizontal ? padding.Horizontal : padding.Vertical;
            var crossMargin = horizontal ? padding.Vertical : padding.Horizontal;
            var main = totalFlex > 0 ? mainLimit : occupied;
            var cross = stretch ? crossLimit : maxCross;
            slot.Size = constraints.Fit(axis.Extent(main + mainMargin, cross + crossMargin));
            slot.Children = measured;

            var extraMain = Math.Max(0, axis.Main(slot.Size) - mainMargin - occupied);
            var availableCross = Math.Max(0, axis.Cross(slot.Size) - crossMargin);
            var distribution = Distribute(layout.MainAxisAlignment, measured.Length, extraMain);
            var mainOffset = (horizontal ? (int)padding.Left : (int)padding.Top) + distribution.Start;
            var crossStart = horizontal ? (int)padding.Top : (int)padding.Left;
            foreach (var child in measured)
            {
                var spareCross = Math.Max(0, availableCross - axis.Cross(child.Size));
                axis.Place(child, mainOffset, crossStart + CrossOffset(alignment, spareCross));
                mainOffset += axis.Main(child.Size) + minimumGap + distribution.Gap;
            }
        }

        private void MeasureSplit(Slot slot, UiSplitPane split, UiLayoutConstraints constraints)
        {
            var axis = new Axis(split.Orientation == UiSplitOrientation.Horizontal);
            const double gap = (int)UiSpace.Md;
            var main = axis.MainLimit(constraints);
            var cross = axis.CrossLimit(constraints);
            Slot first;
            Slot second;
            double firstExtent;
            double secondExtent;
            if (double.IsFinite(main))
            {
                var available = Math.Max(0, main - gap);
                firstExtent = available * split.PrimaryFraction;
                secondExtent = available - firstExtent;
                first = Measure(split.Primary, axis.CreateConstraints(0, firstExtent, 0, cross), slot.Placement, 0);
                second = Measure(split.Secondary, axis.CreateConstraints(0, secondExtent, 0, cross), slot.Placement, 1);
            }
            else
            {
                first = Measure(split.Primary, axis.CreateConstraints(0, main, 0, cross), slot.Placement, 0);
                second = Measure(split.Secondary, axis.CreateConstraints(0, main, 0, cross), slot.Placement, 1);
                var fraction = (double)split.PrimaryFraction;
                var available = Math.Max(axis.Main(first.Size) / fraction, axis.Main(second.Size) / (1 - fraction));
                firstExtent = available * fraction;
                secondExtent = available - firstExtent;
            }

            axis.Place(second, firstExtent + gap, 0);
            slot.Size = constraints.Fit(axis.Extent(firstExtent + gap + secondExtent,
                Math.Max(axis.Cross(first.Size), axis.Cross(second.Size))));
            slot.Children = new[] { first, second };
        }

        private static int FlexOf(UiNode node) => node switch
        {
            UiExpanded expanded => expanded.Flex,
            UiSpacer spacer => spacer.Flex,
            _ => 0,
        };

        private static double CrossOffset(UiCrossAxisAlignment alignment, double spare) => alignment switch
        {
            UiCrossAxisAlignment.Center => spare / 2,
            UiCrossAxisAlignment.End => spare,
            _ => 0,
        };

        private static (double Start, double Gap) Distribute(UiMainAxisAlignment alignment, int count, double spare)
        {
            if (count == 0 || spare <= 0) return default;
            return alignment switch
            {
                UiMainAxisAlignment.Center => (spare / 2, 0),
                UiMainAxisAlignment.End => (spare, 0),
                UiMainAxisAlignment.SpaceBetween when count > 1 => (0, spare / (count - 1)),
                UiMainAxisAlignment.SpaceAround => (spare / (2 * count), spare / count),
                UiMainAxisAlignment.SpaceEvenly => (spare / (count + 1), spare / (count + 1)),
                _ => default,
            };
        }

        private readonly struct Axis
        {
            private readonly bool horizontal;
            internal Axis(bool horizontal) { this.horizontal = horizontal; }

            internal double Main(UiLayoutExtent size) => horizontal ? size.Width : size.Height;
            internal double Cross(UiLayoutExtent size) => horizontal ? size.Height : size.Width;
            internal double MainMinimum(UiLayoutConstraints constraints) => horizontal ? constraints.MinWidth : constraints.MinHeight;
            internal double MainLimit(UiLayoutConstraints constraints) => horizontal ? constraints.MaxWidth : constraints.MaxHeight;
            internal double CrossMinimum(UiLayoutConstraints constraints) => horizontal ? constraints.MinHeight : constraints.MinWidth;
            internal double CrossLimit(UiLayoutConstraints constraints) => horizontal ? constraints.MaxHeight : constraints.MaxWidth;
            internal UiLayoutExtent Extent(double main, double cross) => horizontal
                ? new UiLayoutExtent(main, cross) : new UiLayoutExtent(cross, main);
            internal UiLayoutConstraints CreateConstraints(double minMain, double maxMain, double minCross, double maxCross) => horizontal
                ? new UiLayoutConstraints(minMain, maxMain, minCross, maxCross)
                : new UiLayoutConstraints(minCross, maxCross, minMain, maxMain);

            internal void Place(Slot child, double main, double cross)
            {
                child.X = horizontal ? main : cross;
                child.Y = horizontal ? cross : main;
            }
        }
    }
}
