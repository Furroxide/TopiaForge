using System;
using System.IO;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        internal static void GalleryStatusUsesTheRetainedSoloWorldClock()
        {
            var directory = Directory.CreateTempSubdirectory("ui-gallery-world-");
            try { VerifyRetainedGalleryHud(directory.FullName); }
            finally { directory.Delete(recursive: true); }
            Console.WriteLine("Gallery retained solo epoch: normal launch route, unchanged process HUD, and formatted zero passed.");
        }

        private static void VerifyRetainedGalleryHud(string directory)
        {
            using var rig = new Rig();
            using var session = new SessionFixture(directory, replication: rig.Host.Node.Replication);
            rig.Host.Node.Runtime.FollowUiWorldSessions(session.Hosted);
            session.Launch("gallery-world-first");
            rig.Frames(12);

            using var gallery = new Author(rig.Host.Node, ModExecutionContext.Process, ModNetworkClass.ClientLocal);
            var root = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.UiGallery");
            var manifest = File.ReadAllText(Path.Combine(root, "topiaforge.mod.json"));
            Assert(gallery.LocalUi.AddThemeCatalogue(manifest, path => File.ReadAllText(Path.Combine(root, path))).Succeeded,
                "The local epoch fixture uses the ordinary Gallery package.");
            var request = StatusGalleryPages.CreateRequest(StatusGalleryPage.Countdowns, UiSurfaceKind.Hud,
                gallery.Ui.ClockTime, FoundationPages.SurvivalTheme);
            using var handle = gallery.Ui.CreateSurface(request).Value!;
            gallery.LocalUi.CompletePendingThemeRegistrations();
            var observed = gallery.LocalUi.Surfaces.Single();
            var timer = ((UiColumn)request.Content!).Children.OfType<UiCountdown>().Single(node => node.Id == "timer-hours");
            var epoch = rig.Host.Node.Runtime.UiClockEpoch;
            var identity = timer.ClockIdentity;
            var end = timer.EndsAt;
            Assert(identity != null && timer.ClockEpoch == epoch &&
                observed.TryGetCountdownSeconds("timer-hours", out var before) && before > 3600,
                "The original Survival HUD shows a long running timer before PLAY starts another solo world.");

            session.Launch("gallery-world-next");
            Assert(rig.Host.Node.Runtime.UiClockEpoch != epoch, "A second supported solo launch creates a new clock epoch.");
            Assert(ReferenceEquals(gallery.LocalUi.Surfaces.Single(), observed) && handle.IsVisible &&
                observed.TryGetCountdownSeconds("timer-hours", out var after) && after == 0,
                "The original process-owned Gallery HUD remains visible and immediately expires the long timer.");
            Assert(handle.SetContent(request.Content!).Succeeded &&
                handle.SetContent(new UiColumn(timer)).ErrorCode == ModErrorCode.InvalidArgument &&
                ReferenceEquals(observed.Content, request.Content) && ReferenceEquals(timer.ClockIdentity, identity) &&
                timer.ClockEpoch == epoch && timer.EndsAt == end,
                "Same-tree reapplication keeps the old clock binding and a new stale tree is refused atomically.");
            var reading = observed.Capture(new UiCaptureViewport(1920, 1080));
            Assert(reading.Nodes.Any(node => node.Id == timer.Id && node.IsRendered && node.RemainingSeconds == 0 &&
                    node.ThemeId == FoundationPages.SurvivalTheme.Id) &&
                reading.Nodes.Any(node => node.Kind == "CountdownDigits" && node.IsRendered && node.Text == "0:00:00") &&
                reading.CheckAccessibility().Count == 0, "The held Survival HUD exports formatted zero and accessible paint.");
        }
    }
}
