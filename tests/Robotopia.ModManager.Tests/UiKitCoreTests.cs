using System;
using Robotopia.Mods.UnityUi;

namespace Robotopia.ModManager.Tests
{
    /// <summary>
    /// Unit tests for the QwUi kit's Unity-free Core island: brand palette exactness,
    /// scheme resolution, high-contrast parity with the original Zombies HudColor math,
    /// easing, sprite coverage math, virtual-list math, window clamp/snap, and sorting
    /// band allocation.
    /// </summary>
    internal static class UiKitCoreTests
    {
        public static void Run()
        {
            PaletteMatchesLauncherHexValues();
            SchemesResolveAllRoles();
            AccentOverrideBehaviors();
            HighContrastParityWithHudColor();
            HighContrastTransformsScheme();
            AccessibilityProfilesComposeAndClamp();
            EasingEndpointsAndShape();
            RoundedRectCoverage();
            VirtualListMath();
            PreviewFramingMath();
            WindowMath();
            LayerBandAllocation();
            Console.WriteLine("UiKitCoreTests passed.");
        }

        private static void PaletteMatchesLauncherHexValues()
        {
            // Exact ports of QuantumWorksPalette (launcher_theme.dart). Byte-level checks.
            AssertHex(QwPalette.Paper, 0xF5, 0xF1, 0xE8, "Paper");
            AssertHex(QwPalette.Surface, 0xFF, 0xFC, 0xF6, "Surface");
            AssertHex(QwPalette.SurfaceTint, 0xFF, 0xE0, 0xBE, "SurfaceTint");
            AssertHex(QwPalette.Border, 0xE4, 0xB3, 0x73, "Border");
            AssertHex(QwPalette.Launch, 0xFF, 0x7A, 0x11, "Launch");
            AssertHex(QwPalette.LaunchDark, 0xCC, 0x62, 0x0E, "LaunchDark");
            AssertHex(QwPalette.Ink, 0x2D, 0x37, 0x48, "Ink");
            AssertHex(QwPalette.Accent, 0x20, 0xF6, 0xFE, "Accent");
            AssertHex(QwPalette.AccentDark, 0x16, 0x8E, 0x96, "AccentDark");
            AssertHex(QwPalette.Magenta, 0xFF, 0x6B, 0x9D, "Magenta");
            AssertHex(QwPalette.Good, 0x14, 0x8D, 0x63, "Good");
            AssertHex(QwPalette.Warning, 0xD6, 0x80, 0x17, "Warning");
            AssertHex(QwPalette.Danger, 0xC8, 0x3E, 0x4D, "Danger");
            AssertHex(QwPalette.LogPanel, 0x1F, 0x25, 0x30, "LogPanel");
            AssertHex(QwPalette.SelectedTint, 0xFF, 0xE8, 0xD1, "SelectedTint");
        }

        private static void SchemesResolveAllRoles()
        {
            foreach (var scheme in new[] { QwScheme.Paper, QwScheme.Hud })
            {
                var colors = QwSchemes.Resolve(scheme, null, highContrast: false);
                Assert(colors.Surface.A > 0f, scheme + ".Surface must be visible");
                Assert(colors.Text.A > 0f, scheme + ".Text must be visible");
                Assert(colors.Primary == QwPalette.Launch, scheme + ".Primary is the brand orange (constant across schemes)");
                Assert(colors.OutlineStrong == QwPalette.Launch, scheme + ".OutlineStrong is the brand orange");
                Assert(colors.Shadow.A > 0f && colors.Shadow.A < 1f, scheme + ".Shadow is translucent");
            }

            var paper = QwSchemes.ResolvePaper(null, false);
            Assert(paper.Surface == QwPalette.Surface, "Paper surface is launcher surface");
            Assert(paper.Text == QwPalette.Ink, "Paper text is launcher ink");
            Assert(paper.Accent == QwPalette.AccentDark, "Paper accent uses the dark cyan for legibility on light surfaces");

            var hud = QwSchemes.ResolveHud(null, false);
            Assert(hud.Text == QwPalette.Paper, "HUD text is warm paper on dark panels");
            Assert(hud.Accent == QwPalette.Accent, "HUD accent is the bright brand cyan");
            Assert(hud.Surface.A < 1f, "HUD surfaces are translucent over gameplay");

            // HUD text must actually read against HUD surfaces.
            Assert(QwContrast.Ratio(hud.Text, QwPalette.LogPanel) >= 7f, "HUD text contrast");
            Assert(QwContrast.Ratio(paper.Text, paper.Surface) >= 7f, "Paper text contrast");
        }

        private static void AccentOverrideBehaviors()
        {
            var loudAccent = QwRgba.Hex(0xAAFF66); // a bright mod accent that would vanish on paper

            var hud = QwSchemes.ResolveHud(loudAccent, false);
            Assert(hud.Accent == loudAccent, "HUD accent override is used verbatim");

            var paper = QwSchemes.ResolvePaper(loudAccent, false);
            Assert(paper.Accent != loudAccent, "Paper accent override must be adjusted for contrast");
            Assert(QwContrast.Ratio(paper.Accent, paper.Surface) >= 4.5f, "Paper accent override reaches 4.5:1");

            // Primary is never overridden - one brand.
            Assert(paper.Primary == QwPalette.Launch && hud.Primary == QwPalette.Launch, "accent never touches Primary");
        }

        private static void HighContrastParityWithHudColor()
        {
            // Reference vectors through the ORIGINAL ZombiesHudBehaviour.HudColor math.
            var vectors = new[]
            {
                new QwRgba(0.52f, 1f, 0.28f, 1f),   // acid
                new QwRgba(1f, 0.74f, 0.20f, 1f),   // amber
                new QwRgba(0.20f, 0.92f, 1f, 1f),   // cyan
                new QwRgba(1f, 0.24f, 0.20f, 0.5f), // danger with alpha
                new QwRgba(0.1f, 0.1f, 0.1f, 1f),   // dim gray
                new QwRgba(0f, 0f, 0f, 1f),         // black (guard: max <= 0 passthrough)
            };

            foreach (var input in vectors)
            {
                var expected = ReferenceHudColor(input);
                var actual = QwContrast.Emphasize(input);
                AssertNear(actual.R, expected.R, "Emphasize R parity for " + input);
                AssertNear(actual.G, expected.G, "Emphasize G parity for " + input);
                AssertNear(actual.B, expected.B, "Emphasize B parity for " + input);
                AssertNear(actual.A, expected.A, "Emphasize preserves alpha for " + input);
            }
        }

        private static void HighContrastTransformsScheme()
        {
            var normal = QwSchemes.ResolveHud(null, false);
            var contrast = QwSchemes.ResolveHud(null, true);
            Assert(contrast.Surface.A > normal.Surface.A, "high contrast raises HUD surface opacity");
            Assert(contrast.Text == QwPalette.White, "high contrast HUD text is pure white");

            var paperContrast = QwSchemes.ResolvePaper(null, true);
            Assert(QwContrast.Ratio(paperContrast.Text, paperContrast.Surface) >
                   QwContrast.Ratio(QwSchemes.ResolvePaper(null, false).Text, QwPalette.Surface) - 0.001f,
                "high contrast never reduces paper text contrast");
        }

        private static void AccessibilityProfilesComposeAndClamp()
        {
            var neutral = QwAccessibilityProfile.Default.Resolve(
                globalHighContrast: false,
                globalUiScale: 1f,
                globalReducedMotion: false,
                globalMotionIntensity: 1f);
            Assert(!neutral.HighContrast && !neutral.ReducedMotion, "neutral profile keeps boolean defaults");
            AssertNear(neutral.UiScale, 1f, "neutral profile keeps global scale");
            AssertNear(neutral.MotionIntensity, 1f, "neutral profile keeps global motion");

            var profile = new QwAccessibilityProfile(
                highContrast: true,
                uiScale: 1.25f,
                reducedMotion: false,
                motionIntensity: 0.5f);
            var effective = profile.Resolve(
                globalHighContrast: false,
                globalUiScale: 1.2f,
                globalReducedMotion: false,
                globalMotionIntensity: 1.5f);
            Assert(effective.HighContrast, "host high contrast strengthens global state");
            AssertNear(effective.UiScale, 1.5f, "host and global scale compose and clamp");
            AssertNear(effective.MotionIntensity, 0.75f, "host and global motion multiply");

            effective = profile.Resolve(
                globalHighContrast: true,
                globalUiScale: 0.75f,
                globalReducedMotion: true,
                globalMotionIntensity: 2f);
            Assert(effective.HighContrast, "host cannot weaken global high contrast");
            Assert(effective.ReducedMotion, "host cannot weaken global reduced motion");
            AssertNear(effective.MotionIntensity, 0f, "reduced motion zeroes effective motion");

            var malformed = new QwAccessibilityProfile(
                uiScale: float.NaN,
                motionIntensity: float.PositiveInfinity);
            AssertNear(malformed.UiScale, 1f, "NaN host scale falls back safely");
            AssertNear(malformed.MotionIntensity, 1f, "infinite host motion falls back safely");
            Assert(malformed.Equals(new QwAccessibilityProfile()), "normalized profiles compare by value");
        }

        private static void EasingEndpointsAndShape()
        {
            foreach (QwEase ease in Enum.GetValues(typeof(QwEase)))
            {
                AssertNear(QwEasing.Evaluate(ease, 0f), 0f, ease + " starts at 0");
                AssertNear(QwEasing.Evaluate(ease, 1f), 1f, ease + " ends at 1");
                AssertNear(QwEasing.Evaluate(ease, -5f), 0f, ease + " clamps below");
                AssertNear(QwEasing.Evaluate(ease, 5f), 1f, ease + " clamps above");
            }

            Assert(QwEasing.Evaluate(QwEase.OutQuad, 0.5f) > 0.5f, "OutQuad front-loads");
            Assert(QwEasing.Evaluate(QwEase.InQuad, 0.5f) < 0.5f, "InQuad back-loads");

            var overshoots = false;
            for (var t = 0f; t <= 1f; t += 0.01f)
            {
                if (QwEasing.Evaluate(QwEase.OutBack, t) > 1f)
                {
                    overshoots = true;
                }
            }

            Assert(overshoots, "OutBack overshoots past 1");
        }

        private static void RoundedRectCoverage()
        {
            const float w = 44f;
            const float h = 44f;
            const float r = 18f;

            AssertNear(QwRoundedRectMath.FillCoverage(w / 2f, h / 2f, w, h, r), 1f, "center is fully covered");
            AssertNear(QwRoundedRectMath.FillCoverage(-4f, h / 2f, w, h, r), 0f, "far outside is empty");
            AssertNear(QwRoundedRectMath.FillCoverage(0f, h / 2f, w, h, r), 0.5f, "straight edge boundary is half-covered");

            // Four-corner symmetry at an arbitrary sample point near a corner.
            var reference = QwRoundedRectMath.FillCoverage(3.2f, 4.1f, w, h, r);
            AssertNear(QwRoundedRectMath.FillCoverage(w - 3.2f, 4.1f, w, h, r), reference, "corner symmetry (x mirror)");
            AssertNear(QwRoundedRectMath.FillCoverage(3.2f, h - 4.1f, w, h, r), reference, "corner symmetry (y mirror)");
            AssertNear(QwRoundedRectMath.FillCoverage(w - 3.2f, h - 4.1f, w, h, r), reference, "corner symmetry (both)");

            // Ring: hollow center, present at the edge, ring+inset-fill reconstructs the fill.
            AssertNear(QwRoundedRectMath.RingCoverage(w / 2f, h / 2f, w, h, r, 2f), 0f, "ring center is hollow");
            Assert(QwRoundedRectMath.RingCoverage(1f, h / 2f, w, h, r, 2f) > 0.9f, "ring covers the edge");

            // Circle sanity.
            AssertNear(QwRoundedRectMath.CircleCoverage(12f, 12f, 24f), 1f, "circle center covered");
            AssertNear(QwRoundedRectMath.CircleCoverage(0.2f, 0.2f, 24f), 0f, "circle corner empty");
        }

        private static void VirtualListMath()
        {
            const float row = 38f;
            const float gap = 4f;
            const float viewport = 400f;

            AssertNear(QwVirtualListMath.ContentHeight(0, row, gap), 0f, "empty content height");
            AssertNear(QwVirtualListMath.ContentHeight(1, row, gap), 38f, "single row height");
            AssertNear(QwVirtualListMath.ContentHeight(10, row, gap), (10 * row) + (9 * gap), "ten row height");

            var (first, count) = QwVirtualListMath.VisibleRange(0f, viewport, 100, row, gap);
            Assert(first == 0, "range at top starts at 0");
            Assert(count >= 10 && count <= 14, "range covers viewport plus overscan, got " + count);

            (first, count) = QwVirtualListMath.VisibleRange(420f, viewport, 100, row, gap);
            Assert(first == 9, "scrolled range applies overscan, got " + first);
            Assert(first + count <= 100, "range never exceeds item count");

            (first, count) = QwVirtualListMath.VisibleRange(99999f, viewport, 20, row, gap);
            Assert(first + count <= 20, "overflow scroll clamps to tail");

            Assert(QwVirtualListMath.PoolSize(viewport, row, gap) >= 11, "pool covers viewport");
            AssertNear(QwVirtualListMath.ClampScroll(-50f, viewport, 100, row, gap), 0f, "scroll clamps at 0");

            var max = QwVirtualListMath.ContentHeight(100, row, gap) - viewport;
            AssertNear(QwVirtualListMath.ClampScroll(99999f, viewport, 100, row, gap), max, "scroll clamps at max");

            AssertNear(QwVirtualListMath.ScrollToRow(0, 500f, viewport, 100, row, gap), 0f, "scroll-to-top row");
            var current = QwVirtualListMath.ScrollToRow(5, 100f, viewport, 100, row, gap);
            AssertNear(current, 100f, "visible row does not move the scroll");
        }

        private static void WindowMath()
        {
            var clamped = QwWindowMath.ClampToScreen(new QwRect(-40f, 2000f, 300f, 200f), 1920f, 1080f);
            Assert(clamped.X == 0f && clamped.Y == 880f, "window clamps inside the screen, got " + clamped);

            var (w, h) = QwWindowMath.ClampSize(5000f, 40f, 1920f, 1080f, 200f, 120f);
            AssertNear(w, 1920f * 0.9f, "width caps at 90% viewport");
            AssertNear(h, 120f, "height respects minimum");

            var snapped = QwWindowMath.SnapToEdges(new QwRect(8f, 500f, 300f, 200f), 1920f, 1080f);
            Assert(snapped.X == 0f, "left edge snaps within threshold");
            Assert(snapped.Y == 500f, "far edge does not snap");

            var noSnap = QwWindowMath.SnapToEdges(new QwRect(40f, 500f, 300f, 200f), 1920f, 1080f);
            Assert(noSnap.X == 40f, "outside threshold does not snap");

            var rightSnap = QwWindowMath.SnapToEdges(new QwRect(1612f, 500f, 300f, 200f), 1920f, 1080f);
            Assert(rightSnap.X == 1620f, "right edge snaps to screen edge, got " + rightSnap.X);
        }

        private static void LayerBandAllocation()
        {
            var bands = new QwLayerBands();
            Assert(bands.BaseOf(QwLayerBand.Hud) < bands.BaseOf(QwLayerBand.Window), "hud below windows");
            Assert(bands.BaseOf(QwLayerBand.Window) < bands.BaseOf(QwLayerBand.Modal), "windows below modals");
            Assert(bands.BaseOf(QwLayerBand.Modal) < bands.BaseOf(QwLayerBand.Toast), "modals below toasts");

            Assert(bands.TryAllocate(QwLayerBand.Hud, out var first) && first == QwLayerBands.DefaultHudBase, "first hud allocation at base");
            Assert(bands.TryAllocate(QwLayerBand.Hud, out var second) && second == first + 1, "sequential allocation");

            var tight = new QwLayerBands(0, 2, 4, 6, 8, 10);
            Assert(tight.TryAllocate(QwLayerBand.Hud, out _), "tight band first slot");
            Assert(tight.TryAllocate(QwLayerBand.Hud, out _), "tight band second slot");
            Assert(!tight.TryAllocate(QwLayerBand.Hud, out var exhausted), "third allocation exhausts");
            Assert(exhausted == 1, "exhausted band reuses its last order");
            Assert(tight.Remaining(QwLayerBand.Hud) == 0, "remaining reports zero");
            Assert(tight.TryRelease(exhausted), "an allocated order can be released");
            Assert(tight.Remaining(QwLayerBand.Hud) == 0,
                "an exhaustion-shared slot is not reusable until every holder releases it");
            Assert(tight.TryRelease(exhausted), "the original holder can release the shared order");
            Assert(tight.Remaining(QwLayerBand.Hud) == 1, "fully released slot becomes available");
            Assert(tight.TryAllocate(QwLayerBand.Hud, out var reused) && reused == exhausted,
                "released slots are reused before a band reports exhaustion");
            Assert(!tight.TryRelease(999), "orders outside every band cannot be released");

            var threw = false;
            try
            {
                _ = new QwLayerBands(5, 4, 3, 2, 1, 0);
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert(threw, "descending band bases are rejected");
        }

        // ---- helpers ----

        /// <summary>The original ZombiesHudBehaviour.HudColor math, kept verbatim as the parity reference.</summary>
        private static QwRgba ReferenceHudColor(QwRgba color)
        {
            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            if (max <= 0f)
            {
                return color;
            }

            float Lerp(float a, float b, float t) => a + ((b - a) * t);
            return new QwRgba(
                Lerp(color.R / max, 1f, 0.25f),
                Lerp(color.G / max, 1f, 0.25f),
                Lerp(color.B / max, 1f, 0.25f),
                color.A);
        }

        private static void PreviewFramingMath()
        {
            // The offset is always a unit direction regardless of angle.
            foreach (var (yaw, pitch) in new[] { (0f, 0f), (45f, 30f), (90f, 60f), (180f, -15f) })
            {
                var f = QwPreviewMath.Frame(1f, 1f, 1f, yaw, pitch, 1f);
                var length = Math.Sqrt((f.OffsetX * f.OffsetX) + (f.OffsetY * f.OffsetY) + (f.OffsetZ * f.OffsetZ));
                AssertNear((float)length, 1f, "offset unit length at yaw " + yaw + " pitch " + pitch);
                Assert(f.FarPlane > f.NearPlane, "far beyond near at yaw " + yaw + " pitch " + pitch);
                Assert(f.NearPlane > 0f, "positive near plane at yaw " + yaw + " pitch " + pitch);
                Assert(f.Distance > 0f, "positive distance at yaw " + yaw + " pitch " + pitch);
            }

            // Head-on view of a unit cube (half-extents 1): the view must cover the
            // full projected face exactly at margin 1.
            var headOn = QwPreviewMath.Frame(1f, 1f, 1f, 0f, 0f, 1f);
            AssertNear(headOn.OffsetX, 0f, "head-on offset x");
            AssertNear(headOn.OffsetY, 0f, "head-on offset y");
            AssertNear(headOn.OffsetZ, 1f, "head-on offset z");
            AssertNear(headOn.OrthoHalfSize, 1f, "head-on ortho covers the face");

            // Top-down view of a flat slab: height (y) contributes nothing on screen;
            // the footprint (x/z) decides the framing.
            var topDown = QwPreviewMath.Frame(2f, 0.001f, 3f, 0f, 90f, 1f);
            AssertNear(topDown.OrthoHalfSize, 3f, "top-down framing follows the footprint");

            // The three-quarter default view of a cube must cover at least the cube's
            // own half-extent and scale linearly with the margin.
            var threeQuarter = QwPreviewMath.Frame(1f, 1f, 1f, margin: 1f);
            Assert(threeQuarter.OrthoHalfSize >= 1f, "three-quarter view covers the cube");
            var withMargin = QwPreviewMath.Frame(1f, 1f, 1f, margin: 1.5f);
            AssertNear(withMargin.OrthoHalfSize, threeQuarter.OrthoHalfSize * 1.5f, "margin scales the framing");

            // Degenerate bounds (empty prefab) still produce a valid camera.
            var empty = QwPreviewMath.Frame(0f, 0f, 0f);
            Assert(empty.OrthoHalfSize >= QwPreviewMath.MinHalfSize, "degenerate bounds clamp to the minimum size");
            Assert(empty.FarPlane > empty.NearPlane, "degenerate bounds keep a valid frustum");
        }

        private static void AssertHex(QwRgba color, byte r, byte g, byte b, string name)
        {
            AssertNear(color.R, r / 255f, name + " red");
            AssertNear(color.G, g / 255f, name + " green");
            AssertNear(color.B, b / 255f, name + " blue");
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
