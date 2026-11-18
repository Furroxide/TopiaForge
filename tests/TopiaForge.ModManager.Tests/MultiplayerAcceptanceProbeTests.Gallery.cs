using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.MultiplayerAcceptance;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MultiplayerAcceptanceProbeTests
    {
        private static void GalleryPreviewUsesTheOptInAndOneAdmittedGuest()
        {
            var root = Program.FindRepoRoot();
            var package = Path.Combine(root, "tests", "TopiaForge.MultiplayerAcceptanceMod");
            var manifest = ModManifestJson.Deserialize(File.ReadAllText(Path.Combine(package, "topiaforge.mod.json")));
            const string definition = "ui/themes/transport-preview.json";
            var fixture = File.ReadAllBytes(Path.Combine(package, "ui", "themes", "transport-preview.json"));
            var gallery = Path.Combine(root, "mods", "TopiaForge.UiGallery");
            var galleryManifest = ModManifestJson.Deserialize(File.ReadAllText(Path.Combine(gallery, "topiaforge.mod.json")));
            var catalogue = UiThemeCatalogueParser.ParseManifest(File.ReadAllBytes(Path.Combine(package, "topiaforge.mod.json")));
            var descriptor = UiThemePackParser.Parse(fixture, expectedId: FoundationPages.TransportTheme.Id);
            Check(manifest.Id == UiGalleryCaptureFixture.RemoteFixtureOwner &&
                manifest.EffectiveMultiplayerMode == ModMultiplayerEffectiveMode.Session &&
                catalogue.TryGetValue(out var themes) && themes.Default == null &&
                themes.Themes.Count == 1 && themes.Themes[0].Definition == definition && descriptor.Succeeded &&
                themes.Themes[0].Id == FoundationPages.TransportTheme.Id &&
                !File.Exists(Path.Combine(package, "ui", "themes", "survival.json")),
                "The shared test publisher owns a distinct fixture palette and keeps its normal theme cascade.");
            var localProfile = new ModNetProfile(ModNetworkClasses.Classify(galleryManifest), ModExecutionContext.Process);
            var publisherProfile = new ModNetProfile(ModNetworkClasses.Classify(manifest), ModExecutionContext.Process);
            Check(PresentationRoutingPolicy.Route(NetRealm.Client, localProfile, PresentationKind.HudSurface) == PresentationRoute.Local &&
                PresentationRoutingPolicy.RouteTargeted(NetRealm.ListenHost, localProfile, false, PresentationKind.HudSurface) ==
                PresentationRoute.Refused, "The ordinary Gallery renders on a connected guest and cannot publish to another peer.");
            Check(PresentationRoutingPolicy.Route(NetRealm.ListenHost, publisherProfile, PresentationKind.HudSurface) == PresentationRoute.Local &&
                PresentationRoutingPolicy.RouteTargeted(NetRealm.ListenHost, publisherProfile, false, PresentationKind.HudSurface) ==
                PresentationRoute.TargetPlayer, "The opted-in Shared process uses a targeted route while its own HUD stays local.");

            using var rig = MultiplayerTestRig.CreateListenServer();
            using var players = new FakePlayerDirectory();
            using var context = new FakeModContext();
            var source = rig.Server.Session;
            context.Extensions.Register(GalleryPreviewSessionProxy.Wrap(source, players));
            Check(context.Files.WriteDataTextAsync(GalleryRemotePreview.OptInFile, string.Empty).GetAwaiter().GetResult().Succeeded,
                "The Gallery-only fixture is explicitly armed without an acceptance config.");
            using var runner = ModLifecycleRunner.Create<MultiplayerAcceptanceMod>(context);
            runner.Load();
            Check(context.Input.ActiveActionCount == 1 && context.Ui.Surfaces.Count == 0 &&
                !context.Logger.Entries.Any(entry => entry.Message.StartsWith("TF-MP-", StringComparison.Ordinal)),
                "Opt-in registers manual publication without opening a surface or emitting acceptance markers.");
            Press();
            Check(GalleryRemotePreview.FindGuest(source.Snapshot) == null && context.Ui.Surfaces.Count == 0,
                "A solo process cannot publish the pair.");
            var guest = rig.AddRemoteClient("guest");
            rig.Advance(8);
            Check(Nullable.Equals(GalleryRemotePreview.FindGuest(source.Snapshot), guest.ParticipantId) &&
                GalleryRemotePreview.FindGuest(guest.Session.Snapshot) == null,
                "Only the host may target its single admitted connected guest.");
            players.Add(guest.ParticipantId!.Value, "Fixture guest");
            var target = players.State(guest.ParticipantId.Value).Ui;
            Press();
            Check(context.Ui.Surfaces.Count == 1 && target.Surfaces.Count == 1 &&
                context.Ui.Surfaces[0].Request.Kind == UiSurfaceKind.Window && target.Surfaces[0].Request.Kind == UiSurfaceKind.Window,
                "An explicit publication sends the same fixture through both public surface services.");
            context.Input.IsUiFocused = true;
            Press();
            Check(context.Ui.Surfaces.Single().Request.Kind == UiSurfaceKind.Hud &&
                target.Surfaces.Single().Request.Kind == UiSurfaceKind.Hud,
                "F7 can advance a visible preview and retires the previous pair.");
            var theme = System.Text.Encoding.UTF8.GetString(fixture);
            Check(context.Ui.AddThemeFixture(theme).Succeeded, "Install the test host's fixture definition.");
            Press();
            Check(context.Ui.Surfaces.Count == 0 && target.Surfaces.Count == 0,
                "If the target refuses its fixture, the newly opened local surface is retired.");
            Check(target.AddThemeFixture(theme).Succeeded, "Install the test guest's fixture definition.");
            Press();
            Check(context.Ui.Surfaces.Single().Request.Theme?.Id == FoundationPages.TransportTheme.Id &&
                target.Surfaces.Single().Request.Theme?.Id == FoundationPages.TransportTheme.Id &&
                target.Surfaces[0].Request.Kind == UiSurfaceKind.Window,
                "After a refusal the next press retries the same paired variant.");
            var second = rig.AddRemoteClient("second-guest");
            rig.Advance(8);
            Check(second.ParticipantId.HasValue && GalleryRemotePreview.FindGuest(source.Snapshot) == null &&
                context.Ui.Surfaces.Count == 0 && target.Surfaces.Count == 0,
                "A second guest invalidates the pair and retires both owned surfaces.");
            runner.Unload();
            context.AssertNoLeaks();

            void Press()
            {
                context.Input.SetValue(GalleryRemotePreview.InputAction, 1f);
                context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
                context.Input.SetValue(GalleryRemotePreview.InputAction, 0f);
                context.AdvanceFrame(TimeSpan.Zero);
            }
        }
    }

    public class GalleryPreviewSessionProxy : DispatchProxy
    {
        private IMultiplayerSession session = null!;
        private IPlayerDirectory players = null!;

        internal static IMultiplayerSession Wrap(IMultiplayerSession session, IPlayerDirectory players)
        {
            var proxy = Create<IMultiplayerSession, GalleryPreviewSessionProxy>();
            var capture = (GalleryPreviewSessionProxy)(object)proxy;
            capture.session = session;
            capture.players = players;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "get_Players" ? players : method?.Invoke(session, args);
    }
}
