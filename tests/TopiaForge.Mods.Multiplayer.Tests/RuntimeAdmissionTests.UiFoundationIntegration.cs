using System.Security.Cryptography;
using System.Text;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private const string FoundationDefinitionPath = "ui/themes/survival.json";
    private static readonly UiThemeRef FoundationTheme = new("survival");

    private static void FoundationPackagesCoexistAcrossGuestProfilesAndScopeLifetimes()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        var survival = PackageSpec.Implicit("io.example.p9.survival", "1.0.0", 'a').WithoutReceipt();
        var neutral = PackageSpec.Implicit("io.example.p9.neutral", "1.0.0", 'b').WithoutReceipt();
        var sampleBytes = File.ReadAllBytes(Path.Combine(NetworkFixtures.RepositoryRoot(),
            "mods", "TopiaForge.UiGallery", FoundationDefinitionPath));
        var neutralBytes = Encoding.UTF8.GetBytes("{\"schema\":1,\"id\":\"survival\"}");
        using var rig = new ControlRig(162);
        var guest = rig.AddPeer("Paper scale");
        var accessibleGuest = rig.AddPeer("Larger text");
        foreach (var peer in new[] { rig.Host, guest, accessibleGuest })
        {
            InstallFoundationTheme(rig, peer, survival, sampleBytes);
            InstallFoundationTheme(rig, peer, neutral, neutralBytes);
        }

        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: false);
        using var paper = new UiDeliveryEndpoint(guest, () => rig.Network.Now, author: false);
        using var contrast = new UiDeliveryEndpoint(accessibleGuest, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Foundation package themes", 4));
        rig.JoinAndWait(guest);
        rig.JoinAndWait(accessibleGuest);

        using var survivalAuthor = new FoundationAuthor(host, survival.Id, rig.Host);
        using var neutralAuthor = new FoundationAuthor(host, neutral.Id, rig.Host);
        const string survivalTitle = "Survival HUD";
        const string neutralTitle = "Neutral HUD";
        var text = new UiText("Ready board", UiTextStyle.Label, UiTone.Primary, "status");
        using var survivalHud = survivalAuthor.Open(survivalTitle, text);
        using var neutralHud = neutralAuthor.Open(neutralTitle, text);
        That(rig.Run(() => paper.Ui.Surfaces.Count == 2 && contrast.Ui.Surfaces.Count == 2),
            "The same authenticated shared baseline delivers both package-owned presentations to both guests.");

        var survivalReceipt = paper.Ui.Surfaces[survivalTitle];
        var neutralReceipt = paper.Ui.Surfaces[neutralTitle];
        var accessibleReceipt = contrast.Ui.Surfaces[survivalTitle];
        foreach (var endpoint in new[] { paper, contrast })
        {
            foreach (var (title, package) in new[] { (survivalTitle, survival), (neutralTitle, neutral) })
            {
                var selection = endpoint.Ui.Surfaces[title].Theme;
                var envelope = selection.Envelope!;
                That(envelope.ThemeId == FoundationTheme.Id && envelope.OwnerPackageId == package.Id &&
                    selection.Package?.Manifest.Id == package.Id && selection.Package?.Sha256 ==
                    rig.Host.Control.UiPresentationPackageDigest(package.Id),
                    "Each guest authenticates the exact signed package behind a same-ID theme.");
            }
        }

        var standard = CaptureFoundationStatus(survivalReceipt, paper.Ui.Accessibility);
        var sibling = CaptureFoundationStatus(neutralReceipt, paper.Ui.Accessibility);
        var mismatched = new UiRemoteThemeSelection(survivalReceipt.Theme.Envelope!, neutralReceipt.Theme.Package);
        That(UiPresentationTheme.ViewerTheme(mismatched) == null,
            "A different same-ID descriptor cannot satisfy the authenticated descriptor digest.");
        That(standard.InkColor == "#BEE487" && standard.FillColor == "#141D21" && standard.Text == "READY BOARD" &&
            sibling.InkColor != standard.InkColor && sibling.FillColor != standard.FillColor && sibling.Text == "Ready board",
            "Coexisting same-ID themes keep each package's own resolved ink, fill and typography.");

        var profiles = new UiAccessibilityPreferences(highContrast: true, uiScale: 2f, reducedMotion: true);
        That(contrast.Ui.ApplyAccessibility(profiles).Succeeded, "One guest changes its local accessibility profile on the retained presentation.");
        var accessible = CaptureFoundationStatus(accessibleReceipt, contrast.Ui.Accessibility);
        var unchanged = CaptureFoundationStatus(survivalReceipt, paper.Ui.Accessibility);
        That(accessible.ContrastRatio >= 7f && accessible.TextHeightPixels == 2f * standard.TextHeightPixels &&
            accessible.SemanticLabel == standard.SemanticLabel && accessible.Text == standard.Text &&
            unchanged.TextHeightPixels == standard.TextHeightPixels && unchanged.InkColor == standard.InkColor &&
            host.Ui.Accessibility == UiAccessibilityPreferences.Default &&
            UiPresentationTheme.SameEnvelope(accessibleReceipt.Theme.Envelope, survivalReceipt.Theme.Envelope),
            "Guests resolve their own scale and contrast from the same presentation without changing host or other guest preferences.");

        var sender = host.Players.Presentation!;
        var idleBytes = AllocationProbe.Measure("P9 settled authenticated themed HUDs", () =>
        {
            survivalHud.SetBody("Ready");
            survivalHud.Show();
            if (!survivalHud.SetContent(text).Succeeded || !neutralAuthor.Themes.UseTheme(FoundationTheme).Succeeded)
                throw new InvalidOperationException("The settled themed write must succeed.");
            sender.Send();
        }, frames: 10_000, warmups: 256);
        That(idleBytes == 0, "Settled K1 writes and baseline publication allocate zero managed bytes.");

        using var cycling = new FoundationAuthor(host, survival.Id, rig.Host);
        ulong lastGeneration = 0;
        const string cyclingTitle = "Reopened HUD";
        var initialResources = cycling.Lifetime.TrackedResourceCount;
        for (var index = 0; index <= MultiplayerPresentation.MaximumSurfaces; index++)
        {
            That(cycling.Themes.PreloadAsync(FoundationTheme).Result.Succeeded, "A validated data-only theme preloads in its own scope.");
            using (cycling.Open(cyclingTitle, text))
            {
                That(rig.Run(() => paper.Ui.Surfaces.ContainsKey(cyclingTitle)), "A new retained registration is delivered.");
                var generation = paper.Ui.Surfaces[cyclingTitle].Theme.Envelope!.Generation;
                That(generation > lastGeneration, "Reopening the retired presentation takes a fresh scope generation.");
                lastGeneration = generation;
            }

            That(rig.Run(() => !paper.Ui.Surfaces.ContainsKey(cyclingTitle) && !contrast.Ui.Surfaces.ContainsKey(cyclingTitle)) &&
                cycling.Lifetime.TrackedResourceCount == initialResources &&
                ReferenceEquals(neutralReceipt, paper.Ui.Surfaces[neutralTitle]) &&
                ReferenceEquals(survivalReceipt, paper.Ui.Surfaces[survivalTitle]),
                "Disposal retires each registration and its leases without replacing either coexisting package's presentation.");
        }

        _ = cycling.Open(cyclingTitle, text);
        That(rig.Run(() => paper.Ui.Surfaces.ContainsKey(cyclingTitle)), "The final registration reaches the receiver before the scope stops.");
        cycling.Dispose();
        That(rig.Run(() => !paper.Ui.Surfaces.ContainsKey(cyclingTitle) && !contrast.Ui.Surfaces.ContainsKey(cyclingTitle)) &&
            cycling.Lifetime.TrackedResourceCount == 0 && cycling.Themes.UseTheme(FoundationTheme).ErrorCode == ModErrorCode.Cancelled,
            "Stopping an author scope drains its registrations and leaves its disposed theme selection unusable.");
        Console.WriteLine("K1 P9: two exact admitted themes, two independent guest profiles, zero idle bytes over 10000 calls, and 33 registration cycles passed.");
    }

    private static void InstallFoundationTheme(ControlRig rig, ControlPeer peer, PackageSpec package, byte[] bytes)
    {
        var folder = package.Install(Path.Combine(rig.PackageRoot, peer.Name));
        var definition = Path.Combine(folder, FoundationDefinitionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(definition)!);
        File.WriteAllBytes(definition, bytes);
        var manifest = package.ToManifest();
        manifest.SupportedLoaderVersionRange = ">=" + UiKitRelease.K1;
        manifest.Ui = new ModUiCatalogue
        {
            Schema = 1,
            Themes = { new ModUiCatalogueEntry { Id = FoundationTheme.Id, Definition = FoundationDefinitionPath,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } },
        };
        var problems = ManifestContentValidator.Validate(folder, manifest);
        That(problems.Count == 0 && manifest.AdmittedUi != null, "The content validator admits the fixture package: " + string.Join(", ", problems));
        peer.Runtime.Inventory.Add(manifest, folder);
    }

    private static UiCapturedNode CaptureFoundationStatus(ThemeSurfaceProbe receipt, UiAccessibilityPreferences preferences)
    {
        // Rendering stops at a headless boundary. The live receiver selected the descriptor and decoded the tree above.
        var descriptor = UiPresentationTheme.ViewerTheme(receipt.Theme);
        That(descriptor != null, "The production viewer resolves the package descriptor before headless paint.");
        using var lifetime = new FakeModLifetime();
        var ui = new FakeUiService(lifetime);
        var definition = File.ReadAllText(Path.Combine(receipt.Theme.Package!.PackagePath, FoundationDefinitionPath));
        That(ui.AddThemeFixture(definition).Succeeded && ui.ApplyAccessibility(preferences).Succeeded,
            "The headless boundary uses the exact validated file and the receiver's local profile.");
        var received = receipt.Surface;
        var authored = received.Request;
        var captured = ui.CreateSurface(new UiSurfaceRequest(authored.Id, authored.Title, received.Body,
            authored.Kind, authored.Width, authored.Height, received.Content).WithTheme(new UiThemeRef(descriptor!.Id)));
        That(captured.Succeeded, "The decoded auth-safe tree can render headlessly.");
        ui.CompletePendingThemeRegistrations();
        var snapshot = ui.Capture(authored.Id, new UiCaptureViewport(1920, 1080));
        That(snapshot.CheckAccessibility().Count == 0, "All rendered groups in the received presentation pass the accessibility checks.");
        var status = snapshot.Nodes.Single(node => node.Id == "status");
        That(status.IsRendered && !status.UsedFallback, "The themed status is rendered without fallback.");
        return status;
    }

    private sealed class FoundationAuthor : IDisposable
    {
        private readonly RuntimeReplication origin;
        private readonly MultiplayerPlayers players;
        private readonly ThemeProbe ui;

        internal FoundationAuthor(UiDeliveryEndpoint endpoint, string packageId, ControlPeer peer)
        {
            var manifest = peer.Runtime.Inventory.Packages.Single(item => item.Manifest.Id == packageId).Manifest;
            ui = new ThemeProbe(Lifetime, manifest.AdmittedUi!);
            ui.PresentationThemeScope!.PrepareSelection = endpoint.Players.PrepareThemeSelection;
            origin = endpoint.Replication.Bind(packageId, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), Lifetime);
            players = endpoint.Players;
            That(Themes.UseTheme(FoundationTheme).Succeeded, "The admitted author selects its own published theme.");
        }

        internal FakeModLifetime Lifetime { get; } = new();
        internal IUiThemeService Themes => ui.Themes;

        internal IUiSurface Open(string title, UiNode content)
        {
            var result = players.CreateContextSurface(origin, ui,
                new UiSurfaceRequest(title, title, "Ready", UiSurfaceKind.Hud, 360, 215, content));
            That(result.Succeeded, "A signed, admitted scope can publish a themed HUD: " + result.ErrorMessage);
            return result.Value!;
        }

        public void Dispose() => Lifetime.Dispose();
    }
}
