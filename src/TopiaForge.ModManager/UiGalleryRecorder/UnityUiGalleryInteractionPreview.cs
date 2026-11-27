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
                var preview = new GalleryInteractionPreview(ui);
                ui.host.Hotkey(TopiaForgeKey.F3, () => preview.Advance(FoundationPage.InteractionStates));
                ui.host.Hotkey(TopiaForgeKey.F4, () => preview.Advance(FoundationPage.GraphConnections));
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
            private readonly OwnerUiService ui;
            private IUiSurface? active;
            private FoundationPage? activePage;
            private int next;

            internal GalleryInteractionPreview(OwnerUiService ui) => this.ui = ui;

            internal void Advance(FoundationPage page)
            {
                if (ui.lifetime.IsStopping) return;
                var index = page == activePage ? next : 0;
                var prefix = page == FoundationPage.GraphConnections ? "gallery-graph-" : "gallery-interaction-";
                var palette = GalleryPreviewPalette.At(index, prefix);
                IUiSurface? opened = null;
                var fixture = FoundationPages.CreateRequest(page, UiSurfaceKind.Window,
                    report: message => opened?.SetBody(message));
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
                activePage = page;
                next = (index + 1) % GalleryPreviewPalette.Count;
                previous?.Dispose();
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
