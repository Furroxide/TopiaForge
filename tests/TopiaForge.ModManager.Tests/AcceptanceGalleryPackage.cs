using System;
using System.IO;
using System.Security.Cryptography;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal sealed class AcceptanceGalleryPackage
    {
        private const string ManifestPath = "topiaforge.mod.json";
        internal const string DefinitionPath = "ui/themes/transport-preview.json";
        internal const string SurvivalPath = "ui/themes/survival.json";
        private const string NoticePath = "LICENSES/ui-art.txt";
        private static readonly string ReceiptPath = UiBundleReceipt.PathFor(FoundationArtPages.ArtBundle);

        private readonly string root = Path.Combine(Program.FindRepoRoot(), "tests", "TopiaForge.MultiplayerAcceptanceMod");
        private readonly string survivalSource = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.UiGallery", SurvivalPath);
        private readonly UiBundleReceipt receipt;

        internal AcceptanceGalleryPackage()
        {
            Manifest = File.ReadAllText(Path.Combine(root, ManifestPath));
            BundleHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, FoundationArtPages.ArtBundle))));
            receipt = UiBundleReceipt.Parse(File.ReadAllBytes(Path.Combine(root, ReceiptPath)), BundleHash);

            var stage = Directory.CreateTempSubdirectory("TopiaForgeGalleryPack-");
            try
            {
                foreach (var file in new[] { ManifestPath, DefinitionPath, SurvivalPath, FoundationArtPages.ArtBundle, NoticePath, ReceiptPath })
                {
                    var target = Path.Combine(stage.FullName, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file == SurvivalPath ? survivalSource : Path.Combine(root, file), target);
                }
                var manifestFile = Path.Combine(stage.FullName, ManifestPath);
                var problems = UiThemePackage.Prepare(stage.FullName, ModManifestJson.LoadFile(manifestFile));
                if (problems.Count != 0) throw new InvalidOperationException(string.Join(" ", problems));
                var admitted = UiThemePackage.Read(stage.FullName, ModManifestJson.LoadFile(manifestFile));
                if (!admitted.TryGetValue(out var assets) || !assets.TryGet(FoundationPages.TransportTheme.Id, out var theme) ||
                    theme.Images.Count != 2 || !assets.TryGetBundle(theme.Images[0], out var bundle) ||
                    !assets.TryGetBundle(theme.Images[1], out var maskBundle) || !ReferenceEquals(bundle, maskBundle))
                    throw new InvalidOperationException("The acceptance package must re-admit its own two verified images. " + admitted.ErrorMessage);
                if (OperatingSystem.IsWindows()) VerifyUnavailableBundle(bundle);
                Descriptor = theme;
                if (!assets.TryGet(FoundationPages.SurvivalTheme.Id, out var survival) || !UiGallerySurvivalTheme.Matches(survival))
                    throw new InvalidOperationException("The staged Survival definition must match the Gallery source.");
                Survival = survival;
            }
            finally { stage.Delete(recursive: true); }
        }

        internal string Manifest { get; }
        internal string BundleHash { get; }
        internal UiThemeDescriptor Descriptor { get; }
        internal UiThemeDescriptor Survival { get; }

        private void VerifyUnavailableBundle(UiAdmittedBundle bundle)
        {
            using (File.Open(bundle.FullPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (bundle.ReadVerifiedBytes().ErrorCode != ModErrorCode.Io)
                    throw new InvalidOperationException("An unavailable guest bundle must fail its independent verified read.");
            }
            var opened = bundle.ReadVerifiedBytes();
            if (!opened.TryGetValue(out var bytes) || Convert.ToHexString(SHA256.HashData(bytes)) != BundleHash)
                throw new InvalidOperationException("Releasing the diagnostic handle must restore the exact admitted bundle bytes.");
        }

        internal void Install(FakeUiService ui)
        {
            var configured = ui.AddThemeCatalogue(Manifest, path => File.ReadAllText(
                path == SurvivalPath ? survivalSource : Path.Combine(root, path)));
            if (!configured.Succeeded) throw new InvalidOperationException(configured.ErrorMessage);
            foreach (var image in Descriptor.Images)
            {
                if (!receipt.TryGetSprite(image.AssetName, out var sprite))
                    throw new InvalidOperationException("Each acceptance image must resolve its package artwork receipt.");
                var texture = receipt.Textures[sprite.Texture];
                ui.AddAssetFixture(new FakeUiAssetMetadata(image.BundlePath, image.AssetName, BundleHash,
                    sprite.Width, sprite.Height, texture.DecodedBytes, isMask: texture.IsMask));
            }
        }
    }
}
