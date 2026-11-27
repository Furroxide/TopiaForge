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
        private static void StatusVisualFixtures(AcceptanceGalleryPackage fixture)
        {
            using var rig = MultiplayerTestRig.CreateListenServer();
            using var players = new FakePlayerDirectory();
            using var context = new FakeModContext();
            var session = rig.Server.Session;
            context.Extensions.Register(GalleryPreviewSessionProxy.Wrap(session, players));
            Check(context.Files.WriteDataTextAsync(GalleryRemotePreview.StatusOnlyOptInFile, string.Empty)
                .GetAwaiter().GetResult().Succeeded, "Prepare the shortened F7 cycle.");

            using (var unarmed = ModLifecycleRunner.Create<MultiplayerAcceptanceMod>(context))
            {
                unarmed.Load();
                Check(context.Input.ActiveActionCount == 0 && context.Ui.Surfaces.Count == 0,
                    "The shortened cycle alone cannot arm the multiplayer publisher.");
                unarmed.Unload();
            }

            var guest = rig.AddRemoteClient("status-guest");
            rig.Advance(8);
            players.Add(guest.ParticipantId!.Value, "Status guest");
            var target = players.State(guest.ParticipantId.Value).Ui;
            fixture.Install(target);
            using var author = new FakeModContext();
            author.Extensions.Register(GalleryPreviewSessionProxy.Wrap(session, players));
            foreach (var marker in new[] { GalleryRemotePreview.OptInFile, GalleryRemotePreview.StatusOnlyOptInFile })
                Check(author.Files.WriteDataTextAsync(marker, string.Empty).GetAwaiter().GetResult().Succeeded,
                    "Arm the acceptance publisher and the shortened status cycle.");
            fixture.Install(author.Ui);
            var now = 120d;
            const string epoch = "acceptance-status-clock";
            ((IUiClockReceiver)author.Ui).FollowClock(() => now, () => epoch);
            ((IUiClockReceiver)target).FollowClock(() => now, () => epoch);
            using var runner = ModLifecycleRunner.Create<MultiplayerAcceptanceMod>(author);
            runner.Load();

            for (var index = 0; index < 8; index++)
            {
                PressGallery(author);
                var local = author.Ui.Surfaces.Single();
                var paired = target.Surfaces.Single();
                var page = index < 4 ? "gallery-status" : "gallery-countdowns";
                var kind = (index & 1) == 0 ? UiSurfaceKind.Window : UiSurfaceKind.Hud;
                var theme = (index & 2) == 0 ? null : FoundationPages.TransportTheme;
                Check(local.Id == page && paired.Id == page && local.Request.Kind == kind && paired.Request.Kind == kind &&
                    local.Request.Theme?.Id == theme?.Id && paired.Request.Theme?.Id == theme?.Id,
                    "The short cycle visits both status pages in the established kit and transport palettes.");
                if (page == "gallery-countdowns")
                {
                    var onset = now;
                    CheckPair(12); // Initial native sample after F7.
                    now = onset + 2d; CheckPair(10);
                    now = onset + 7d; CheckPair(5);
                    now = onset + 12d; CheckPair(0);

                    void CheckPair(int expected)
                    {
                        Check(local.TryGetCountdownSeconds("timer-transfer", out var hostSeconds) &&
                            paired.TryGetCountdownSeconds("timer-transfer", out var guestSeconds) &&
                            hostSeconds == expected && guestSeconds == expected,
                            "Both surfaces keep the original transfer deadline through warning, critical and ordinary expiry.");
                    }
                }
            }

            PressGallery(author);
            Check(author.Ui.Surfaces.Single().Id == "gallery-status" &&
                author.Ui.Surfaces.Single().Request.Theme == null, "The short F7 cycle returns to kit Paper Status.");
            rig.StartNewSession("session:status-fixture-replacement");
            rig.Advance(8);
            Check(author.Ui.Surfaces.Count == 0 && target.Surfaces.Count == 0,
                "An authenticated session replacement retires the F7 pair rather than publishing to a new session.");
            runner.Unload();
            author.AssertNoLeaks();
            context.AssertNoLeaks();
            Console.WriteLine("Gallery status visual fixtures: eight paired variants, four timer states, and session retirement passed.");
            RetainedCountdownPreview(fixture);
        }

        private static void RetainedCountdownPreview(AcceptanceGalleryPackage fixture)
        {
            using var rig = MultiplayerTestRig.CreateListenServer();
            using var players = new FakePlayerDirectory();
            using var author = new FakeModContext();
            var session = rig.Server.Session;
            author.Extensions.Register(GalleryPreviewSessionProxy.Wrap(session, players));
            foreach (var marker in new[] { GalleryRemotePreview.OptInFile, GalleryRemotePreview.RetainedCountdownOptInFile })
                Check(author.Files.WriteDataTextAsync(marker, string.Empty).GetAwaiter().GetResult().Succeeded,
                    "Arm the retained Countdown fixture for a single in-place replacement.");
            fixture.Install(author.Ui);
            using var runner = ModLifecycleRunner.Create<MultiplayerAcceptanceMod>(author);
            runner.Load();
            PressGallery(author);
            Check(author.Ui.Surfaces.Count == 0, "Retained mode still requires a ready listen pair before opening.");

            var guest = rig.AddRemoteClient("retained-status-guest");
            rig.Advance(8);
            var guestId = guest.ParticipantId!.Value;
            var player = players.Add(guestId, "Countdown guest");
            var target = players.State(guestId).Ui;
            fixture.Install(target);
            string Epoch() => session.Snapshot.Id.Value;
            ((IUiClockReceiver)author.Ui).FollowClock(() => 120d, Epoch);
            ((IUiClockReceiver)target).FollowClock(() => 120d, Epoch);
            PressGallery(author);
            var local = author.Ui.Surfaces.Single();
            var paired = target.Surfaces.Single();
            var timer = ((UiColumn)local.Content!).Children.OfType<UiCountdown>().Single(node => node.Id == "timer-hours");
            var epoch = Epoch();
            Check(local.Id == "gallery-countdowns" && local.Request.Kind == UiSurfaceKind.Hud &&
                local.Request.Theme?.Id == FoundationPages.TransportTheme.Id && ReferenceEquals(local.Content, paired.Content) &&
                timer is { Id: "timer-hours", EndsAt: 3843d } && timer.ClockEpoch == epoch,
                "One F7 press publishes the fixed transport Countdowns HUD and binds it to the original epoch.");

            rig.StartNewSession("session:status-fixture-first-map");
            rig.Advance(8);
            Check(Epoch() != epoch && ReferenceEquals(author.Ui.Surfaces.Single(), local) &&
                ReferenceEquals(target.Surfaces.Single(), paired) && ReferenceEquals(local.Content, paired.Content) &&
                timer.ClockEpoch == epoch && timer.EndsAt == 3843d &&
                local.TryGetCountdownSeconds("timer-hours", out var localTime) && localTime == 0 &&
                paired.TryGetCountdownSeconds("timer-hours", out var guestTime) && guestTime == 0,
                "A retained in-place replacement preserves both issued handles and expires their original clock binding.");
            rig.StartNewSession("session:status-fixture-second-map");
            rig.Advance(8);
            Check(author.Ui.Surfaces.Count == 0 && target.Surfaces.Count == 0,
                "The retained fixture handles cover only one replacement per publication.");

            PressGallery(author);
            Check(author.Ui.Surfaces.Count == 1 && target.Surfaces.Count == 1,
                "A new explicit press starts another single-replacement run.");
            players.ReplaceGeneration();
            Check(!player.IsPresent && players.TryGet(guestId, out var successor) && successor?.IsPresent == true,
                "A reused participant ID now names a different player handle.");
            rig.StartNewSession("session:status-fixture-new-player");
            rig.Advance(8);
            Check(author.Ui.Surfaces.Count == 0 && target.Surfaces.Count == 0,
                "A new player incarnation cannot inherit the old publication even when it has the same participant ID.");
            runner.Unload();
            author.AssertNoLeaks();
            Console.WriteLine("Gallery retained Countdown fixture: one replacement on the original player route, no inheritance for replacement players.");
        }
    }
}
