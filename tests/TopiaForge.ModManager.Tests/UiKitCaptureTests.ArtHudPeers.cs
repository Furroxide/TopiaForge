using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void GuestArtworkNoticesMeasureAndReportOverflow()
        {
            using var context = new FakeModContext(new ModIdentity("io.github.furroxide.topiaforge.uigallery", "Example Pack",
                SemanticVersion.Parse("1.0.0")));
            var ui = context.Ui;
            new GalleryArtFixtures().Install(ui);
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 2f)).Succeeded, "Use the scale-2 HUD safe area.");
            using var left = Peer("left", 400f, UiHudAnchor.TopLeft);
            using var right = Peer("right", 252f, UiHudAnchor.TopRight);
            var art = ThemeForWire(ui, new UiThemeRef("preview-art"))!;
            var tiny = new UiImage(FoundationArtPages.Card, "Private art label", 1f, 1f, id: "tiny");
            var (received, envelope) = ReceiveArt(tiny.WithWireMetadata(1f, 1f, art.Digest.Span));
            using var guest = Make(CreateReceivedSurface(ui, new UiSurfaceRequest("guest", "Private title", "", UiSurfaceKind.Hud,
                80f, 80f, received).WithHudAnchor(UiHudAnchor.Top).WithHudVisibility(UiHudVisibility.Always), envelope));

            var leftBounds = left.Capture(Viewport).Nodes[0];
            var rightBounds = right.Capture(Viewport).Nodes[0];
            var opening = guest.Capture(Viewport);
            var panel = opening.Nodes[0];
            var artBounds = Row(opening, "tiny");
            Check(panel.X == leftBounds.X + leftBounds.Width && panel.Width == 520f &&
                panel.X + panel.Width == rightBounds.X && panel.Height < 2f * UiImageNotice.RemoteHudHeight &&
                opening.Nodes.Any(node => node.Kind == "ImageFallback" && node.IsRendered && !node.Clipped) &&
                opening.CheckAccessibility().Single().Kind == UiAccessibilityIssueKind.MissingAsset &&
                SameBounds(leftBounds, left.Capture(Viewport).Nodes[0]) && SameBounds(rightBounds, right.Capture(Viewport).Nodes[0]) &&
                ui.Toasts.Count == 0,
                "The Fake guest notice measures its height and paints in a 260-unit opening beside stable peers.");

            right.Hide();
            using var occupyingRight = Peer("occupying-right", 512f, UiHudAnchor.TopRight);
            var queued = guest.Capture(Viewport);
            var diagnostic = queued.Nodes.SingleOrDefault(row => row.Kind == UiHudOverflowNotice.CaptureKind);
            var queuedArt = Row(queued, "tiny");
            const string safe = "Example Pack · HUD at top";
            Check(queuedArt is { Width: 2f, Height: 2f, IsRendered: false } && queuedArt.X == artBounds.X &&
                queuedArt.Y == artBounds.Y && diagnostic is { SemanticLabel: safe, Text: "", IsRendered: false, Clipped: false } &&
                queued.Nodes.All(row => !row.IsRendered && row.Kind != "ImageFallback") &&
                queued.CheckAccessibility().Single().Kind == UiAccessibilityIssueKind.MissingAsset &&
                ui.Toasts.Count == 1 && ui.Toasts[0].Message == safe,
                "An occupied Fake viewport preserves the image box and records the same safe message as the one queued toast.");

            FakeUiSurface Peer(string id, float width, UiHudAnchor anchor) => Make(ui,
                new UiSurfaceRequest(id, "Reserved", "", UiSurfaceKind.Hud, width, 430f, new UiText("Ready"))
                    .WithHudAnchor(anchor).WithHudVisibility(UiHudVisibility.Always));

            static bool SameBounds(UiCapturedNode a, UiCapturedNode b) =>
                a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
        }

        private static void GuestArtworkUsesTheWideOpening()
        {
            using var context = new FakeModContext(new ModIdentity("io.github.furroxide.topiaforge.uigallery", "Example Pack",
                SemanticVersion.Parse("1.0.0")));
            var ui = context.Ui;
            new GalleryArtFixtures().Install(ui);
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 2f)).Succeeded, "Use the scale-2 HUD safe area.");
            var art = ThemeForWire(ui, new UiThemeRef("preview-art"))!;
            var tiny = new UiImage(FoundationArtPages.Card, "Supply card", 1f, 1f, id: "tiny");
            var (received, envelope) = ReceiveArt(tiny.WithWireMetadata(1f, 1f, art.Digest.Span));
            using var guest = Make(CreateReceivedSurface(ui, new UiSurfaceRequest("guest", new string('F', 114), "", UiSurfaceKind.Hud,
                80f, 146f, received).WithHudAnchor(UiHudAnchor.TopRight).WithHudVisibility(UiHudVisibility.Always), envelope));
            using var peer = Make(ui, new UiSurfaceRequest("peer", "Reserved", "", UiSurfaceKind.Hud, 912f, 276f, new UiText("Ready"))
                .WithHudAnchor(UiHudAnchor.TopRight).WithHudVisibility(UiHudVisibility.Always));

            var peerBefore = peer.Capture(Viewport).Nodes.Single(row => row.Path == "/");
            var capture = guest.Capture(Viewport);
            var panel = capture.Nodes.SingleOrDefault(row => row.Path == "/");
            var peerAfter = peer.Capture(Viewport).Nodes.Single(row => row.Path == "/");
            const float safeTop = 48f;
            Check(peerBefore.Y - safeTop == 308f && panel is { IsRendered: true, Y: safeTop } &&
                panel.Width > 2f * UiImageNotice.RemoteHudWidth && panel.Y + panel.Height <= peerBefore.Y &&
                capture.Nodes.Any(row => row.Kind == "ImageFallback" && row.IsRendered && !row.Clipped) &&
                capture.Nodes.All(row => row.Kind != UiHudOverflowNotice.CaptureKind) &&
                capture.CheckAccessibility().Single().Kind == UiAccessibilityIssueKind.MissingAsset &&
                peerBefore.X == peerAfter.X && peerBefore.Y == peerAfter.Y && peerBefore.Width == peerAfter.Width &&
                peerBefore.Height == peerAfter.Height && ui.Toasts.Count == 0,
                "The Fake guest artwork notice paints in the measured wide opening beside its unchanged peer.");
        }

        private static void ArtworkAndLayoutNoticesSharePaintBounds()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            new GalleryArtFixtures().Install(ui);
            using var early = Make(ui, new UiSurfaceRequest("layout-notice", "Status", "", UiSurfaceKind.Hud, 80, 54)
                .WithHudAnchor(UiHudAnchor.BottomRight).WithHudVisibility(UiHudVisibility.Always));
            early.SetBody(string.Concat(Enumerable.Repeat("Many words ", 100)));
            using var wide = Make(ui, new UiSurfaceRequest("artwork-notice", "Supplies", "", UiSurfaceKind.Hud, 1920, 1080,
                new UiImage(FoundationArtPages.Card, "Supply card", 1f, 1f, id: "tiny"))
                .WithHudAnchor(UiHudAnchor.TopLeft).WithHudVisibility(UiHudVisibility.Always));

            var layout = early.Capture(Viewport);
            var artwork = wide.Capture(Viewport);
            Check(layout.Nodes.Any(node => node.Kind == "LayoutFallback" && node.IsRendered) &&
                artwork.Nodes.Any(node => node.Kind == "ImageFallback" && node.IsRendered),
                "A pending artwork HUD reserves its notice while the retained peer keeps its own measured slot.");
            var layoutPanel = layout.Nodes.Single(node => node.Path == "/");
            var artworkPanel = artwork.Nodes.Single(node => node.Path == "/");
            Check(artwork.CheckAccessibility().Single().Kind == UiAccessibilityIssueKind.MissingAsset &&
                !Overlaps(layoutPanel, artworkPanel) && ui.Toasts.Count == 0,
                "Pending artwork and retained layout paint disjoint notices while keeping the image diagnostic.");

            ui.CompletePendingThemeRegistrations();
            Check(Row(wide.Capture(Viewport), "tiny").IsRendered &&
                early.Capture(Viewport).Nodes.Any(node => node.Kind == UiHudOverflowNotice.CaptureKind) && ui.Toasts.Count == 1,
                "Ready artwork restores its authored frame and queues the peer once when it cannot paint.");
            wide.Hide();
            Check(early.Capture(Viewport).Nodes.Any(node => node.Kind == "LayoutFallback" && node.IsRendered) && ui.Toasts.Count == 1,
                "Hiding the artwork reopens the peer notice's actual slot.");

            static bool Overlaps(UiCapturedNode a, UiCapturedNode b) =>
                a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
        }
    }
}
