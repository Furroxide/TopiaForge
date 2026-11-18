using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.MultiplayerAcceptance;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void GalleryNativeCaptureOnlyAdmitsAuthoredFixtures()
        {
            const string owner = UiGalleryCaptureFixture.Owner;
            const string publisher = UiGalleryCaptureFixture.RemoteFixtureOwner;
            foreach (var page in Enum.GetValues<FoundationPage>())
            {
                var request = FoundationPages.CreateRequest(page, UiSurfaceKind.Window, FoundationPages.SurvivalTheme);
                Check(UiGalleryCaptureFixture.Match(owner, false, null, request) != null,
                    "The native recorder admits each authored Gallery fixture.");
                var transport = FoundationPages.CreateRequest(page, UiSurfaceKind.Window, FoundationPages.TransportTheme);
                var targeted = Reframe(transport, "remote-37");
                Check((UiGalleryCaptureFixture.Match("topiaforge.presentation", true, publisher, targeted,
                        FoundationPages.TransportTheme.Id)?.Id == request.Id) ==
                    (page != FoundationPage.InteractionStates),
                    "A targeted receiver's reconstructed id maps to the authenticated canonical Gallery id for wire-supported pages.");
                Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, owner, targeted) == null,
                    "The client-local Gallery is ineligible to publish a remote fixture.");
                Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, publisher, targeted,
                        FoundationPages.SurvivalTheme.Id) == null &&
                    UiGalleryCaptureFixture.Match(owner, false, null, transport) == null,
                    "The transport and public Gallery themes are scoped to their separate packages.");
                Check(UiGalleryCaptureFixture.Match(owner, true, null, targeted) == null &&
                    UiGalleryCaptureFixture.Match(owner, true, "io.example.other", targeted) == null,
                    "A remote fixture requires the authenticated Gallery owner.");
                Check(UiGalleryCaptureFixture.Match(owner, false, null, targeted) == null,
                    "The local capture route requires the original Gallery id.");
                Check(UiGalleryCaptureFixture.Match("io.example.other", false, null, request) == null,
                    "An unrelated local package cannot opt in by copying the fixture.");
                if (page == FoundationPage.InteractionStates) continue;
                var hud = FoundationPages.CreateRequest(page, UiSurfaceKind.Hud);
                Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, publisher, Reframe(hud, "shared-19"))?.Id == hud.Id,
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
            Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, publisher,
                Reframe(Replace(original.Body, new UiColumn(content)), "remote-37")) == null,
                "An authenticated remote Gallery envelope cannot export unlisted nested text.");
            Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, publisher, Reframe(original, "remote-0")) == null,
                "A remote preview needs a nonzero transport id.");
            Check(UiGalleryCaptureFixture.Match(owner, false, null, Replace("Unlisted body", original.Content!)) == null,
                "A fixture id does not admit unlisted body text.");
            Check(UiGalleryCaptureFixture.Match(owner, false, null, original.WithTheme(new UiThemeRef("other"))) == null,
                "Only a declared Gallery variant qualifies.");

            Check(UiGalleryCaptureFixture.SafeDisplayText("FIELD NOTE", "Field note", true) == "FIELD NOTE" &&
                UiGalleryCaptureFixture.SafeDisplayText("Field note", "Field note", false) == "Field note",
                "The export emits the verified authored text and allowed case variant.");
            Check(UiGalleryCaptureFixture.SafeDisplayText("Unlisted text", "Field note", true) == null &&
                UiGalleryCaptureFixture.SafeDisplayText("FIELD NOTE", "Field note", false) == null,
                "The native text gate rejects unexpected content and unapproved transformations.");

            var variants = Enumerable.Range(0, GalleryRemotePreview.VariantCount).Select(GalleryRemotePreview.Request).ToArray();
            Check(variants.Select(request => (request.Id, request.Kind, request.Theme?.Id)).Distinct().Count() == 8,
                "The test publisher cycles the complete supported page, surface and theme matrix.");
            foreach (var request in variants)
            {
                var local = request.WithTheme(request.Theme == null ? null : FoundationPages.SurvivalTheme);
                Check(UiGalleryCaptureFixture.Match(owner, false, null, local) != null,
                    "Connected guests can open the same Theme packs and Typography pages with Survival in their own Gallery.");
                Check(UiGalleryCaptureFixture.Match(publisher, false, null, request) != null,
                    "The opt-in host may also record its own transport fixture.");
                const string clock = "gallery-test";
                var digest = Enumerable.Repeat((byte)0x5A, 32).ToArray();
                var themeDigest = request.Theme == null ? new byte[32] : digest;
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
                    UiGalleryCaptureFixture.Match("topiaforge.presentation", true, publisher, received, observedEnvelope.ThemeId)?.Id == request.Id,
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
    }
}
