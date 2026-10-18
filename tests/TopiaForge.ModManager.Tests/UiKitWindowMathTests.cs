using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Window placement against a canvas Unity has not measured yet. A canvas created this frame
    /// reads 0x0 until its first layout, and a kit window shown in that frame collapsed to 0x0
    /// because its size was capped to 90% of nothing. An unmeasured canvas must leave a window's
    /// size and position alone, while a measured small screen still clamps.
    /// </summary>
    internal static class UiKitWindowMathTests
    {
        private static readonly (float Width, float Height)[] UnmeasuredCanvases =
        {
            (0f, 0f),
            (0f, 1080f),
            (1920f, 0f),
            (-1920f, -1080f),
            (float.NaN, 1080f),
            (1920f, float.NaN),
            (float.PositiveInfinity, 1080f),
            (1920f, float.NegativeInfinity),
        };

        public static void Run()
        {
            WindowShownOnUnmeasuredCanvasKeepsItsSize();
            UnmeasuredCanvasBoundsNothing();
            AnyPositiveFiniteCanvasIsMeasured();
            MeasuredSmallScreensStillClamp();
            Console.WriteLine("UiKitWindowMathTests passed.");
        }

        private static void WindowShownOnUnmeasuredCanvasKeepsItsSize()
        {
            // The UI Gallery's centred 860x640 window as Show measures it in the frame its canvas
            // was built: canvas-space x = 0 + (0 / 2) - (860 / 2).
            var shown = new TopiaForgeRect(-430f, -320f, 860f, 640f);
            var (w, h) = TopiaForgeWindowMath.ClampSize(shown.Width, shown.Height, 0f, 0f, 240f, 120f);
            var placed = TopiaForgeWindowMath.ClampToScreen(new TopiaForgeRect(shown.X, shown.Y, w, h), 0f, 0f);
            AssertSame(placed, shown, "a 0x0 canvas keeps the window at 860x640 where it was");

            // The first measured frame then clamps as before, and a window that fits stays put.
            var measured = new TopiaForgeRect(530f, 220f, 860f, 640f);
            (w, h) = TopiaForgeWindowMath.ClampSize(measured.Width, measured.Height, 1920f, 1080f, 240f, 120f);
            placed = TopiaForgeWindowMath.ClampToScreen(new TopiaForgeRect(measured.X, measured.Y, w, h), 1920f, 1080f);
            AssertSame(placed, measured, "a 1920x1080 canvas keeps a window that fits");
        }

        private static void UnmeasuredCanvasBoundsNothing()
        {
            foreach (var (width, height) in UnmeasuredCanvases)
            {
                var canvas = " on a " + width + "x" + height + " canvas";
                Assert(!TopiaForgeWindowMath.HasViewport(width, height), "no viewport" + canvas);

                var (w, h) = TopiaForgeWindowMath.ClampSize(860f, 640f, width, height, 240f, 120f);
                Assert(w == 860f && h == 640f, "size is not capped" + canvas + ", got " + w + "x" + h);

                (w, h) = TopiaForgeWindowMath.ClampSize(100f, 50f, width, height, 240f, 120f);
                Assert(w == 240f && h == 120f, "the minimum size still applies" + canvas + ", got " + w + "x" + h);

                // A position restored from a larger screen must survive until a measured frame.
                var restored = new TopiaForgeRect(-500f, 2000f, 860f, 640f);
                AssertSame(TopiaForgeWindowMath.ClampToScreen(restored, width, height), restored, "position is not clamped" + canvas);

                // Within the snap threshold of the origin, where a 0x0 screen's edges would pull it.
                var nearOrigin = new TopiaForgeRect(5f, 7f, 860f, 640f);
                AssertSame(TopiaForgeWindowMath.SnapToEdges(nearOrigin, width, height), nearOrigin, "edges do not snap" + canvas);
            }
        }

        private static void AnyPositiveFiniteCanvasIsMeasured()
        {
            Assert(TopiaForgeWindowMath.HasViewport(1920f, 1080f), "a 1920x1080 canvas is measured");
            Assert(TopiaForgeWindowMath.HasViewport(float.Epsilon, float.Epsilon), "the smallest positive canvas is measured");
            Assert(TopiaForgeWindowMath.HasViewport(float.MaxValue, float.MaxValue), "the largest finite canvas is measured");
        }

        private static void MeasuredSmallScreensStillClamp()
        {
            var (w, h) = TopiaForgeWindowMath.ClampSize(860f, 640f, 800f, 600f, 240f, 120f);
            AssertNear(w, 720f, "an 800x600 canvas caps the width to 90%");
            AssertNear(h, 540f, "an 800x600 canvas caps the height to 90%");
            var placed = TopiaForgeWindowMath.ClampToScreen(new TopiaForgeRect(-30f, -20f, w, h), 800f, 600f);
            Assert(placed.X == 0f && placed.Y == 0f, "an 800x600 canvas pulls the window inside, got " + placed);

            var restored = TopiaForgeWindowMath.ClampToScreen(new TopiaForgeRect(1500f, 900f, 860f, 640f), 1280f, 720f);
            Assert(restored.X == 420f && restored.Y == 80f, "a restored rect comes back on-screen, got " + restored);

            // Below the minimum the viewport still wins, so a window never outgrows a tiny screen.
            (w, h) = TopiaForgeWindowMath.ClampSize(860f, 640f, 200f, 100f, 240f, 120f);
            AssertNear(w, 180f, "a 200x100 canvas caps below the minimum width");
            AssertNear(h, 90f, "a 200x100 canvas caps below the minimum height");

            // Drag end snaps, then clamps; on a measured canvas that is unchanged.
            var dragged = TopiaForgeWindowMath.ClampToScreen(
                TopiaForgeWindowMath.SnapToEdges(new TopiaForgeRect(1065f, -40f, 860f, 640f), 1920f, 1080f),
                1920f,
                1080f);
            Assert(dragged.X == 1060f && dragged.Y == 0f, "drag end snaps right and clamps up, got " + dragged);
        }

        private static void AssertSame(TopiaForgeRect actual, TopiaForgeRect expected, string message)
        {
            Assert(
                actual.X == expected.X && actual.Y == expected.Y
                    && actual.Width == expected.Width && actual.Height == expected.Height,
                message + " (expected " + expected + ", got " + actual + ")");
        }

        private static void AssertNear(float actual, float expected, string message)
        {
            if (Math.Abs(actual - expected) > 0.0001f)
            {
                throw new InvalidOperationException("Assertion failed: " + message + " (expected " + expected + ", got " + actual + ")");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
