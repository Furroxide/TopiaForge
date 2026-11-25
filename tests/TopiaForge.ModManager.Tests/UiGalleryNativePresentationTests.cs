using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryNativePresentationTests
    {
        private const string Gallery = UiGalleryCaptureFixture.Owner;
        private const string Publisher = UiGalleryCaptureFixture.RemoteFixtureOwner;
        private const string Receiver = UiPresentationTheme.ReceiverOwner;
        private const string Hud = UiGalleryCaptureFixture.PresentationHudId;
        private static readonly byte[] Package = Enumerable.Repeat((byte)0x41, 32).ToArray();

        internal static void Run()
        {
            var transport = new AcceptanceGalleryPackage().Descriptor;
            ExactOverlayRequests(transport);
            RetainedHudKeepsItsRoute(transport);
            ExactAnnouncementAndToastRequests(transport);
            ToastArmsKeepTheirOwnerAndViewer();
            UiGalleryNativeStatusTests.CaptureNativeStatusChildren(presentation: true);
            Console.WriteLine("Native Gallery presentation capture passed exact fixtures, ownership and observed paint.");
        }

        private static void ExactOverlayRequests(UiThemeDescriptor transport)
        {
            var palette = FoundationPages.TransportTheme;
            foreach (var mode in PresentationPages.OverlayOrder)
            {
                var local = PresentationPages.Overlay(mode, static () => { });
                Check(UiGalleryCaptureFixture.MatchesCommittedBody(local, null, local) &&
                    UiGalleryCaptureFixture.MatchesCommittedBody(local, local.Body, local) &&
                    !UiGalleryCaptureFixture.MatchesCommittedBody(local, string.Empty, local),
                    "The initially rendered overlay body is known, and a different committed body revokes capture.");
                var themed = local.WithTheme(palette);
                var remote = Remote(local);
                var envelope = Surface(2, transport);
                Check(UiGalleryCaptureFixture.Match(Gallery, false, null, local)?.Id == local.Id &&
                    UiGalleryCaptureFixture.Match(Publisher, false, null, themed, transport)?.Id == local.Id &&
                    UiGalleryCaptureFixture.Match(Receiver, true, envelope, remote, transport)?.Id == local.Id,
                    "The three authored overlays record on the local Gallery and the authenticated acceptance routes.");
                Check(UiGalleryCaptureFixture.Match(Receiver, true, envelope, remote.WithDismissible(!local.Dismissible), transport) == null &&
                    UiGalleryCaptureFixture.Match(Receiver, true, envelope, Remote(local, id: "shared-8"), transport) == null &&
                    UiGalleryCaptureFixture.Match(Gallery, true, envelope, remote, transport) == null &&
                    UiGalleryCaptureFixture.Match(Receiver, true, Surface(2, transport, owner: "other.author"), remote, transport) == null,
                    "A wrong dismissibility, shared route, receiver, or publisher cannot record a native overlay.");
                Check(UiGalleryCaptureFixture.Match(Receiver, true, envelope, Remote(local, body: "private body"), transport) == null &&
                    UiGalleryCaptureFixture.Match(Receiver, true, envelope, Remote(local, content: new UiText("private field")), transport) == null &&
                    UiGalleryCaptureFixture.Match(Receiver, true, new UiWireEnvelope(Publisher, Package, 2,
                        palette.Id, Package), remote, transport) == null,
                    "Overlay admission verifies exact authored content and the actual transport descriptor digest.");
            }

            var hidden = PresentationPages.Overlay(PresentationOverlay.ButtonOnly, static () => { });
            var disabled = new UiButton(PresentationPages.OverlayCloseId, "Confirm route", static () => { }, enabled: false);
            Check(UiGalleryCaptureFixture.Match(Receiver, true, Surface(2, null), Remote(hidden, content: disabled)) == null,
                "The button-only sample checks its enabled authored button.");
            foreach (var board in new[]
            {
                PresentationPages.OverlayControls(static _ => { }, static () => { }),
                PresentationPages.StingerControls(static _ => { }, static () => { }),
                PresentationPages.ToastControls(static _ => { }, static () => { }),
            })
                Check(UiGalleryCaptureFixture.Match(Gallery, false, null, board)?.Id == board.Id &&
                    UiGalleryCaptureFixture.Match(Publisher, false, null, board) == null,
                    "The authored launcher boards are local Gallery fixtures.");
        }

        private static void RetainedHudKeepsItsRoute(UiThemeDescriptor transport)
        {
            var initial = PresentationPages.RetainedHud(FoundationPages.TransportTheme);
            var changed = PresentationPages.HudStatus("Supplies loaded.");
            var first = Surface(2, transport);
            var verified = UiGalleryCaptureFixture.Match(Receiver, true, first, Remote(initial), declared: null);
            Check(verified?.Id == Hud &&
                UiGalleryCaptureFixture.Match(Receiver, true, first, Remote(initial, content: changed), transport) == null,
                "The updated HUD state cannot open a recording route on its own.");
            ulong generation = 2;
            Check(UiGalleryCaptureFixture.MatchRetainedPresentation(verified!, changed, null, transport, first,
                Surface(3, transport), ref generation)?.Content is UiText { Text: "Supplies loaded." } && generation == 3 &&
                UiGalleryCaptureFixture.MatchRetainedPresentation(verified!, initial.Content, transport, transport, first,
                    Surface(4, transport), ref generation)?.Content is UiText { Text: "Signal clear." },
                "The two authored HUD states stay eligible on the original route, including a verified theme becoming ready.");
            Check(UiGalleryCaptureFixture.MatchRetainedPresentation(verified!, changed, transport, transport, first,
                Surface(3, transport), ref generation) == null &&
                UiGalleryCaptureFixture.MatchRetainedPresentation(verified!, changed, transport, transport, first,
                    Surface(5, transport, owner: "another.package"), ref generation) == null &&
                UiGalleryCaptureFixture.MatchRetainedPresentation(verified!, changed, transport, transport, first,
                    new UiWireEnvelope(Publisher, new byte[32], 5, transport.Id, transport.Digest.Span), ref generation) == null &&
                UiGalleryCaptureFixture.MatchRetainedPresentation(verified!, changed, transport, null, first,
                    Surface(5, null), ref generation) == null,
                "Stale, changed owner, package digest or theme cannot update a retained HUD recording.");
            Check(UiGalleryCaptureFixture.MatchRetainedPresentation(verified!, new UiText("private content"), transport, transport,
                first, Surface(7, transport), ref generation) == null &&
                UiGalleryCaptureFixture.MatchRetainedPresentation(verified!, changed, transport, transport,
                    first, Surface(6, transport), ref generation) == null,
                "Unlisted content clears the recording and advances its verified generation floor.");
            generation = 0;
            Check(UiGalleryCaptureFixture.MatchRetainedPresentation(initial, changed, transport, transport, null, null,
                ref generation)?.Content is UiText { Text: "Supplies loaded." } &&
                UiGalleryCaptureFixture.MatchRetainedPresentation(initial, changed, transport, null, null, null,
                    ref generation) == null, "Local HUD updates also keep the initial scoped theme.");
        }

        private static void ExactAnnouncementAndToastRequests(UiThemeDescriptor transport)
        {
            var envelope = new UiWireEventEnvelope(Publisher, Package, transport.Id, transport.Digest.Span);
            foreach (var role in PresentationPages.StingerRoles)
            {
                var request = PresentationPages.Stinger(role);
                var id = PresentationPages.StingerButtonId(role);
                Check(UiGalleryCaptureFixture.MatchStinger(Gallery, null, request, null) == id &&
                    UiGalleryCaptureFixture.MatchStinger(Receiver, envelope, request, transport) == id,
                    "Every authored announcement keeps its exact local and authenticated remote fixture ID.");
            }
            foreach (var request in PresentationPages.StingerQueue())
                Check(UiGalleryCaptureFixture.MatchStinger(Receiver, envelope, request, transport) == PresentationPages.StingerQueueId,
                    "The timed queue has its own authored capture ID.");
            var example = PresentationPages.Stinger(UiPaintRole.Primary);
            var changed = new UiStingerRequest(example.Title, example.Subtitle, example.Glyph, example.Role, durationSeconds: 6d);
            var wrong = new UiWireEventEnvelope(Publisher, Package, transport.Id, Package);
            Check(UiGalleryCaptureFixture.MatchStinger(Receiver, envelope, changed, transport) == null &&
                UiGalleryCaptureFixture.MatchStinger(Receiver, wrong, example, transport) == null &&
                UiGalleryCaptureFixture.MatchStinger(Publisher, envelope, example, transport) == null &&
                UiGalleryCaptureFixture.MatchStinger(Receiver, null, example, transport) == null,
                "A mismatched duration, descriptor, or unverified receiver route cannot record an announcement.");
            foreach (var tone in PresentationPages.ToastTones)
                Check(UiGalleryCaptureFixture.MatchesToast(PresentationPages.ToastMessage(tone), tone),
                    "Every exact authored legacy toast pair is eligible in the armed fixture.");
            Check(!UiGalleryCaptureFixture.MatchesToast(PresentationPages.ToastMessage(UiTone.Success), UiTone.Danger) &&
                !UiGalleryCaptureFixture.MatchesToast("Other player text", UiTone.Success) &&
                !UiGalleryCaptureFixture.MatchesToast("Other state", (UiTone)255),
                "The recorder refuses cross-paired tones and every unlisted toast message.");
        }

        private static void ToastArmsKeepTheirOwnerAndViewer()
        {
            var visible = true;
            using var local = UiGalleryToastArm.Arm(Gallery, false, UiGalleryCaptureFixture.ToastControlsId, "fixture", null, () => visible);
            using var remote = UiGalleryToastArm.Arm(Publisher, true, Hud, "fixture", null, static () => true);
            Check(local != null && remote != null && ReferenceEquals(UiGalleryToastArm.Find(Gallery, false), local) &&
                ReferenceEquals(UiGalleryToastArm.Find(Publisher, true), remote) &&
                UiGalleryToastArm.Find(Publisher, false) == null && UiGalleryToastArm.Find(Gallery, true) == null &&
                UiGalleryToastArm.Arm(Gallery, false, Hud, "fixture", null, static () => true) == null,
                "Only the verified local launcher or targeted presentation HUD arms its own toast route.");
            visible = false;
            Check(UiGalleryToastArm.Find(Gallery, false) == null && !local!.Eligible,
                "A hidden launcher also revokes a queued observation.");
            remote!.Dispose();
            Check(UiGalleryToastArm.Find(Publisher, true) == null && !remote.Eligible,
                "Retiring the armed HUD revokes later legacy toast observations.");
        }

        private static UiWireEnvelope Surface(ulong generation, UiThemeDescriptor? descriptor, string owner = Publisher) =>
            new UiWireEnvelope(owner, Package, generation, descriptor?.Id,
                descriptor == null ? new byte[32] : descriptor.Digest.Span);

        private static UiSurfaceRequest Remote(UiSurfaceRequest source, string id = "remote-8", string? body = null,
            UiNode? content = null)
        {
            var target = new UiSurfaceRequest(id, source.Title, body ?? source.Body, source.Kind, source.Width,
                source.Height, content ?? source.Content);
            return source.Kind == UiSurfaceKind.Hud ? target.WithHudAnchor(source.HudAnchor).WithHudVisibility(source.HudVisibility)
                : target.WithDismissible(source.Dismissible);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
