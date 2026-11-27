using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitLayoutCaptureTests
    {
        private static readonly UiCaptureViewport Viewport = new UiCaptureViewport(1920, 1080);

        internal static void Run()
        {
            DisabledCardKeepsItsTextAndStopsCallbacks();
            LaterPaintersCannotReplaceAnAdmittedTree();
            AScaledHudFallsBackAndRecovers();
            ScrollApertureAdmissionIsTransactional();
            AStackedHudNoticeFindsRoomWithoutMovingItsPeer();
            FullHudQueuesOnlyManifestIdentity();
            HudOverflowRecorderBoundaryTests.Run();
            TheNativeLayoutNoticeFixtureRequiresItsExactAuthoredShape();
        }

        private static void DisabledCardKeepsItsTextAndStopsCallbacks()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            Require(ui.AddThemeFixture("""
                {"schema":1,"id":"card-sample","paper":{"colors":{"raised":{"fill":"#10141B","on":"#FFFFFF"}}},
                    "styles":[{"id":"imported-panel","target":"card","radius":"none","fill":"raised","border":"standard"}]}
                """).Succeeded, "The card style must be declared by the theme fixture.");
            var invoked = 0;
            var content = new UiCard(new UiPadding(new UiButton("confirm", "Confirm", () => invoked++), new UiInsets(UiSpace.Sm)),
                header: new UiText("Shelter", id: "shelter-heading"), style: new UiStyleRef("imported-panel"),
                selected: true, disabled: true, id: "shelter-card");
            using var surface = Make(ui, new UiSurfaceRequest("shelter", "Shelter", "", content: content)
                .WithTheme(new UiThemeRef("card-sample")));
            ui.CompletePendingThemeRegistrations();
            Require(surface.TryFindNode("shelter-heading", out var heading) && heading is UiText, "The card header shares the node visitor.");
            Require(surface.ActivateButton("confirm").ErrorCode == ModErrorCode.InvalidState && invoked == 0,
                "A disabled Card stops callbacks below another layout wrapper.");
            var capture = surface.Capture(Viewport);
            Require(capture.Nodes.Single(row => row.Id == "shelter-card") is
            { StyleId: "imported-panel", CornerRadiusPixels: 0, Selected: true, Disabled: true },
                "The resolved Card captures its named flat style and state.");
            Require(capture.Nodes.Single(row => row.Id == "shelter-heading") is { FillColor: "#10141B", InkColor: "#FFFFFF" },
                "Text inside a styled Card uses ink on its actual effective fill.");
            Require(capture.CheckAccessibility().Count == 0, "The selected and disabled card remains readable.");
        }

        private static void LaterPaintersCannotReplaceAnAdmittedTree()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var title = new UiPositioned(new UiText("Route clear", id: "route"), left: 8f, top: 8f, width: 200f);
            var background = new UiBox(250f, 80f, UiPaintRole.Raised);
            var admitted = new UiStack(new UiNode[] { background, title }, width: 250, height: 80);
            using var surface = Make(ui, new UiSurfaceRequest("layout", "Layout", "", content: admitted));
            var rejected = surface.SetContent(new UiStack(new UiNode[] { title, background }, width: 250, height: 80));
            Require(rejected.ErrorCode == ModErrorCode.InvalidArgument && ReferenceEquals(surface.Content, admitted),
                "A later Box cannot cover admitted text or replace the previous content on failure.");
            Require(surface.Capture(Viewport).Nodes.Single(row => row.Id == "route").IsRendered,
                "The protected label remains visible after a rejected replacement.");
        }

        private static void AScaledHudFallsBackAndRecovers()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var request = FoundationLayoutPages.LayoutNotice();
            var content = request.Content!;
            using var surface = Make(ui, request);
            Require(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 2f)).Succeeded, "The viewer doubles the UI scale.");
            var overflow = surface.Capture(Viewport);
            Require(ReferenceEquals(content, surface.Content) && overflow.Nodes.Any(row => row.Kind == "LayoutFallback"
                && row.IsRendered && row.Text == "Panel does not fit at this scale" && row.ContrastRatio >= 4.5f),
                "An oversized retained HUD shows the readable notice without replacing its tree.");
            Require(overflow.CheckAccessibility().Any(issue => issue.Kind == UiAccessibilityIssueKind.InvalidLayout),
                "The captured issue identifies the rejected geometry.");
            var replacementId = new UiSurfaceRequest("second-wide", request.Title, request.Body, request.Kind,
                request.Width, request.Height, request.Content).WithHudAnchor(UiHudAnchor.TopRight);
            Require(ui.CreateSurface(replacementId).ErrorCode == ModErrorCode.InvalidArgument &&
                surface.SetContent(FoundationLayoutPages.LayoutNotice().Content!).ErrorCode == ModErrorCode.InvalidArgument &&
                ReferenceEquals(content, surface.Content) && ui.Surfaces.Count == 1,
                "At double scale, new admission refuses the same physically unfit tree and preserves the retained notice.");
            Require(ui.ApplyAccessibility(UiAccessibilityPreferences.Default).Succeeded
                && surface.Capture(Viewport).Nodes.Single(row => row.Id == "positioned-status").IsRendered,
                "The original HUD returns automatically when it fits again.");
        }

        private static void AStackedHudNoticeFindsRoomWithoutMovingItsPeer()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            using var other = Make(ui, new UiSurfaceRequest("neighbour", "Neighbour", "", UiSurfaceKind.Hud,
                340f, 430f, new UiText("Protected")).WithHudAnchor(UiHudAnchor.TopLeft));
            var content = new UiText("OK", id: "compact-status");
            using var surface = Make(ui, new UiSurfaceRequest("compact", "Ready", "", UiSurfaceKind.Hud,
                80f, 80f, content).WithHudAnchor(UiHudAnchor.TopLeft));
            Require(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 2f)).Succeeded, "The viewer can prefer double scale.");
            var before = other.Capture(Viewport).Nodes.Single(row => row.Path == "/");
            var rendered = surface.Capture(Viewport);
            var notice = rendered.Nodes.Single(row => row.Kind == "LayoutFallback");
            var panel = rendered.Nodes.SingleOrDefault(row => row.Path == "/");
            var after = other.Capture(Viewport).Nodes.Single(row => row.Path == "/");
            Require(panel != null && notice.IsRendered && !notice.Clipped && notice.ContrastRatio >= 4.5f &&
                rendered.Nodes.Any(row => row.Kind == "SurfaceTitle" && row.Text == "Ready" && row.IsRendered && !row.Clipped) &&
                (panel.X >= before.X + before.Width || panel.Y >= before.Y + before.Height),
                "A taller notice outside the original dock renders at a clear alternate slot with its readable surface title.");
            Require(panel!.Y + panel.Height <= 1080f * 0.88f - 48f && before.X == after.X && before.Y == after.Y &&
                before.Width == after.Width && before.Height == after.Height && ReferenceEquals(surface.Content, content),
                "The alternate slot leaves the subtitle reserve, neighbouring HUD and authored content intact.");
            Require(ui.ApplyAccessibility(UiAccessibilityPreferences.Default).Succeeded &&
                surface.Capture(Viewport).Nodes.Any(row => row.Id == "compact-status" && row.IsRendered),
                "The small authored slot recovers when the layout fits again.");
        }

        private static void FullHudQueuesOnlyManifestIdentity()
        {
            using var context = new FakeModContext(new ModIdentity("example.opaque", "Example\nPack\u202E",
                SemanticVersion.Parse("1.0.0")));
            var ui = context.Ui;
            var content = new UiStack(new UiNode[]
            {
                new UiPositioned(new UiText("Ready", id: "retained"), left: 920, top: 0, width: 64),
            }, width: 1000, height: 20);
            var title = "private title " + new string('F', 240);
            using var surface = Make(ui, Hud("wide", title, "private body", 1080, 120, content));
            using var peer = Make(ui, Hud("reserved", "Space reserved", "", 912, 396, null));
            var original = surface.Capture(Viewport).Nodes.Single(row => row.Path == "/");
            var doubled = new UiAccessibilityPreferences(uiScale: 2f);
            Require(ui.ApplyAccessibility(doubled).Succeeded && ui.Toasts.Count == 1 &&
                ui.Toasts[0].Message == "Example Pack · HUD at top right" && ui.Toasts[0].Tone == UiTone.Neutral,
                "An occupied Fake HUD queues only its admitted display name and kit dock.");
            var peerBefore = peer.Capture(Viewport).Nodes.Single(row => row.Path == "/");
            var queued = surface.Capture(Viewport);
            Require(queued.Nodes.Any(row => row.Kind == UiHudOverflowNotice.CaptureKind &&
                row.SemanticLabel == "Example Pack · HUD at top right" && !row.IsRendered && !row.Clipped) &&
                !queued.Nodes.Any(row => row.IsRendered) && ReferenceEquals(surface.Content, content) &&
                peerBefore.X == 48f && peerBefore.Y == 304f && peerBefore.Width == 1824f,
                "The read-only capture records an explicit queued notice with the original composition still admitted.");
            for (var index = 0; index < 3; index++) _ = surface.Capture(Viewport);
            Require(ui.Toasts.Count == 1, "Reading the same pending capture never sends another toast.");

            for (var index = 0; index < 64; index++) surface.SetContent(content);
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 4096; index++)
                Require(surface.SetContent(content).Succeeded, "The unchanged composition retains its original admission.");
            Require(GC.GetAllocatedBytesForCurrentThread() == start && ui.Toasts.Count == 1,
                "Settled overflow checks allocate zero bytes and send no repeat toast.");

            peer.Hide();
            Require(surface.Capture(Viewport).Nodes.Any(row => row.Kind == "LayoutFallback" && row.IsRendered) &&
                ui.Toasts.Count == 1, "An available slot displays the panel notice during the same episode.");
            peer.Show();
            var peerAfter = peer.Capture(Viewport).Nodes.Single(row => row.Path == "/");
            Require(surface.Capture(Viewport).Nodes.Any(row => row.Kind == UiHudOverflowNotice.CaptureKind) &&
                ui.Toasts.Count == 1 && peerBefore.X == peerAfter.X && peerBefore.Y == peerAfter.Y &&
                peerBefore.Width == peerAfter.Width && peerBefore.Height == peerAfter.Height,
                "The occupied episode resumes with no repeat and no change to the other panel.");

            Require(ui.ApplyAccessibility(UiAccessibilityPreferences.Default).Succeeded, "Restore the initial geometry.");
            var recovered = surface.Capture(Viewport);
            var shell = recovered.Nodes.Single(row => row.Path == "/");
            Require(shell.X == original.X && shell.Y == original.Y && shell.Width == original.Width &&
                shell.Height == original.Height && recovered.Nodes.Single(row => row.Id == "retained").IsRendered,
                "The original title, body and measured panel are restored when the retained content fits.");
            Require(ui.ApplyAccessibility(doubled).Succeeded && ui.Toasts.Count == 2,
                "A recovered panel can report a later independent overflow episode.");

            static UiSurfaceRequest Hud(string id, string title, string body, float width, float height, UiNode? content) =>
                new UiSurfaceRequest(id, title, body, UiSurfaceKind.Hud, width, height, content)
                    .WithHudAnchor(UiHudAnchor.TopRight).WithHudVisibility(UiHudVisibility.Always);
        }

        private static void TheNativeLayoutNoticeFixtureRequiresItsExactAuthoredShape()
        {
            var request = FoundationLayoutPages.LayoutNotice(FoundationPages.SurvivalTheme);
            var altered = new UiSurfaceRequest(request.Id, request.Title, request.Body, request.Kind, request.Width,
                request.Height, new UiText("Unapproved value")).WithHudAnchor(request.HudAnchor).WithHudVisibility(request.HudVisibility)
                .WithTheme(request.Theme);
            Require(UiGalleryCaptureFixture.Match(UiGalleryCaptureFixture.Owner, false, null, request) != null &&
                UiGalleryCaptureFixture.Match(UiGalleryCaptureFixture.Owner, false, null, altered) == null &&
                UiGalleryCaptureFixture.Match("example.unrelated", false, null, request) == null,
                "Layout fallback diagnostics attach only to the exact first-party authored HUD.");
        }

        private static FakeUiSurface Make(FakeUiService ui, UiSurfaceRequest request)
        {
            var result = ui.CreateSurface(request);
            if (result.TryGetValue(out var surface) && surface is FakeUiSurface fake) return fake;
            throw new InvalidOperationException(result.ErrorMessage);
        }

        private static void ScrollApertureAdmissionIsTransactional()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var prior = new UiText("Ready for input", id: "ready");
            using var surface = Make(ui, new UiSurfaceRequest("scroll-window", "Route", "", content: prior));
            var button = new UiButton("inaccessible", "Go", () => { });
            var tooShort = new UiSizedBox(new UiScroll(button, 80), width: 180, height: 30);
            var rejected = surface.SetContent(tooShort);
            Require(rejected.ErrorCode == ModErrorCode.InvalidArgument && ReferenceEquals(surface.Content, prior),
                "An unreachable native-size target is rejected before replacing the previous tree.");
            var again = ui.CreateSurface(new UiSurfaceRequest("invalid-scroll", "Route", "", content: tooShort));
            Require(again.ErrorCode == ModErrorCode.InvalidArgument, "Initial surface admission uses the same aperture check.");

            var transcript = new UiText(string.Join("\n", Enumerable.Repeat("The full report remains readable.", 20)), id: "report");
            var reachable = new UiSizedBox(new UiScroll(transcript, 80), width: 180, height: 36);
            Require(surface.SetContent(reachable).Succeeded && ReferenceEquals(surface.Content, reachable),
                "A real multiline label still admits when its complete lines can scroll into view.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
