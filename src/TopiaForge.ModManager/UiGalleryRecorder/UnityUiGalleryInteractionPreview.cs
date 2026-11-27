using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        internal const string GalleryInteractionOwner = "TopiaForge.GalleryInteractionPreview";

        internal static IDisposable? StartGalleryInteractionPreview(string dataDirectory, IModLogger logger,
            UiGameplayGate gameplay, UnityUiHudPlacement hudPlacement, UnityUiThemePackages themePackages, Func<NetRealm> view)
        {
            if (!GalleryCaptureEnabled) return null;
            var lifetime = new OwnerModLifetime();
            try
            {
                var ui = new OwnerUiService(GalleryInteractionOwner, dataDirectory, lifetime, logger, gameplay, hudPlacement, themePackages);
                ui.FollowGalleryViewer(view);
                // Sandbox the sample bindings so editing them cannot affect a loaded mod.
                var registry = new UnityInputRegistry();
                lifetime.Track(registry);
                var input = new OwnerInputService(GalleryInteractionOwner, lifetime, registry);
                if (!input.RegisterAction(ControlGalleryPages.Expedition()).Succeeded ||
                    !input.RegisterAction(ControlGalleryPages.Ledger()).Succeeded)
                    throw new InvalidOperationException("The Gallery preview input actions could not be registered.");
                ui.AttachUiInput(registry);

                var preview = new GalleryInteractionPreview(ui);
                ui.host.Hotkey(TopiaForgeKey.F2, () => preview.Advance(TopiaForgeKey.F2));
                ui.host.Hotkey(TopiaForgeKey.F3, () => preview.Advance(TopiaForgeKey.F3));
                ui.host.Hotkey(TopiaForgeKey.F4, () => preview.Advance(TopiaForgeKey.F4));
                return lifetime;
            }
            catch
            {
                lifetime.Dispose();
                throw;
            }
        }

        // Only this process-owned controller can supply the private Window stage inputs.
        private sealed class GalleryInteractionPreview
        {
            private static readonly ControlGalleryPage[] ControlPages =
                (ControlGalleryPage[])Enum.GetValues(typeof(ControlGalleryPage));
            private readonly OwnerUiService ui;
            private IUiSurface? active;
            private TopiaForgeKey? activeKey;
            private int next;

            internal GalleryInteractionPreview(OwnerUiService ui) => this.ui = ui;

            internal void Advance(TopiaForgeKey key)
            {
                if (ui.lifetime.IsStopping) return;
                var index = key == activeKey ? next : 0;
                IUiSurface? opened = null;
                void Report(string message) => opened?.SetBody(message);
                var (fixture, prefix, stages) = key switch
                {
                    TopiaForgeKey.F2 => ControlStage(index, Report),
                    TopiaForgeKey.F3 => (FoundationPages.CreateRequest(FoundationPage.InteractionStates,
                        UiSurfaceKind.Window, report: Report), "gallery-interaction-", GalleryPreviewPalette.Count),
                    TopiaForgeKey.F4 => (FoundationPages.CreateRequest(FoundationPage.GraphConnections,
                        UiSurfaceKind.Window, report: Report), "gallery-graph-", GalleryPreviewPalette.Count),
                    _ => throw new ArgumentOutOfRangeException(nameof(key)),
                };
                var palette = GalleryPreviewPalette.At(index % GalleryPreviewPalette.Count, prefix);
                var request = new UiSurfaceRequest(palette.Id, fixture.Title + " · " + palette.Label, fixture.Body,
                    fixture.Kind, fixture.Width, fixture.Height, fixture.Content);
                var result = ui.CreateSurface(request, null, palette);
                if (!result.TryGetValue(out opened) || opened == null)
                {
                    ui.logger.Warn("The local Gallery preview could not open (" + result.ErrorCode + ").");
                    return;
                }

                var previous = active;
                active = opened;
                activeKey = key;
                next = (index + 1) % stages;
                previous?.Dispose();
            }

            private static (UiSurfaceRequest Fixture, string Prefix, int Stages) ControlStage(int index, Action<string> report)
            {
                var fixture = ControlGalleryPages.Create(ControlPages[index / GalleryPreviewPalette.Count], report: report);
                return (fixture, fixture.Id + "-", ControlPages.Length * GalleryPreviewPalette.Count);
            }
        }

        private readonly struct GalleryPreviewPalette
        {
            internal const int Count = 4;

            private GalleryPreviewPalette(string id, string label, UiThemeDescriptor descriptor, TopiaForgeScheme scheme)
            {
                Id = id;
                Label = label;
                Descriptor = descriptor;
                Scheme = scheme;
            }

            internal string Id { get; }
            internal string Label { get; }
            internal UiThemeDescriptor Descriptor { get; }
            internal TopiaForgeScheme Scheme { get; }

            internal static GalleryPreviewPalette At(int index, string prefix) => index switch
            {
                0 => new GalleryPreviewPalette(prefix + "kit-paper", "Kit Paper",
                    UiThemePackParser.Default, TopiaForgeScheme.Paper),
                1 => new GalleryPreviewPalette(prefix + "kit-hud", "Kit HUD",
                    UiThemePackParser.Default, TopiaForgeScheme.Hud),
                2 => new GalleryPreviewPalette(prefix + "survival-paper", "Survival Paper",
                    UiGallerySurvivalTheme.Descriptor, TopiaForgeScheme.Paper),
                3 => new GalleryPreviewPalette(prefix + "survival-hud", "Survival HUD",
                    UiGallerySurvivalTheme.Descriptor, TopiaForgeScheme.Hud),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
        }
    }
}
