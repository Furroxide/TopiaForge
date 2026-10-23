using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The Unity wiring of SDK surface lifetimes, which no offline test can run, pinned by its shape: persistent canvases
    /// for every surface and modal, sorting slots released by the allocator's record, canvases destroyed outside the kit
    /// reported to their owners, the gameplay gate sampled every frame, toasts clear of the level objective, and windows
    /// that stay shown when shown again during a close fade and that follow their canvas's size.
    /// </summary>
    internal static class UiSurfaceLifetimeConventionTests
    {
        public static void Run()
        {
            EverySdkSurfaceAndModalIsPersistent();
            HudTextColumnFillsItsPanel();
            SurfacesReportVisibilityFromTheirPresence();
            SlotsGoBackByTheRecordNeverByReadingTheCanvas();
            DestroyedCanvasesReachTheirOwners();
            TheGateIsSampledEveryFrameBeforeModsUpdate();
            ToastsStackFromTheBottomRight();
            AWindowShownDuringItsCloseFadeStaysShown();
            AWindowFollowsItsCanvasSize();
            Console.WriteLine("UiSurfaceLifetimeConventionTests passed.");
        }

        // A HUD made in OnLoad was destroyed by the game's first scene load, and a window made in a world by the next one.
        private static void EverySdkSurfaceAndModalIsPersistent()
        {
            var runtime = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager");
            var service = File.ReadAllText(Path.Combine(runtime, "UnityUiService.cs")) + File.ReadAllText(Path.Combine(runtime, "UnityUiHud.cs"));
            var calls = new Regex(@"\bhost\.(?<kind>HudLayer|Window|FullscreenTool|Modal\.Custom)\((?<arguments>[^;]*)\);", RegexOptions.CultureInvariant);
            var seen = 0;
            foreach (Match call in calls.Matches(service))
            {
                seen++;
                Check(call.Groups["arguments"].Value.Contains("persistent: true", StringComparison.Ordinal),
                    "OwnerUiService makes a " + call.Groups["kind"].Value + " that a scene load would destroy: " + call.Value.Trim());
            }

            Check(seen == 4, "OwnerUiService must make its HUD layer, window, fullscreen tool and modal in one place each; found " + seen + ".");
        }

        // The column kept a new GameObject's 100 x 100 units, centred in the panel, so a short line wrapped.
        private static void HudTextColumnFillsItsPanel()
        {
            var hud = Read("src", "TopiaForge.ModManager", "UnityUiHud.cs");
            RequireSource(hud, "var column = TopiaForgeHudColumn.For(request.Width, request.Height);", "The HUD must be laid out by the column math.");
            RequireSource(hud, ".Size(column.PanelWidth, column.PanelHeight);", "The HUD panel must take the size the column math gives it.");
            RequireSource(hud, ".Column(TopiaForgeGap.Sm, TopiaForgeGap.Md).Stretch();", "The HUD's text column must fill its panel.");
            RequireSource(hud, "hudPlacement.Add(host, panel.Rect, corner, column.PanelHeight)", "The HUD must dock through the shared placement, which stacks HUDs.");
        }

        // A window a scene destroyed was still reported visible.
        private static void SurfacesReportVisibilityFromTheirPresence()
        {
            var surface = Read("src", "TopiaForge.ModManager", "UnityUiSurface.cs");
            var presence = Read("src", "TopiaForge.ModManager", "UnityUiSurface.Presence.cs");
            var service = Read("src", "TopiaForge.ModManager", "UnityUiService.cs");
            RequireSource(surface, "public bool IsVisible => presence.IsVisible;", "A surface must report what its presence knows, lost canvases included.");
            RequireSource(presence, "var effects = presence.Lose();", "A surface whose canvas is lost must say so to its presence.");
            RequireSource(service, "host.LayerLost += OnLayerLost;", "OwnerUiService must hear of canvases destroyed outside the kit.");
            RequireSource(service, "lifetime.Track(gameplay.Subscribe(OnGameplayChanged));", "OwnerUiService must follow the gameplay gate for as long as it lives.");
            RequireSource(service, "surface.Present();", "A new surface must be shown, or kept hidden outside gameplay, by its presence.");
        }

        // A destroyed canvas could no longer say which order it held, so its slot leaked: 30001 became 30002 per world.
        private static void SlotsGoBackByTheRecordNeverByReadingTheCanvas()
        {
            var layers = Read("src", "TopiaForge.Mods.UnityUi", "Runtime", "TopiaForgeLayers.cs");
            var registry = Read("src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeWindowRuntime.cs");
            foreach (Match use in Regex.Matches(layers, @"\.sortingOrder\b(?<rest>[^;]*);", RegexOptions.CultureInvariant))
            {
                Check(Regex.IsMatch(use.Value, @"^\.sortingOrder\s*=[^=]", RegexOptions.CultureInvariant),
                    "TopiaForgeLayers reads a canvas's sorting order; slots must go back by the allocator's record: " + use.Value);
            }

            RequireMatch(layers, @"internal static void Release\(GameObject root\)\s*\{\s*Ledger\.Release\(root\);\s*\}",
                "TopiaForgeLayers.Release must release by the ledger's record.");
            RequireMatch(layers, @"var order = Ledger\.Claim\(root, band, out var exhausted\);[\s\S]*?canvas\.sortingOrder = order;[\s\S]*?AddComponent<TopiaForgeLayerSlot>\(\)",
                "Every kit canvas must be claimed through the ledger and carry the slot component that releases it.");
            Check(!registry.Contains(".sortingOrder", StringComparison.Ordinal),
                "The window registry reads a canvas's sorting order; it must take each window's slot from the allocator's record.");
            Check(Regex.Matches(registry, @"TopiaForgeLayers\.TryGetOrder\(window\.CanvasRoot, out var slot\)").Count == 2,
                "The window registry must register and unregister each window's slot by the allocator's record.");
        }

        private static void DestroyedCanvasesReachTheirOwners()
        {
            var slot = Read("src", "TopiaForge.Mods.UnityUi", "Runtime", "TopiaForgeLayerSlot.cs");
            var host = Read("src", "TopiaForge.Mods.UnityUi", "UiHost.cs");
            var lifetime = Read("src", "TopiaForge.Mods.UnityUi", "UiHost.Lifetime.cs");
            var modal = Read("src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeModal.cs");
            RequireMatch(slot, @"private void OnDestroy\(\)[\s\S]*?TopiaForgeLayers\.ReleaseDestroyed\(root, lost\);",
                "The slot component must give the slot back, and tell the owner, when anything destroys the canvas.");
            RequireSource(host, "TopiaForgeLayers.WatchLoss(root, HandleLayerLost);", "Every layer a host makes must report its loss to the host.");
            foreach (var call in new[] { "modal.Lose();", "window.Lose();", "tool.Lose();" })
            {
                RequireSource(lifetime, call, "A host must let go of what was on a lost canvas: " + call);
            }

            var lose = Regex.Match(modal, @"internal void Lose\(\)\s*\{\s*if \(tornDown\)\s*\{\s*return;\s*\}(?<body>[^}]*)\}", RegexOptions.CultureInvariant);
            Check(lose.Success && lose.Groups["body"].Value.Contains("cursorLease.Release();", StringComparison.Ordinal)
                && lose.Groups["body"].Value.Contains("RaiseClosed();", StringComparison.Ordinal),
                "A modal on a lost canvas must free the cursor and end as a cancellation.");
        }

        private static void TheGateIsSampledEveryFrameBeforeModsUpdate()
        {
            var core = Read("src", "TopiaForge.ModManager", "CoreGameplayServices.cs");
            RequireMatch(core, @"public GameTimeSample BeginFrame\(float deltaTime\)[\s\S]*?SampleUiGameplay\(\);[\s\S]*?return sample;",
                "The runtime must sample the gameplay gate at the start of every frame, before mods update.");
            RequireMatch(core, @"uiGameplay\.Observe\([\s\S]*?SceneManager\.sceneCount,\s*SceneManager\.loadedSceneCount\);",
                "The gameplay gate must see a scene load through the scene counts.");
            RequireMatch(core, @"if \(!activeSceneSampled \|\| active\.handle != sampledActiveScene\)",
                "The active scene's name must be read only when the active scene changes, so a settled frame allocates nothing.");
        }

        // The game draws a level's objective at the top right, above every kit canvas.
        private static void ToastsStackFromTheBottomRight()
        {
            var toast = Read("src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeToast.cs");
            RequireSource(toast, "root.Rect.anchorMin = new Vector2(1f, 0f);", "Toasts must dock at the bottom right.");
            RequireSource(toast, "root.Rect.pivot = new Vector2(1f, 0f);", "A toast must rest on its bottom edge.");
            RequireSource(toast, "var targetY = TopiaForgeTokens.SafeMargin + top;", "Toasts must stack upwards from the bottom margin.");
        }

        // A surface's Hide followed at once by Show left the window hidden, though the surface said it showed.
        private static void AWindowShownDuringItsCloseFadeStaysShown()
        {
            var window = Read("src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeWindow.cs");
            RequireMatch(window, @"public void Show\(\)\s*\{\s*ThrowIfTornDown\(\);\s*if \(open && !closing\)\s*\{\s*BringToFront\(\);\s*return;\s*\}",
                "TopiaForgeWindow.Show may return early only for a window that is open and not closing.");
            RequireMatch(window, @"if \(closing\) TopiaForgeTween\.Cancel\(this\);\s*open = true;\s*closing = false;",
                "TopiaForgeWindow.Show must stop a close fade, so the fade's end never hides the window it shows again.");
            var tool = Read("src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeFullscreenTool.cs");
            RequireMatch(tool, @"if \(open && !closing\) return;\s*if \(closing\)\s*\{\s*TopiaForgeTween\.Cancel\(this\);",
                "TopiaForgeFullscreenTool.Show must stop a close fade too.");
        }

        // A window was clamped only when shown, so a new resolution or UI scale could leave its title bar off screen.
        private static void AWindowFollowsItsCanvasSize()
        {
            var window = Read("src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeWindow.cs");
            var runtime = Read("src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeWindowRuntime.cs");
            RequireSource(window, "canvasRoot.AddComponent<TopiaForgeWindowCanvasResize>().Window = this;",
                "Every window must watch its canvas's size.");
            RequireMatch(window, @"internal void ArmClamp\(\)\s*\{\s*if \(!tornDown && open && !closing\) deferredClamp\.enabled = true;",
                "A canvas size change must arm the deferred clamp for an open window.");
            RequireMatch(runtime, @"private void OnRectTransformDimensionsChange\(\)\s*\{\s*Window\?\.ArmClamp\(\);",
                "The canvas watcher must arm the clamp whenever the canvas changes size.");
        }

        private static string Read(params string[] parts)
        {
            var segments = new string[parts.Length + 1];
            segments[0] = Program.FindRepoRoot();
            Array.Copy(parts, 0, segments, 1, parts.Length);
            return File.ReadAllText(Path.Combine(segments));
        }

        private static void RequireSource(string source, string expected, string message)
        {
            Check(source.Contains(expected, StringComparison.Ordinal), message);
        }

        private static void RequireMatch(string source, string pattern, string message)
        {
            Check(Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant), message);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
