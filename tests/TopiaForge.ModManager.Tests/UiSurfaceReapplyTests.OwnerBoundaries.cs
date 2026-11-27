namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiSurfaceReapplyTests
    {
        private const string OwnerBoundaries = """
            using System;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;

            namespace TopiaForge.Mods.UnityUi
            {
                internal static class TopiaForgeLayerPriority { internal const int Overlay = 5; }
                internal static class TopiaForgeLayers { internal static bool HasRoom(int band) => true; }
            }

            namespace TopiaForge.ModManager
            {
                internal sealed class UnityUiThemePackages { }
                internal sealed class BoundaryManifest
                {
                    internal string Name => "Fixture";
                    internal object? AdmittedUi => null;
                }
                internal readonly record struct GalleryPreviewPalette(UiThemeDescriptor Descriptor, TopiaForgeScheme Scheme);
                internal sealed class BoundaryPackage { internal BoundaryManifest Manifest => new BoundaryManifest(); }
                internal readonly struct UiRemoteThemeSelection
                {
                    internal BoundaryPackage? Package => null;
                    internal UiWireEnvelope? Envelope => null;
                }

                internal sealed partial class OwnerUiService
                {
                    private readonly BoundaryLogger logger = new BoundaryLogger();
                    private readonly UiThemeScope themeScope;
                    private readonly UnityUiThemePackages themePackages = new UnityUiThemePackages();
                    private readonly object? uiInput;
                    private readonly string? hudNoticePackageName;

                    private OwnerUiService()
                    {
                        themeScope = new UiThemeScope(lifetime, null);
                    }

                    private static UiThemeDescriptor? ResolveRemoteTheme(UiRemoteThemeSelection selection) => null;
                    private static OperationResult<bool> DescribePresentationTheme(UiSurfaceRequest request, out UiThemeDescriptor? descriptor)
                    {
                        descriptor = null;
                        return OperationResult<bool>.Success(true);
                    }

                    private UnityUiSurface CreateHud(UiSurfaceRequest request, string? packageDisplayName, ref TopiaForgeWidget? root)
                    {
                        var hud = new TopiaForgeHudLayer(host);
                        return UnityUiSurface.ForHud(request.Id, hud, new TopiaForgeLabel(), new TopiaForgeContainer(host),
                            false, true, lifetime, logger, ReleaseSurface);
                    }

                    private void ObserveGalleryFixture(UnityUiSurface surface, UiSurfaceRequest request,
                        UiRemoteThemeSelection? remoteTheme, UiThemeDescriptor? descriptor, GalleryPreviewPalette? preview) { }

                    private sealed partial class UnityUiSurface
                    {
                        internal void InitializeTheme(UiThemeRef? theme, bool remote, UiThemeDescriptor? descriptor,
                            UiThemeScope scope, UnityUiThemePackages packages, object? catalogue) => remoteComposition = remote;
                    }
                }
            }
            """;
    }
}
