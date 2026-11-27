using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiBundleReceiptTests
    {
        private const string Definition = "ui/themes/survival.json";
        private const string BundlePath = "ui/example.bundle";
        private const string NoticePath = "licenses/sprite.txt";
        private const string SpriteName = "example-mark";
        private const string DefinitionSource = "{\"schema\":1,\"id\":\"survival\",\"images\":[{" +
            "\"id\":\"example\",\"bundlePath\":\"" + BundlePath + "\",\"assetName\":\"" + SpriteName +
            "\",\"licensePath\":\"" + NoticePath + "\",\"isMask\":true}]}";

        internal static void Run()
        {
            TestPreparation();
            TestPreparationChecksManifestBeforeWriting();
            TestRejections();
            TestNativeMetadata();
            TestFontMetadata();
            TestCallerRootAliases();
        }

        private static void TestPreparation()
        {
            using var stage = new Stage();
            var relativeRoot = Path.GetRelativePath(Environment.CurrentDirectory, stage.Root);
            Require(UiThemePackage.Prepare(relativeRoot, stage.Manifest).Count == 0, "The verified bundle can be packed from a normalized package root.");
            var final = stage.ReadBytes(Definition);
            var data = Encoding.UTF8.GetString(final);
            Require(data.Contains("\"pixelWidth\":48", StringComparison.Ordinal) &&
                data.Contains("\"pixelHeight\":20", StringComparison.Ordinal),
                "Enrichment uses the Sprite rectangle, not its 128 by 64 atlas.");
            Require(data.Contains("\"isMask\":true", StringComparison.Ordinal), "An Alpha8 texture admits the mask assertion.");
            var manifest = stage.LoadManifest();
            Require(manifest.Ui!.Themes[0].Sha256 == Hash(final).ToLowerInvariant(), "The catalogue hashes final bytes.");
            var result = UiThemePackage.Read(stage.Root, manifest);
            Require(result.TryGetValue(out var catalogue) && catalogue.TryGet("survival", out var descriptor) &&
                descriptor.Images[0].PixelWidth == 48, "The emitted package re-admits using the same receipt.");
        }

        private static void TestPreparationChecksManifestBeforeWriting()
        {
            using var stage = new Stage();
            stage.Delete("topiaforge.mod.json");
            try
            {
                UiThemePackage.Prepare(stage.Root, stage.Manifest);
                throw new InvalidOperationException("Preparation must require the staged manifest.");
            }
            catch (FileNotFoundException)
            {
                Require(stage.ReadText(Definition) == DefinitionSource, "A missing manifest leaves the original definition intact.");
            }
        }

        private static void TestCallerRootAliases()
        {
            using var workspace = new Stage();
            var callerDirectory = Stage.CreateChildDirectory(workspace.Root, "caller");
            using var stage = new Stage(Stage.CreateChildDirectory(callerDirectory, "package"));
            var alias = PathSafety.CombineRelativeChild(workspace.Root, "caller-link");
            TomatoCakeLauncherFixture.LinkDirectory(alias, callerDirectory);
            try
            {
                var packageRoot = PathSafety.CombineRelativeChild(alias, "package");
                var separator = Path.DirectorySeparatorChar;
                Require(PathSafety.RequireOrdinaryDirectory(packageRoot + separator) == packageRoot,
                    "An ordinary package root under a caller alias normalizes trailing separators.");
                Require(UiThemePackage.Prepare(packageRoot, stage.Manifest).Count == 0 &&
                    UiThemePackage.Read(packageRoot, stage.LoadManifest()).Succeeded,
                    "The prepare and read paths accept a staged package below a caller-owned directory alias.");

                RequireRejectedPath(() => PathSafety.RequireOrdinaryDirectory(alias), "package root is not an ordinary directory");
                RequireRejectedPath(() => PathSafety.RequireOrdinaryDirectory(alias + separator + separator),
                    "package root is not an ordinary directory");

                var ordinary = Stage.CreateChildDirectory(stage.Root, "ordinary");
                var linkedMember = PathSafety.CombineRelativeChild(stage.Root, "linked-member");
                TomatoCakeLauncherFixture.LinkDirectory(linkedMember, ordinary);
                try
                {
                    RequireRejectedPath(() => PathSafety.RequireOrdinaryPackageFile(packageRoot, "linked-member/entry.txt"),
                        "path contains a link or special entry");
                }
                finally
                {
                    Directory.Delete(linkedMember);
                }
            }
            finally
            {
                Directory.Delete(alias);
            }
        }

        private static void RequireRejectedPath(Action operation, string diagnostic)
        {
            try
            {
                operation();
            }
            catch (InvalidDataException error)
            {
                Require(error.Message == diagnostic, "The path rejection keeps its diagnostic.");
                return;
            }

            throw new InvalidOperationException("An ordinary package path check should reject " + diagnostic + ".");
        }

        private static void TestRejections()
        {
            using var stage = new Stage();
            var receipt = UiBundleReceipt.PathFor(BundlePath);
            stage.Write(receipt, stage.Receipt.Replace(stage.BundleSha, new string('0', 64)));
            Require(Reason(stage).Contains("does not match the bundle bytes", StringComparison.Ordinal), "A stale receipt fails.");
            stage.Delete(receipt);
            Require(Reason(stage).Contains("$.uiReceipt", StringComparison.Ordinal), "A missing receipt fails.");
            stage.Write(receipt, stage.Receipt.Replace("Alpha8", "RGBA32"));
            Require(Reason(stage).Contains("Declared image metadata differs", StringComparison.Ordinal), "RGBA cannot satisfy a mask assertion.");
            stage.Write(receipt, stage.Receipt);
            stage.Write(Definition, DefinitionSource.Replace("\"isMask\":true", "\"pixelWidth\":128,\"isMask\":true"));
            Require(Reason(stage).Contains("Declared image metadata differs", StringComparison.Ordinal), "An atlas width is not a Sprite width.");
            stage.Write(Definition, DefinitionSource);
            stage.Manifest.Ui!.Themes[0].Sha256 = Hash(Encoding.UTF8.GetBytes(DefinitionSource));
            Require(Reason(stage).Contains("final definition bytes", StringComparison.Ordinal), "A source hash cannot assert an enriched file.");
            Require(stage.ReadText(Definition) == DefinitionSource, "Failure leaves the staged input untouched.");
            stage.Manifest.Ui.Themes[0].Sha256 = null;
            stage.Write(NoticePath, "Different notice");
            Require(Reason(stage).Contains("$.ui.licensePath", StringComparison.Ordinal), "The declared notice must match the recorded notice.");
            stage.Write(NoticePath, Stage.Notice);
            stage.Write(receipt, stage.Receipt.Replace("\"width\":128,\"height\":64", "\"width\":4096,\"height\":4096")
                .Replace("Alpha8", "RGBAFloat"));
            Require(Reason(stage).Contains("decoded UI texture budget", StringComparison.Ordinal), "Too much decoded texture data fails preflight.");
        }

        private static void TestNativeMetadata()
        {
            using var stage = new Stage();
            Require(UiThemePackage.Prepare(stage.Root, stage.Manifest).Count == 0, "Stage the verified fixture.");
            var manifest = stage.LoadManifest();
            var catalogue = UiThemePackage.Read(stage.Root, manifest).Value ??
                throw new InvalidOperationException("Expected the packaged catalogue.");
            Require(catalogue.TryGet("survival", out var descriptor), "Resolve the admitted theme.");
            var image = descriptor.Images[0];
            Require(catalogue.TryGetBundle(image, out var bundle) && bundle.ReadVerifiedBytes().Succeeded, "Resolve the admitted bundle.");
            var audit = new UiBundleNativeAudit(bundle);
            Require(audit.CheckSprite(image, 48, 20, Texture(1, "RGBA32")).ErrorCode == ModErrorCode.InvalidArgument,
                "The observed native format, rather than the receipt flag, controls mask qualification.");
            Require(audit.CheckSprite(image, 128, 20, Texture(1)).ErrorCode == ModErrorCode.InvalidArgument,
                "The native rectangle cannot be replaced by the texture bounds.");
            Require(audit.CheckSprite(image, 48, 20, Texture(1, bytes: 65L * 1024 * 1024)).ErrorCode == ModErrorCode.RateLimited,
                "Resident bytes are measured independently of declared decoded size.");
            Require(audit.ResidentTextures.Count == 0, "Failed observation does not commit texture identities.");
            Require(audit.CheckSprite(image, 48, 20, Texture(1)).Succeeded &&
                audit.CheckSprite(image, 48, 20, Texture(1)).Succeeded && audit.ResidentTextures.Count == 1,
                "A shared native backing texture is counted once.");
            Require(audit.CheckSprite(image, 48, 20, Texture(2)).ErrorCode == ModErrorCode.InvalidArgument,
                "One receipt texture identity cannot claim different native textures.");
            stage.Write(BundlePath, "changed native bytes");
            Require(bundle.ReadVerifiedBytes().ErrorCode == ModErrorCode.InvalidArgument, "Reverification refuses changed bundle bytes.");
        }

        private static UiObservedTexture Texture(int id, string format = "Alpha8", long bytes = 8192) =>
            new UiObservedTexture(id, 128, 64, 1, format, bytes);

        private static void TestFontMetadata()
        {
            using var stage = new Stage();
            stage.Write(Definition, "{\"schema\":1,\"id\":\"survival\",\"fonts\":[{\"id\":\"face\"," +
                "\"bundlePath\":\"" + BundlePath + "\",\"assetName\":\"example-font\",\"licensePath\":\"" + NoticePath + "\"}]}");
            var font = "{\"assetName\":\"example-font\",\"family\":\"Example\",\"style\":\"Regular\"," +
                "\"ascent\":20,\"descent\":-5,\"textures\":[0],\"sourceSha256\":\"" +
                Hash(Encoding.UTF8.GetBytes("font source")) + "\",\"license\":\"OFL-1.1\",\"licenseSha256\":\"" +
                Hash(Encoding.UTF8.GetBytes(Stage.Notice)) + "\"}";
            stage.Write(UiBundleReceipt.PathFor(BundlePath), stage.Receipt.Replace("\"fonts\":[]", "\"fonts\":[" + font + "]"));
            Require(UiThemePackage.Prepare(stage.Root, stage.Manifest).Count == 0, "A font with a static atlas is admitted.");
            var manifest = stage.LoadManifest();
            var catalogue = UiThemePackage.Read(stage.Root, manifest).Value ??
                throw new InvalidOperationException("Expected the packaged catalogue.");
            Require(catalogue.TryGet("survival", out var descriptor), "Resolve the admitted font theme.");
            var face = descriptor.Fonts[0];
            Require(catalogue.TryGetBundle(face, out var bundle), "Resolve the font bundle.");
            var audit = new UiBundleNativeAudit(bundle);
            var observation = new[] { Texture(1) };
            Require(audit.CheckFont(face, "Different", "Regular", 20, -5, observation).ErrorCode == ModErrorCode.InvalidArgument &&
                audit.ResidentTextures.Count == 0, "Different font family fails before admitting atlas pages.");
            Require(audit.CheckFont(face, "Example", "Regular", 20, -5, observation).Succeeded &&
                audit.ResidentTextures.Count == 1, "Matching font metrics and atlas are admitted.");
        }

        private static string Reason(Stage stage)
        {
            var errors = UiThemePackage.Prepare(stage.Root, stage.Manifest);
            if (errors.Count != 1) throw new InvalidOperationException("Expected one bounded packaging error.");
            return errors[0];
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return UiThemeData.Hex(sha.ComputeHash(bytes));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Stage : IDisposable
        {
            internal const string Notice = "CC0-1.0 notice";
            private const string Bundle = "Native fixture data";

            internal Stage(string? root = null)
            {
                Root = PathSafety.RequireOrdinaryDirectory(root ?? Directory.CreateTempSubdirectory("tf-ui-bundle-").FullName);
                var ui = CreateChildDirectory(Root, "ui");
                CreateChildDirectory(ui, "themes");
                CreateChildDirectory(ui, "receipts");
                CreateChildDirectory(Root, "licenses");
                BundleSha = Hash(Encoding.UTF8.GetBytes(Bundle));
                Manifest = new ModManifest
                {
                    SchemaVersion = ModManifest.CurrentSchemaVersion,
                    Id = "example.ui",
                    Name = "Example",
                    Version = "1.0.0",
                    SupportedLoaderVersionRange = ">=" + UiKitRelease.K1,
                    Ui = new ModUiCatalogue
                    {
                        Schema = 1,
                        Themes = new List<ModUiCatalogueEntry>
                    {
                        new ModUiCatalogueEntry { Id = "survival", Definition = Definition },
                    }
                    },
                };
                Write("topiaforge.mod.json", JsonUtil.Serialize(Manifest));
                Write(Definition, DefinitionSource);
                Write(BundlePath, Bundle);
                Write(NoticePath, Notice);
                Receipt = "{\"schema\":1,\"bundleSha256\":\"" + BundleSha + "\",\"editorVersion\":\"6000.0.23f1\"," +
                    "\"textures\":[{\"width\":128,\"height\":64,\"mipCount\":1,\"format\":\"Alpha8\"}]," +
                    "\"sprites\":[{\"assetName\":\"" + SpriteName + "\",\"width\":48,\"height\":20,\"texture\":0," +
                    "\"sourceSha256\":\"" + Hash(Encoding.UTF8.GetBytes("source")) + "\",\"license\":\"CC0-1.0\"," +
                    "\"licenseSha256\":\"" + Hash(Encoding.UTF8.GetBytes(Notice)) + "\"}],\"fonts\":[]}";
                Write(UiBundleReceipt.PathFor(BundlePath), Receipt);
            }

            internal string Root { get; }
            internal string BundleSha { get; }
            internal string Receipt { get; }
            internal ModManifest Manifest { get; }

            internal ModManifest LoadManifest() =>
                ModManifestJson.LoadFile(PathSafety.RequireOrdinaryPackageFile(Root, "topiaforge.mod.json"));

            internal byte[] ReadBytes(string relative)
            {
                using var input = PathSafety.OpenOrdinaryPackageFile(Root, relative);
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                return bytes.ToArray();
            }

            internal string ReadText(string relative)
            {
                using var input = PathSafety.OpenOrdinaryPackageFile(Root, relative);
                using var reader = new StreamReader(input, Encoding.UTF8);
                return reader.ReadToEnd();
            }

            internal void Delete(string relative) => File.Delete(PathSafety.RequireOrdinaryPackageFile(Root, relative));

            internal void Write(string relative, string text)
            {
                using var output = PathSafety.OpenOrdinaryPackageFile(Root, relative, FileAccess.Write, create: true);
                var bytes = Encoding.UTF8.GetBytes(text);
                output.Write(bytes, 0, bytes.Length);
                output.SetLength(bytes.Length);
            }

            internal static string CreateChildDirectory(string parent, string child)
            {
                var path = PathSafety.CombineRelativeChild(PathSafety.RequireOrdinaryDirectory(parent), child);
                try
                {
                    return PathSafety.RequireOrdinaryDirectory(path);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    return Directory.CreateDirectory(path).FullName;
                }
            }

            public void Dispose() => Directory.Delete(PathSafety.RequireOrdinaryDirectory(Root), recursive: true);
        }
    }
}
