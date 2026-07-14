using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Unit tests for the TopiaForgeUi kit's Unity-free Core island: brand palette exactness,
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
            // Exact ports of TopiaForgePalette (launcher_theme.dart). Byte-level checks.
            AssertHex(TopiaForgePalette.Paper, 0xF5, 0xF1, 0xE8, "Paper");
            AssertHex(TopiaForgePalette.Surface, 0xFF, 0xFC, 0xF6, "Surface");
            AssertHex(TopiaForgePalette.SurfaceTint, 0xFF, 0xE0, 0xBE, "SurfaceTint");
            AssertHex(TopiaForgePalette.Border, 0xE4, 0xB3, 0x73, "Border");
            AssertHex(TopiaForgePalette.Launch, 0xFF, 0x7A, 0x11, "Launch");
            AssertHex(TopiaForgePalette.LaunchDark, 0xCC, 0x62, 0x0E, "LaunchDark");
            AssertHex(TopiaForgePalette.Ink, 0x2D, 0x37, 0x48, "Ink");
            AssertHex(TopiaForgePalette.Accent, 0x20, 0xF6, 0xFE, "Accent");
            AssertHex(TopiaForgePalette.AccentDark, 0x16, 0x8E, 0x96, "AccentDark");
            AssertHex(TopiaForgePalette.Magenta, 0xFF, 0x6B, 0x9D, "Magenta");
            AssertHex(TopiaForgePalette.Good, 0x14, 0x8D, 0x63, "Good");
            AssertHex(TopiaForgePalette.Warning, 0xD6, 0x80, 0x17, "Warning");
            AssertHex(TopiaForgePalette.Danger, 0xC8, 0x3E, 0x4D, "Danger");
            AssertHex(TopiaForgePalette.LogPanel, 0x1F, 0x25, 0x30, "LogPanel");
            AssertHex(TopiaForgePalette.SelectedTint, 0xFF, 0xE8, 0xD1, "SelectedTint");
        }

        private static void SchemesResolveAllRoles()
        {
            foreach (var scheme in new[] { TopiaForgeScheme.Paper, TopiaForgeScheme.Hud })
            {
                var colors = TopiaForgeSchemes.Resolve(scheme, null, highContrast: false);
                Assert(colors.Surface.A > 0f, scheme + ".Surface must be visible");
                Assert(colors.Text.A > 0f, scheme + ".Text must be visible");
                Assert(colors.Primary == TopiaForgePalette.Launch, scheme + ".Primary is the brand orange (constant across schemes)");
                Assert(colors.OutlineStrong == TopiaForgePalette.Launch, scheme + ".OutlineStrong is the brand orange");
                Assert(colors.Shadow.A > 0f && colors.Shadow.A < 1f, scheme + ".Shadow is translucent");
            }

            var paper = TopiaForgeSchemes.ResolvePaper(null, false);
            Assert(paper.Surface == TopiaForgePalette.Surface, "Paper surface is launcher surface");
            Assert(paper.Text == TopiaForgePalette.Ink, "Paper text is launcher ink");
            Assert(paper.Accent == TopiaForgePalette.AccentDark, "Paper accent uses the dark cyan for legibility on light surfaces");

            var hud = TopiaForgeSchemes.ResolveHud(null, false);
            Assert(hud.Text == TopiaForgePalette.Paper, "HUD text is warm paper on dark panels");
            Assert(hud.Accent == TopiaForgePalette.Accent, "HUD accent is the bright brand cyan");
            Assert(hud.Surface.A < 1f, "HUD surfaces are translucent over gameplay");

            // HUD text must actually read against HUD surfaces.
            Assert(TopiaForgeContrast.Ratio(hud.Text, TopiaForgePalette.LogPanel) >= 7f, "HUD text contrast");
            Assert(TopiaForgeContrast.Ratio(paper.Text, paper.Surface) >= 7f, "Paper text contrast");
        }

        private static void AccentOverrideBehaviors()
        {
            var loudAccent = TopiaForgeRgba.Hex(0xAAFF66); // a bright mod accent that would vanish on paper

            var hud = TopiaForgeSchemes.ResolveHud(loudAccent, false);
            Assert(hud.Accent == loudAccent, "HUD accent override is used verbatim");

            var paper = TopiaForgeSchemes.ResolvePaper(loudAccent, false);
            Assert(paper.Accent != loudAccent, "Paper accent override must be adjusted for contrast");
            Assert(TopiaForgeContrast.Ratio(paper.Accent, paper.Surface) >= 4.5f, "Paper accent override reaches 4.5:1");

            // Primary is never overridden - one brand.
            Assert(paper.Primary == TopiaForgePalette.Launch && hud.Primary == TopiaForgePalette.Launch, "accent never touches Primary");
        }

        private static void HighContrastParityWithHudColor()
        {
            // Reference vectors through the ORIGINAL ZombiesHudBehaviour.HudColor math.
            var vectors = new[]
            {
                new TopiaForgeRgba(0.52f, 1f, 0.28f, 1f),   // acid
                new TopiaForgeRgba(1f, 0.74f, 0.20f, 1f),   // amber
                new TopiaForgeRgba(0.20f, 0.92f, 1f, 1f),   // cyan
                new TopiaForgeRgba(1f, 0.24f, 0.20f, 0.5f), // danger with alpha
                new TopiaForgeRgba(0.1f, 0.1f, 0.1f, 1f),   // dim gray
                new TopiaForgeRgba(0f, 0f, 0f, 1f),         // black (guard: max <= 0 passthrough)
            };

            foreach (var input in vectors)
            {
                var expected = ReferenceHudColor(input);
                var actual = TopiaForgeContrast.Emphasize(input);
                AssertNear(actual.R, expected.R, "Emphasize R parity for " + input);
                AssertNear(actual.G, expected.G, "Emphasize G parity for " + input);
                AssertNear(actual.B, expected.B, "Emphasize B parity for " + input);
                AssertNear(actual.A, expected.A, "Emphasize preserves alpha for " + input);
            }
        }

        private static void HighContrastTransformsScheme()
        {
            var normal = TopiaForgeSchemes.ResolveHud(null, false);
            var contrast = TopiaForgeSchemes.ResolveHud(null, true);
            Assert(contrast.Surface.A > normal.Surface.A, "high contrast raises HUD surface opacity");
            Assert(contrast.Text == TopiaForgePalette.White, "high contrast HUD text is pure white");

            var paperContrast = TopiaForgeSchemes.ResolvePaper(null, true);
            Assert(TopiaForgeContrast.Ratio(paperContrast.Text, paperContrast.Surface) >
                   TopiaForgeContrast.Ratio(TopiaForgeSchemes.ResolvePaper(null, false).Text, TopiaForgePalette.Surface) - 0.001f,
                "high contrast never reduces paper text contrast");
        }

        private static void AccessibilityProfilesComposeAndClamp()
        {
            var neutral = TopiaForgeAccessibilityProfile.Default.Resolve(
                globalHighContrast: false,
                globalUiScale: 1f,
                globalReducedMotion: false,
                globalMotionIntensity: 1f);
            Assert(!neutral.HighContrast && !neutral.ReducedMotion, "neutral profile keeps boolean defaults");
            AssertNear(neutral.UiScale, 1f, "neutral profile keeps global scale");
            AssertNear(neutral.MotionIntensity, 1f, "neutral profile keeps global motion");

            var profile = new TopiaForgeAccessibilityProfile(
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

            var malformed = new TopiaForgeAccessibilityProfile(
                uiScale: float.NaN,
                motionIntensity: float.PositiveInfinity);
            AssertNear(malformed.UiScale, 1f, "NaN host scale falls back safely");
            AssertNear(malformed.MotionIntensity, 1f, "infinite host motion falls back safely");
            Assert(malformed.Equals(new TopiaForgeAccessibilityProfile()), "normalized profiles compare by value");
        }

        private static void EasingEndpointsAndShape()
        {
            foreach (TopiaForgeEase ease in Enum.GetValues(typeof(TopiaForgeEase)))
            {
                AssertNear(TopiaForgeEasing.Evaluate(ease, 0f), 0f, ease + " starts at 0");
                AssertNear(TopiaForgeEasing.Evaluate(ease, 1f), 1f, ease + " ends at 1");
                AssertNear(TopiaForgeEasing.Evaluate(ease, -5f), 0f, ease + " clamps below");
                AssertNear(TopiaForgeEasing.Evaluate(ease, 5f), 1f, ease + " clamps above");
            }

            Assert(TopiaForgeEasing.Evaluate(TopiaForgeEase.OutQuad, 0.5f) > 0.5f, "OutQuad front-loads");
            Assert(TopiaForgeEasing.Evaluate(TopiaForgeEase.InQuad, 0.5f) < 0.5f, "InQuad back-loads");

            var overshoots = false;
            for (var t = 0f; t <= 1f; t += 0.01f)
            {
                if (TopiaForgeEasing.Evaluate(TopiaForgeEase.OutBack, t) > 1f)
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

            AssertNear(TopiaForgeRoundedRectMath.FillCoverage(w / 2f, h / 2f, w, h, r), 1f, "center is fully covered");
            AssertNear(TopiaForgeRoundedRectMath.FillCoverage(-4f, h / 2f, w, h, r), 0f, "far outside is empty");
            AssertNear(TopiaForgeRoundedRectMath.FillCoverage(0f, h / 2f, w, h, r), 0.5f, "straight edge boundary is half-covered");

            // Four-corner symmetry at an arbitrary sample point near a corner.
            var reference = TopiaForgeRoundedRectMath.FillCoverage(3.2f, 4.1f, w, h, r);
            AssertNear(TopiaForgeRoundedRectMath.FillCoverage(w - 3.2f, 4.1f, w, h, r), reference, "corner symmetry (x mirror)");
            AssertNear(TopiaForgeRoundedRectMath.FillCoverage(3.2f, h - 4.1f, w, h, r), reference, "corner symmetry (y mirror)");
            AssertNear(TopiaForgeRoundedRectMath.FillCoverage(w - 3.2f, h - 4.1f, w, h, r), reference, "corner symmetry (both)");

            // Ring: hollow center, present at the edge, ring+inset-fill reconstructs the fill.
            AssertNear(TopiaForgeRoundedRectMath.RingCoverage(w / 2f, h / 2f, w, h, r, 2f), 0f, "ring center is hollow");
            Assert(TopiaForgeRoundedRectMath.RingCoverage(1f, h / 2f, w, h, r, 2f) > 0.9f, "ring covers the edge");

            // Circle sanity.
            AssertNear(TopiaForgeRoundedRectMath.CircleCoverage(12f, 12f, 24f), 1f, "circle center covered");
            AssertNear(TopiaForgeRoundedRectMath.CircleCoverage(0.2f, 0.2f, 24f), 0f, "circle corner empty");
        }

        private static void VirtualListMath()
        {
            const float row = 38f;
            const float gap = 4f;
            const float viewport = 400f;

            AssertNear(TopiaForgeVirtualListMath.ContentHeight(0, row, gap), 0f, "empty content height");
            AssertNear(TopiaForgeVirtualListMath.ContentHeight(1, row, gap), 38f, "single row height");
            AssertNear(TopiaForgeVirtualListMath.ContentHeight(10, row, gap), (10 * row) + (9 * gap), "ten row height");

            var (first, count) = TopiaForgeVirtualListMath.VisibleRange(0f, viewport, 100, row, gap);
            Assert(first == 0, "range at top starts at 0");
            Assert(count >= 10 && count <= 14, "range covers viewport plus overscan, got " + count);

            (first, count) = TopiaForgeVirtualListMath.VisibleRange(420f, viewport, 100, row, gap);
            Assert(first == 9, "scrolled range applies overscan, got " + first);
            Assert(first + count <= 100, "range never exceeds item count");

            (first, count) = TopiaForgeVirtualListMath.VisibleRange(99999f, viewport, 20, row, gap);
            Assert(first + count <= 20, "overflow scroll clamps to tail");

            Assert(TopiaForgeVirtualListMath.PoolSize(viewport, row, gap) >= 11, "pool covers viewport");
            AssertNear(TopiaForgeVirtualListMath.ClampScroll(-50f, viewport, 100, row, gap), 0f, "scroll clamps at 0");

            var max = TopiaForgeVirtualListMath.ContentHeight(100, row, gap) - viewport;
            AssertNear(TopiaForgeVirtualListMath.ClampScroll(99999f, viewport, 100, row, gap), max, "scroll clamps at max");

            AssertNear(TopiaForgeVirtualListMath.ScrollToRow(0, 500f, viewport, 100, row, gap), 0f, "scroll-to-top row");
            var current = TopiaForgeVirtualListMath.ScrollToRow(5, 100f, viewport, 100, row, gap);
            AssertNear(current, 100f, "visible row does not move the scroll");
        }

        private static void WindowMath()
        {
            var clamped = TopiaForgeWindowMath.ClampToScreen(new TopiaForgeRect(-40f, 2000f, 300f, 200f), 1920f, 1080f);
            Assert(clamped.X == 0f && clamped.Y == 880f, "window clamps inside the screen, got " + clamped);

            var (w, h) = TopiaForgeWindowMath.ClampSize(5000f, 40f, 1920f, 1080f, 200f, 120f);
            AssertNear(w, 1920f * 0.9f, "width caps at 90% viewport");
            AssertNear(h, 120f, "height respects minimum");

            var snapped = TopiaForgeWindowMath.SnapToEdges(new TopiaForgeRect(8f, 500f, 300f, 200f), 1920f, 1080f);
            Assert(snapped.X == 0f, "left edge snaps within threshold");
            Assert(snapped.Y == 500f, "far edge does not snap");

            var noSnap = TopiaForgeWindowMath.SnapToEdges(new TopiaForgeRect(40f, 500f, 300f, 200f), 1920f, 1080f);
            Assert(noSnap.X == 40f, "outside threshold does not snap");

            var rightSnap = TopiaForgeWindowMath.SnapToEdges(new TopiaForgeRect(1612f, 500f, 300f, 200f), 1920f, 1080f);
            Assert(rightSnap.X == 1620f, "right edge snaps to screen edge, got " + rightSnap.X);
        }

        private static void LayerBandAllocation()
        {
            var bands = new TopiaForgeLayerBands();
            Assert(bands.BaseOf(TopiaForgeLayerBand.Hud) < bands.BaseOf(TopiaForgeLayerBand.Window), "hud below windows");
            Assert(bands.BaseOf(TopiaForgeLayerBand.Window) < bands.BaseOf(TopiaForgeLayerBand.Modal), "windows below modals");
            Assert(bands.BaseOf(TopiaForgeLayerBand.Modal) < bands.BaseOf(TopiaForgeLayerBand.Toast), "modals below toasts");

            Assert(bands.TryAllocate(TopiaForgeLayerBand.Hud, out var first) && first == TopiaForgeLayerBands.DefaultHudBase, "first hud allocation at base");
            Assert(bands.TryAllocate(TopiaForgeLayerBand.Hud, out var second) && second == first + 1, "sequential allocation");

            var tight = new TopiaForgeLayerBands(0, 2, 4, 6, 8, 10);
            Assert(tight.TryAllocate(TopiaForgeLayerBand.Hud, out _), "tight band first slot");
            Assert(tight.TryAllocate(TopiaForgeLayerBand.Hud, out _), "tight band second slot");
            Assert(!tight.TryAllocate(TopiaForgeLayerBand.Hud, out var exhausted), "third allocation exhausts");
            Assert(exhausted == 1, "exhausted band reuses its last order");
            Assert(tight.Remaining(TopiaForgeLayerBand.Hud) == 0, "remaining reports zero");
            Assert(tight.TryRelease(exhausted), "an allocated order can be released");
            Assert(tight.Remaining(TopiaForgeLayerBand.Hud) == 0,
                "an exhaustion-shared slot is not reusable until every holder releases it");
            Assert(tight.TryRelease(exhausted), "the original holder can release the shared order");
            Assert(tight.Remaining(TopiaForgeLayerBand.Hud) == 1, "fully released slot becomes available");
            Assert(tight.TryAllocate(TopiaForgeLayerBand.Hud, out var reused) && reused == exhausted,
                "released slots are reused before a band reports exhaustion");
            Assert(!tight.TryRelease(999), "orders outside every band cannot be released");

            var threw = false;
            try
            {
                _ = new TopiaForgeLayerBands(5, 4, 3, 2, 1, 0);
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert(threw, "descending band bases are rejected");
        }

        // ---- helpers ----

        /// <summary>The original ZombiesHudBehaviour.HudColor math, kept verbatim as the parity reference.</summary>
        private static TopiaForgeRgba ReferenceHudColor(TopiaForgeRgba color)
        {
            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            if (max <= 0f)
            {
                return color;
            }

            float Lerp(float a, float b, float t) => a + ((b - a) * t);
            return new TopiaForgeRgba(
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
                var f = TopiaForgePreviewMath.Frame(1f, 1f, 1f, yaw, pitch, 1f);
                var length = Math.Sqrt((f.OffsetX * f.OffsetX) + (f.OffsetY * f.OffsetY) + (f.OffsetZ * f.OffsetZ));
                AssertNear((float)length, 1f, "offset unit length at yaw " + yaw + " pitch " + pitch);
                Assert(f.FarPlane > f.NearPlane, "far beyond near at yaw " + yaw + " pitch " + pitch);
                Assert(f.NearPlane > 0f, "positive near plane at yaw " + yaw + " pitch " + pitch);
                Assert(f.Distance > 0f, "positive distance at yaw " + yaw + " pitch " + pitch);
            }

            // Head-on view of a unit cube (half-extents 1): the view must cover the
            // full projected face exactly at margin 1.
            var headOn = TopiaForgePreviewMath.Frame(1f, 1f, 1f, 0f, 0f, 1f);
            AssertNear(headOn.OffsetX, 0f, "head-on offset x");
            AssertNear(headOn.OffsetY, 0f, "head-on offset y");
            AssertNear(headOn.OffsetZ, 1f, "head-on offset z");
            AssertNear(headOn.OrthoHalfSize, 1f, "head-on ortho covers the face");

            // Top-down view of a flat slab: height (y) contributes nothing on screen;
            // the footprint (x/z) decides the framing.
            var topDown = TopiaForgePreviewMath.Frame(2f, 0.001f, 3f, 0f, 90f, 1f);
            AssertNear(topDown.OrthoHalfSize, 3f, "top-down framing follows the footprint");

            // The three-quarter default view of a cube must cover at least the cube's
            // own half-extent and scale linearly with the margin.
            var threeQuarter = TopiaForgePreviewMath.Frame(1f, 1f, 1f, margin: 1f);
            Assert(threeQuarter.OrthoHalfSize >= 1f, "three-quarter view covers the cube");
            var withMargin = TopiaForgePreviewMath.Frame(1f, 1f, 1f, margin: 1.5f);
            AssertNear(withMargin.OrthoHalfSize, threeQuarter.OrthoHalfSize * 1.5f, "margin scales the framing");

            // Degenerate bounds (empty prefab) still produce a valid camera.
            var empty = TopiaForgePreviewMath.Frame(0f, 0f, 0f);
            Assert(empty.OrthoHalfSize >= TopiaForgePreviewMath.MinHalfSize, "degenerate bounds clamp to the minimum size");
            Assert(empty.FarPlane > empty.NearPlane, "degenerate bounds keep a valid frustum");
        }

        private static void AssertHex(TopiaForgeRgba color, byte r, byte g, byte b, string name)
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
