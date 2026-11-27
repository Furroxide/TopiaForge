using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_AuthorInputDefaults")]
        private static extern void SetGalleryAuthorDefaults(FakeUiService service, Func<string, IReadOnlyList<InputBinding>?> defaults);

        private static void GalleryPreviewUsesTheOptInAndOneAdmittedGuest()
        {
            var root = Program.FindRepoRoot();
            var package = Path.Combine(root, "tests", "TopiaForge.MultiplayerAcceptanceMod");
            var fixture = new AcceptanceGalleryPackage();
            var manifest = ModManifestJson.Deserialize(fixture.Manifest);
            var gallery = Path.Combine(root, "mods", "TopiaForge.UiGallery");
            var galleryManifest = ModManifestJson.Deserialize(File.ReadAllText(Path.Combine(gallery, "topiaforge.mod.json")));
            var catalogue = UiThemeCatalogueParser.ParseManifest(File.ReadAllBytes(Path.Combine(package, "topiaforge.mod.json")));
            using var declared = JsonDocument.Parse(fixture.Manifest);
            Check(manifest.Id == UiGalleryCaptureFixture.RemoteFixtureOwner &&
                manifest.EffectiveMultiplayerMode == ModMultiplayerEffectiveMode.Session &&
                declared.RootElement.GetProperty("x-topiaforge-non-distributable").GetBoolean() &&
                catalogue.TryGetValue(out var themes) && themes.Default == null &&
                themes.Themes.Count == 2 && themes.Themes[0].Definition == AcceptanceGalleryPackage.DefinitionPath &&
                themes.Themes[0].Id == FoundationPages.TransportTheme.Id &&
                themes.Themes[1].Definition == "ui/themes/survival.json" && themes.Themes[1].Id == FoundationPages.SurvivalTheme.Id &&
                !File.Exists(Path.Combine(package, "ui", "themes", "survival.json")),
                "The non-distributable Shared test publisher has both non-default palettes and stages Survival at pack time.");
            Check(fixture.Descriptor.Images[0] is { Id: "card", PixelWidth: 96, PixelHeight: 48, IsMask: false } &&
                fixture.Descriptor.Images[1] is { Id: "mark", PixelWidth: 32, PixelHeight: 32, IsMask: true },
                "The actual packer admits this package's artwork and keeps the receipt's sprite dimensions and Alpha8 mask.");
            var localProfile = new ModNetProfile(ModNetworkClasses.Classify(galleryManifest), ModExecutionContext.Process);
            var publisherProfile = new ModNetProfile(ModNetworkClasses.Classify(manifest), ModExecutionContext.Process);
            Check(publisherProfile.NetworkClass == ModNetworkClass.Shared, "The F7 publisher uses the Shared package profile.");
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
            Check(context.Input.ActiveActionCount == 9 && context.Ui.Surfaces.Count == 0 &&
                !context.Logger.Entries.Any(entry => entry.Message.StartsWith("TF-MP-", StringComparison.Ordinal)),
                "Opt-in registers publication and all fixture actions without opening a surface or emitting acceptance markers.");
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
            SetGalleryAuthorDefaults(target, action =>
                ControlGalleryPages.DefaultBindings(action) ?? ActionGalleryPages.DefaultBindings(action));
            ((IUiClockReceiver)context.Ui).FollowClock(() => 120d, () => "acceptance-gallery");
            ((IUiClockReceiver)target).FollowClock(() => 120d, () => "acceptance-gallery");
            Press();
            Check(context.Ui.Surfaces.Count == 1 && target.Surfaces.Count == 1 &&
                context.Ui.Surfaces[0].Request.Kind == UiSurfaceKind.Window && target.Surfaces[0].Request.Kind == UiSurfaceKind.Window,
                "An explicit publication sends the same fixture through both public surface services.");
            context.Input.IsUiFocused = true;
            Press();
            Check(context.Ui.Surfaces.Single().Request.Kind == UiSurfaceKind.Hud &&
                target.Surfaces.Single().Request.Kind == UiSurfaceKind.Hud,
                "F7 can advance a visible preview and retires the previous pair.");
            fixture.Install(context.Ui);
            Press();
            Check(context.Ui.Surfaces.Count == 0 && target.Surfaces.Count == 0,
                "If the target refuses its fixture, the newly opened local surface is retired.");
            fixture.Install(target);
            Press();
            Check(context.Ui.Surfaces.Single().Request.Theme?.Id == FoundationPages.TransportTheme.Id &&
                target.Surfaces.Single().Request.Theme?.Id == FoundationPages.TransportTheme.Id &&
                target.Surfaces[0].Request.Kind == UiSurfaceKind.Window,
                "After a refusal the next press retries the same paired variant.");
            var survivalControls = new HashSet<string>();
            for (var variant = 3; variant < GalleryRemotePreview.VariantCount; variant++)
            {
                Press();
                var expected = GalleryRemotePreview.Request(variant, context.Ui.ClockTime);
                var hostSurface = context.Ui.Surfaces.Single();
                var guestSurface = target.Surfaces.Single();
                Check(hostSurface.Request.Id == expected.Id && guestSurface.Request.Id == expected.Id &&
                    hostSurface.Request.Kind == expected.Kind && guestSurface.Request.Kind == expected.Kind &&
                    hostSurface.Request.Theme?.Id == expected.Theme?.Id && guestSurface.Request.Theme?.Id == expected.Theme?.Id,
                    "F7 navigates every paired page without retaining stale surfaces.");
                if (variant >= GalleryRemotePreview.K1VariantCount && variant < GalleryRemotePreview.StatusStart &&
                    expected.Theme?.Id == FoundationPages.SurvivalTheme.Id)
                    Check(expected.Kind == UiSurfaceKind.Window && survivalControls.Add(expected.Id),
                        "A distinct Survival controls page is published as a paired Paper Window.");
                if (expected.Id != FoundationLayoutPages.LayoutNotice().Id) continue;

                var doubleScale = new UiAccessibilityPreferences(uiScale: 2f);
                Check(context.Ui.ApplyAccessibility(doubleScale).Succeeded && target.ApplyAccessibility(doubleScale).Succeeded,
                    "Both viewers can change scale after the wide HUD has been admitted.");
                var viewport = new UiCaptureViewport(1920, 1080);
                Check(ShowsNotice(hostSurface.Capture(viewport)) && ShowsNotice(guestSurface.Capture(viewport)),
                    "Both paired HUDs keep their titles and replace the wide content with an unclipped layout notice.");
                Check(context.Ui.ApplyAccessibility(UiAccessibilityPreferences.Default).Succeeded &&
                    target.ApplyAccessibility(UiAccessibilityPreferences.Default).Succeeded &&
                    !ShowsNotice(hostSurface.Capture(viewport)) && !ShowsNotice(guestSurface.Capture(viewport)),
                    "Both paired HUDs restore the original content when the viewer's scale recovers.");
            }
            Check(survivalControls.SetEquals(new[] { "gallery-controls-buttons", "gallery-controls-fields",
                    "gallery-controls-selections", "gallery-controls-keys", "gallery-controls-help" }),
                "F7 includes all five controls pages using the Shared package's canonical Survival theme.");
            Press();
            Check(context.Ui.Surfaces.Single().Request.Id == GalleryRemotePreview.Request(0, context.Ui.ClockTime).Id,
                "F7 wraps from the diagnostics back to Theme packs.");
            var second = rig.AddRemoteClient("second-guest");
            rig.Advance(8);
            Check(second.ParticipantId.HasValue && GalleryRemotePreview.FindGuest(source.Snapshot) == null &&
                context.Ui.Surfaces.Count == 0 && target.Surfaces.Count == 0,
                "A second guest invalidates the pair and retires both owned surfaces.");
            runner.Unload();
            context.AssertNoLeaks();
            StatusVisualFixtures(fixture);

            static bool ShowsNotice(UiTreeCapture captured) =>
                captured.Nodes.Any(node => node.Kind == "LayoutFallback" && node.IsRendered && !node.Clipped) &&
                captured.Nodes.Any(node => node.Kind == "SurfaceTitle" && node.IsRendered && !node.Clipped);

            void Press() => PressGallery(context);
        }

        private static void PressGallery(FakeModContext context)
        {
            context.Input.SetValue(GalleryRemotePreview.InputAction, 1f);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            context.Input.SetValue(GalleryRemotePreview.InputAction, 0f);
            context.AdvanceFrame(TimeSpan.Zero);
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
