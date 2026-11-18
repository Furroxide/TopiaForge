using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void GalleryNativeCaptureOnlyAdmitsAuthoredFixtures()
        {
            const string owner = UiGalleryCaptureFixture.Owner;
            foreach (var page in Enum.GetValues<FoundationPage>())
            {
                var request = FoundationPages.CreateRequest(page, UiSurfaceKind.Window, FoundationPages.SurvivalTheme);
                Check(UiGalleryCaptureFixture.Match(owner, false, null, request) != null,
                    "The native recorder admits each authored Gallery fixture.");
                var targeted = Reframe(request, "remote-37");
                Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, owner, targeted)?.Id == request.Id,
                    "A targeted receiver's reconstructed id maps to the authenticated canonical Gallery id.");
                Check(UiGalleryCaptureFixture.Match(owner, true, null, targeted) == null &&
                    UiGalleryCaptureFixture.Match(owner, true, "io.example.other", targeted) == null,
                    "A remote fixture requires the authenticated Gallery owner.");
                Check(UiGalleryCaptureFixture.Match(owner, false, null, targeted) == null,
                    "The local capture route requires the original Gallery id.");
                Check(UiGalleryCaptureFixture.Match("io.example.other", false, null, request) == null,
                    "An unrelated local package cannot opt in by copying the fixture.");
                if (page == FoundationPage.InteractionStates) continue;
                var hud = FoundationPages.CreateRequest(page, UiSurfaceKind.Hud);
                Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, owner, Reframe(hud, "shared-19"))?.Id == hud.Id,
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
            Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, owner,
                Reframe(Replace(original.Body, new UiColumn(content)), "remote-37")) == null,
                "An authenticated remote Gallery envelope cannot export unlisted nested text.");
            Check(UiGalleryCaptureFixture.Match("topiaforge.presentation", true, owner, Reframe(original, "remote-0")) == null,
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

            static UiSurfaceRequest Reframe(UiSurfaceRequest request, string transportId)
            {
                var converted = new UiSurfaceRequest(transportId, request.Title, request.Body, request.Kind,
                    request.Width, request.Height, request.Content);
                return request.Kind == UiSurfaceKind.Hud
                    ? converted.WithHudAnchor(request.HudAnchor).WithHudVisibility(request.HudVisibility) : converted;
            }
        }
    }
}
