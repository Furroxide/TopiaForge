using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// HUD layout math. A HUD's text column fills its panel inside the padding, where it kept a new GameObject's 100 x 100
    /// units, centred, so "Shared count: 12" wrapped in a 260-unit panel (in the game the body had 53 pixels at 1600x900).
    /// HUDs docked at one corner stack instead of overlapping, by their size on screen whatever each host's UI scale.
    /// </summary>
    internal static class UiKitHudLayoutTests
    {
        private const float Margin = TopiaForgeTokens.SafeMargin;
        private const float Gap = TopiaForgeHudDock.Gap;

        public static void Run()
        {
            TheTextColumnFillsThePanelInsideItsPadding();
            UiScaleChangesNoColumnWidth();
            DegeneratePanelsAreRefused();
            OnePanelRestsAtTheSafeMargin();
            PanelsAtOneCornerStackAndCloseUp();
            EdgeStacksAreCentred();
            PanelsOnScaledHostsStackBySizeOnScreen();
            TheCrosshairIsNeverADock();
            TheDockChangesItsVersionOnlyWhenPositionsMayMove();
            Console.WriteLine("UiKitHudLayoutTests passed.");
        }

        private static void TheTextColumnFillsThePanelInsideItsPadding()
        {
            // The multiplayer template's HUD.
            var column = TopiaForgeHudColumn.For(260f, 96f);
            Assert(column.Padding == (float)TopiaForgeGap.Md, "the column keeps the kit's HUD padding");
            Assert(column.TextWidth == 236f && column.Height == 72f, "the title has the panel's whole inner width: 260 - 2 x 12");
            Assert(column.BodyWidth == 224f, "the body wraps at the inner width less the scroll bar's 12-unit gutter");
            Assert(column.BodyWidth > 2f * 100f, "more than twice the 100-unit column the panel used to centre");

            // At 14 units, Medium's digits and letters average well under 9 units, so the longest count fits on one line.
            const float GenerousAdvance = 9f;
            var longest = "Shared count: " + int.MaxValue;
            Assert(column.FitsOnOneLine(longest.Length * GenerousAdvance), "\"" + longest + "\" stays on one line");
            Assert(!column.FitsOnOneLine(column.BodyWidth + 0.5f), "a line wider than the body still wraps");

            var wide = TopiaForgeHudColumn.For(520f, 180f);
            Assert(wide.TextWidth == 496f && wide.BodyWidth == 484f && wide.Height == 156f, "the acceptance mod's 520 x 180 HUD");
            var custom = TopiaForgeHudColumn.For(100f, 50f, 0f, 0f);
            Assert(custom.TextWidth == 100f && custom.BodyWidth == 100f, "without padding or gutter the text takes the whole panel");
        }

        private static void UiScaleChangesNoColumnWidth()
        {
            // A canvas at UI scale 1.5 draws every unit 1.5 times larger, panel and text alike, so where a line wraps in
            // units is the same at every scale. The column is laid out in units and takes no scale.
            var atOne = TopiaForgeHudColumn.For(260f, 96f);
            var again = TopiaForgeHudColumn.For(260f, 96f);
            Assert(atOne.BodyWidth == again.BodyWidth && atOne.TextWidth == again.TextWidth, "the column depends only on the request");
            var dock = new TopiaForgeHudDock();
            var normal = dock.Add(TopiaForgeCorner.BottomLeft, 96f, 1f);
            var large = dock.Add(TopiaForgeCorner.BottomRight, 96f, 1.5f);
            dock.SetShown(normal, true);
            dock.SetShown(large, true);
            var placedNormal = dock.TryGetPosition(normal, out var x1, out var y1);
            var placedLarge = dock.TryGetPosition(large, out var x2, out var y2);
            Assert(placedNormal && placedLarge, "both panels are placed");
            AssertNear(x1 * 1f, Margin, "the margin is the same on screen at UI scale 1");
            AssertNear(-x2 * 1.5f, Margin, "and at UI scale 1.5, in its own smaller units");
            AssertNear(y1 * 1f, y2 * 1.5f, "both rest the same distance above the bottom");
        }

        private static void DegeneratePanelsAreRefused()
        {
            AssertThrows(() => TopiaForgeHudColumn.For(0f, 96f), "a zero width");
            AssertThrows(() => TopiaForgeHudColumn.For(260f, -1f), "a negative height");
            AssertThrows(() => TopiaForgeHudColumn.For(float.PositiveInfinity, 96f), "an infinite width");
            AssertThrows(() => TopiaForgeHudColumn.For(float.NaN, 96f), "a width that is not a number");
            AssertThrows(() => TopiaForgeHudColumn.For(260f, 96f, -1f, 12f), "negative padding");
            var tiny = TopiaForgeHudColumn.For(20f, 10f);
            Assert(tiny.TextWidth == 0f && tiny.BodyWidth == 0f && tiny.Height == 0f, "a panel smaller than its padding has no text area, never a negative one");
        }

        private static void OnePanelRestsAtTheSafeMargin()
        {
            var dock = new TopiaForgeHudDock();
            var corners = new[]
            {
                (TopiaForgeCorner.TopLeft, Margin, -Margin),
                (TopiaForgeCorner.Top, 0f, -Margin),
                (TopiaForgeCorner.TopRight, -Margin, -Margin),
                (TopiaForgeCorner.BottomLeft, Margin, Margin),
                (TopiaForgeCorner.Bottom, 0f, Margin),
                (TopiaForgeCorner.BottomRight, -Margin, Margin),
                (TopiaForgeCorner.Left, Margin, 0f),
                (TopiaForgeCorner.Right, -Margin, 0f),
            };
            foreach (var (corner, x, y) in corners)
            {
                var handle = dock.Add(corner, 96f, 1f);
                Assert(!dock.TryGetPosition(handle, out _, out _), corner + ": a panel is placed only once shown");
                dock.SetShown(handle, true);
                Assert(dock.TryGetPosition(handle, out var px, out var py), corner + ": a shown panel is placed");
                AssertNear(px, x, corner + " x");
                AssertNear(py, y, corner + " y");
            }
        }

        private static void PanelsAtOneCornerStackAndCloseUp()
        {
            var dock = new TopiaForgeHudDock();
            var counter = dock.Add(TopiaForgeCorner.BottomLeft, 96f, 1f);
            var zombies = dock.Add(TopiaForgeCorner.BottomLeft, 230f, 1f);
            var other = dock.Add(TopiaForgeCorner.TopRight, 50f, 1f);
            foreach (var handle in new[] { counter, zombies, other }) dock.SetShown(handle, true);
            dock.TryGetPosition(counter, out _, out var first);
            dock.TryGetPosition(zombies, out _, out var second);
            dock.TryGetPosition(other, out _, out var elsewhere);
            AssertNear(first, Margin, "the first HUD at a corner rests at the margin");
            AssertNear(second, Margin + 96f + Gap, "the next rests above it, a small gap apart, so the two never overlap");
            AssertNear(elsewhere, -Margin, "a HUD at another corner does not move");

            dock.SetShown(counter, false);
            dock.TryGetPosition(zombies, out _, out second);
            AssertNear(second, Margin, "when the first hides, the next closes up to the margin");
            dock.SetShown(counter, true);
            dock.Remove(zombies);
            dock.TryGetPosition(counter, out _, out first);
            Assert(!dock.TryGetPosition(zombies, out _, out _) && dock.Count == 2, "a removed HUD is no longer placed");
            AssertNear(first, Margin, "and the one before it keeps its place");

            var top = new TopiaForgeHudDock();
            var a = top.Add(TopiaForgeCorner.TopLeft, 40f, 1f);
            var b = top.Add(TopiaForgeCorner.TopLeft, 40f, 1f);
            top.SetShown(a, true);
            top.SetShown(b, true);
            top.TryGetPosition(b, out _, out var below);
            AssertNear(below, -(Margin + 40f + Gap), "at a top corner the stack grows downwards");
        }

        private static void EdgeStacksAreCentred()
        {
            var dock = new TopiaForgeHudDock();
            var one = dock.Add(TopiaForgeCorner.Left, 100f, 1f);
            var two = dock.Add(TopiaForgeCorner.Left, 60f, 1f);
            dock.SetShown(one, true);
            dock.TryGetPosition(one, out _, out var alone);
            AssertNear(alone, 0f, "a single panel sits at the middle of its edge");
            dock.SetShown(two, true);
            dock.TryGetPosition(one, out _, out var upper);
            dock.TryGetPosition(two, out _, out var lower);
            var total = 100f + Gap + 60f;
            AssertNear(upper, (total / 2f) - 50f, "the first panel sits at the top of the centred stack");
            AssertNear(lower, (total / 2f) - 100f - Gap - 30f, "the second sits below it");
            AssertNear((upper + 50f) + (lower - 30f), 0f, "the stack is centred on the edge");
        }

        private static void PanelsOnScaledHostsStackBySizeOnScreen()
        {
            var dock = new TopiaForgeHudDock();
            var large = dock.Add(TopiaForgeCorner.BottomRight, 100f, 1.5f);
            var normal = dock.Add(TopiaForgeCorner.BottomRight, 50f, 1f);
            dock.SetShown(large, true);
            dock.SetShown(normal, true);
            dock.TryGetPosition(normal, out _, out var above);
            AssertNear(above, Margin + (100f * 1.5f) + Gap, "a 100-unit panel at UI scale 1.5 takes 150 units of a scale-1 canvas");
            dock.SetScale(large, 1f);
            dock.TryGetPosition(normal, out _, out above);
            AssertNear(above, Margin + 100f + Gap, "a UI scale change moves the panels after it");
            dock.SetScale(large, float.NaN);
            dock.TryGetPosition(normal, out _, out above);
            AssertNear(above, Margin + 50f + Gap, "a scale that is not a number counts as the smallest supported, never as NaN");
        }

        private static void TheCrosshairIsNeverADock()
        {
            var dock = new TopiaForgeHudDock();
            AssertThrows(() => dock.Add(TopiaForgeCorner.Center, 96f, 1f), "the centre, over the crosshair");
            AssertThrows(() => dock.Add(TopiaForgeCorner.TopLeft, 0f, 1f), "a panel without height");
            AssertThrows(() => dock.Add(TopiaForgeCorner.TopLeft, float.PositiveInfinity, 1f), "an infinite height");
        }

        private static void TheDockChangesItsVersionOnlyWhenPositionsMayMove()
        {
            var dock = new TopiaForgeHudDock();
            var handle = dock.Add(TopiaForgeCorner.BottomLeft, 96f, 1f);
            var version = dock.Version;
            dock.SetShown(handle, false);
            dock.SetScale(handle, 1f);
            dock.SetShown(12345, true);
            dock.Remove(12345);
            Assert(dock.Version == version, "a change that moves nothing keeps the version, so nothing is written to the screen");
            dock.SetShown(handle, true);
            Assert(dock.Version != version, "showing a panel changes it");
            var allocated = Seam.Allocated(() =>
            {
                dock.SetShown(handle, true);
                dock.SetScale(handle, 1f);
                dock.TryGetPosition(handle, out _, out _);
            });
            Assert(allocated == 0, "a frame that changes nothing in the dock allocates nothing (" + allocated + " bytes)");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private static void AssertNear(float actual, float expected, string message)
        {
            if (Math.Abs(actual - expected) > 0.001f)
            {
                throw new InvalidOperationException("Assertion failed: " + message + " (expected " + expected + ", got " + actual + ")");
            }
        }

        private static void AssertThrows(Action action, string message)
        {
            try
            {
                action();
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            throw new InvalidOperationException("Assertion failed: " + message + " is refused");
        }
    }
}
