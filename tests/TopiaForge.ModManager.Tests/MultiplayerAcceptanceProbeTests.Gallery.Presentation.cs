using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.MultiplayerAcceptance;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MultiplayerAcceptanceProbeTests
    {
        private static void PresentationVisualFixtures()
        {
            PresentationMarkerPrecedence();
            using var fixture = new PresentationPair();
            fixture.Press();
            Check(fixture.Host.Created.Count == 0, "Presentation mode stays closed while the listen host is alone.");
            fixture.AddGuest("presentation-guest");
            var expected = fixture.DirectoryTarget!;
            fixture.DirectoryTarget = fixture.WrongGuest;
            fixture.Press();
            Check(fixture.Host.ThemeSelections == 0 && fixture.Host.Created.Count == 0 &&
                fixture.WrongUi.Created.Count == 0 && fixture.Guest.Created.Count == 0,
                "A directory handle for the wrong participant cannot receive a paired presentation.");
            fixture.DirectoryTarget = expected;

            fixture.Guest.RefuseNextSurface = true;
            fixture.Press();
            Check(fixture.Host.Created.Single().Disposed && fixture.Guest.Open.Count == 0,
                "A refused guest surface retires the partially opened host fixture.");

            var newHandle = GalleryPresentationServiceProxy.With<IPlayer>(expected, nameof(IPlayer.Ui), fixture.WrongUi);
            fixture.Host.AfterCreate = () => fixture.DirectoryTarget = newHandle;
            fixture.Press();
            fixture.Host.AfterCreate = null;
            Check(fixture.Host.Created.Last().Disposed && fixture.Guest.Created.Count == 0 && fixture.WrongUi.Created.Count == 0,
                "Replacing a guest's handle during publication retires the partial host surface without sending to either handle.");
            fixture.DirectoryTarget = expected;

            Check(GalleryRemotePresentation.VariantsPerPalette == 15, "The presentation matrix has 15 bounded steps per palette.");
            foreach (var theme in new[] { null, FoundationPages.TransportTheme })
            {
                PairedOverlayFixtures(fixture, theme?.Id);
                PairedStingerFixtures(fixture, theme?.Id);
                var hostToasts = fixture.Host.Toasts.Count;
                var guestToasts = fixture.Guest.Toasts.Count;
                fixture.Press();
                Check(fixture.Host.Open.Single().Id == "gallery-presentation-hud" &&
                    fixture.Guest.Open.Single().Request.Theme?.Id == theme?.Id &&
                    fixture.Host.SelectedTheme?.Id == theme?.Id, "The toast fixture keeps its surrounding themed HUD visible.");
                Check(fixture.Host.Toasts.Count == hostToasts + 4 && fixture.Guest.Toasts.Count == guestToasts + 4,
                    "The paired step requests exactly four stock toasts on each viewer.");
                foreach (var view in new[] { fixture.Host, fixture.Guest })
                    Check(view.Toasts.TakeLast(4).SequenceEqual(PresentationPages.ToastTones.Select(tone =>
                        (PresentationPages.ToastMessage(tone), tone))), "Both viewers request the exact four tones through the stock toast API.");
                fixture.CheckStep("gallery-toast-stack", theme?.Id);
            }

            Check(fixture.WrongUi.Created.Count == 0 && fixture.WrongUi.Offers.Count == 0 && fixture.WrongUi.Toasts.Count == 0,
                "No surface, stinger or toast targets the unrelated participant.");
            var extra = fixture.AddGuest("presentation-extra", replaceTarget: false);
            Check(fixture.Host.Open.Count == 0 && fixture.Guest.Open.Count == 0,
                "A second admitted guest retires the entire open pair.");
            Check(fixture.Host.SelectedTheme == null, "Losing the pair restores the scope's prior theme selection.");
            fixture.Press();
            Check(fixture.Host.Open.Count == 0, "An ambiguous audience leaves the presentation closed.");
            fixture.Rig.Disconnect(extra);
            fixture.Rig.Advance(8);
            fixture.Press();
            Check(fixture.Host.Open.Single().Id == "gallery-overlay-passive" && fixture.Host.SelectedTheme == null,
                "The thirty-step presentation cycle wraps to the kit passive overlay.");

            PresentationReplacementAndClose();
            Console.WriteLine("Gallery presentation fixtures passed 30 paired steps, scope palettes, dismissal, queue, and audience retirement.");
        }

        private static void PairedOverlayFixtures(PresentationPair fixture, string? theme)
        {
            fixture.Author.Input.IsUiFocused = true;
            foreach (var mode in PresentationPages.OverlayOrder)
            {
                fixture.Press();
                var host = fixture.Host.Open.Single();
                var guest = fixture.Guest.Open.Single();
                var id = PresentationPages.OverlayButtonId(mode);
                Check(host.Id == id && guest.Id == id && host.Request.Kind == UiSurfaceKind.Overlay &&
                    guest.Request.Kind == UiSurfaceKind.Overlay && host.Request.Theme?.Id == theme && guest.Request.Theme?.Id == theme,
                    "Each overlay uses the same authored fixture and effective palette on both public UI routes.");
                fixture.CheckStep(id, theme);
                if (mode == PresentationOverlay.Passive)
                {
                    Check(host.ContentKind == UiOverlayContentKind.PresentationOnly &&
                        guest.ContentKind == UiOverlayContentKind.PresentationOnly && !host.Back() && !guest.Back(),
                        "The paired passive overlay has no interactive nodes or Back claim.");
                    continue;
                }

                var enabled = guest.Content is UiButton { Enabled: true, Id: PresentationPages.OverlayCloseId };
                Check(enabled && host.ContentKind == UiOverlayContentKind.Interactive && guest.ContentKind == UiOverlayContentKind.Interactive,
                    "Both interactive fixtures include an admitted viewer-local button.");
                if (mode == PresentationOverlay.Dismissible)
                {
                    Check(guest.Back() && !guest.IsVisible && host.IsVisible, "Guest Back hides only the guest dismissible surface.");
                    var click = ((UiButton)guest.Content!).Activated;
                    click();
                    Check(guest.Hides == 1, "A stale activation on a hidden surface has no effect.");
                }
                else
                {
                    Check(!host.Back() && !guest.Back() && guest.IsVisible, "Back leaves the button-only overlay open.");
                    ((UiButton)guest.Content!).Activated();
                    Check(!guest.IsVisible && host.IsVisible, "The guest's button closes only its own overlay.");
                    ((UiButton)host.Content!).Activated();
                    Check(!host.IsVisible && host.Hides == 1, "The host's button independently closes its local overlay.");
                }
            }

            var retiredHost = fixture.Host.Open.Single();
            var retiredGuest = fixture.Guest.Open.Single();
            fixture.Press();
            fixture.CheckStep(PresentationPages.OverlayStackId, theme);
            Check(fixture.Host.LiveOverlayCount + fixture.Guest.LiveOverlayCount == UiOverlayLayout.MaximumLive,
                "The author spends exactly four slots across both views while retaining the paired HUDs.");
            Check(retiredHost.Disposed && retiredGuest.Disposed, "F7 retires both prior handles before opening the stack.");
            ((UiButton)retiredGuest.Content!).Activated();
            Check(retiredGuest.Hides == 1, "A callback from an already disposed fixture cannot affect a new publication.");
            foreach (var view in new[] { fixture.Host, fixture.Guest })
            {
                var open = view.Open;
                Check(open.Select(surface => surface.Id).SequenceEqual(new[]
                    {
                        "gallery-presentation-hud", "gallery-overlay-passive", "gallery-overlay-buttononly"
                    }) && open.All(surface => surface.Request.Theme?.Id == theme),
                    "Both peers open the HUD first, followed by passive and button-only overlays.");
                var hud = open[0];
                Check(hud.Updates == 1 && hud.Content is UiText { Id: "gallery-presentation-status", Text: "Supplies loaded." },
                    "Both original HUD handles update after the two overlays are in front.");
            }
        }

        private static void PairedStingerFixtures(PresentationPair fixture, string? theme)
        {
            foreach (var role in PresentationPages.StingerRoles)
            {
                var hostOffers = fixture.Host.Offers.Count;
                var guestOffers = fixture.Guest.Offers.Count;
                fixture.Press();
                fixture.CheckStep(PresentationPages.StingerButtonId(role), theme);
                Check(fixture.Host.Offers.Count == hostOffers + 1 && fixture.Guest.Offers.Count == guestOffers + 1,
                    "Each stinger step calls exactly one host and one guest SDK route.");
                var local = fixture.Host.Offers.Last();
                var remote = fixture.Guest.Offers.Last();
                var expected = PresentationPages.Stinger(role);
                Check(local.Request.Role == role && ReferenceEquals(local.Request, remote.Request) &&
                    local.Request.Title == expected.Title && local.Request.Subtitle == expected.Subtitle &&
                    local.Request.Glyph == expected.Glyph && local.Theme == theme && remote.Theme == theme &&
                    local.Result == UiStingerQueueResult.Started && remote.Result == UiStingerQueueResult.Started &&
                    fixture.Host.Open.Count == 0 && fixture.Guest.Open.Count == 0,
                    "Both real SDK stinger calls use the chosen scope palette and retire earlier retained surfaces.");
                fixture.Advance(3d);
            }

            fixture.Press();
            fixture.CheckStep(PresentationPages.StingerQueueId, theme);
            foreach (var view in new[] { fixture.Host, fixture.Guest })
                Check(view.Offers.TakeLast(6).Select(item => item.Result).SequenceEqual(new[]
                    {
                        UiStingerQueueResult.Started, UiStingerQueueResult.Queued, UiStingerQueueResult.Queued,
                        UiStingerQueueResult.Coalesced, UiStingerQueueResult.Queued, UiStingerQueueResult.RateLimited
                    }) && view.Offers.TakeLast(6).All(item => item.Theme == theme),
                    "Each queue starts once, admits three waiting entries, coalesces the duplicate and refuses overflow.");
            foreach (var title in new[] { "Depot open", "Water ready", "Weather report", "Lanterns lit" })
            {
                Check(fixture.Host.Active?.Title == title && fixture.Guest.Active?.Title == title,
                    "The paired announcement queues play in order with the original one-second duration.");
                fixture.Advance(1d);
            }
            Check(fixture.Host.Active == null && fixture.Guest.Active == null,
                "The duplicate and overflow never display on either viewer.");
        }

        private static void PresentationReplacementAndClose()
        {
            using var fixture = new PresentationPair();
            fixture.AddGuest("presentation-replacement");
            fixture.Press();
            fixture.Press();
            var stale = fixture.Guest.Open.Single();
            fixture.Rig.StartNewSession("session:f7-presentation-replacement");
            fixture.Rig.Advance(8);
            Check(fixture.Host.Open.Count == 0 && fixture.Guest.Open.Count == 0 && stale.Disposed,
                "Presentation fixtures retire on an authenticated session replacement.");
            ((UiButton)stale.Content!).Activated();
            Check(stale.Hides == 0, "A callback from an earlier session cannot hide a surface.");
            fixture.Press();
            Check(fixture.Host.Open.Single().Id == "gallery-overlay-buttononly",
                "The next explicit press can open the next step in the replacement session.");
            var created = fixture.Host.Created.Count;
            fixture.Rig.Dispose();
            Check(fixture.Session.Snapshot.State == MultiplayerSessionState.Ended &&
                fixture.Host.Open.Count == 0 && fixture.Guest.Open.Count == 0,
                "Ending the session closes both presentation routes.");
            fixture.Press();
            Check(fixture.Host.Created.Count == created, "A closed session cannot publish a replacement surface.");
        }

        private static void PresentationMarkerPrecedence()
        {
            using var unarmedRig = MultiplayerTestRig.CreateListenServer();
            using var author = new FakeModContext();
            author.Extensions.Register(unarmedRig.Server.Session);
            Arm(author, GalleryRemotePresentation.OptInFile);
            using (var unarmed = ModLifecycleRunner.Create<MultiplayerAcceptanceMod>(author))
            {
                unarmed.Load();
                Check(author.Input.ActiveActionCount == 0 && author.Ui.Surfaces.Count == 0,
                    "The presentation selector alone does not arm the F7 package.");
                unarmed.Unload();
            }
            foreach (var mode in new[] { GalleryRemotePreview.StatusOnlyOptInFile, GalleryRemotePreview.RetainedCountdownOptInFile })
            {
                using var pair = new PresentationPair(GalleryRemotePreview.StatusOnlyOptInFile, mode);
                pair.AddGuest("presentation-marker-guest");
                pair.Press();
                var expected = mode == GalleryRemotePreview.StatusOnlyOptInFile
                    ? ("gallery-status", UiSurfaceKind.Window, (string?)null)
                    : ("gallery-countdowns", UiSurfaceKind.Hud, FoundationPages.TransportTheme.Id);
                foreach (var view in new[] { pair.Host, pair.Guest })
                {
                    var surface = view.Open.Single();
                    Check((surface.Id, surface.Request.Kind, surface.Request.Theme?.Id) == expected,
                        "Existing Status and retained modes take precedence over the new presentation marker.");
                }
            }
        }

        private static void Arm(FakeModContext context, string marker) =>
            Check(context.Files.WriteDataTextAsync(marker, string.Empty).GetAwaiter().GetResult().Succeeded, "Write the opt-in fixture marker.");

        private sealed class PresentationPair : IDisposable
        {
            private readonly MultiplayerAcceptanceMod mod = new();
            private readonly FakePlayerDirectory players = new();
            private readonly AudienceDirectory audience;

            internal PresentationPair(params string[] additionalMarkers)
            {
                Guest = new GalleryPresentationUiProbe(() => Host.SelectedTheme);
                Host.ScopeOverlayCount = Guest.ScopeOverlayCount = () => Host.LiveOverlayCount + Guest.LiveOverlayCount;
                var stranger = players.Add(new ParticipantId("fixture-unrelated"), "Unrelated Viewer");
                WrongGuest = GalleryPresentationServiceProxy.With<IPlayer>(stranger, nameof(IPlayer.Ui), WrongUi);
                audience = new AudienceDirectory(players);
                Session = GalleryPreviewSessionProxy.Wrap(Rig.Server.Session, audience);
                Author.Extensions.Register(Session);
                Arm(Author, GalleryRemotePreview.OptInFile);
                Arm(Author, GalleryRemotePresentation.OptInFile);
                foreach (var marker in additionalMarkers) Arm(Author, marker);
                mod.Load(GalleryPresentationServiceProxy.With<IModContext>(Author, nameof(IModContext.Ui), Host));
            }

            internal MultiplayerTestRig Rig { get; } = MultiplayerTestRig.CreateListenServer();
            internal FakeModContext Author { get; } = new();
            internal IMultiplayerSession Session { get; }
            internal GalleryPresentationUiProbe Host { get; } = new();
            internal GalleryPresentationUiProbe Guest { get; }
            internal GalleryPresentationUiProbe WrongUi { get; } = new();
            internal IPlayer WrongGuest { get; }
            internal IPlayer? DirectoryTarget { get => audience.Target; set => audience.Target = value; }

            internal MultiplayerTestNode AddGuest(string label, bool replaceTarget = true)
            {
                var guest = Rig.AddRemoteClient(label);
                Rig.Advance(8);
                if (replaceTarget)
                {
                    var player = players.Add(guest.ParticipantId!.Value, "Admitted Guest");
                    DirectoryTarget = GalleryPresentationServiceProxy.With<IPlayer>(player, nameof(IPlayer.Ui), Guest);
                }
                return guest;
            }

            internal void Press() => PressGallery(Author);

            internal void Advance(double seconds)
            {
                Host.Advance(seconds);
                Guest.Advance(seconds);
            }

            internal void CheckStep(string fixture, string? theme) =>
                Check(Author.Logger.Entries.Last().Message ==
                    "Published the paired Gallery presentation fixture '" + fixture + "' in " + (theme ?? "kit") + ".",
                    "The public receipt uses the exact fixed matrix ID and palette.");

            public void Dispose()
            {
                mod.Unload();
                Author.Dispose();
                Check(Host.Open.Count == 0 && Guest.Open.Count == 0, "Mod lifetime disposal closes all fixture handles.");
                players.Dispose();
                Rig.Dispose();
            }
        }

        private sealed class AudienceDirectory : IPlayerDirectory
        {
            private readonly IPlayerDirectory source;
            internal AudienceDirectory(IPlayerDirectory source) => this.source = source;
            internal IPlayer? Target { get; set; }
            public System.Collections.Generic.IReadOnlyList<IPlayer> Players => source.Players;
            public IPlayer? Local => source.Local;
            public bool TryGet(ParticipantId id, out IPlayer? player)
            {
                player = Target;
                return player != null;
            }
            public IDisposable SubscribeJoined(Action<IPlayer> handler) => source.SubscribeJoined(handler);
            public IDisposable SubscribeLeft(Action<IPlayer> handler) => source.SubscribeLeft(handler);
            public OperationResult<IScoreboardColumn> RegisterScoreboardColumn(ScoreboardColumnDefinition definition) =>
                source.RegisterScoreboardColumn(definition);
        }
    }
}
