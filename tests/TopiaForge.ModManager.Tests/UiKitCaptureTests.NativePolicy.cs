using System;
using System.IO;
using System.Linq;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.MultiplayerAcceptance;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void GalleryNativeCaptureOnlyAdmitsAuthoredFixtures()
        {
            UiGalleryInteractionPreviewTests.Run();
            const string owner = UiGalleryCaptureFixture.Owner;
            const string publisher = UiGalleryCaptureFixture.RemoteFixtureOwner;
            const string receiver = UiPresentationTheme.ReceiverOwner;
            var package = new AcceptanceGalleryPackage();
            var registered = package.Descriptor;
            var survival = package.Survival;
            var galleryPath = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.UiGallery");
            var catalogue = UiThemeCatalogueParser.ParseManifest(File.ReadAllBytes(Path.Combine(galleryPath, "topiaforge.mod.json")));
            Check(catalogue.TryGetValue(out var gallery) && gallery?.Default == survival.Id,
                "The ordinary Gallery inherits its declared Survival default.");
            var alteredSurvival = File.ReadAllText(Path.Combine(galleryPath, "ui", "themes", "survival.json"))
                .Replace("\"small\": 3", "\"small\": 4", StringComparison.Ordinal);
            var otherSurvival = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(alteredSurvival)).Value;
            Check(otherSurvival != null && !UiGallerySurvivalTheme.Matches(otherSurvival),
                "The fixture distinguishes a different pack with the Survival id.");
            var packageDigest = Enumerable.Repeat((byte)0x5A, 32).ToArray();
            var otherDigest = Enumerable.Repeat((byte)0x62, 32).ToArray();
            var defaultDigest = new byte[32];
            UiWireEnvelope Envelope(string? theme = null, string sender = publisher) =>
                new UiWireEnvelope(sender, packageDigest, 1UL << 63, theme,
                    theme == survival.Id ? survival.Digest.Span : theme == registered.Id ? registered.Digest.Span : defaultDigest);

            UiNode? Normalize(UiNode? content) => UiImageProjection.Rewrite(content, image =>
            {
                Check(image.Image.PackId == registered.Id, "The acceptance package references its own admitted art in each scheme.");
                var metadata = UiImageProjection.Find(registered, image.Image.ImageId)!;
                var frame = UiImageProjection.ResolveFrame(image, metadata);
                return image.WithWireMetadata(frame.Width, frame.Height, registered.Digest.Span);
            });

            foreach (var page in Enum.GetValues<FoundationPage>())
            {
                var localOnly = page is FoundationPage.InteractionStates or FoundationPage.GraphConnections;
                var request = FoundationPages.CreateRequest(page, UiSurfaceKind.Window, FoundationPages.SurvivalTheme);
                Check(UiGalleryCaptureFixture.Match(owner, false, null, request) != null &&
                    UiGalleryCaptureFixture.Match(owner, false, null, request.WithTheme(null), survival) != null,
                    "The native recorder admits each authored Gallery fixture and its inherited Survival case.");
                if (!FoundationPages.CanPublish(page))
                {
                    Check(UiGalleryCaptureFixture.Match(receiver, true, Envelope(), Reframe(request, "remote-37")) == null,
                        "The local Interaction states page cannot be recorded from a remote route.");
                    continue;
                }
                var transport = FoundationPages.CreateTransportRequest(page, UiSurfaceKind.Window, FoundationPages.TransportTheme);
                var targeted = Reframe(transport, "remote-37", Normalize(transport.Content));
                Check(UiGalleryCaptureFixture.Match(receiver, true, Envelope(registered.Id), targeted)?.Id == request.Id,
                    "A targeted receiver's authenticated id maps to the canonical Gallery id for wire-supported pages.");
                Check(UiGalleryCaptureFixture.Match(receiver, true, Envelope(registered.Id, owner), targeted) == null,
                    "The client-local Gallery is ineligible to publish a remote fixture.");
                var survivalRequest = FoundationPages.CreateTransportRequest(page, UiSurfaceKind.Window, FoundationPages.SurvivalTheme);
                var survivalTarget = Reframe(survivalRequest, "remote-48", Normalize(survivalRequest.Content));
                var survivalEnvelope = Envelope(survival.Id);
                Check(UiGalleryCaptureFixture.Match(receiver, true, survivalEnvelope, survivalTarget, survival)?.Id == request.Id &&
                    UiGalleryCaptureFixture.Match(receiver, true, survivalEnvelope, survivalTarget, otherSurvival) == null &&
                    UiGalleryCaptureFixture.Match(receiver, true, survivalEnvelope, survivalTarget) == null &&
                    UiGalleryCaptureFixture.Match(receiver, true, new UiWireEnvelope(publisher, packageDigest,
                        1, survival.Id, otherDigest), survivalTarget, survival) == null,
                    "The paired Survival presentation requires its exact delivered and admitted descriptors.");
                Check(UiGalleryCaptureFixture.Match(owner, false, null, transport) == null,
                    "The ordinary Gallery cannot publish the acceptance palette.");
                Check(UiGalleryCaptureFixture.Match(receiver, true, null, targeted) == null &&
                    UiGalleryCaptureFixture.Match(receiver, true, Envelope(registered.Id, "io.example.other"), targeted) == null &&
                    UiGalleryCaptureFixture.Match(owner, true, Envelope(registered.Id), targeted) == null,
                    "A remote fixture requires the authenticated Gallery publisher and the loader-owned receiver.");
                Check(UiGalleryCaptureFixture.Match(receiver, true, new UiWireEnvelope(publisher, packageDigest,
                    1, registered.Id, otherDigest), targeted) == null,
                    "A transport palette with the right name also requires the exact admitted fixture digest.");
                Check(UiGalleryCaptureFixture.Match(owner, false, null, targeted) == null,
                    "The local capture route requires the original Gallery id.");
                Check(UiGalleryCaptureFixture.Match("io.example.other", false, null, request) == null,
                    "An unrelated local package cannot opt in by copying the fixture.");
                var hud = FoundationPages.CreateTransportRequest(page, UiSurfaceKind.Hud, theme: null);
                Check(UiGalleryCaptureFixture.Match(receiver, true, Envelope(), Reframe(hud, "shared-19", Normalize(hud.Content)))?.Id == hud.Id,
                    "A shared HUD receiver's reconstructed id maps to its canonical Gallery fixture.");
            }

            var original = FoundationPages.CreateRequest(FoundationPage.Typography, UiSurfaceKind.Window);
            var content = ((UiColumn)original.Content!).Children.ToArray();
            var caption = (UiText)content[0];
            content[0] = new UiText("Unlisted caption", caption.Style, caption.Tone, caption.Id);
            UiSurfaceRequest Replace(string body, UiNode node) => new UiSurfaceRequest(
                original.Id, original.Title, body, original.Kind, original.Width, original.Height, node);
            Check(UiGalleryCaptureFixture.Match(owner, false, null, Replace(original.Body, new UiColumn(content))) == null,
                "A fixture id does not admit unlisted nested text.");
            Check(UiGalleryCaptureFixture.Match(receiver, true, Envelope(),
                Reframe(Replace(original.Body, new UiColumn(content)), "remote-37")) == null,
                "An authenticated remote Gallery envelope cannot export unlisted nested text.");
            foreach (var id in new[] { "remote-0", "remote-0037", "shared-37" })
                Check(UiGalleryCaptureFixture.Match(receiver, true, Envelope(), Reframe(original, id)) == null,
                    "A remote window needs a canonical nonzero targeted id.");
            Check(UiGalleryCaptureFixture.Match(owner, false, null, Replace("Unlisted body", original.Content!)) == null,
                "A fixture id does not admit unlisted body text.");
            Check(UiGalleryCaptureFixture.Match(owner, false, null, original.WithTheme(new UiThemeRef("other"))) == null,
                "Only a declared Gallery variant qualifies.");

            var stack = FoundationPages.CreateRequest(FoundationPage.StackAndBounds, UiSurfaceKind.Window);
            var panelChildren = ((UiColumn)stack.Content!).Children.ToArray();
            var panel = (UiStack)panelChildren[1];
            panelChildren[1] = new UiStack(panel.Children, panel.Width + 1, panel.Height, panel.Id);
            var wrongBounds = new UiSurfaceRequest(stack.Id, stack.Title, stack.Body, stack.Kind,
                stack.Width, stack.Height, new UiColumn(panelChildren));
            Check(UiGalleryCaptureFixture.Match(owner, false, null, wrongBounds) == null &&
                UiGalleryCaptureFixture.Match(receiver, true, Envelope(), Reframe(wrongBounds, "remote-37")) == null,
                "A copied Gallery label tree with altered stack geometry cannot reach the native export.");

            var art = FoundationPages.CreateTransportRequest(FoundationPage.Images, UiSurfaceKind.Window, theme: null);
            var cleanArt = Normalize(art.Content);
            Check(UiGalleryCaptureFixture.Match(receiver, true, Envelope(), Reframe(art, "remote-37", cleanArt))?.Id == art.Id,
                "Exact remote art qualifies even if the guest must render a placeholder.");
            RejectImage("image-natural", image => image.WithWireMetadata(97f, 48f, registered.Digest.Span));
            RejectImage("image-natural", image => image.WithWireMetadata(96f, 48f, otherDigest));
            RejectImage("image-contain", image => image.WithWireMetadata(144f, 88f, otherDigest));
            RejectImage("image-contain", image => new UiImage(image.Image, image.SemanticLabel, image.Width, image.Height, id: image.Id));
            RejectImage("image-contain", image => new UiImage(FoundationArtPages.Card, image.SemanticLabel, image.Width, image.Height,
                id: image.Id).WithWireMetadata(144f, 88f, registered.Digest.Span));

            void RejectImage(string id, Func<UiImage, UiImage> change)
            {
                var altered = UiImageProjection.Rewrite(cleanArt, image => image.Id == id ? change(image) : image);
                Check(UiGalleryCaptureFixture.Match(receiver, true, Envelope(), Reframe(art, "remote-37", altered)) == null,
                    "Every remote fixture image requires its exact package-local reference, digest and normalized frame.");
            }

            Check(UiGalleryCaptureFixture.SafeDisplayText("FIELD NOTE", "Field note", true) == "FIELD NOTE" &&
                UiGalleryCaptureFixture.SafeDisplayText("Field note", "Field note", false) == "Field note",
                "The export emits the verified authored text and allowed case variant.");
            Check(UiGalleryCaptureFixture.SafeDisplayText("Unlisted text", "Field note", true) == null &&
                UiGalleryCaptureFixture.SafeDisplayText("FIELD NOTE", "Field note", false) == null,
                "The native text gate rejects unexpected content and unapproved transformations.");
            const string value = "Awaiting check-in";
            Check(UiGalleryCaptureFixture.SafeAuthoredFieldValue(value, value, value) == value &&
                UiGalleryCaptureFixture.SafeAuthoredFieldValue(value, value + '\u200B', value) == value,
                "The unchanged authored input value may carry one TMP caret sentinel.");
            Check(UiGalleryCaptureFixture.SafeAuthoredFieldValue("Player input", value, value) == null &&
                UiGalleryCaptureFixture.SafeAuthoredFieldValue(value, "Player input", value) == null &&
                UiGalleryCaptureFixture.SafeAuthoredFieldValue(value, value + "\u200B\u200B", value) == null &&
                UiGalleryCaptureFixture.SafeAuthoredFieldValue(string.Empty, "\u200B", string.Empty) == null &&
                UiGalleryCaptureFixture.SafeDisplayText(value + '\u200B', value, false) == null,
                "Edited input and unexpected transformations are ineligible for native recording.");
            GalleryPortFixturesRequireExactAuthoredPorts();

            var variants = Enumerable.Range(0, GalleryRemotePreview.VariantCount)
                .Select(index => GalleryRemotePreview.Request(index, 120d)).ToArray();
            Check(GalleryRemotePreview.K1VariantCount == 69 && GalleryRemotePreview.StatusStart == 79 &&
                variants.Select(request => (request.Id, request.Kind, request.Theme?.Id)).Distinct().Count() == 87 &&
                variants.Count(request => request.Id == FoundationLayoutPages.LayoutNotice().Id &&
                    request.Kind == UiSurfaceKind.Hud) == 3,
                "The test publisher cycles the complete supported page, surface and theme matrix.");
            var foundationPages = new[]
            {
                FoundationPage.ThemePacks, FoundationPage.Typography, FoundationPage.FlexAndPadding,
                FoundationPage.WrapAndGrid, FoundationPage.StackAndBounds, FoundationPage.CardsAndDividers,
                FoundationPage.Icons, FoundationPage.Images, FoundationPage.BadgesAndPips,
                FoundationPage.FoundationIntegration, FoundationPage.TrustAndFallback,
            };
            var originalPalettes = new[] { null, FoundationPages.TransportTheme, FoundationPages.SurvivalTheme };
            for (var index = 0; index < GalleryRemotePreview.K1VariantCount; index++)
            {
                var foundationCount = foundationPages.Length * 6;
                var kind = index % 2 == 0 ? UiSurfaceKind.Window : UiSurfaceKind.Hud;
                var expected = index < foundationCount
                    ? FoundationPages.CreateTransportRequest(foundationPages[index / 6], kind, originalPalettes[index % 6 / 2])
                    : FoundationLayoutPages.LayoutNotice(originalPalettes[index - foundationCount]);
                var actual = variants[index];
                Check((actual.Id, actual.Kind, actual.Theme?.Id) == (expected.Id, expected.Kind, expected.Theme?.Id),
                    "All 69 K1 publication positions retain their authored page, surface kind and palette.");
            }
            var controls = new[] { ControlGalleryPage.Buttons, ControlGalleryPage.Fields, ControlGalleryPage.Selections,
                ControlGalleryPage.Bindings, ControlGalleryPage.HelpAndTiles };
            for (var index = 0; index < controls.Length * 2; index++)
            {
                var expected = ControlGalleryPages.Create(controls[index / 2],
                    index % 2 == 0 ? null : FoundationPages.TransportTheme);
                var actual = variants[GalleryRemotePreview.K1VariantCount + index];
                Check((actual.Id, actual.Kind, actual.Theme?.Id) == (expected.Id, expected.Kind, expected.Theme?.Id),
                    "K2 adds the ten authored control positions after the original K1 fixtures.");
            }
            var statusPages = new[] { StatusGalleryPage.Status, StatusGalleryPage.Countdowns };
            for (var index = 0; index < statusPages.Length * 4; index++)
            {
                var kind = index % 2 == 0 ? UiSurfaceKind.Window : UiSurfaceKind.Hud;
                var palette = index % 4 < 2 ? null : FoundationPages.TransportTheme;
                var expected = StatusGalleryPages.CreateRequest(statusPages[index / 4], kind, 120d, palette);
                var actual = variants[GalleryRemotePreview.StatusStart + index];
                Check((actual.Id, actual.Kind, actual.Theme?.Id) == (expected.Id, expected.Kind, expected.Theme?.Id),
                    "Status and Countdowns append eight page, kind and palette positions after the controls.");
            }

            foreach (var request in variants)
            {
                var palette = request.Theme?.Id == survival.Id ? survival : request.Theme?.Id == registered.Id ? registered : null;
                Check(UiGalleryCaptureFixture.Match(publisher, false, null, request, palette) != null,
                    "The opt-in host may also record its own transport fixture.");
                const string clock = "gallery-test";
                var envelope = Envelope(request.Theme?.Id);
                var normalized = UiWireCodec.ProjectInputDefaults(Normalize(request.Content), ControlGalleryPages.DefaultBindings);
                var writer = new WorldWireWriter(32 * 1024);
                Check(UiWireCodec.NeedsEnvelope(request.Content), "Each fixture exercises authenticated ownership framing.");
                Check(UiWireCodec.MeasureTargetedSurface(publisher, "gallery-guest", 118, 1, request, request.Body,
                    visible: true, normalized, clock, envelope: envelope) <= 32 * 1024,
                    "Each Gallery preview fits production targeted framing with its envelope and verified art metadata.");
                UiWireCodec.Write(writer, normalized, clock, envelope);
                var reader = new WorldWireReader();
                reader.Reset(writer.Segment.ToArray());
                var decoded = UiWireCodec.ReadWithEnvelope(reader, (_, _) => { }, clock, out var observedEnvelope);
                UiWireCodec.End(reader);
                var received = Reframe(request, "remote-118", decoded);
                Check(decoded != null && observedEnvelope?.OwnerPackageId == publisher &&
                    UiGalleryCaptureFixture.Match(receiver, true, observedEnvelope, received, palette)?.Id == request.Id,
                    "Each published page keeps its canonical fixture identity after production codec round-trip and receiver ID reconstruction.");
                if (request.Id == "gallery-controls-keys")
                {
                    var changed = UiWireCodec.ProjectInputDefaults(request.Content, _ =>
                        new[] { InputBinding.Key("W"), InputBinding.GamepadButton(InputGamepadButton.West) });
                    Check(UiGalleryCaptureFixture.Match(receiver, true, observedEnvelope, Reframe(request, "remote-118", changed)) == null,
                        "The native recording policy refuses altered binding hints on an otherwise exact paired fixture.");
                }
            }

            static UiSurfaceRequest Reframe(UiSurfaceRequest request, string transportId, UiNode? decoded = null)
            {
                var converted = new UiSurfaceRequest(transportId, request.Title, request.Body, request.Kind,
                    request.Width, request.Height, decoded ?? request.Content);
                return request.Kind == UiSurfaceKind.Hud
                    ? converted.WithHudAnchor(request.HudAnchor).WithHudVisibility(request.HudVisibility) : converted;
            }
        }

        private static void GalleryPortFixturesRequireExactAuthoredPorts()
        {
            var request = FoundationPages.CreateRequest(FoundationPage.InteractionStates, UiSurfaceKind.Window);
            var layout = ((UiColumn)request.Content!).Children.ToArray();
            var graph = (UiGraphCanvas)layout[^1];
            var nodes = graph.Nodes.ToArray();
            var camp = nodes[0];
            var port = camp.Ports[0];
            var mismatched = new[]
            {
                new UiGraphPort("altered", port.Label, port.Direction, port.DataType, port.Required),
                new UiGraphPort(port.Id, "Unlisted input", port.Direction, port.DataType, port.Required),
                new UiGraphPort(port.Id, port.Label, UiGraphPortDirection.Output, port.DataType, port.Required),
                new UiGraphPort(port.Id, port.Label, port.Direction, "other", port.Required),
                new UiGraphPort(port.Id, port.Label, port.Direction, port.DataType, !port.Required),
            };
            foreach (var offered in mismatched)
            {
                nodes[0] = new UiGraphNode(camp.Id, camp.Type, camp.Title, camp.Position,
                    new[] { offered, camp.Ports[1] }, camp.Subtitle, camp.Tone, camp.Enabled);
                layout[^1] = new UiGraphCanvas(graph.Id!, nodes, graph.Edges, graph.SelectionChanged, graph.SelectedNodeId,
                    graph.Viewport, graph.Height, graph.Enabled);
                var altered = new UiSurfaceRequest(request.Id, request.Title, request.Body, request.Kind, request.Width,
                    request.Height, new UiColumn(layout));
                Check(UiGalleryCaptureFixture.Match(UiGalleryCaptureFixture.Owner, false, null, altered) == null,
                    "Native Gallery recording admits only the exact authored port fields.");
            }
        }
    }
}
