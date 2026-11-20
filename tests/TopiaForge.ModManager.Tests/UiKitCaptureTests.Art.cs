using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CreateReceivedSurface")]
        private static extern OperationResult<IUiSurface> CreateReceivedSurface(FakeUiService ui,
            UiSurfaceRequest request, UiWireEnvelope envelope);

        internal static void RunArtCaptureTests()
        {
            ImageFramesAndTintFollowAdmission();
            NativeCoverRequiresParticipatingMask();
            ArtFirstUseHoldsAnIndependentPackLease();
            UnavailableArtKeepsItsFrameAndSurface();
            OverlayBackplatesFollowImageReadiness();
            InvalidArtLeavesRetainedStateIntact();
            GuestArtUsesTheReceivedDigestAndBox();
            NarrowGuestHudUsesABoundedImageNotice();
            GuestArtworkNoticesMeasureAndReportOverflow();
            GuestArtworkUsesTheWideOpening();
            RetainedLocalHudMovesItsImageNotice();
            ArtworkAndLayoutNoticesSharePaintBounds();
            AStatusLabelWithoutReadableWidthIsRefusedBeforePaint();
            StatusPaintRetainsItsCuesAtScale();
            BadgeAndPipCapturesRetainAuthoredStyle();
            NativeStatusCapturesUseAppliedStyles();
        }

        private static void ImageFramesAndTintFollowAdmission()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            new GalleryArtFixtures().Install(ui);
            var request = FoundationPages.CreateRequest(FoundationPage.Images, UiSurfaceKind.Window, FoundationPages.SurvivalTheme);
            using var surface = Make(ui, request);
            var pending = surface.Capture(Viewport);
            Check(pending.Nodes.Where(node => node.Kind == nameof(UiImage)).All(node => node.UsedFallback && node.IsRendered) &&
                pending.Nodes.Count(node => node.Kind == "ImagePlaceholder" && node.IsRendered && node.ContrastRatio >= 4.5f) == 4,
                "Each meaningful Gallery image has a readable in-place placeholder and decorative art has no label.");
            Check(pending.CheckAccessibility().Count == 5 &&
                pending.CheckAccessibility().All(issue => issue.Kind == UiAccessibilityIssueKind.MissingAsset),
                "Pending asset diagnostics point only to the five unavailable images.");
            var card = Row(pending, "image-natural");
            Check(card is { Width: 96, Height: 48 }, "Pending natural image axes come from the verified sample receipt.");

            ui.CompletePendingThemeRegistrations();
            var loaded = surface.Capture(Viewport);
            Check(loaded.CheckAccessibility().Count == 0 && Row(loaded, "image-natural") is { Width: 96, Height: 48, UsedFallback: false },
                "Completion keeps the same normalized frame and retires the asset diagnostics.");
            var contain = Child(loaded, "image-contain", "ImageSprite");
            var cover = Child(loaded, "image-cover", "ImageSprite");
            Check(contain.Width == 144 && contain.Height == 72 && contain.Y == Row(loaded, "image-contain").Y + 8 &&
                cover.Width == 144 && cover.Height == 88 && cover.Y == Row(loaded, "image-cover").Y,
                "Contain centers the source ratio and Cover crops within the whole fixed frame.");
            Check(Child(loaded, "image-mask", "ImageSprite") is { InkColor: "#27614D", Width: 58, Height: 58 } &&
                Row(loaded, "image-decorative") is { Width: 32, Height: 32, Decorative: true, SemanticLabel: "" },
                "A verified mask uses the current backdrop's Primary ink and decorative art has no semantic label.");

            var explicitAxis = new UiImage(FoundationArtPages.Card, "Card", width: 110f, id: "one-axis");
            Check(surface.SetContent(new UiColumn(explicitAxis)).Succeeded &&
                Row(surface.Capture(Viewport), "one-axis") is { Width: 110, Height: 48 } &&
                explicitAxis.Height == null, "An explicit width does not infer height or mutate the author's reference.");
        }

        private static void ArtFirstUseHoldsAnIndependentPackLease()
        {
            using var lifetime = new FakeModLifetime();
            using var firstLife = new FakeModLifetime();
            using var secondLife = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            new GalleryArtFixtures().Install(ui);
            var firstUi = CreateThemeScope(ui, firstLife);
            var secondUi = CreateThemeScope(ui, secondLife);
            using var first = Make(firstUi, ImageRequest("first"));
            using var second = Make(secondUi, ImageRequest("second"));
            firstLife.Dispose();
            var art = new UiThemeRef("preview-art");
            using var cancelled = new CancellationTokenSource();
            var cancelledWaiter = ui.Themes.PreloadAsync(art, cancelled.Token);
            cancelled.Cancel();
            Check(cancelledWaiter.Result.ErrorCode == ModErrorCode.Cancelled,
                "A cancelled waiter cannot hold a pack after its owner drops it.");
            ui.CompletePendingThemeRegistrations();
            Check(!Row(second.Capture(Viewport), "card").UsedFallback,
                "An art-only sibling surface keeps the shared load alive after the initiator stops.");
            secondLife.Dispose();
            using var retired = Make(ui, ImageRequest("retired"));
            retired.Dispose();
            using var fresh = Make(ui, ImageRequest("fresh"));
            ui.CompletePendingThemeRegistrations();
            Check(Row(fresh.Capture(Viewport), "card").UsedFallback, "A new surface cannot join the drained attempt.");
            ui.CompletePendingThemeRegistrations();
            var ready = fresh.Capture(Viewport);
            Check(!Row(ready, "card").UsedFallback && ready.Nodes[0].ThemeId == FoundationPages.SurvivalTheme.Id &&
                ui.Themes.SelectedTheme == null,
                "The later surface reacquires its separate art pack and keeps the Gallery's inherited Survival theme.");

            var retained = fresh.Content!;
            var stable = true;
            for (var index = 0; index < 4096; index++) stable &= fresh.SetContent(retained).Succeeded;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 4096; index++) stable &= fresh.SetContent(retained).Succeeded;
            Check(stable && GC.GetAllocatedBytesForCurrentThread() == before,
                "A ready image tree allocates zero bytes across 4,096 warmed unchanged reapplications.");
        }

        private static void UnavailableArtKeepsItsFrameAndSurface()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            new GalleryArtFixtures().Install(ui);
            var image = new UiImage(FoundationArtPages.Card, "Full original semantic label", 1, 1, id: "tiny");
            var tree = new UiColumn(image, new UiToggle("retained-control", "Enable signal", true, _ => { }));
            using var surface = Make(ui, new UiSurfaceRequest("tiny-image", "Supply station", "This body is hidden when the art label cannot fit.",
                content: tree));
            var capture = surface.Capture(Viewport);
            var notice = capture.Nodes.Single(node => node.Kind == "ImageFallback");
            Check(Row(capture, "tiny") is { Width: 1, Height: 1, SemanticLabel: "Full original semantic label", UsedFallback: true, IsRendered: false } &&
                notice is { Text: "Image unavailable", IsRendered: true, ContrastRatio: >= 4.5f, TextHeightPixels: >= 18f } &&
                capture.Nodes.All(node => node.Kind != "SurfaceBody" && node.Id != "retained-control") &&
                capture.CheckAccessibility().Single().Kind == UiAccessibilityIssueKind.MissingAsset,
                "An unfit label keeps the exact hidden art box and replaces the visible body with a readable notice.");
            Check(!surface.ChangeToggle("retained-control", false).Succeeded &&
                surface.TryGetToggleValue("retained-control", out var before) && before,
                "The notice leaves the original control alive and blocks its input.");
            ui.CompletePendingThemeRegistrations();
            Check(surface.SetContent(tree).Succeeded && surface.TryGetToggleValue("retained-control", out var after) && after &&
                surface.ChangeToggle("retained-control", false).Succeeded && Row(surface.Capture(Viewport), "tiny").IsRendered,
                "Art completion restores the same control and its previous value.");

            using var failureLife = new FakeModLifetime();
            var failureUi = new FakeUiService(failureLife);
            new GalleryArtFixtures().Install(failureUi, FakeUiAssetOutcome.LoadFailure);
            var decorative = new UiImage(FoundationArtPages.Mark, string.Empty, 1f, 1f, decorative: true, id: "decoration");
            using var bounded = Make(failureUi, new UiSurfaceRequest("decorative", "Supply station", "",
                content: new UiColumn(decorative, new UiText("Station ready", id: "status-copy"))));
            failureUi.CompletePendingThemeRegistrations();
            var failed = bounded.Capture(Viewport);
            Check(Row(failed, "decoration") is { Width: 1, Height: 1, IsRendered: true, Decorative: true, SemanticLabel: "", UsedFallback: true } &&
                Row(failed, "status-copy").IsRendered &&
                failed.Nodes.All(node => node.Kind != "ImageFallback" && node.Kind != "ImagePlaceholder"),
                "Failed decorative art keeps its exact frame and adds no visible or semantic caption.");
        }

        private static void OverlayBackplatesFollowImageReadiness()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            new GalleryArtFixtures().Install(ui);
            var image = new UiPositioned(new UiImage(FoundationArtPages.Card, "Card", 128, 58, id: "card"), left: 0, top: 0);
            var caption = new UiPositioned(new UiText("Route", id: "caption"), left: 8, top: 8);
            var protectedCaption = new UiStack(new UiNode[] { image, caption }, 150, 70);
            using var surface = Make(ui, new UiSurfaceRequest("layered", "Map", "", content: protectedCaption));
            Check(surface.Capture(Viewport).Nodes.Any(node => node.Kind == "ImageFallback" && node.IsRendered),
                "An otherwise valid caption cannot cover the pending image's labelled placeholder.");
            ui.CompletePendingThemeRegistrations();
            var ready = surface.Capture(Viewport);
            var visibleCaption = Row(ready, "caption");
            Check(visibleCaption.IsRendered && visibleCaption.ContrastRatio >= 4.5f &&
                ready.Nodes.Any(node => node.Kind == "TextBackplate" && node.Path == visibleCaption.Path + "/backplate" && node.IsRendered),
                "The same composition restores with an opaque backplate over its admitted image.");
            var wrongOrder = new UiStack(new UiNode[] { caption, image }, 150, 70);
            Check(surface.SetContent(wrongOrder).ErrorCode == ModErrorCode.InvalidArgument &&
                ReferenceEquals(surface.Content, protectedCaption), "An authored later image cannot cover its caption.");
        }

        private static void InvalidArtLeavesRetainedStateIntact()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            new GalleryArtFixtures().Install(ui);
            const string large = """
                {"schema":1,"id":"oversized","images":[{"id":"photo","bundlePath":"ui/large.bundle","assetName":"Large","licensePath":"LICENSE"}]}
                """;
            Check(ui.AddThemeFixture(large).Succeeded, "Declare the larger verified source.");
            ui.AddAssetFixture(new FakeUiAssetMetadata("ui/large.bundle", "Large", new string('d', 64), 2048, 32, 262144));
            var original = new UiToggle("keep", "Signal", false, _ => { });
            using var retained = Make(ui, new UiSurfaceRequest("keep", "Station", "", content: original));
            Check(retained.ChangeToggle("keep", true).Succeeded, "Change the retained toggle before proposing invalid artwork.");
            var clock = new UiCountdown(12, "Deadline", id: "new-clock");
            var tintOnRgba = new UiImage(FoundationArtPages.Card, "Card", tint: UiTone.Neutral);
            Check(retained.SetContent(new UiColumn(clock, tintOnRgba)).ErrorCode == ModErrorCode.InvalidArgument &&
                ReferenceEquals(retained.Content, original) && clock.ClockEpoch == null &&
                retained.TryGetToggleValue("keep", out var value) && value,
                "Tint on an ordinary sprite refuses the whole update without binding its countdown or changing controls.");
            var photo = new UiImageRef("oversized", "photo");
            Check(retained.SetContent(new UiImage(photo, "Large")).ErrorCode == ModErrorCode.InvalidArgument &&
                retained.SetContent(new UiImage(new UiImageRef("unowned", "photo"), "Foreign")).ErrorCode == ModErrorCode.NotFound,
                "Local use rejects a natural dimension over 1,024 and an undeclared package-local reference.");
            Check(retained.SetContent(new UiImage(photo, "Large", width: 180, id: "large")).Succeeded &&
                Row(retained.Capture(Viewport), "large") is { Width: 180, Height: 32 },
                "An explicit bounded axis admits the same source before its load.");
        }

        private static void GuestArtUsesTheReceivedDigestAndBox()
        {
            var fixture = new GalleryArtFixtures();
            using var players = new FakePlayerDirectory();
            var recipient = players.Add(new ParticipantId("art-fake-guest"), "Sam");
            var owner = players.State(recipient.Id).Ui;
            fixture.Install(owner);
            var authored = new UiImage(FoundationArtPages.Card, "Card", id: "card");
            using var targeted = Make(recipient.Ui.CreateSurface(new UiSurfaceRequest("targeted-art", "Supplies", "", content: authored)));
            Check(ReferenceEquals(authored, targeted.Content) && authored.Width == null && Row(targeted.Capture(Viewport), "card").Width == 96,
                "The targeted send measures normalized wire bytes without replacing the host-authored tree.");
            var descriptor = ThemeForWire(owner, new UiThemeRef("preview-art"))!;
            var resolved = authored.WithWireMetadata(118, 62, descriptor.Digest.Span);
            var (received, envelope) = ReceiveArt(resolved);
            foreach (var fixtureState in new[] { "missing", "mismatch", "matching" })
            {
                using var peerLife = new FakeModLifetime();
                var guest = new FakeUiService(peerLife);
                if (fixtureState != "missing") fixture.Install(guest, digestOverride: fixtureState == "mismatch" ? new string('c', 64) : null);
                using var surface = Make(CreateReceivedSurface(guest, new UiSurfaceRequest("received", "Supplies", "", content: received), envelope));
                Check(Row(surface.Capture(Viewport), "card") is { Width: 118, Height: 62, UsedFallback: true },
                    "Receiver pending and mismatched images retain the transmitted frame.");
                guest.CompletePendingThemeRegistrations();
                var captured = surface.Capture(Viewport);
                Check(Row(captured, "card").UsedFallback == (fixtureState != "matching") &&
                    captured.Nodes[0].ThemeId == UiThemePackParser.Default.Id,
                    "Only a matching receiver pack loads and the art pack never supplies the selected palette.");
            }
        }

        private static void NarrowGuestHudUsesABoundedImageNotice()
        {
            using var lifetime = new FakeModLifetime();
            const float width = 180f;
            const float height = 80f;
            var ui = new FakeUiService(lifetime);
            new GalleryArtFixtures().Install(ui);
            var tiny = new UiImage(FoundationArtPages.Card, "Full original semantic label", 1f, 1f, id: "tiny");
            var local = Request(tiny);
            Check(ui.CreateSurface(local).ErrorCode == ModErrorCode.InvalidArgument && ui.Surfaces.Count == 0,
                "A locally authored HUD cannot use a box that fits neither the pending label nor the kit notice.");

            var art = ThemeForWire(ui, new UiThemeRef("preview-art"))!;
            var (content, envelope) = ReceiveArt(tiny.WithWireMetadata(1f, 1f, art.Digest.Span));
            using var remote = Make(CreateReceivedSurface(ui, Request(content), envelope));
            var fallback = remote.Capture(Viewport);
            var shell = fallback.Nodes[0];
            var notice = fallback.Nodes.Single(node => node.Kind == "ImageFallback");
            var retained = Row(fallback, "tiny");
            Check(shell is { Width: UiImageNotice.RemoteHudWidth, Height: UiImageNotice.RemoteHudHeight, UsedFallback: true } &&
                notice is { Text: UiImageNotice.Text, IsRendered: true, Clipped: false, ContrastRatio: >= 4.5f } &&
                notice.X >= shell.X && notice.Y >= shell.Y && notice.X + notice.Width <= shell.X + shell.Width &&
                notice.Y + notice.Height <= shell.Y + shell.Height &&
                retained is { Width: 1, Height: 1, IsRendered: false, UsedFallback: true } &&
                fallback.CheckAccessibility().Single().Kind == UiAccessibilityIssueKind.MissingAsset,
                "A narrow received HUD shows a bounded kit notice while retaining its original tiny art diagnostic.");

            using (var peer = Make(ui, new UiSurfaceRequest("neighbour", "Progress", "", UiSurfaceKind.Hud, 340, 830, new UiText("Survey ready"))
                .WithHudAnchor(UiHudAnchor.BottomRight).WithHudVisibility(UiHudVisibility.Always)))
            {
                var peerBefore = peer.Capture(Viewport).Nodes[0];
                var moved = remote.Capture(Viewport);
                var peerAfter = peer.Capture(Viewport).Nodes[0];
                Check(moved.Nodes.Any(node => node.Kind == "ImageFallback" && node.IsRendered && !node.Clipped) &&
                    moved.Nodes[0].X + moved.Nodes[0].Width <= peerBefore.X && peerBefore.X == peerAfter.X && peerBefore.Y == peerAfter.Y,
                    "The remote notice uses an available edge without moving or covering an existing HUD.");
            }

            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 2f)).Succeeded, "Use double scale on a portrait viewport.");
            var portrait = remote.Capture(new UiCaptureViewport(320, 4320));
            Check(portrait.Nodes[0].Width < UiImageNotice.RemoteHudWidth &&
                portrait.Nodes.Any(node => node.Kind == "ImageFallback" && node.IsRendered && !node.Clipped) &&
                portrait.CheckAccessibility().Single().Kind == UiAccessibilityIssueKind.MissingAsset,
                "A physically narrow viewport measures a readable image notice and keeps the unavailable asset diagnostic.");

            Check(ui.ApplyAccessibility(UiAccessibilityPreferences.Default).Succeeded, "Return to the reference capture scale.");
            ui.CompletePendingThemeRegistrations();
            var recovered = remote.Capture(Viewport);
            var recoveredShell = recovered.Nodes[0];
            var image = Row(recovered, "tiny");
            Check(recoveredShell.Width == width && recoveredShell.Height == height &&
                recoveredShell.X + width == shell.X + shell.Width && recoveredShell.Y + height == shell.Y + shell.Height &&
                image.X == retained.X && image.Y == retained.Y && image is { Width: 1, Height: 1, IsRendered: true } &&
                recovered.CheckAccessibility().Count == 0,
                "Readiness restores the received HUD's authored size, dock alignment and exact original image position.");

            static UiSurfaceRequest Request(UiNode tree) => new UiSurfaceRequest("narrow-hud", "Supplies", "", UiSurfaceKind.Hud,
                width, height, tree).WithHudAnchor(UiHudAnchor.BottomRight).WithHudVisibility(UiHudVisibility.Always);
        }

        private static void RetainedLocalHudMovesItsImageNotice()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            new GalleryArtFixtures().Install(ui);
            using var peer = Make(ui, AtTopLeft("neighbour", "Readiness", 180f, 384f, new UiText("Survey ready")));
            peer.Hide();
            using var surface = Make(ui, AtTopLeft("local-art", "Supplies", 140f, 150f,
                new UiImage(FoundationArtPages.Card, "Supply card", 1f, 1f, id: "tiny")));
            var original = surface.Capture(Viewport);
            Check(original.Nodes.Any(node => node.Kind == "ImageFallback" && node.IsRendered && !node.Clipped),
                "The pending local artwork starts with a readable notice in its authored slot.");

            peer.Show();
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 2f)).Succeeded, "Double the viewer scale.");
            var occupied = peer.Capture(Viewport).Nodes[0];
            var moved = surface.Capture(Viewport);
            var shell = moved.Nodes.SingleOrDefault(node => node.Path == "/");
            Check(moved.Nodes.Any(node => node.Kind == "ImageFallback" && node.IsRendered && !node.Clipped) &&
                moved.Nodes.Any(node => node.Kind == "SurfaceTitle" && node.IsRendered && !node.Clipped) &&
                shell != null && shell.X >= occupied.X + occupied.Width &&
                Row(moved, "tiny") is { Width: 2f, Height: 2f, IsRendered: false, UsedFallback: true },
                "A retained local image notice moves into available HUD space and preserves its semantic diagnostic.");

            peer.Hide();
            Check(ui.ApplyAccessibility(UiAccessibilityPreferences.Default).Succeeded, "Restore the initial viewer scale.");
            ui.CompletePendingThemeRegistrations();
            var recovered = surface.Capture(Viewport);
            var restored = recovered.Nodes[0];
            Check(restored.X == original.Nodes[0].X && restored.Y == original.Nodes[0].Y &&
                restored.Width == 140f && restored.Height == 150f && Row(recovered, "tiny").IsRendered &&
                recovered.CheckAccessibility().Count == 0,
                "Local art readiness restores the original dock and its artwork.");

            static UiSurfaceRequest AtTopLeft(string id, string title, float width, float height, UiNode content) =>
                new UiSurfaceRequest(id, title, "", UiSurfaceKind.Hud, width, height, content)
                    .WithHudAnchor(UiHudAnchor.TopLeft).WithHudVisibility(UiHudVisibility.Always);
        }

        private static void AStatusLabelWithoutReadableWidthIsRefusedBeforePaint()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            using var surface = Make(ui, new UiSurfaceRequest("badge-test", "Badge test", "", content: new UiText("Ready")));
            var invalid = new UiRow(new UiBadge("\u0301", UiPaintRole.Danger, id: "empty-ink"));
            Check(surface.SetContent(invalid).ErrorCode == ModErrorCode.InvalidArgument &&
                surface.Capture(Viewport).Nodes.Any(node => node.Text == "Ready" && node.IsRendered),
                "A badge without measurable label width is refused during staging and leaves the prior view visible.");
        }

        private static void StatusPaintRetainsItsCuesAtScale()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            new GalleryArtFixtures().Install(ui);
            using var surface = Make(ui, FoundationPages.CreateRequest(FoundationPage.BadgesAndPips, UiSurfaceKind.Hud, FoundationPages.SurvivalTheme));
            ui.CompletePendingThemeRegistrations();
            var standard = surface.Capture(Viewport);
            Check(Child(standard, "badge-warning", "BadgeGlyph").Path.EndsWith("/glyph:Info", StringComparison.Ordinal) &&
                !standard.Nodes.Any(node => node.Kind == "BadgeGlyph" && node.Path.StartsWith(Row(standard, "badge-closed").Path + "/", StringComparison.Ordinal)),
                "Standard badges show their explicitly chosen redundant glyphs.");
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast: true, uiScale: 2f)).Succeeded,
                "Enable the checked high-contrast double-scale HUD.");
            var high = surface.Capture(Viewport);
            Check(Row(high, "badge-warning").StyleId == "status.outline" &&
                Child(high, "badge-warning", "BadgeGlyph") is { Width: 36, Height: 36, Decorative: true } warning &&
                warning.Path.EndsWith("/glyph:Warning", StringComparison.Ordinal) &&
                Child(high, "badge-closed", "BadgeGlyph").Path.EndsWith("/glyph:Cross", StringComparison.Ordinal) &&
                Child(high, "badge-warning", "BadgeBoundary").BorderWidthPixels >= 6f,
                "High contrast keeps the named decoration, requires redundant status glyphs and scales a meaningful outline.");
            var dots = Row(high, "pips-dots");
            var partial = high.Nodes.Where(node => node.Kind == "PipFill" && node.Path.StartsWith(dots.Path + "/", StringComparison.Ordinal)).ToArray();
            var edge = high.Nodes.Single(node => node.Path == dots.Path + "/3/edge");
            Check(partial.Length == 4 && partial[0].Width == 24f &&
                partial[3] is { Width: 2.4f, Height: 24f, IsRendered: true } &&
                partial[3].X == edge.X + 6f && partial[3].Y == edge.Y + 6f &&
                dots.SemanticLabel == "Energy: 3 / 5 (10% refilling)" && high.CheckAccessibility().Count == 0,
                "Only the next dot is partly filled, visibly inside its high-contrast outline.");
        }

        private static UiSurfaceRequest ImageRequest(string id) =>
            new UiSurfaceRequest(id, "Supply station", "", content: new UiImage(FoundationArtPages.Card, "Card", id: "card"));

        private static UiCapturedNode Row(UiTreeCapture capture, string id) => capture.Nodes.Single(row => row.Id == id);

        private static UiCapturedNode Child(UiTreeCapture capture, string id, string kind)
        {
            var parent = Row(capture, id).Path + "/";
            return capture.Nodes.Single(row => row.Kind == kind && row.Path.StartsWith(parent, StringComparison.Ordinal));
        }

        private static (UiNode Content, UiWireEnvelope Envelope) ReceiveArt(UiNode content, UiThemeDescriptor? palette = null)
        {
            var owner = new UiWireEnvelope("io.github.furroxide.topiaforge.uigallery", new byte[32], 1, palette?.Id,
                palette == null ? new byte[32] : palette.Digest.Span);
            var writer = new WorldWireWriter();
            UiWireCodec.Write(writer, content, "gallery-epoch", owner);
            var reader = new WorldWireReader();
            reader.Reset(writer.Segment.ToArray());
            var tree = UiWireCodec.ReadWithEnvelope(reader, (_, _) => { }, "gallery-epoch", out var received)!;
            UiWireCodec.End(reader);
            return (tree, received!);
        }
    }
}
