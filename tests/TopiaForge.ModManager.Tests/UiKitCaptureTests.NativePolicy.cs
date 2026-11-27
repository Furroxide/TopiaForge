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
            var survival = UiGallerySurvivalTheme.Descriptor;
            var galleryPath = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.UiGallery");
            var manifest = UiThemeCatalogueParser.ParseManifest(File.ReadAllBytes(Path.Combine(galleryPath, "topiaforge.mod.json")));
            Check(manifest.TryGetValue(out var gallery) && gallery?.Default == survival.Id,
                "The ordinary Gallery inherits its declared Survival default.");
            var digest = Enumerable.Repeat((byte)0x5A, 32).ToArray();
            var kit = new UiWireEnvelope(publisher, digest, 1, null, new byte[32]);
            var transportEnvelope = new UiWireEnvelope(publisher, digest, 1, FoundationPages.TransportTheme.Id, digest);
            var offeredSurvival = new UiWireEnvelope(publisher, digest, 1, survival.Id, survival.Digest.Span);
            var badSurvival = new UiWireEnvelope(publisher, digest, 1, survival.Id, digest);
            var foreignSurvival = new UiWireEnvelope(owner, digest, 1, survival.Id, survival.Digest.Span);
            var foreignPackage = new UiWireEnvelope("io.example.other", digest, 1, null, new byte[32]);
            var altered = File.ReadAllText(Path.Combine(galleryPath, "ui", "themes", "survival.json"))
                .Replace("\"small\": 3", "\"small\": 4", StringComparison.Ordinal);
            var otherTheme = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(altered));
            Check(otherTheme.TryGetValue(out var otherSurvival) && otherSurvival != null &&
                !survival.Digest.Span.SequenceEqual(otherSurvival.Digest.Span),
                "The recorder can distinguish two packages' separate Survival descriptors.");
            foreach (var page in Enum.GetValues<FoundationPage>())
            {
                var localOnly = page is FoundationPage.InteractionStates or FoundationPage.GraphConnections;
                var request = FoundationPages.CreateRequest(page, UiSurfaceKind.Window, FoundationPages.SurvivalTheme);
                Check(UiGalleryCaptureFixture.Match(owner, false, null, request) != null,
                    "The native recorder admits each authored Gallery fixture.");
                Check(UiGalleryCaptureFixture.Match(owner, false, null, request.WithTheme(null), survival) != null,
                    "The inherited Gallery theme keeps the safe F8 fixture recordable.");
                var transport = FoundationPages.CreateRequest(page, UiSurfaceKind.Window, FoundationPages.TransportTheme);
                var targeted = Reframe(transport, "remote-37");
                Check((UiGalleryCaptureFixture.Match("topiaforge.presentation", true, transportEnvelope, targeted)?.Id == request.Id) ==
                    !localOnly,
                    "A targeted receiver's reconstructed id maps to the authenticated canonical Gallery id for wire-supported pages.");
                Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, foreignSurvival, targeted, survival) == null,
                    "The client-local Gallery is ineligible to publish a remote fixture.");
                Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, badSurvival, targeted, survival) == null &&
                    UiGalleryCaptureFixture.Match("topiaforge.presentation", true, offeredSurvival, targeted, otherSurvival) == null &&
                    UiGalleryCaptureFixture.Match("topiaforge.presentation", true, offeredSurvival, targeted) == null &&
                    UiGalleryCaptureFixture.Match(owner, false, null, transport) == null,
                    "Exact Survival capture needs both its authenticated digest and the matching admitted descriptor.");
                Check(UiGalleryCaptureFixture.Match(owner, true, null, targeted) == null &&
                    UiGalleryCaptureFixture.Match(owner, true, foreignPackage, targeted) == null,
                    "A remote fixture requires the authenticated Gallery owner.");
                Check(UiGalleryCaptureFixture.Match(owner, false, null, targeted) == null,
                    "The local capture route requires the original Gallery id.");
                Check(UiGalleryCaptureFixture.Match("io.example.other", false, null, request) == null,
                    "An unrelated local package cannot opt in by copying the fixture.");
                if (localOnly) continue;
                var hud = FoundationPages.CreateRequest(page, UiSurfaceKind.Hud);
                Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, kit, Reframe(hud, "shared-19"))?.Id == hud.Id,
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
            Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, kit,
                Reframe(Replace(original.Body, new UiColumn(content)), "remote-37")) == null,
                "An authenticated remote Gallery envelope cannot export unlisted nested text.");
            Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, kit, Reframe(original, "remote-0")) == null,
                "A remote preview needs a nonzero transport id.");
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
                UiGalleryCaptureFixture.Match("topiaforge.presentation", true, kit, Reframe(wrongBounds, "remote-37")) == null,
                "A copied Gallery label tree with altered stack geometry cannot reach the native export.");

            var art = FoundationPages.CreateRequest(FoundationPage.Images, UiSurfaceKind.Window);
            var sampleDigest = Enumerable.Repeat((byte)0x62, 32).ToArray();
            UiNode? WithNormalizedArt(float width) => UiImageProjection.Rewrite(art.Content, image =>
                image.Id == "image-natural" ? image.WithWireMetadata(width, 48f, sampleDigest) : image);
            Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, kit,
                    Reframe(art, "remote-37", WithNormalizedArt(96f)))?.Id == art.Id &&
                UiGalleryCaptureFixture.Match("topiaforge.presentation", true, kit,
                    Reframe(art, "remote-37", WithNormalizedArt(97f))) == null,
                "Only the exact normalized natural sprite dimensions qualify for native remote capture.");

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

            var variants = Enumerable.Range(0, GalleryRemotePreview.VariantCount).Select(GalleryRemotePreview.Request).ToArray();
            Check(variants.Select(request => (request.Id, request.Kind, request.Theme?.Id)).Distinct().Count() == 12,
                "The test publisher cycles the complete supported page, surface and theme matrix.");
            foreach (var request in variants)
            {
                var local = request.WithTheme(null);
                Check(UiGalleryCaptureFixture.Match(owner, false, null, local, survival) != null,
                    "Connected guests can open the same Theme packs and Typography pages with Survival in their own Gallery.");
                var declared = request.Theme?.Id == survival.Id ? survival : null;
                Check(UiGalleryCaptureFixture.Match(publisher, false, null, request, declared) != null,
                    "The opt-in host may also record its own transport fixture.");
                const string clock = "gallery-test";
                var themeDigest = declared?.Digest.ToArray() ?? (request.Theme == null ? new byte[32] : digest);
                var envelope = new UiWireEnvelope(publisher, digest, 1, request.Theme?.Id, themeDigest);
                var writer = new WorldWireWriter(32 * 1024);
                Check(UiWireCodec.NeedsEnvelope(request.Content), "Each fixture exercises authenticated ownership framing.");
                UiWireCodec.Write(writer, request.Content, clock, envelope);
                var reader = new WorldWireReader();
                reader.Reset(writer.Segment.ToArray());
                var decoded = UiWireCodec.ReadWithEnvelope(reader, (_, _) => { }, clock, out var observedEnvelope);
                UiWireCodec.End(reader);
                var received = Reframe(request, "remote-118", decoded);
                Check(decoded != null && observedEnvelope?.OwnerPackageId == publisher &&
                    UiGalleryCaptureFixture.Match("topiaforge.presentation", true, observedEnvelope, received, declared)?.Id == request.Id,
                    "Each published page keeps its canonical fixture identity after production codec round-trip and receiver ID reconstruction.");
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
