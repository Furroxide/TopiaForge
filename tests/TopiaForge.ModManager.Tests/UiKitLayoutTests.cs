using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitLayoutTests
    {
        internal static void Run()
        {
            UiOuterScrollTests.Run();
            PublicTypesValidateAndCopy();
            AxesAndSpacingKeepLegacyDefaults();
            LegacyNestedColumnsLeaveRoomForFollowingControls();
            AlternatingStretchBoundsIntrinsicWork();
            StretchRealignsWrapRuns();
            StretchedDividerPaintProtectsEarlierText();
            StretchReflowsSizedResponsiveChildren();
            StretchRetainsTheMainAxisBudgetForTallerWraps();
            SizedBoxesClampAndPreserveLooseAxes();
            FlexDistributesPhysicalPixelsInSourceOrder();
            WrapAndGridUseTheViewerWidth();
            PositionedChildrenAreBoundedByTheirStack();
            CardHeadersAndAncestorsParticipateInTraversal();
            OnlyActualNodesAndExactDepthAreCounted();
            DecorationFlowsFromTheEffectiveTheme();
            DividerUsesTheHighContrastLineWidth();
            PaintOrderProtectsTextAndHitAreas();
            OnlyOneFocusRingCanPaint();
            OffscreenPaintInsideScrollIsValidated();
            ScrollAperturesMustRevealProtectedGeometry();
            SiblingPaintersRespectTheFullScrollRange();
            ReadSettledLayoutWithoutAllocations();
        }

        private static void PublicTypesValidateAndCopy()
        {
            That((int)UiSpace.Xxxl == 48 && (int)UiMainAxisAlignment.SpaceEvenly == 5 && (int)UiCrossAxisAlignment.Stretch == 3,
                "The layout wire tokens keep their assigned integers.");
            var symmetric = new UiInsets(UiSpace.Sm, UiSpace.Md);
            That(symmetric.Left == UiSpace.Sm && symmetric.Right == UiSpace.Sm &&
                symmetric.Top == UiSpace.Md && symmetric.Bottom == UiSpace.Md, "Symmetric insets follow horizontal and vertical order.");
            Reject<ArgumentOutOfRangeException>(() => new UiInsets((UiSpace)6));
            Reject<ArgumentOutOfRangeException>(() => new UiFlexLayout(crossAxisAlignment: (UiCrossAxisAlignment)255));
            Reject<ArgumentOutOfRangeException>(() => new UiWrap(Array.Empty<UiNode>(), alignment: (UiMainAxisAlignment)255));
            Reject<ArgumentException>(() => new UiSizedBox());
            Reject<ArgumentOutOfRangeException>(() => new UiSizedBox(width: float.NaN));
            Reject<ArgumentOutOfRangeException>(() => new UiExpanded(new UiText("Heading"), flex: 0));
            Reject<ArgumentOutOfRangeException>(() => new UiSpacer(17));
            Reject<ArgumentOutOfRangeException>(() => new UiGrid(Array.Empty<UiNode>(), 47));
            Reject<ArgumentOutOfRangeException>(() => new UiGrid(Array.Empty<UiNode>(), 128, maximumColumns: 13));
            Reject<ArgumentOutOfRangeException>(() => new UiStack(Array.Empty<UiNode>(), 1, float.PositiveInfinity));
            Reject<ArgumentOutOfRangeException>(() => new UiPositioned(new UiText("Heading"), -1, 0));
            Reject<ArgumentOutOfRangeException>(() => new UiDivider(lineStyle: (UiLineStyle)8));
            Reject<ArgumentOutOfRangeException>(() => new UiBox(4097, 1));
            Reject<ArgumentException>(() => new UiStyleRef("Style"));
            Reject<ArgumentException>(() => new UiStyleRef("topiaforge.card"));
            That(new UiStyleRef("utility-card").Equals(new UiStyleRef("utility-card")), "Style ids use ordinal value equality.");

            var children = new[] { new UiText("Original", id: "original") };
            var wrap = new UiWrap(children, id: "wrap");
            children[0] = new UiText("Replacement");
            That(wrap.Id == "wrap" && wrap.Children[0].Id == "original", "A multi-child node copies its input exactly once.");
            Reject<ArgumentException>(() => new UiGrid(Enumerable.Repeat<UiNode>(new UiBox(0, 0), 257), 48));
        }

        private static void AxesAndSpacingKeepLegacyDefaults()
        {
            var child = new UiBox(20, 10, id: "child");
            var row = new UiRow(child);
            var column = new UiColumn(child);
            var explicitRow = new UiRow(new[] { child }, new UiFlexLayout(spacing: UiSpace.Xs), "root");
            var startRow = new UiRow(new[] { child }, new UiFlexLayout(crossAxisAlignment: UiCrossAxisAlignment.Start));
            That(row.Layout.CrossAxisAlignment == UiCrossAxisAlignment.Center && column.Layout.CrossAxisAlignment == UiCrossAxisAlignment.Stretch
                && explicitRow.Layout.CrossAxisAlignment == null && explicitRow.Id == "root", "Old overloads describe their historical defaults and new null remains authored null.");

            var frame = UiLayoutConstraints.Tight(100, 50);
            Rect(Find(Arrange(row, frame), "child").Bounds, 0, 20, 20, 10);
            Rect(Find(Arrange(explicitRow, frame), "child").Bounds, 0, 20, 20, 10);
            Rect(Find(Arrange(startRow, frame), "child").Bounds, 0, 0, 20, 10);
            Rect(Find(Arrange(column, frame), "child").Bounds, 0, 0, 100, 10);

            var pair = new UiNode[] { new UiSizedBox(width: 10, height: 10, id: "a"), new UiSizedBox(width: 10, height: 10, id: "b") };
            var around = Arrange(new UiRow(pair, new UiFlexLayout(mainAxisAlignment: UiMainAxisAlignment.SpaceAround)), frame);
            var even = Arrange(new UiRow(pair, new UiFlexLayout(mainAxisAlignment: UiMainAxisAlignment.SpaceEvenly)), frame);
            Rect(Find(around, "a").Bounds, 18, 20, 10, 10);
            Rect(Find(around, "b").Bounds, 72, 20, 10, 10);
            Rect(Find(even, "a").Bounds, 24, 20, 10, 10);
            Rect(Find(even, "b").Bounds, 66, 20, 10, 10);
            var between = new UiRow(new[] { pair[0] }, new UiFlexLayout(mainAxisAlignment: UiMainAxisAlignment.SpaceBetween));
            Rect(Find(Arrange(between, frame), "a").Bounds, 0, 20, 10, 10);
        }

        private static void SizedBoxesClampAndPreserveLooseAxes()
        {
            var oversized = new UiSizedBox(new UiText("ABCDEFGHI", id: "label"), width: 200, id: "sized");
            var allocated = Arrange(oversized, UiLayoutConstraints.Loose(64, 32));
            Rect(Find(allocated, "sized").Bounds, 0, 0, 64, 20);
            Rect(Find(allocated, "label").Bounds, 0, 0, 64, 20);
            var gap = Arrange(new UiSizedBox(width: 28, height: 0, id: "gap"), UiLayoutConstraints.Loose(99, 99));
            Rect(Find(gap, "gap").Bounds, 0, 0, 28, 0);

            LayoutFailure(UiLayoutFailure.ClippedContent, "/0/0", () => Arrange(
                new UiSizedBox(new UiText("Visible words"), width: 0, height: 40), UiLayoutConstraints.Loose(100, 100)));
            var inset = new UiPadding(new UiBox(20, 10, id: "padded"), new UiInsets(UiSpace.Sm, UiSpace.Xs));
            var padded = Arrange(inset, UiLayoutConstraints.Loose(60, 60));
            Rect(Find(padded, "padded").Bounds, 8, 4, 20, 10);
            That(padded.Size.Width == 36 && padded.Size.Height == 18, "Padding surrounds the measured size.");
        }

        private static void LegacyNestedColumnsLeaveRoomForFollowingControls()
        {
            var column = new UiColumn(new UiText("Name", id: "name"), new UiText("Detail", id: "detail"));
            var button = new UiButton("go", "Go", () => { });
            var viewport = UiLayoutConstraints.Tight(180, 120);
            var besideColumn = Arrange(new UiRow(column, button), viewport);
            That(Find(besideColumn, "name").Bounds.Width == 60 &&
                Find(besideColumn, "detail").Bounds.Width == 60,
                "A legacy column stretches its children to its measured width.");
            That(Find(besideColumn, "go").Bounds.X == 68,
                "The following button retains space beside a legacy column.");

            var besideCard = Arrange(new UiRow(new UiCard(column), button), viewport);
            That(Find(besideCard, "name").Bounds.Width == 60 && Find(besideCard, "go").Bounds.X == 100,
                "Card padding preserves the intrinsic column width and leaves room for the following control.");
        }

        private static void AlternatingStretchBoundsIntrinsicWork()
        {
            const int levels = 24;
            var layout = new UiFlexLayout(spacing: UiSpace.None, crossAxisAlignment: UiCrossAxisAlignment.Stretch);
            foreach (var wideSibling in new[] { false, true })
            {
                UiNode tree = new UiBox(2, 2);
                for (var depth = 1; depth <= levels; depth++)
                {
                    var side = wideSibling ? (float)depth * 2 : 1f;
                    var children = new[] { tree, new UiBox(side, side) };
                    tree = depth % 2 == 0 ? new UiColumn(children) : new UiRow(children, layout);
                }

                var calls = 0;
                UiLayoutEngine.Arrange(tree, UiLayoutConstraints.Loose(4096, 4096), (node, limits, disabled) =>
                {
                    That(++calls <= (levels + 1) * (levels + 1),
                        "A box has at most one intrinsic measurement per ancestor and one final measurement.");
                    return Measure(node, limits, disabled);
                });
            }
        }

        private static void StretchRealignsWrapRuns()
        {
            var wrap = new UiWrap(new UiNode[] { new UiBox(20, 10, id: "inside") },
                alignment: UiMainAxisAlignment.Center, id: "wrap");
            var column = new UiColumn(wrap, new UiBox(100, 10));
            var result = Arrange(column, UiLayoutConstraints.Loose(200, 200));
            That(Find(result, "wrap").Bounds.Width == 100 && Find(result, "inside").Bounds.X == 40,
                "A wrap realigns already measured runs against its assigned width.");
        }

        private static void StretchedDividerPaintProtectsEarlierText()
        {
            var divider = new UiSizedBox(new UiDivider(before: UiSpace.None, after: UiSpace.None), width: 10);
            var stretch = new UiColumn(divider, new UiBox(100, 10));
            var stack = new UiStack(new UiNode[]
            {
                new UiPositioned(new UiText("Protected"), 50, 0, width: 20, height: 1),
                new UiPositioned(stretch, 0, 0),
            }, 140, 40);
            var error = Reject<UiLayoutException>(() => UiLayoutEngine.Arrange(stack, UiLayoutConstraints.Tight(140, 40),
                (node, limits, disabled) => node is UiText
                    ? new UiLayoutLeafMeasurement(new UiLayoutExtent(20, 1), UiLayoutPaintKind.Protected)
                    : Measure(node, limits, disabled)));
            That(error.Failure == UiLayoutFailure.OccludedContent,
                "The full width of a stretched divider participates in paint validation.");
        }

        private static void StretchReflowsSizedResponsiveChildren()
        {
            var split = new UiSplitPane(new UiBox(10, 10), new UiBox(10, 10, id: "secondary"));
            var grid = new UiGrid(new[] { new UiBox(10, 10, id: "first-cell"), new UiBox(10, 10, id: "second-cell") },
                minimumCellWidth: 48, spacing: UiSpace.None);
            var column = new UiColumn(new UiSizedBox(split, width: 42), new UiSizedBox(grid, width: 48),
                new UiBox(128, 10));
            var result = Arrange(column, UiLayoutConstraints.Loose(200, 200));
            That(Find(result, "secondary").Bounds.X == 70,
                "A stretched pane reapplies its split fraction to the final size.");
            That(Find(result, "first-cell").Bounds.Y == Find(result, "second-cell").Bounds.Y &&
                Find(result, "second-cell").Bounds.X == 64,
                "A stretched grid reflows its cells at the final size.");
        }

        private static void StretchRetainsTheMainAxisBudgetForTallerWraps()
        {
            var wrap = new UiWrap(new UiNode[] { new UiBox(60, 10), new UiText("Wide"), new UiText("Tall") },
                spacing: UiSpace.None, runSpacing: UiSpace.None);
            var column = new UiColumn(new UiSizedBox(wrap, width: 100), new UiBox(110, 10, id: "after"));
            var row = new UiRow(new UiNode[] { column, new UiBox(10, 10) },
                new UiFlexLayout(crossAxisAlignment: UiCrossAxisAlignment.Stretch));
            var result = UiLayoutEngine.Arrange(row, UiLayoutConstraints.Loose(200, 200),
                (node, limits, disabled) => node is UiText text
                    ? new UiLayoutLeafMeasurement(new UiLayoutExtent(text.Text == "Wide" ? 50 : 40, 40),
                        UiLayoutPaintKind.Protected)
                    : Measure(node, limits, disabled));
            That(Find(result, "after").Bounds.Y == 88,
                "The final wrap can grow taller within the original viewport and moves the next child down.");
        }

        private static void FlexDistributesPhysicalPixelsInSourceOrder()
        {
            var row = new UiRow(new UiNode[]
            {
                new UiSizedBox(width: 25, height: 8, id: "fixed"),
                new UiExpanded(new UiBox(1, 8), id: "first"),
                new UiSpacer(2, "spacer"),
                new UiExpanded(new UiBox(1, 8), id: "third"),
            }, new UiFlexLayout(spacing: UiSpace.None));
            var result = Arrange(row, UiLayoutConstraints.Tight(99.5, 8), scale: 2);
            Rect(Find(result, "first").Bounds, 25, 0, 19, 8);
            Rect(Find(result, "spacer").Bounds, 44, 4, 37, 0);
            Rect(Find(result, "third").Bounds, 81, 0, 18.5, 8);

            LayoutFailure(UiLayoutFailure.InvalidLayout, "/0/0", () => Arrange(
                new UiWrap(new UiNode[] { new UiSpacer() }), UiLayoutConstraints.Loose(100, 100)));
            LayoutFailure(UiLayoutFailure.InvalidLayout, "/0/0/0", () => Arrange(
                new UiScroll(new UiColumn(new UiExpanded(new UiText("Read me")))), UiLayoutConstraints.Loose(100, 100)));
            LayoutFailure(UiLayoutFailure.ClippedContent, "/0/1/0", () => Arrange(
                new UiRow(new UiSizedBox(width: 90, height: 20), new UiExpanded(new UiButton("go", "Go", () => { }))),
                UiLayoutConstraints.Tight(90, 60)));
        }

        private static void WrapAndGridUseTheViewerWidth()
        {
            var wrapping = new UiWrap(new UiNode[] { new UiBox(55, 10, id: "one"), new UiBox(60, 12, id: "two"),
                new UiText("Longlineplus", id: "three") }, runSpacing: UiSpace.Xs);
            var wrapped = Arrange(wrapping, UiLayoutConstraints.Tight(96, 72));
            Rect(Find(wrapped, "one").Bounds, 0, 0, 55, 10);
            Rect(Find(wrapped, "two").Bounds, 0, 14, 60, 12);
            Rect(Find(wrapped, "three").Bounds, 0, 30, 96, 20);

            var grid = new UiGrid(new[] { new UiBox(20, 10, id: "one"), new UiBox(40, 16, id: "two"),
                new UiBox(40, 12, id: "three") }, minimumCellWidth: 64, runSpacing: UiSpace.Xs);
            var ordinary = Arrange(grid, UiLayoutConstraints.Loose(180, 200));
            Rect(Find(ordinary, "two").Bounds, 94, 0, 86, 16);
            Rect(Find(ordinary, "three").Bounds, 0, 20, 86, 12);
            var doubled = Arrange(grid, UiLayoutConstraints.Loose(90, 100), scale: 2);
            Rect(Find(doubled, "two").Bounds, 0, 14, 90, 16);
            Rect(Find(doubled, "three").Bounds, 0, 34, 90, 12);
        }

        private static void PositionedChildrenAreBoundedByTheirStack()
        {
            var stack = new UiStack(new[] { new UiPositioned(new UiBox(40, 5, id: "box"), 70, 16, id: "place") }, 100, 60);
            var result = Arrange(stack, UiLayoutConstraints.Loose(180, 200));
            Rect(Find(result, "place").Bounds, 70, 16, 30, 5);
            Rect(Find(result, "box").Bounds, 70, 16, 30, 5);
            LayoutFailure(UiLayoutFailure.InvalidLayout, "/0/0", () => Arrange(
                new UiStack(new[] { new UiPositioned(new UiBox(20, 5), 70, 16, width: 31) }, 100, 60),
                UiLayoutConstraints.Loose(180, 200)));
            LayoutFailure(UiLayoutFailure.InvalidLayout, "/0", () => Arrange(
                new UiPositioned(new UiBox(20, 5), 10, 10), UiLayoutConstraints.Loose(180, 200)));
        }

        private static void CardHeadersAndAncestorsParticipateInTraversal()
        {
            var header = new UiText("Intro", id: "header");
            var action = new UiButton("continue", "Go", () => { });
            var card = new UiCard(new UiPadding(action, default), header, selected: true, disabled: true, id: "card");
            var result = Arrange(card, UiLayoutConstraints.Loose(200, 200));
            That(UiLayoutTree.ContainsInteractive(card), "A disabled card does not permit controls onto a HUD.");
            That(UiLayoutTree.TryFind(card, "continue", out var found, out var disabled) && ReferenceEquals(found, action) && disabled,
                "Lookup reports ancestor disablement for callback routing.");
            That(Find(result, "continue").Disabled && Find(result, "header").Disabled, "The arranged card inherits the disabled state through every child.");
            Rect(Find(result, "header").Bounds, 16, 16, 50, 10);
            Rect(Find(result, "continue").Bounds, 16, 34, 40, 40);
            LayoutFailure(UiLayoutFailure.InvalidLayout, "/0/1", () => UiLayoutTree.Validate(
                new UiCard(new UiText("Duplicate", id: "header"), header)));
        }

        private static void OnlyActualNodesAndExactDepthAreCounted()
        {
            UiLayoutTree.Validate(Depth(31));
            UiLayoutTree.Validate(Depth(32));
            var tooDeep = Reject<UiLayoutException>(() => UiLayoutTree.Validate(Depth(33)));
            That(tooDeep.Path.Count(character => character == '/') == 33, "The root is level one and the diagnostic identifies the first excess node.");
            UiLayoutTree.Validate(NodeCount(4095));
            UiLayoutTree.Validate(NodeCount(4096));
            That(Reject<UiLayoutException>(() => UiLayoutTree.Validate(NodeCount(4097))).Failure == UiLayoutFailure.InvalidLayout,
                "Repeated idless node instances count at every occurrence.");
            LayoutFailure(UiLayoutFailure.InvalidLayout, "/0/0", () => UiLayoutTree.Validate(
                new UiPadding(new UiText(new string('x', 4097)), default)));
        }

        private static UiNode Depth(int levels)
        {
            UiNode node = new UiText("Leaf");
            for (var number = 1; number < levels; number++) node = new UiPadding(node, default);
            return node;
        }

        private static UiNode NodeCount(int count)
        {
            var branches = new List<UiNode>();
            var reused = new UiBox(0, 0);
            while (count > 1)
            {
                var length = Math.Min(256, count - 2);
                branches.Add(new UiColumn(Enumerable.Repeat<UiNode>(reused, length)));
                count -= length + 1;
            }
            return new UiColumn(branches);
        }

        private static UiLayoutResult Arrange(UiNode root, UiLayoutConstraints constraints, double scale = 1, bool highContrast = false) =>
            UiLayoutEngine.Arrange(root, constraints, Measure, scale, highContrast);

        private static UiLayoutLeafMeasurement Measure(UiNode node, UiLayoutConstraints constraints, bool disabled)
        {
            if (node is UiText text)
            {
                var intrinsic = text.Text.Length * 10d;
                var width = constraints.MaxWidth > 0 ? Math.Min(intrinsic, constraints.MaxWidth) : intrinsic;
                var lines = width > 0 ? Math.Ceiling(intrinsic / width) : 0;
                return new UiLayoutLeafMeasurement(new UiLayoutExtent(width, lines * 10), UiLayoutPaintKind.Protected,
                    scrollLineHeight: 10);
            }
            if (node is UiButton)
                return new UiLayoutLeafMeasurement(new UiLayoutExtent(40, 40), UiLayoutPaintKind.Protected,
                    node.Id == "shadow" ? new UiLayoutOutsets(0, 0, 4, 0) : default,
                    node.Id!.StartsWith("focus-", StringComparison.Ordinal) ? new UiLayoutOutsets(5, 5, 5, 5) : default);
            if (node.Id == "image")
                return new UiLayoutLeafMeasurement(new UiLayoutExtent(50, 50), UiLayoutPaintKind.Image);
            return new UiLayoutLeafMeasurement(default, UiLayoutPaintKind.None);
        }

        private static UiLayoutPlacement Find(UiLayoutResult result, string id) => result.Placements.Single(placement => placement.Node.Id == id);

        private static void Rect(UiLayoutRect rect, double x, double y, double width, double height) =>
            That(rect.X == x && rect.Y == y && rect.Width == width && rect.Height == height,
                FormattableString.Invariant($"Expected ({x}, {y}, {width}, {height}). Measured ({rect.X}, {rect.Y}, {rect.Width}, {rect.Height})."));

        private static void LayoutFailure(UiLayoutFailure kind, string path, Action action)
        {
            var failure = Reject<UiLayoutException>(action);
            That(failure.Failure == kind && failure.Path == path, $"Expected {kind} at {path}, got {failure.Failure} at {failure.Path}.");
        }

        private static T Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T failure) { return failure; }
            throw new InvalidOperationException("The invalid layout was accepted.");
        }

        private static void That(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
