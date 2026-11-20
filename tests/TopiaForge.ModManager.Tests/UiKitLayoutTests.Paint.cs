using System;
using System.Text;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitLayoutTests
    {
        private static void DecorationFlowsFromTheEffectiveTheme()
        {
            const string json = "{\"schema\":1,\"id\":\"field\",\"components\":{"
                + "\"box\":{\"radius\":\"small\",\"border\":\"thin\",\"shadow\":\"small\"}},\"styles\":["
                + "{\"id\":\"paper-box\",\"target\":\"box\",\"radius\":\"pill\",\"border\":\"standard\",\"shadow\":\"card\",\"fill\":null},"
                + "{\"id\":\"flat-card\",\"target\":\"card\",\"radius\":\"none\",\"border\":\"none\",\"shadow\":\"none\"}]}";
            var result = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(json));
            if (!result.TryGetValue(out var theme)) throw new InvalidOperationException(result.ErrorMessage);
            var bounds = new UiLayoutExtent(48, 20);
            var style = new UiStyleRef("paper-box");
            var plain = UiLayoutDecoration.Resolve(new UiBox(48, 20, UiPaintRole.Primary), theme, theme, bounds, highContrast: false);
            That(plain.Fill == UiPaintRole.Primary && plain.BorderWidth == 1 && plain.ShadowX == -3 && plain.ShadowY == 4,
                "The component defaults inherit appearance and preserve the unstyled Box fill.");
            var box = new UiBox(48, 20, UiPaintRole.Primary, style);
            var styled = UiLayoutDecoration.Resolve(box, theme, theme, bounds, highContrast: false);
            That(styled.StyleId == style.Id && styled.Fill == UiPaintRole.Surface && styled.CornerRadius == 10 &&
                styled.BorderWidth == 2 && styled.ShadowOutsets.Left == 4 && styled.ShadowOutsets.Bottom == 8,
                "A named style overrides the component triple, and a null named Box fill resolves to Surface.");
            var high = UiLayoutDecoration.Resolve(box, theme, theme, bounds, highContrast: true);
            That(high.BorderWidth == 3 && high.ShadowX == 0 && high.ShadowY == 0, "High contrast raises the border and removes shadow extents.");
            var card = new UiCard(new UiText("Checklist"), style: new UiStyleRef("flat-card"), selected: true);
            That(UiLayoutDecoration.Resolve(card, theme, theme, bounds, highContrast: false).BorderWidth == 2,
                "A selected card retains its selection ring with a borderless named style.");
            Reject<ArgumentException>(() => UiLayoutDecoration.Resolve(new UiCard(new UiText("Checklist"), style: style),
                theme, theme, bounds, highContrast: false));
            var fallback = UiLayoutDecoration.Resolve(box, UiThemePackParser.Default, theme, bounds, highContrast: false);
            That(fallback.StyleId == null && fallback.Fill == UiPaintRole.Primary,
                "A pending or missing resolved theme uses its component default without applying a foreign named style.");
        }

        private static void DividerUsesTheHighContrastLineWidth()
        {
            var divider = new UiDivider(UiLineStyle.Dashed, UiSpace.Xs, UiSpace.Md, "rule");
            var constraints = UiLayoutConstraints.Loose(100, 60);
            var ordinary = Find(Arrange(divider, constraints), "rule");
            Rect(ordinary.Bounds, 0, 0, 100, 18);
            Rect(ordinary.PaintBounds, 0, 4, 100, 2);
            var highContrast = Find(Arrange(divider, constraints, highContrast: true), "rule");
            Rect(highContrast.Bounds, 0, 0, 100, 19);
            Rect(highContrast.PaintBounds, 0, 4, 100, 3);
        }

        private static void PaintOrderProtectsTextAndHitAreas()
        {
            var label = new UiText("A", id: "label");
            var background = new UiBox(50, 50, UiPaintRole.Danger, id: "background");
            var imageGeometry = new UiProgress(0.5f, id: "image");
            var bounds = UiLayoutConstraints.Loose(200, 200);

            var plain = Arrange(new UiStack(new UiNode[] { background, label }, 120, 60), bounds);
            That(Find(plain, "label").RequiresBackplate, "An independent semantic fill behind a label needs contextual backing.");
            var partialBackground = Arrange(new UiStack(new UiNode[] { new UiPositioned(background, 5, 0), label },
                120, 60), bounds);
            That(Find(partialBackground, "label").RequiresBackplate, "A partially overlapping Box also needs opaque backing.");
            var backed = Arrange(new UiStack(new UiNode[] { imageGeometry, label }, 120, 60), bounds);
            That(Find(backed, "label").RequiresBackplate, "A later protected label needs a semantic backplate over arbitrary pixels.");
            LayoutFailure(UiLayoutFailure.OccludedContent, "/0/1", () => Arrange(
                new UiStack(new UiNode[] { label, imageGeometry }, 120, 60), bounds));
            LayoutFailure(UiLayoutFailure.OccludedContent, "/0/1", () => Arrange(
                new UiStack(new UiNode[] { label, background }, 120, 60), bounds));

            var adjacent = new UiStack(new UiNode[] { label, new UiPositioned(background, 10, 0) }, 120, 60);
            _ = Arrange(adjacent, bounds);
            var offsetBackground = new UiStack(new UiNode[] { new UiPositioned(background, 10, 0), label }, 120, 60);
            That(!Find(Arrange(offsetBackground, bounds), "label").RequiresBackplate, "Touching edges leave the label's background unchanged.");
            var card = new UiCard(label);
            That(!Find(Arrange(card, bounds), "label").RequiresBackplate, "A card establishes its descendants' backdrop without a second fill.");
            LayoutFailure(UiLayoutFailure.OccludedContent, "/0/1/0", () => Arrange(
                new UiStack(new UiNode[] { label, new UiPositioned(background, 9.5f, 0) }, 120, 60), bounds));

            var button = new UiButton("shadow", "Go", () => { });
            var nearShadow = new UiPositioned(background, 41, 8);
            LayoutFailure(UiLayoutFailure.OccludedContent, "/0/1/0", () => Arrange(
                new UiStack(new UiNode[] { button, nearShadow }, 140, 60), bounds));
            LayoutFailure(UiLayoutFailure.OccludedContent, "/0/1/0", () => Arrange(
                new UiStack(new UiNode[] { button, new UiPositioned(new UiButton("other", "Stop", () => { }), 16, 0) },
                    140, 60), bounds));

            var nested = new UiStack(new UiNode[]
            {
                new UiPositioned(new UiStack(new[] { new UiPositioned(label, 20, 0) }, 45, 20), 10, 10),
                new UiPositioned(new UiBox(10, 10), 32, 12),
            }, 80, 50);
            LayoutFailure(UiLayoutFailure.OccludedContent, "/0/1/0", () => Arrange(nested, bounds));

            var clippedOutset = new UiStack(new UiNode[]
            {
                new UiStack(new[] { button }, 40, 40),
                new UiPositioned(new UiBox(10, 10), 40, 0),
            }, 100, 80);
            var allowed = Arrange(clippedOutset, bounds);
            Rect(Find(allowed, "shadow").PaintBounds, 0, 0, 40, 40);
        }

        private static void OnlyOneFocusRingCanPaint()
        {
            var first = new UiButton("focus-first", "First", () => { });
            var second = new UiButton("focus-second", "Second", () => { });
            var bounds = UiLayoutConstraints.Loose(120, 120);
            var spaced = Arrange(new UiColumn(first, second), bounds, highContrast: true);
            That(Find(spaced, "focus-first").PaintBounds.Intersects(Find(spaced, "focus-second").PaintBounds),
                "The fixture overlaps only the two potential focus rings at the default spacing.");
            var close = new UiColumn(new[] { first, second }, new UiFlexLayout(UiSpace.Xs));
            LayoutFailure(UiLayoutFailure.OccludedContent, "/0/1", () => Arrange(close, bounds, highContrast: true));
        }

        private static void OffscreenPaintInsideScrollIsValidated()
        {
            var hiddenCollision = new UiStack(new UiNode[] { new UiText("Shelf"), new UiBox(80, 24) }, 100, 40);
            var content = new UiColumn(new UiSizedBox(height: 110), hiddenCollision);
            LayoutFailure(UiLayoutFailure.OccludedContent, "/0/0/1/1", () => Arrange(new UiScroll(content, 80),
                UiLayoutConstraints.Loose(120, 110)));

            var longContent = new UiColumn(new UiSizedBox(height: 110), new UiText("Shelf", id: "offscreen"));
            var scroll = Arrange(new UiScroll(longContent, 80), UiLayoutConstraints.Loose(120, 110));
            var below = Find(scroll, "offscreen");
            That(!below.PaintBounds.HasArea && below.Bounds.Y > 80, "The viewport crops drawing but preserves reachable scroll content.");
        }

        private static void SiblingPaintersRespectTheFullScrollRange()
        {
            var row = new UiText("Shelf", id: "revealed-row");
            var scroll = new UiScroll(new UiColumn(new UiSizedBox(height: 100), row), 80);
            var bounds = UiLayoutConstraints.Loose(120, 180);
            var clear = Arrange(new UiStack(new UiNode[] { scroll, new UiPositioned(new UiBox(20, 12), 0, 106) }, 120, 150), bounds);
            That(!Find(clear, "revealed-row").PaintBounds.HasArea, "The row begins below the visible scroll viewport.");
            _ = Arrange(new UiStack(new UiNode[] { scroll, new UiPositioned(new UiBox(20, 12), 0, 56) }, 120, 150), bounds);
            LayoutFailure(UiLayoutFailure.OccludedContent, "/0/1/0", () => Arrange(
                new UiStack(new UiNode[] { scroll, new UiPositioned(new UiBox(20, 12), 0, 69) }, 120, 150), bounds));

            // Both rows could occupy the same screen rectangle at different offsets of the outer scroll.
            // They always remain disjoint in the shared outer canvas.
            var first = new UiScroll(new UiColumn(new UiSizedBox(height: 100), new UiText("First")), 80);
            var second = new UiScroll(new UiColumn(new UiSizedBox(height: 100), new UiText("Second")), 80);
            _ = Arrange(new UiScroll(new UiColumn(first, second), 80), bounds);

            var rows = new UiColumn(new UiSizedBox(height: 50), new UiText("Line 1"), new UiText("Line 2"), new UiSizedBox(height: 110));
            _ = Arrange(new UiScroll(rows, 80), bounds);
        }

        private static void ScrollAperturesMustRevealProtectedGeometry()
        {
            var limits = UiLayoutConstraints.Loose(210, 200);
            var target = new UiButton("scroll-target", "Go", () => { });
            var scroll = new UiScroll(target, 80);
            LayoutFailure(UiLayoutFailure.ClippedContent, "/0/0/0", () => Arrange(
                new UiSizedBox(scroll, width: 180, height: 30), limits));
            var exactly = Arrange(new UiSizedBox(scroll, width: 180, height: 40), limits);
            That(Find(exactly, "scroll-target").PaintBounds.Height == 40, "A stationary 40-unit hit target fits a 40-unit aperture exactly.");

            var focus = new UiPadding(new UiButton("focus-scroll", "Go", () => { }), new UiInsets(UiSpace.Sm));
            LayoutFailure(UiLayoutFailure.ClippedContent, "/0/0/0/0", () => Arrange(
                new UiSizedBox(new UiScroll(focus, 80), width: 180, height: 49), limits));
            _ = Arrange(new UiSizedBox(new UiScroll(focus, 80), width: 180, height: 50), limits);

            var inner = new UiScroll(new UiColumn(new UiSizedBox(height: 90), target), 80);
            var nested = new UiScroll(new UiStack(new UiNode[] { inner }, 180, 80), 80);
            LayoutFailure(UiLayoutFailure.ClippedContent, "/0/0/0/0/0/1", () => Arrange(
                new UiSizedBox(nested, width: 180, height: 30), limits));
            var admitted = Arrange(new UiSizedBox(nested, width: 180, height: 40), limits);
            That(!Find(admitted, "scroll-target").PaintBounds.HasArea,
                "Independent offsets can reveal the offscreen hit through the nested fixed Stack and both scrolls.");

            var paragraph = new UiText(new string('L', 90), id: "scroll-words");
            var passive = new UiScroll(paragraph, 80);
            var lines = Arrange(new UiSizedBox(passive, width: 180, height: 10), limits);
            That(Find(lines, "scroll-words").Bounds.Height > 10, "A passive transcript remains scrollable one full line at a time.");
            LayoutFailure(UiLayoutFailure.ClippedContent, "/0/0/0", () => Arrange(
                new UiSizedBox(passive, width: 180, height: 9), limits));
        }

        private static void ReadSettledLayoutWithoutAllocations()
        {
            var arranged = Arrange(new UiRow(new UiText("Supplies"), new UiSpacer(1)), UiLayoutConstraints.Tight(100, 24));
            var placements = arranged.Placements;
            var last = placements[placements.Count - 1];
            for (var index = 0; index < 4096; index++) _ = arranged.Placements[index % placements.Count].Bounds;
            var consistent = true;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 4096; index++)
            {
                var current = arranged.Placements;
                consistent &= ReferenceEquals(current, placements) && ReferenceEquals(current[current.Count - 1], last)
                    && current[0].Bounds.Width == 100;
            }
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            That(consistent && bytes == 0, "Reading 4096 settled layouts allocated " + bytes + " bytes.");
        }
    }
}
