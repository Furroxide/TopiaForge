using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiHudRetainedNoticeTests
    {
        private static readonly UiCaptureViewport Viewport = new UiCaptureViewport(1920, 1080);
        private static readonly UiAccessibilityPreferences Doubled = new UiAccessibilityPreferences(uiScale: 2f);

        internal static void Run()
        {
            BodyOnlyHudRetainsItsBodyAndRecovers();
            BodyOnlyOverflowUsesTheSameEpisode();
            MeasuredNoticeUsesTheNarrowOpening();
            Console.WriteLine("Fake retained HUD checks passed for body-only admission, SetBody recovery, the 300-unit slot and 4,096 settled calls at 0 B.");
        }

        private static void BodyOnlyHudRetainsItsBodyAndRecovers()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var typography = UiThemePackParser.Default;
            var bodyStyle = typography.GetText(UiTextStyle.Body);
            var glyph = bodyStyle.ReferenceSize * (0.65f + bodyStyle.TrackingEm);
            var body = new string('P', (int)Math.Ceiling(1880f / glyph));
            var height = 32f + typography.GetText(UiTextStyle.Heading).ReferenceSize + 2.6f * bodyStyle.ReferenceSize;
            Require(ui.ApplyAccessibility(Doubled).Succeeded, "The body-only HUD uses the viewer's doubled UI scale.");
            using var surface = Create(ui, "body-only", "Readiness", body, 1080f, height, UiHudAnchor.TopLeft);
            var fallback = surface.Capture(Viewport);
            Require(surface.Content == null && surface.Body == body &&
                fallback.Nodes.Any(node => node.Kind == "LayoutFallback" && node.IsRendered && !node.Clipped) &&
                fallback.Nodes.All(node => node.Kind != "SurfaceBody") && ui.Toasts.Count == 0,
                "The actual body-only creation retains legacy data and displays an in-bounds notice for the overlong body.");

            surface.SetBody("Ready");
            var recovered = surface.Capture(Viewport);
            Require(surface.Content == null &&
                recovered.Nodes.Single(node => node.Kind == "SurfaceBody") is { Text: "Ready", IsRendered: true, Clipped: false } &&
                recovered.Nodes.Single(node => node.Path == "/") is { X: 48f, Width: 1824f } &&
                recovered.CheckAccessibility().Count == 0,
                "SetBody replaces the retained notice with readable body-only chrome inside its physical viewport.");

            for (var index = 0; index < 64; index++) surface.SetBody("Ready");
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 4096; index++) surface.SetBody("Ready");
            Require(GC.GetAllocatedBytesForCurrentThread() == start && ui.Toasts.Count == 0,
                "The settled Fake body-only SetBody path measures zero allocations and emits no toast.");

            surface.SetBody(body);
            Require(surface.Capture(Viewport).Nodes.Any(node => node.Kind == "LayoutFallback" && node.IsRendered),
                "A later body change measures its own fit.");
            Require(ui.ApplyAccessibility(UiAccessibilityPreferences.Default).Succeeded, "Restore normal scale.");
            var restored = surface.Capture(Viewport);
            Require(restored.Nodes.Single(node => node.Path == "/").Width == 1080f &&
                restored.Nodes.Single(node => node.Kind == "SurfaceBody") is { IsRendered: true, Clipped: false } label &&
                label.SemanticLabel == body && restored.CheckAccessibility().Count == 0,
                "Scale recovery restores the full authored frame and body text.");
        }

        private static void BodyOnlyOverflowUsesTheSameEpisode()
        {
            using var context = new FakeModContext(new ModIdentity("example.hud", "Example Pack",
                SemanticVersion.Parse("1.0.0")));
            var ui = context.Ui;
            Require(ui.ApplyAccessibility(Doubled).Succeeded, "Use the same physical safe area.");
            var title = "Private heading " + new string('W', 240);
            using var body = Create(ui, "overlaid", title, "Building", 1080f, 110f, UiHudAnchor.TopRight);
            using var peer = Create(ui, "occupied", "Reserved", "", 912f, 396f, UiHudAnchor.TopRight);
            var peerBefore = peer.Capture(Viewport).Nodes.Single(node => node.Path == "/");
            var privateBody = string.Join("\n", Enumerable.Repeat("Private body", 8));
            body.SetBody(privateBody);
            var queued = body.Capture(Viewport);
            Require(ui.Toasts.Count == 1 && ui.Toasts[0].Message == "Example Pack · HUD at top right" &&
                queued.Nodes.Any(node => node.Kind == UiHudOverflowNotice.CaptureKind && !node.IsRendered && !node.Clipped) &&
                queued.Nodes.All(node => !node.IsRendered),
                "Body-only overflow queues one manifest-and-dock notice when the entire viewport is occupied.");
            body.SetBody(privateBody + "\nmore");
            Require(ui.Toasts.Count == 1 && body.Content == null, "An unfit body edit stays within the same overflow episode.");
            body.SetBody("Ready");
            Require(body.Capture(Viewport).Nodes.Any(node => node.Kind == "SurfaceBody" && node.IsRendered) &&
                ui.Toasts.Count == 1 && SameBounds(peerBefore, peer.Capture(Viewport).Nodes.Single(node => node.Path == "/")),
                "A fit body recovers in its existing dock while preserving the neighbouring HUD and toast history.");
            body.SetBody(privateBody);
            Require(ui.Toasts.Count == 2, "A later independent body overflow gets one new toast.");
        }

        private static void MeasuredNoticeUsesTheNarrowOpening()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            Require(ui.ApplyAccessibility(Doubled).Succeeded, "Use a 912-unit safe width at scale 2.");
            using var left = Create(ui, "left-peer", "Reserved", "", 400f, 430f, UiHudAnchor.TopLeft);
            using var right = Create(ui, "right-peer", "Reserved", "", 212f, 430f, UiHudAnchor.TopRight);
            var kit = UiThemePackParser.Default;
            var heading = kit.GetText(UiTextStyle.Heading);
            var glyph = heading.ReferenceSize * (0.65f + heading.TrackingEm);
            var title = new string('W', (int)Math.Ceiling(302f / glyph));
            using var surface = Create(ui, "narrow", title, new string('W', 200), 80f, 80f, UiHudAnchor.Top);
            var leftBefore = left.Capture(Viewport).Nodes.Single(node => node.Path == "/");
            var rightBefore = right.Capture(Viewport).Nodes.Single(node => node.Path == "/");
            var narrow = surface.Capture(Viewport);
            Require(narrow.Nodes.Any(node => node.Kind == "LayoutFallback" && node.IsRendered && !node.Clipped) &&
                HasOnlyRetainedDiagnostic(narrow) && ui.Toasts.Count == 0,
                "The narrow Fake opening renders the measured title and notice instead of queuing.");
            var shell = narrow.Nodes.Single(node => node.Path == "/");
            Require(shell.X == leftBefore.X + leftBefore.Width && shell.Width == 600f &&
                shell.X + shell.Width == rightBefore.X &&
                SameBounds(leftBefore, left.Capture(Viewport).Nodes.Single(node => node.Path == "/")) &&
                SameBounds(rightBefore, right.Capture(Viewport).Nodes.Single(node => node.Path == "/")),
                "The 300-unit Fake panel fits between both neighbours without moving them.");

            right.Hide();
            var wide = surface.Capture(Viewport);
            var wideShell = wide.Nodes.Single(node => node.Path == "/");
            Require(wideShell.Width == 720f && wideShell.Height < shell.Height &&
                HasOnlyRetainedDiagnostic(wide) && ui.Toasts.Count == 0,
                "When room opens up, the preferred 360-unit width returns and the extra wrapped line disappears.");
        }

        private static FakeUiSurface Create(FakeUiService ui, string id, string title, string body, float width, float height,
            UiHudAnchor anchor)
        {
            var request = new UiSurfaceRequest(id, title, body, UiSurfaceKind.Hud, width, height)
                .WithHudAnchor(anchor).WithHudVisibility(UiHudVisibility.Always);
            var created = ui.CreateSurface(request);
            if (created.TryGetValue(out var surface) && surface is FakeUiSurface fake) return fake;
            throw new InvalidOperationException(created.ErrorMessage);
        }

        private static bool SameBounds(UiCapturedNode left, UiCapturedNode right) =>
            left.X == right.X && left.Y == right.Y && left.Width == right.Width && left.Height == right.Height;

        private static bool HasOnlyRetainedDiagnostic(UiTreeCapture capture) => capture.CheckAccessibility()
            .SingleOrDefault() is { Kind: UiAccessibilityIssueKind.ClippedContent, Path: "/0" };

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
