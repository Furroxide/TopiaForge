namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryInteractionPreviewTests
    {
        private const string Scenario = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using TopiaForge.ModManager.Core;
            using TopiaForge.ModManager.Core.Multiplayer;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.UiGallery.Pages;
            using UnityEngine;

            namespace TopiaForge.ModManager
            {
                internal static class GalleryInteractionPreviewProbe
                {
                    public static void Run(bool enabled)
                    {
                        const string option = "TOPIAFORGE_UI_GALLERY_CAPTURE";
                        UnityMainThreadGuard.CaptureCurrentThread();
                        Require(OwnerUiService.GalleryCaptureEnabled == enabled, "Use exact startup opt-in.");
                        Environment.SetEnvironmentVariable(option, enabled ? "0" : "1");
                        var logger = new PreviewLogger();
                        var gameplay = new UiGameplayGate();
                        var placement = new UnityUiHudPlacement();
                        using var dispatcher = new HostDispatcher();
                        var themePackages = new UnityUiThemePackages(dispatcher);
                        using var preview = OwnerUiService.StartGalleryInteractionPreview("captured-preview", logger, gameplay,
                            placement, themePackages, () => NetRealm.Client);
                        Require((preview != null) == enabled, "Changing the option after startup cannot arm or disarm the preview.");
                        if (!enabled)
                        {
                            Require(TopiaForgeHotkeys.Count == 0, "With opt-in off the loader registers nothing.");
                            return;
                        }

                        var modes = new[]
                        {
                            ("gallery-interaction-kit-paper", "Kit Paper", TopiaForgeScheme.Paper, UiThemePackParser.Default),
                            ("gallery-interaction-kit-hud", "Kit HUD", TopiaForgeScheme.Hud, UiThemePackParser.Default),
                            ("gallery-interaction-survival-paper", "Survival Paper", TopiaForgeScheme.Paper, UiGallerySurvivalTheme.Descriptor),
                            ("gallery-interaction-survival-hud", "Survival HUD", TopiaForgeScheme.Hud, UiGallerySurvivalTheme.Descriptor),
                        };
                        Require(TopiaForgeHotkeys.Count == 2 && TopiaForgeWindow.Active.Count == 0,
                            "There are two startup hotkeys and no native Window before either is pressed.");
                        TopiaForgeHotkeys.Press(TopiaForgeKey.F6);
                        Require(TopiaForgeWindow.Active.Count == 0, "The preview does not take SDK acceptance's F6.");
                        UiHost.ViewerAccessibility = new TopiaForgeAccessibilityProfile(true, 2, true, 0);
                        for (var index = 0; index <= modes.Length; index++)
                        {
                            var mode = modes[index % modes.Length];
                            var earlier = UiGalleryCaptureJson.Records.Count;
                            TopiaForgeHotkeys.Press(TopiaForgeKey.F3);
                            Require(TopiaForgeWindow.Active.Count == 1, "Cycling retires the preceding preview Window.");
                            var active = TopiaForgeWindow.Active.Single();
                            Require(active.Id == mode.Item1 && active.Title == "Interaction states · " + mode.Item2 &&
                                active.Scheme == mode.Item3 && ReferenceEquals(active.Theme.Descriptor, mode.Item4) &&
                                active.Persistent && active.Width == 840 && active.Height == 460,
                                "F3 cycles the exact four explicit palette and scheme pairs through the ordinary Window.");
                            Require(active.Admitted != null && active.ThemeAtAdmission == mode.Item4,
                                "The fixed safe composition was staged after selecting the private descriptor.");
                            var authored = FoundationPages.CreateRequest(FoundationPage.InteractionStates, UiSurfaceKind.Window);
                            Require(All(active.Admitted!).Select(Shape).SequenceEqual(All(authored.Content!).Select(Shape)),
                                "The stage uses the same Interaction states nodes as public F8.");
                            FindButton(active.Admitted!).Activated();
                            Require(active.Body?.Value == "Primary accepted.",
                                "An interaction routes to the exact preview window's own body.");
                            GameObject.Tick();
                            Require(UiGalleryCaptureJson.Records.Count == earlier,
                                "The fixture is not recorded before its settled frame.");
                            GameObject.Tick();
                            var capture = UiGalleryCaptureJson.Records.Last();
                            Require(UiGalleryCaptureJson.Records.Count == earlier + 1 && capture.Fixture == active.Id &&
                                capture.Viewer == "guest-local" && capture.Envelope == null && capture.Theme.Scheme == mode.Item3 &&
                                capture.Accessibility.HighContrast && capture.Accessibility.UiScale == 2 &&
                                capture.Accessibility.ReducedMotion,
                                "The existing recorder samples the visible Window with viewer-owned accessibility and local identity.");
                            TopiaForgeHotkeys.Press(TopiaForgeKey.F11);
                            GameObject.Tick();
                            GameObject.Tick();
                            Require(UiGalleryCaptureJson.Records.Count == earlier + 2,
                                "F11 requests another capture of only the current fixture.");
                            GameObject.Tick();
                            Require(UiGalleryCaptureJson.Records.Count == earlier + 2,
                                "A settled capture does no further recording work until requested.");
                        }

                        using var galleryLifetime = new OwnerModLifetime();
                        var service = new OwnerUiService(UiGalleryCaptureFixture.Owner, "gallery", galleryLifetime, logger, gameplay, placement, themePackages);
                        var survival = UiGallerySurvivalTheme.Descriptor;
                        ((IUiThemeScopeHost)service).ConfigureThemeScope(new AdmittedUiCatalogue(survival.Id,
                            new Dictionary<string, UiThemeDescriptor> { [survival.Id] = survival }), null, "UI Gallery");
                        var ordinaryRequest = FoundationPages.CreateRequest(FoundationPage.InteractionStates, UiSurfaceKind.Window);
                        using var ordinary = Get(service.CreateSurface(ordinaryRequest));
                        var window = TopiaForgeWindow.Active.Single(view => view.Id == ordinaryRequest.Id);
                        Require(window.Scheme == TopiaForgeScheme.Paper && window.Theme.Descriptor == survival,
                            "Ordinary F8 uses its inherited Survival descriptor in the standard Paper Window.");
                        ordinary.Dispose();

                        var plain = UiThemePackParser.Parse("{\"schema\":1,\"id\":\"plain-style-switch\"}"u8).Value
                            ?? throw new InvalidOperationException("The plain theme fixture must parse.");
                        using (var parentLife = new OwnerModLifetime())
                        using (var childLife = new OwnerModLifetime())
                        {
                            var parent = new OwnerUiService("example.style-parent", "parent", parentLife, logger, gameplay, placement, themePackages);
                            var child = new OwnerUiService("example.style-child", "child", childLife, logger, gameplay, placement, themePackages);
                            ((IUiThemeScopeHost)parent).ConfigureThemeScope(new AdmittedUiCatalogue(null,
                                new Dictionary<string, UiThemeDescriptor> { [plain.Id] = plain, [survival.Id] = survival }), null, "Style parent");
                            ((IUiThemeScopeHost)child).ConfigureThemeScope(null, parent, "Style child");
                            var stylePack = new UiThemeRef(survival.Id);
                            var plainPack = new UiThemeRef(plain.Id);
                            Require(parent.Themes.UseTheme(stylePack).Succeeded, "Select the inherited Card styles.");
                            var styled = new UiSurfaceRequest("styled", "Styled", string.Empty,
                                content: new UiCard(new UiText("Ready"), style: new UiStyleRef("panel.flat")));
                            using var inheriting = Get(child.CreateSurface(styled));
                            var selected = TopiaForgeWindow.Active.Single(view => view.Id == styled.Id);
                            Require(child.Themes.UseTheme(plainPack).ErrorCode == ModErrorCode.InvalidArgument &&
                                parent.Themes.UseTheme(plainPack).ErrorCode == ModErrorCode.InvalidArgument &&
                                parent.Themes.SelectedTheme == stylePack && child.Themes.SelectedTheme == null &&
                                selected.Theme.Descriptor == survival, "An incompatible direct or inherited theme leaves local paint and selection intact.");
                            inheriting.Dispose();
                            using var explicitPalette = Get(child.CreateSurface(styled.WithTheme(stylePack)));
                            Require(parent.Themes.UseTheme(plainPack).Succeeded &&
                                TopiaForgeWindow.Active.Single(view => view.Id == styled.Id).Theme.Descriptor == survival,
                                "An explicitly themed surface does not veto a compatible parent selection.");
                        }

                        // A package reusing the private fixture's id and tree stays on the ordinary Window route.
                        TopiaForgeHotkeys.Press(TopiaForgeKey.F3);
                        GameObject.Tick();
                        GameObject.Tick();
                        var actual = TopiaForgeWindow.Active.Single();
                        Require(actual.Scheme == TopiaForgeScheme.Hud, "The active comparison uses HUD paint when the lookalike opens.");
                        var imitation = new UiSurfaceRequest(actual.Id, actual.Title, ordinaryRequest.Body, ordinaryRequest.Kind,
                            ordinaryRequest.Width, ordinaryRequest.Height, ordinaryRequest.Content);
                        using var lookalike = Get(service.CreateSurface(imitation));
                        var impostor = TopiaForgeWindow.Active.Single(view => view != actual);
                        Require(impostor.Scheme == TopiaForgeScheme.Paper,
                            "A local package lookalike never reaches the private Window scheme or recorder.");
                        var beforeImitation = UiGalleryCaptureJson.Records.Count;
                        GameObject.Tick();
                        GameObject.Tick();
                        Require(UiGalleryCaptureJson.Records.Count == beforeImitation,
                            "A local package lookalike cannot export under the private fixture identity.");
                        lookalike.Dispose();

                        using var spoofLifetime = new OwnerModLifetime();
                        var spoof = new OwnerUiService(OwnerUiService.GalleryInteractionOwner, "spoof", spoofLifetime, logger, gameplay, placement, themePackages);
                        using var spoofed = Get(spoof.CreateSurface(imitation));
                        Require(TopiaForgeWindow.Active.Single(view => view != actual).Scheme == TopiaForgeScheme.Paper,
                            "Copying the loader's host label cannot select the preview scheme.");
                        GameObject.Tick();
                        GameObject.Tick();
                        Require(UiGalleryCaptureJson.Records.Count == beforeImitation,
                            "Copying the loader's host label cannot export a private fixture.");
                        spoofed.Dispose();

                        var passive = new UiSurfaceRequest("gallery-interaction-kit-hud", ordinaryRequest.Title, string.Empty,
                            UiSurfaceKind.Hud, ordinaryRequest.Width, ordinaryRequest.Height, new UiText("Passive text"));
                        using var hud = Get(service.CreateSurface(passive));
                        Require(!hud.SetContent(ordinaryRequest.Content!).Succeeded,
                            "An ordinary HUD remains presentation-only even if its id resembles a preview variant.");

                        var publisher = UiGalleryCaptureFixture.RemoteFixtureOwner;
                        var descriptor = UiGallerySurvivalTheme.Descriptor;
                        var digest = Enumerable.Repeat((byte)0x62, 32).ToArray();
                        var envelope = new UiWireEnvelope(publisher, digest, 1, descriptor.Id, descriptor.Digest.Span);
                        var package = new MultiplayerContentInventory.LoadedPackage(new ModManifest
                        {
                            Id = publisher,
                            AdmittedUi = new AdmittedUiCatalogue(null,
                                new Dictionary<string, UiThemeDescriptor> { [descriptor.Id] = descriptor }),
                        });
                        var remotePalette = new UiRemoteThemeSelection(envelope, package);
                        using var receiverLifetime = new OwnerModLifetime();
                        var receiver = new OwnerUiService(UiPresentationTheme.ReceiverOwner, "receiver", receiverLifetime,
                            logger, gameplay, placement, themePackages);
                        var remoteRequest = new UiSurfaceRequest("remote-18", ordinaryRequest.Title, ordinaryRequest.Body,
                            ordinaryRequest.Kind, ordinaryRequest.Width, ordinaryRequest.Height, ordinaryRequest.Content);
                        using var remote = Get(receiver.RemoteSurface(remoteRequest, remotePalette));
                        var remoteWindow = TopiaForgeWindow.Active.Single(view => view.Id == remoteRequest.Id);
                        Require(remoteWindow.Scheme == TopiaForgeScheme.Paper && remoteWindow.Theme.Descriptor == survival,
                            "An authenticated remote window resolves its admitted descriptor in ordinary Paper.");
                        GameObject.Tick();
                        GameObject.Tick();
                        Require(UiGalleryCaptureJson.Records.Count == beforeImitation,
                            "A remote caller cannot export interactive preview content.");
                        remote.Dispose();

                        var typography = FoundationPages.CreateTransportRequest(FoundationPage.Typography,
                            UiSurfaceKind.Window, FoundationPages.SurvivalTheme);
                        var received = new UiSurfaceRequest("remote-29", typography.Title, typography.Body,
                            typography.Kind, typography.Width, typography.Height, typography.Content);
                        using (Get(service.RemoteSurface(received, remotePalette)))
                        {
                            GameObject.Tick();
                            GameObject.Tick();
                            Require(UiGalleryCaptureJson.Records.Count == beforeImitation,
                                "Only the loader presentation receiver can record a paired fixture.");
                        }
                        using var remoteFixture = Get(receiver.RemoteSurface(received, remotePalette));
                        GameObject.Tick();
                        GameObject.Tick();
                        var reflected = UiGalleryCaptureJson.Records.Last();
                        Require(UiGalleryCaptureJson.Records.Count == beforeImitation + 1 && reflected.Fixture == typography.Id &&
                            reflected.Viewer == "remote-guest" && reflected.Envelope == envelope &&
                            reflected.Theme.Descriptor == descriptor && reflected.Theme.Scheme == TopiaForgeScheme.Paper,
                            "The recorder receives the original authenticated Survival envelope and canonical admitted descriptor.");
                        remoteFixture.Dispose();

                        var graphFixture = FoundationPages.CreateRequest(FoundationPage.GraphConnections, UiSurfaceKind.Window);
                        foreach (var highContrast in new[] { false, true })
                        {
                            UiHost.ViewerAccessibility = new TopiaForgeAccessibilityProfile(highContrast, 2, true, 0);
                            for (var index = 0; index <= modes.Length; index++)
                            {
                                var mode = modes[index % modes.Length];
                                var earlier = UiGalleryCaptureJson.Records.Count;
                                TopiaForgeHotkeys.Press(TopiaForgeKey.F4);
                                var graphWindow = TopiaForgeWindow.Active.Single();
                                Require(graphWindow.Id == mode.Item1.Replace("gallery-interaction-", "gallery-graph-") &&
                                    graphWindow.Title == "Graph connections · " + mode.Item2 &&
                                    graphWindow.Scheme == mode.Item3 && ReferenceEquals(graphWindow.Theme.Descriptor, mode.Item4) &&
                                    graphWindow.Width == graphFixture.Width && graphWindow.Height == graphFixture.Height,
                                    "F4 cycles the four approved palette and scheme pairs in the graph Window.");
                                Require(All(graphWindow.Admitted!).Select(Shape).SequenceEqual(All(graphFixture.Content!).Select(Shape)),
                                    "F4 uses the shared graph fixture and all its authored nodes.");
                                var relay = All(graphWindow.Admitted!).OfType<UiGraphCanvas>().Single(graph => graph.Id == "relay-map");
                                Require(relay.Edges.Count == 2, "The retained relay exposes both measured connections.");
                                relay.ConnectionRemoved!("lower-relay");
                                Require(graphWindow.Body?.Value == "Remove requested for lower-relay.",
                                    "The source-owned graph callback updates the active Window.");
                                GameObject.Tick();
                                GameObject.Tick();
                                var capture = UiGalleryCaptureJson.Records.Last();
                                Require(UiGalleryCaptureJson.Records.Count == earlier + 1 && capture.Fixture == graphWindow.Id &&
                                    capture.Viewer == "guest-local" && capture.Envelope == null &&
                                    capture.Theme.Scheme == mode.Item3 && capture.Accessibility.HighContrast == highContrast,
                                    "The recorder uses the graph identity, local viewer and actual contrast profile.");
                            }
                            TopiaForgeHotkeys.Press(TopiaForgeKey.F3);
                            Require(TopiaForgeWindow.Active.Single().Id == modes[0].Item1,
                                "Switching preview keys replaces the Window and restarts the other cycle.");
                        }

                        preview!.Dispose();
                        Require(TopiaForgeWindow.Active.Count == 0 && TopiaForgeHotkeys.Count == 0,
                            "Loader teardown retires its Window, F3, F4 and the settled F11 recorder.");
                        TopiaForgeHotkeys.Press(TopiaForgeKey.F3);
                        TopiaForgeHotkeys.Press(TopiaForgeKey.F4);
                        Require(TopiaForgeWindow.Active.Count == 0, "Disposed preview cannot reopen.");
                    }

                    private static IUiSurface Get(OperationResult<IUiSurface> result) =>
                        result.TryGetValue(out var value) ? value : throw new InvalidOperationException(result.ErrorMessage);

                    private static string Shape(UiNode node) => node.GetType().Name + "/" + node.Id;

                    private static UiButton FindButton(UiNode root) =>
                        All(root).OfType<UiButton>().Single(node => node.Id == "button-primary");

                    private static IEnumerable<UiNode> All(UiNode root)
                    {
                        yield return root;
                        foreach (var child in Children(root))
                            foreach (var item in All(child))
                                yield return item;
                    }

                    private static IEnumerable<UiNode> Children(UiNode node) => node switch
                    {
                        UiLayoutNode layout => layout.Children,
                        UiSplitPane split => new[] { split.Primary, split.Secondary },
                        UiScroll scroll => new[] { scroll.Content },
                        _ => Array.Empty<UiNode>(),
                    };

                    private static void Require(bool condition, string text)
                    {
                        if (!condition) throw new InvalidOperationException(text);
                    }
                }
            }
            """;
    }
}
