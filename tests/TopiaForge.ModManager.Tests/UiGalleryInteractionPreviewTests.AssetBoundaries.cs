namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryInteractionPreviewTests
    {
        private const string AssetBoundaries = """
            using System;
            using System.Threading.Tasks;
            using TMPro;
            using TopiaForge.ModManager.Core;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;

            namespace UnityEngine { public sealed class Sprite { } }
            namespace TMPro { public sealed class TMP_FontAsset { } }

            namespace TopiaForge.ModManager
            {
                internal sealed class UnityUiThemeNativeLoader : IUiThemeNativeLoader
                {
                    internal UnityUiThemeNativeLoader(IHostDispatcher dispatcher, AdmittedUiCatalogue catalogue) { }

                    public Task<OperationResult<UiThemeRegistration>> LoadAsync(UiThemeDescriptor descriptor, OwnerModLifetime owner)
                        => throw new InvalidOperationException("These palette fixtures have no native resource declarations.");
                }

                internal sealed partial class OwnerUiService
                {
                    private sealed partial class UnityUiSurface
                    {
                        private readonly IModLifetime lifetime;
                        private readonly IModLogger logger;
                        private UiSurfaceArtwork? artwork;
                        private int artworkVersion;

                        private static void PrepareStagedTheme(TopiaForgeContainer stage, UiThemeDescriptor? descriptor,
                            Func<UiTextStyle, TMP_FontAsset?>? fonts = null)
                            => stage.Host.SetSurfaceTheme(stage.Go, descriptor, fonts);
                    }
                }
            }
            """;
    }
}
