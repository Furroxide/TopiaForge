using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Measures and arranges the safe composition before a renderer commits its native elements.</summary>
    internal sealed partial class UiLayoutEngine
    {
        private readonly UiLayoutMeasureLeaf measureLeaf;
        private readonly double pixelsPerUnit;
        private readonly bool highContrast;
        private readonly List<UiLayoutPlacement> placements = new List<UiLayoutPlacement>();

        private UiLayoutEngine(UiLayoutMeasureLeaf measureLeaf, double pixelsPerUnit, bool highContrast)
        {
            this.measureLeaf = measureLeaf;
            this.pixelsPerUnit = pixelsPerUnit;
            this.highContrast = highContrast;
        }

        internal static UiLayoutResult Arrange(UiNode? root, UiLayoutConstraints constraints, UiLayoutMeasureLeaf measureLeaf,
            double pixelsPerUnit = 1, bool highContrast = false)
        {
            if (measureLeaf == null) throw new ArgumentNullException(nameof(measureLeaf));
            if (!double.IsFinite(pixelsPerUnit) || pixelsPerUnit <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerUnit));
            if (root == null) return UiLayoutResult.Empty;

            UiLayoutTree.Validate(root);
            var engine = new UiLayoutEngine(measureLeaf, pixelsPerUnit, highContrast);
            var measured = engine.Measure(root, constraints, null, 0);
            var clip = new UiLayoutRect(0, 0, constraints.MaxWidth, constraints.MaxHeight);
            engine.Place(measured, clip, constraints.OuterScroll);
            engine.ValidatePaint();
            return new UiLayoutResult(measured.Size, engine.placements.AsReadOnly());
        }

        private Slot Measure(UiNode node, UiLayoutConstraints constraints, UiLayoutPlacement? parent, int index)
        {
            var slot = new Slot(node, parent, index);
            switch (node)
            {
                case UiRow row: MeasureFlex(slot, row.Layout, constraints, horizontal: true); break;
                case UiColumn column: MeasureFlex(slot, column.Layout, constraints, horizontal: false); break;
                case UiWrap wrap: MeasureWrap(slot, wrap, constraints); break;
                case UiGrid grid: MeasureGrid(slot, grid, constraints); break;
                case UiStack stack: MeasureStack(slot, stack, constraints); break;
                case UiPadding padding: MeasurePadding(slot, padding, constraints); break;
                case UiSizedBox sized: MeasureSized(slot, sized, constraints); break;
                case UiExpanded expanded: MeasureSingle(slot, expanded.Child, constraints); break;
                case UiSpacer: slot.Size = constraints.Fit(default); break;
                case UiCard card: MeasureCard(slot, card, constraints); break;
                case UiBox box: MeasureBox(slot, box, constraints); break;
                case UiDivider divider: MeasureDivider(slot, divider, constraints); break;
                case UiScroll scroll: MeasureScroll(slot, scroll, constraints); break;
                case UiSplitPane split: MeasureSplit(slot, split, constraints); break;
                case UiTabs tabs: MeasureTabs(slot, tabs, constraints); break;
                case UiTooltip tooltip: MeasureTooltip(slot, tooltip, constraints); break;
                case UiPositioned: throw LayoutError(slot, "Positioned requires a measured Stack parent.");
                default: MeasureLeaf(slot, constraints); break;
            }

            if (!double.IsFinite(slot.Size.Width) || !double.IsFinite(slot.Size.Height))
                throw LayoutError(slot, "The layout exceeds finite measured dimensions.");
            return slot;
        }

        private void MeasureLeaf(Slot slot, UiLayoutConstraints constraints)
        {
            var metrics = slot.Node is UiGraphCanvas graph
                ? MeasureGraph(graph, constraints)
                : ReadMetrics(slot, constraints);
            if (UiLayoutTree.IsInteractive(slot.Node) || slot.Node is UiText text && !string.IsNullOrWhiteSpace(text.Text) ||
                slot.Node is UiCountdown || slot.Node is UiProgress progress && progress.Label.Length > 0)
                slot.PaintKind = UiLayoutPaintKind.Protected;
            else
                slot.PaintKind = metrics.PaintKind;

            var fitted = constraints.Fit(metrics.Size);
            if ((slot.PaintKind == UiLayoutPaintKind.Protected || slot.Node is UiImage) &&
                (metrics.Size.Width > constraints.MaxWidth || metrics.Size.Height > constraints.MaxHeight ||
                metrics.Size.Width <= 0 || metrics.Size.Height <= 0 || fitted.Width <= 0 || fitted.Height <= 0))
                throw Clipped(slot);
            slot.Size = fitted;
            slot.PaintOutsets = metrics.PaintOutsets;
            slot.FocusOutsets = metrics.FocusOutsets;
            slot.LocalPaintBounds = metrics.LocalPaintBounds;
            slot.ScrollLineHeight = slot.Node is UiText ? metrics.ScrollLineHeight : null;
        }

        private static UiLayoutLeafMeasurement MeasureGraph(UiGraphCanvas graph, UiLayoutConstraints constraints)
        {
            // The graph clips its own nodes, ports, and focus rings. Only the viewport participates in surface layout.
            const double minimumWidth = 40d;
            var width = double.IsFinite(constraints.MaxWidth) ? Math.Max(minimumWidth, constraints.MaxWidth) : minimumWidth;
            return new UiLayoutLeafMeasurement(new UiLayoutExtent(width, graph.Height), UiLayoutPaintKind.None);
        }

        private UiLayoutLeafMeasurement ReadMetrics(Slot slot, UiLayoutConstraints constraints)
        {
            UiLayoutLeafMeasurement result;
            try { result = measureLeaf(slot.Node, constraints, slot.Placement.Disabled); }
            catch (ArgumentException error) when (error is not UiLayoutException)
            {
                throw LayoutError(slot, error.Message);
            }
            if (!double.IsFinite(result.Size.Width) || !double.IsFinite(result.Size.Height) || result.Size.Width < 0 || result.Size.Height < 0)
                throw LayoutError(slot, "The leaf returned invalid measured dimensions.");
            if (result.ScrollLineHeight is { } lineHeight && (!double.IsFinite(lineHeight) || lineHeight <= 0))
                throw LayoutError(slot, "The leaf returned an invalid measured line height.");
            return result;
        }

        private void MeasureSingle(Slot slot, UiNode node, UiLayoutConstraints constraints)
        {
            var child = Measure(node, constraints, slot.Placement, 0);
            slot.Children = new[] { child };
            slot.Size = child.Size;
        }

        private void MeasureTooltip(Slot slot, UiTooltip tooltip, UiLayoutConstraints constraints)
        {
            MeasureSingle(slot, tooltip.Child, constraints);
            if (UiLayoutTree.ContainsInteractive(tooltip.Child)) return;
            const double minimumTarget = 40d;
            if (constraints.MaxWidth < minimumTarget || constraints.MaxHeight < minimumTarget) throw Clipped(slot);
            slot.Size = constraints.Fit(new UiLayoutExtent(Math.Max(minimumTarget, slot.Size.Width),
                Math.Max(minimumTarget, slot.Size.Height)));
        }

        private void MeasurePadding(Slot slot, UiPadding node, UiLayoutConstraints constraints)
        {
            var padding = node.Padding;
            var child = Measure(node.Child, constraints.Inset(padding.Horizontal, padding.Vertical), slot.Placement, 0);
            child.X = (int)padding.Left;
            child.Y = (int)padding.Top;
            slot.Children = new[] { child };
            slot.Size = constraints.Fit(new UiLayoutExtent(child.Size.Width + padding.Horizontal, child.Size.Height + padding.Vertical));
        }

        private void MeasureSized(Slot slot, UiSizedBox node, UiLayoutConstraints constraints)
        {
            var childConstraints = Prefer(constraints, node.Width, node.Height);
            var inner = default(UiLayoutExtent);
            if (node.Child != null)
            {
                var child = Measure(node.Child, childConstraints, slot.Placement, 0);
                slot.Children = new[] { child };
                inner = child.Size;
            }
            slot.Size = constraints.Fit(childConstraints.Fit(inner));
        }

        private void MeasureBox(Slot slot, UiBox node, UiLayoutConstraints constraints)
        {
            slot.Size = constraints.Fit(new UiLayoutExtent(node.Width, node.Height));
            SetDecoration(slot);
        }

        private void MeasureDivider(Slot slot, UiDivider node, UiLayoutConstraints constraints)
        {
            var thickness = highContrast ? 3d : 2d;
            var width = double.IsFinite(constraints.MaxWidth) ? constraints.MaxWidth : constraints.MinWidth;
            slot.Size = constraints.Fit(new UiLayoutExtent(width, (int)node.Before + thickness + (int)node.After));
            slot.PaintKind = UiLayoutPaintKind.Painter;
            slot.LocalPaintBounds = new UiLayoutRect(0, (int)node.Before, slot.Size.Width,
                Math.Max(0, Math.Min(thickness, slot.Size.Height - (int)node.Before)));
        }

        private void MeasureCard(Slot slot, UiCard card, UiLayoutConstraints constraints)
        {
            var padding = card.Padding ?? new UiInsets(UiSpace.Lg);
            var inner = constraints.Inset(padding.Horizontal, padding.Vertical);
            var content = UiNodeChildren.Of(card);
            var height = 0d;
            var width = inner.MinWidth;
            var children = new Slot[content.Count];
            for (var index = 0; index < children.Length; index++)
            {
                if (index > 0) height += (int)UiSpace.Sm;
                var child = Measure(content[index], UiLayoutConstraints.Loose(inner.MaxWidth, Math.Max(0, inner.MaxHeight - height)),
                    slot.Placement, index);
                child.X = (int)padding.Left;
                child.Y = (int)padding.Top + height;
                children[index] = child;
                height += child.Size.Height;
                width = Math.Max(width, child.Size.Width);
            }
            slot.Children = children;
            slot.Size = constraints.Fit(new UiLayoutExtent(width + padding.Horizontal, height + padding.Vertical));
            SetDecoration(slot);
        }

        private void SetDecoration(Slot slot)
        {
            slot.PaintKind = slot.Size.Width > 0 && slot.Size.Height > 0 ? UiLayoutPaintKind.Painter : UiLayoutPaintKind.None;
            slot.PaintOutsets = ReadMetrics(slot, UiLayoutConstraints.Tight(slot.Size.Width, slot.Size.Height)).PaintOutsets;
        }

        private void MeasureScroll(Slot slot, UiScroll node, UiLayoutConstraints constraints)
        {
            var child = Measure(node.Content, UiLayoutConstraints.Loose(constraints.MaxWidth, double.PositiveInfinity), slot.Placement, 0);
            slot.Children = new[] { child };
            slot.Size = constraints.Fit(new UiLayoutExtent(child.Size.Width, node.Height));
            slot.ClipsChildren = true;
        }

        private void MeasureStack(Slot slot, UiStack node, UiLayoutConstraints constraints)
        {
            slot.Size = constraints.Fit(new UiLayoutExtent(node.Width, node.Height));
            var children = new Slot[node.Children.Count];
            for (var index = 0; index < children.Length; index++)
            {
                var child = node.Children[index];
                children[index] = child is UiPositioned positioned
                    ? MeasurePositioned(positioned, slot, index)
                    : Measure(child, UiLayoutConstraints.Loose(slot.Size.Width, slot.Size.Height), slot.Placement, index);
            }
            slot.Children = children;
            slot.ClipsChildren = true;
        }

        private Slot MeasurePositioned(UiPositioned node, Slot stack, int index)
        {
            var slot = new Slot(node, stack.Placement, index) { X = node.Left, Y = node.Top };
            var width = stack.Size.Width - node.Left;
            var height = stack.Size.Height - node.Top;
            if (width < 0 || height < 0 || node.Width.HasValue && node.Width.Value > width || node.Height.HasValue && node.Height.Value > height)
                throw LayoutError(slot, "The position and explicit dimensions must fit the remaining Stack extent.");

            MeasureSingle(slot, node.Child, Prefer(UiLayoutConstraints.Loose(width, height), node.Width, node.Height));
            return slot;
        }

        private static UiLayoutConstraints Prefer(UiLayoutConstraints constraints, float? width, float? height)
        {
            var x = width.HasValue ? Math.Clamp(width.Value, constraints.MinWidth, constraints.MaxWidth) : 0d;
            var y = height.HasValue ? Math.Clamp(height.Value, constraints.MinHeight, constraints.MaxHeight) : 0d;
            return new UiLayoutConstraints(x, width.HasValue ? x : constraints.MaxWidth, y, height.HasValue ? y : constraints.MaxHeight);
        }

        private static UiLayoutException LayoutError(Slot slot, string message) =>
            new UiLayoutException(UiLayoutFailure.InvalidLayout, slot.Placement.Path, message);

        private static UiLayoutException Clipped(Slot slot) =>
            new UiLayoutException(UiLayoutFailure.ClippedContent, slot.Placement.Path, "The measured text or control cannot fit its allocation.");

        private sealed class Slot
        {
            internal Slot(UiNode node, UiLayoutPlacement? parent, int index)
            {
                Placement = new UiLayoutPlacement(node, parent, index, parent?.Disabled == true || UiLayoutTree.IsDisabled(node));
            }

            internal UiNode Node => Placement.Node;
            internal UiLayoutPlacement Placement { get; }
            internal UiLayoutExtent Size { get; set; }
            internal double X { get; set; }
            internal double Y { get; set; }
            internal Slot[] Children { get; set; } = Array.Empty<Slot>();
            internal UiLayoutPaintKind PaintKind { get; set; }
            internal UiLayoutOutsets PaintOutsets { get; set; }
            internal UiLayoutOutsets FocusOutsets { get; set; }
            internal double? ScrollLineHeight { get; set; }
            internal UiLayoutRect? LocalPaintBounds { get; set; }
            internal bool ClipsChildren { get; set; }
        }
    }
}
