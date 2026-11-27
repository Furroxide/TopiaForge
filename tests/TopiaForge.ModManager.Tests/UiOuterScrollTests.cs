using System;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiOuterScrollTests
    {
        private static readonly UiCaptureViewport Reference = new UiCaptureViewport(1920, 1080);

        internal static void Run()
        {
            SharedProjection();
            FakeWindowsAndTools();
            Console.WriteLine("Outer scroll admission passed full hit and focus bounds, nested clips, readable long text, accessibility fallback and recovery.");
        }

        private static void SharedProjection()
        {
            var small = UiLayoutConstraints.ForVerticalScroll(180d, 70d, beforeComposition: 140d, afterComposition: 8d);
            var wide = UiLayoutConstraints.ForVerticalScroll(180d, 120d, beforeComposition: 140d, afterComposition: 8d);
            var button = new UiButton("bounded-hit", "Launch", static () => { });
            var positioned = new UiPositioned(new UiSizedBox(button, height: 96f), 0f, 96f, width: 145f);
            var nested = new UiScroll(new UiStack(new UiNode[] { positioned }, 180f, 240f), 112f);
            Reject(() => UiLayoutEngine.Arrange(nested, small, Measure), "An inner scroll never bypasses the measured outer aperture.");
            Check(UiLayoutEngine.Arrange(nested, wide, Measure).Placements.Any(node => ReferenceEquals(node.Node, button)),
                "The larger aperture reaches the full target even after scrolling both ancestors.");
            Reject(() => UiLayoutEngine.Arrange(new UiSizedBox(button, height: 66f),
                UiLayoutConstraints.ForVerticalScroll(180d, 70d, beforeComposition: 8d, afterComposition: 8d), Measure),
                "A hit that fits by itself still needs room for its measured focus outsets.");

            var tallText = new UiText("Measured multiline text");
            var scrollable = new UiColumn(new UiNode[] { tallText, new UiButton("after", "After", static () => { }) });
            var arranged = UiLayoutEngine.Arrange(scrollable, small, Measure);
            Check(arranged.Size.Height > 400d && arranged.Placements.Single(node => ReferenceEquals(node.Node, tallText)).Bounds.Height == 400d,
                "Multiline text can remain taller than the aperture when every complete line is reachable.");
            Reject(() => UiLayoutEngine.Arrange(scrollable, UiLayoutConstraints.ForVerticalScroll(180d, 19d), Measure),
                "The outer scroller requires enough room for one complete measured text line.");

            static UiLayoutLeafMeasurement Measure(UiNode node, UiLayoutConstraints limits, bool disabled) => node is UiText
                ? new UiLayoutLeafMeasurement(new UiLayoutExtent(80d, 400d), UiLayoutPaintKind.Protected, scrollLineHeight: 20d)
                : new UiLayoutLeafMeasurement(new UiLayoutExtent(100d, 40d), UiLayoutPaintKind.Protected,
                    focusOutsets: new UiLayoutOutsets(4d, 4d, 4d, 4d));
        }

        private static void FakeWindowsAndTools()
        {
            foreach (var kind in new[] { UiSurfaceKind.Window, UiSurfaceKind.FullscreenTool })
            {
                using var life = new FakeModLifetime();
                var ui = new FakeUiService(life);
                var impossible = new UiSizedBox(new UiButton("oversized", "Oversized", static () => { }), height: 2000f);
                var refused = ui.CreateSurface(new UiSurfaceRequest("oversized", "Outer viewport", "", kind, 500f, 260f, impossible));
                Check(refused.ErrorCode == ModErrorCode.InvalidArgument && refused.ErrorMessage.Contains("/0/0", StringComparison.Ordinal),
                    "The public SDK Fake rejects a 2,000-unit hit with the offending path in Window and FullscreenTool.");

                var line = string.Join(" ", Enumerable.Repeat("Receiver position and steering checks complete.", 64));
                var readable = new UiText(line, id: "wrapped");
                var buttons = Enumerable.Range(0, 28).Select(index =>
                    new UiButton("row-" + index, "Inspect relay", static () => { })).Cast<UiNode>();
                var rows = new UiColumn(new UiNode[] { readable }.Concat(buttons).Append(new UiDivider(id: "tail")));
                var opened = ui.CreateSurface(new UiSurfaceRequest("reachable", "Reachable scroll", "A body inside the same scroll content.",
                    kind, 500f, 260f, rows));
                Check(opened.Succeeded, "Many independently reachable controls and a multiline label admit in the real Fake route.");
                using var surface = (FakeUiSurface)opened.Value!;
                var before = surface.Capture(Reference);
                var last = before.Nodes.Single(node => node.Id == "row-27" && node.Kind == nameof(UiButton));
                var first = before.Nodes.Single(node => node.Id == "wrapped" && node.Kind == nameof(UiText));
                Check(before.CheckAccessibility().Count == 0 && first.SemanticLabel == line && first.IsRendered &&
                    !last.IsRendered, "The starting crop preserves the full text and only paints visible controls.");
                var frame = before.Nodes[0];
                var tail = before.Nodes.Single(node => node.Id == "tail");
                var opening = tail.Y + tail.Height - (frame.Y + frame.Height - 12f);
                var moved = CaptureScroll(surface, Reference, "Rest", opening, null);
                Check(moved.CheckAccessibility().Count == 0 &&
                    moved.Nodes.Any(node => node.Id == "row-27" && node.Kind == nameof(UiButton) && node.IsRendered && !node.Clipped) &&
                    moved.Nodes.Any(node => node.Id == "tail" && node.IsRendered),
                    "A legal outer position reveals the final control and its divider without changing admission.");
                Check(surface.SetContent(impossible).ErrorCode == ModErrorCode.InvalidArgument && ReferenceEquals(surface.Content, rows),
                    "Refused replacement preserves the admitted tree and its scrollable geometry.");

                var settle = AllocationProbe.Measure("settled Fake scroll admission", () =>
                    Check(surface.SetContent(rows).Succeeded, "Identical scroll content reapplication succeeds."),
                    frames: 4096, warmups: 32);
                Check(settle == 0, "An unchanged Fake outer viewport reapply allocates 0 B.");

                var tall = new UiSizedBox(new UiButton("resize-hit", "Resize target", static () => { }),
                    height: kind == UiSurfaceKind.Window ? 510f : 720f);
                var retained = ui.CreateSurface(new UiSurfaceRequest("retained", "Retained viewport", "", kind, 680f, 700f, tall));
                Check(retained.Succeeded, "The independently measured large viewport initially admits the complete protected group.");
                using var resized = (FakeUiSurface)retained.Value!;
                Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 2f)).Succeeded, "Use large readable scale.");
                var fallback = CaptureScroll(resized, Reference, "Rest", 180f, null);
                Check(fallback.CheckAccessibility().Any(issue => issue.Kind == UiAccessibilityIssueKind.ClippedContent) &&
                    fallback.Nodes.Any(node => node.Kind == "LayoutFallback" && node.IsRendered) &&
                    fallback.Nodes.All(node => node.Id != "resize-hit" || !node.IsRendered) && ReferenceEquals(resized.Content, tall),
                    "A retained protected group shows a fallback when the outer aperture shrinks at 2×.");
                Check(ui.ApplyAccessibility(new UiAccessibilityPreferences()).Succeeded, "Restore readable scale.");
                var recovered = resized.Capture(Reference);
                Check(recovered.CheckAccessibility().Count == 0 &&
                    recovered.Nodes.Any(node => node.Id == "resize-hit" && node.IsRendered && !node.Clipped) &&
                    ReferenceEquals(resized.Content, tall), "Restoring the aperture restores the original authored control.");
            }
        }

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CaptureForFixture")]
        private static extern UiTreeCapture CaptureScroll(FakeUiSurface surface, UiCaptureViewport viewport, string state,
            float offset, bool? hud);

        private static void Reject(Action arrange, string message)
        {
            try { arrange(); }
            catch (UiLayoutException error) when (error.Failure == UiLayoutFailure.ClippedContent) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
