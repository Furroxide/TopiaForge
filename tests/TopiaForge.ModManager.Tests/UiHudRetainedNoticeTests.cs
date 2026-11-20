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
            AnUnfitAuthoredSlotDoesNotBlockAPeerNotice();
            StackedNoticesReserveActualPaint();
            PeerLayoutChangesRefreshOverflowEpisodes();
            Console.WriteLine("Fake retained HUD checks passed for body-only admission, narrow and stacked notices, peer overflow and settled calls at 0 B.");
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

        private static void StackedNoticesReserveActualPaint()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var body = string.Join(" ", Enumerable.Repeat("Many words", 100));
            using var first = Create(ui, "first", "A", "", 80f, 54f, UiHudAnchor.TopLeft);
            using var second = Create(ui, "second", "B", "", 80f, 54f, UiHudAnchor.TopLeft);
            second.SetBody(body);
            first.SetBody(body);

            var secondAtNormalScale = NoticePanel(second, Viewport);
            var firstAtNormalScale = NoticePanel(first, Viewport);
            Require(firstAtNormalScale is { X: 24f, Y: 24f, Width: 360f, Height: 76f } &&
                secondAtNormalScale.Width == 360f && secondAtNormalScale.Height == 76f &&
                !Overlaps(firstAtNormalScale, secondAtNormalScale),
                "Two 80×54 HUDs reserve the painted 360×76 peer notice without overlapping.");
            Require(SameBounds(firstAtNormalScale, NoticePanel(first, Viewport)) &&
                SameBounds(secondAtNormalScale, NoticePanel(second, Viewport)) && ui.Toasts.Count == 0,
                "Capturing in either order gives the same two positions without enqueueing a toast.");

            second.SetBody("");
            Require(second.Capture(Viewport).Nodes.Single(node => node.Path == "/") is { X: 24f, Y: 86f, Width: 80f, Height: 54f },
                "The neighbour's enlarged notice leaves its original 54-unit dock reservation for stacking.");
            second.SetBody(body);

            Require(ui.ApplyAccessibility(Doubled).Succeeded, "Set doubled UI scale for alternate viewport capture.");
            var largeViewport = new UiCaptureViewport(2560, 1440);
            var firstLarge = NoticePanel(first, largeViewport);
            var secondLarge = NoticePanel(second, largeViewport);
            Require(firstLarge is { X: 64f, Width: 960f } && !Overlaps(firstLarge, secondLarge) &&
                !Overlaps(NoticePanel(first, Viewport), NoticePanel(second, Viewport)) &&
                SameBounds(firstLarge, NoticePanel(first, largeViewport)),
                "Alternating resolutions produces independent non-overlapping paint at doubled scale.");
            Require(ui.ApplyAccessibility(UiAccessibilityPreferences.Default).Succeeded, "Restore normal UI scale.");

            first.Hide();
            Require(NoticePanel(second, Viewport) is { X: 24f, Y: 24f },
                "Hiding the first HUD releases its paint and its dock reservation.");
            first.Show();
            Require(SameBounds(secondAtNormalScale, NoticePanel(second, Viewport)),
                "Showing the first HUD restores the shared plan in original creation order.");
            first.Dispose();
            Require(NoticePanel(second, Viewport) is { X: 24f, Y: 24f } && ui.Toasts.Count == 0,
                "Retiring an earlier HUD releases both reservations and keeps the other notice visible.");

            for (var count = 0; count < 64; count++) second.SetBody(body);
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var count = 0; count < 4096; count++) second.SetBody(body);
            Require(GC.GetAllocatedBytesForCurrentThread() == start,
                "Unchanged body calls on a settled visible fallback allocate 0 B.");
        }

        private static void PeerLayoutChangesRefreshOverflowEpisodes()
        {
            using var context = new FakeModContext(new ModIdentity("example.hud", "Example Pack",
                SemanticVersion.Parse("1.0.0")));
            var ui = context.Ui;
            Require(ui.ApplyAccessibility(Doubled).Succeeded, "Arrange one 360×76 notice opening at doubled scale.");
            using var right = Create(ui, "filled-right", "Reserved", "", 552f, 492f, UiHudAnchor.TopRight);
            using var bottom = Create(ui, "filled-bottom", "Reserved", "", 360f, 350f, UiHudAnchor.BottomLeft);
            using var first = Create(ui, "gameplay", "A", "", 80f, 80f, UiHudAnchor.Left, UiHudVisibility.Gameplay);
            using var second = Create(ui, "always", "B", "", 80f, 80f, UiHudAnchor.Right);
            var body = string.Join(" ", Enumerable.Repeat("Many words", 100));
            first.SetBody(body);
            second.SetBody(body);
            Require(!first.IsVisible && NoticePanel(second, Viewport) is { Width: 720f, Height: 152f } && ui.Toasts.Count == 0,
                "In the menu, only the always-visible HUD reserves the measured notice opening.");

            context.Scenes.Load("TestCity");
            Require(first.IsVisible && NoticePanel(first, Viewport).Width == 720f &&
                HasQueuedNotice(second) && ui.Toasts.Count == 1 && ui.Toasts[0].Message == "Example Pack · HUD at right",
                "Entering gameplay fills the sole slot with the first notice and queues one kit toast for its peer.");
            first.SetBody("");
            _ = NoticePanel(second, Viewport);
            first.SetBody(body);
            Require(HasQueuedNotice(second) && ui.Toasts.Count == 1,
                "Changing a peer's body remeasures the notice without repeating the still-unfit HUD's toast.");

            first.SetBody("");
            second.SetBody("");
            second.SetBody(body);
            _ = NoticePanel(second, Viewport);
            Require(ui.Toasts.Count == 1, "The peer gets a visible notice while the first HUD fits.");
            first.SetBody(body);
            Require(HasQueuedNotice(second) && ui.Toasts.Count == 2,
                "Changing the first HUD to a notice can queue its peer after the peer's earlier unfit episode ended.");
            context.Scenes.Load("TestCityStartMenu");
            Require(!first.IsVisible && NoticePanel(second, Viewport).Width == 720f && ui.Toasts.Count == 2,
                "Leaving gameplay releases its notice and retains the existing overflow history.");
        }

        private static void AnUnfitAuthoredSlotDoesNotBlockAPeerNotice()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            using var early = Create(ui, "early-notice", "A", "", 80, 54, UiHudAnchor.BottomRight);
            using var wide = Create(ui, "wide-notice", "B", "", 1920, 1080, UiHudAnchor.TopLeft);

            early.SetBody(string.Concat(Enumerable.Repeat("Many words ", 100)));
            Require(HasQueuedNotice(early) && ui.Toasts.Count == 1,
                "A painted full-area peer leaves no space for the early notice.");
            wide.SetBody(new string('\n', 180));
            Require(!HasQueuedNotice(early) && !HasQueuedNotice(wide),
                "A later unfit full-area allocation must release its authored paint obstacle.");
            var earlyPanel = NoticePanel(early, Viewport);
            var widePanel = NoticePanel(wide, Viewport);
            Require(!Overlaps(earlyPanel, widePanel) && ui.Toasts.Count == 1,
                "A later unfit full-area allocation leaves room for both actual notices.");
            wide.SetBody("Ready");
            Require(HasQueuedNotice(early) && ui.Toasts.Count == 1,
                "Restored authored paint blocks the slot without repeating the same episode's toast.");
        }

        private static UiCapturedNode NoticePanel(FakeUiSurface surface, UiCaptureViewport viewport)
        {
            var capture = surface.Capture(viewport);
            Require(capture.Nodes.Any(node => node is { Kind: "LayoutFallback", IsRendered: true, Clipped: false }),
                "The HUD has a readable retained notice at the selected viewport.");
            return capture.Nodes.Single(node => node.Path == "/");
        }

        private static bool HasQueuedNotice(FakeUiSurface surface) => surface.Capture(Viewport).Nodes.Any(node =>
            node is { Kind: UiHudOverflowNotice.CaptureKind, IsRendered: false, Clipped: false });

        private static bool Overlaps(UiCapturedNode first, UiCapturedNode second) =>
            first.X < second.X + second.Width && second.X < first.X + first.Width &&
            first.Y < second.Y + second.Height && second.Y < first.Y + first.Height;

        private static FakeUiSurface Create(FakeUiService ui, string id, string title, string body, float width, float height,
            UiHudAnchor anchor, UiHudVisibility visibility = UiHudVisibility.Always)
        {
            var request = new UiSurfaceRequest(id, title, body, UiSurfaceKind.Hud, width, height)
                .WithHudAnchor(anchor).WithHudVisibility(visibility);
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
