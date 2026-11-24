using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace TopiaForge
{
    /// <summary>Builds one package-local UI bundle and records facts from the resulting native assets.</summary>
    public static partial class UiResourceBundleBuilder
    {
        private const BuildTarget Target = BuildTarget.StandaloneWindows64;

        /// <summary>Batch entry point taking -topiaforgeUiPackage and -topiaforgeUiRecipe paths.</summary>
        public static void Build()
        {
            try
            {
                var arguments = Environment.GetCommandLineArgs();
                var packageRoot = Path.GetFullPath(Argument(arguments, "-topiaforgeUiPackage"));
                var recipe = ReadJson<Recipe>(Path.GetFullPath(Argument(arguments, "-topiaforgeUiRecipe")), 512 * 1024);
                Publish(packageRoot, recipe);
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogError("[UiResourceBundleBuilder] " + error.Message);
                EditorApplication.Exit(1);
            }
        }

        private static void Publish(string packageRoot, Recipe recipe)
        {
            var manifest = ReadJson<PackageManifest>(PackageFile(packageRoot, "topiaforge.mod.json"), 1024 * 1024);
            if (string.IsNullOrWhiteSpace(manifest.name))
                throw new InvalidDataException("The package manifest must identify the mod by name.");
            var destination = PackageFile(packageRoot, recipe.bundlePath, allowMissing: true);
            var declarations = ReadDeclarations(packageRoot, manifest, recipe.bundlePath);
            var sources = BindSources(recipe, declarations);
            var bundleName = "ui-" + Sha256(Encoding.UTF8.GetBytes(manifest.name + "\n" + recipe.bundlePath));
            var (bundleFile, digest, receiptFile) = Bake(bundleName, sources);
            var receiptPath = "ui/receipts/" + Sha256(Encoding.UTF8.GetBytes(recipe.bundlePath)) + ".json";
            var targetReceipt = PackageFile(packageRoot, receiptPath, allowMissing: true);
            ReplaceFile(bundleFile, destination);
            ReplaceFile(receiptFile, targetReceipt);
            Debug.Log("[UiResourceBundleBuilder] " + recipe.bundlePath + " SHA-256 " + digest + ", receipt " + receiptPath);
        }

        private static (string BundleFile, string Digest, string ReceiptFile) Bake(string name, BoundSource[] sources)
        {
            var imports = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            try
            {
                foreach (var source in sources)
                    if (!source.Font) PrepareSprite(source, imports);
                return BuildAndInspect(name, sources);
            }
            finally
            {
                foreach (var import in imports)
                    File.WriteAllBytes(import.Key + ".meta", import.Value);
                if (imports.Count != 0) AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        private static (string BundleFile, string Digest, string ReceiptFile) BuildAndInspect(string bundleName, BoundSource[] sources)
        {
            var build = new AssetBundleBuild
            {
                assetBundleName = bundleName,
                assetNames = sources.Select(source => source.AssetPath).ToArray(),
                addressableNames = sources.Select(source => source.Name).ToArray(),
            };
            var output = Path.Combine("Build", "UiResources", bundleName, "Bundles");
            Directory.CreateDirectory(output);
            var built = BuildPipeline.BuildAssetBundles(output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, Target);
            if (built == null || !built.GetAllAssetBundles().Contains(bundleName, StringComparer.Ordinal) ||
                built.GetAllDependencies(bundleName).Length != 0)
                throw new InvalidDataException("Unity did not build the requested UI asset bundle.");

            var bundleFile = Path.Combine(output, bundleName);
            if (new FileInfo(bundleFile).Length > 32L * 1024 * 1024)
                throw new InvalidDataException("The UI asset bundle exceeds 32 MiB.");
            var digest = Sha256File(bundleFile);
            var receipt = InspectBundle(bundleFile, digest, sources);
            var receiptFile = Path.Combine(output, "receipt.json");
            File.WriteAllText(receiptFile, JsonUtility.ToJson(receipt) + "\n", new UTF8Encoding(false));
            return (bundleFile, digest, receiptFile);
        }

        private static Dictionary<string, Declaration> ReadDeclarations(string root, PackageManifest manifest, string bundlePath)
        {
            var declarations = new Dictionary<string, Declaration>(StringComparer.Ordinal);
            var themes = manifest.ui?.themes;
            if (themes == null || themes.Length < 1 || themes.Length > 8)
                throw new InvalidDataException("The package manifest must declare its UI themes before building a bundle.");
            foreach (var theme in themes)
            {
                var definition = ReadJson<ThemeDefinition>(PackageFile(root, theme.definition), 64 * 1024);
                AddDeclarations(definition.images, font: false);
                AddDeclarations(definition.fonts, font: true);
            }
            return declarations;

            void AddDeclarations(AssetDeclaration[] entries, bool font)
            {
                if (entries == null) return;
                if (entries.Length > (font ? 9 : 64))
                    throw new InvalidDataException("The theme exceeds its UI resource count.");
                foreach (var entry in entries)
                {
                    if (entry.bundlePath != bundlePath) continue;
                    var notice = Sha256File(PackageFile(root, entry.licensePath));
                    if (declarations.TryGetValue(entry.assetName, out var existing))
                    {
                        if (existing.Font != font || existing.Notice != notice)
                            throw new InvalidDataException("A bundle address has conflicting resource or notice declarations.");
                        existing.Mask |= entry.isMask;
                    }
                    else declarations.Add(entry.assetName, new Declaration(font, entry.isMask, notice));
                }
            }
        }

        private static BoundSource[] BindSources(Recipe recipe, Dictionary<string, Declaration> declarations)
        {
            if (declarations.Count == 0 || recipe.assets.Length != declarations.Count)
                throw new InvalidDataException("The recipe must name one Unity source for every declared bundle address.");
            var matched = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<BoundSource>(recipe.assets.Length);
            foreach (var source in recipe.assets)
            {
                if (string.IsNullOrEmpty(source.assetName) || source.assetName.Length > 256 ||
                    !declarations.TryGetValue(source.assetName, out var declared) || !matched.Add(source.assetName) ||
                    !paths.Add(source.assetPath) || string.IsNullOrWhiteSpace(source.license) || source.license.Length > 128)
                    throw new InvalidDataException("The recipe requires distinct exact resource addresses and source paths, with a licence identifier.");

                var project = Path.GetFullPath(".");
                PackageFile(project, source.assetPath);
                if (!source.assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                    throw new InvalidDataException("The Unity source must be inside Assets.");
                var path = string.IsNullOrEmpty(source.sourcePath) ? source.assetPath : source.sourcePath;
                sources.Add(new BoundSource(source.assetName, source.assetPath,
                    Sha256File(PackageFile(project, path)), source.license, declared));
            }
            return sources.OrderBy(source => source.Name, StringComparer.Ordinal).ToArray();
        }

        private static void PrepareSprite(BoundSource source, Dictionary<string, byte[]> imports)
        {
            if (AssetImporter.GetAtPath(source.AssetPath) is not TextureImporter importer)
            {
                if (source.Mask) throw new InvalidDataException("Each declared mask requires an imported sprite texture source.");
                return;
            }
            imports.Add(source.AssetPath,
                UiSmokeAssemblyFileIo.ReadStableBytes(source.AssetPath + ".meta", 1024 * 1024, "UI sprite importer metadata"));
            importer.textureShape = TextureImporterShape.Texture2D;
            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
            }
            if (source.Mask)
            {
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.isReadable = false;
                importer.mipmapEnabled = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                var platform = importer.GetPlatformTextureSettings("Standalone");
                platform.overridden = true;
                platform.format = TextureImporterFormat.Alpha8;
                platform.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SetPlatformTextureSettings(platform);
            }
            importer.SaveAndReimport();
        }

        private static string Argument(string[] arguments, string name)
        {
            var index = Array.IndexOf(arguments, name);
            if (index < 0 || index + 1 == arguments.Length || arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException("Expected " + name + " <path>.");
            return arguments[index + 1];
        }

        private static T ReadJson<T>(string file, int maximumBytes) where T : class
        {
            var data = UiSmokeAssemblyFileIo.ReadStableBytes(file, maximumBytes, "UI authoring JSON");
            using var input = new MemoryStream(data, writable: false);
            using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return JsonUtility.FromJson<T>(reader.ReadToEnd())
                ?? throw new InvalidDataException("Expected a UI authoring JSON object.");
        }

        private static string PackageFile(string root, string relative, bool allowMissing = false)
        {
            if (string.IsNullOrEmpty(relative) || Encoding.UTF8.GetByteCount(relative) > 256 ||
                relative.IndexOfAny(new[] { '\\', ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0 ||
                (File.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("Expected a slash-relative file in an ordinary directory.");

            var segments = relative.Split('/');
            var current = root;
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                if (segment.Length == 0 || segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(" ", StringComparison.Ordinal) || segment.Any(char.IsControl))
                    throw new InvalidDataException("A UI bundle input or output path is unsafe.");
                current = Path.Combine(current, segment);
                var isLast = index + 1 == segments.Length;
                try
                {
                    var kind = File.GetAttributes(current) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device);
                    if (kind != (isLast ? 0 : FileAttributes.Directory))
                        throw new InvalidDataException("A UI bundle path names a link or the wrong kind of file.");
                }
                catch (Exception error) when (allowMissing && (error is FileNotFoundException or DirectoryNotFoundException)) { }
            }
            return current;
        }

        private static void ReplaceFile(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var pending = destination + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, pending);
                if (File.Exists(destination)) File.Replace(pending, destination, null);
                else File.Move(pending, destination);
            }
            finally
            {
                if (File.Exists(pending)) File.Delete(pending);
            }
        }

        private static string Sha256File(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static string Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(bytes));
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();

        [Serializable] private sealed class Recipe
        {
            public string bundlePath = "";
            public Source[] assets = Array.Empty<Source>();
        }
        [Serializable] private sealed class Source
        {
            public string assetName = "";
            public string assetPath = "";
            public string sourcePath = "";
            public string license = "";
        }
        [Serializable] private sealed class PackageManifest
        {
            public string name = "";
            public Catalogue ui;
        }
        [Serializable] private sealed class Catalogue { public Definition[] themes = Array.Empty<Definition>(); }
        [Serializable] private sealed class Definition { public string definition = ""; }
        [Serializable] private sealed class ThemeDefinition
        {
            public AssetDeclaration[] images = Array.Empty<AssetDeclaration>();
            public AssetDeclaration[] fonts = Array.Empty<AssetDeclaration>();
        }
        [Serializable] private sealed class AssetDeclaration
        {
            public string bundlePath = "";
            public string assetName = "";
            public string licensePath = "";
            public bool isMask;
        }

        private sealed class Declaration
        {
            internal Declaration(bool font, bool mask, string notice) { Font = font; Mask = mask; Notice = notice; }
            internal bool Font { get; }
            internal bool Mask { get; set; }
            internal string Notice { get; }
        }

        private sealed class BoundSource
        {
            internal BoundSource(string name, string assetPath, string digest, string license, Declaration declaration)
            {
                Name = name;
                AssetPath = assetPath;
                SourceSha256 = digest;
                License = license;
                Font = declaration.Font;
                Mask = declaration.Mask;
                LicenseSha256 = declaration.Notice;
            }
            internal string Name { get; }
            internal string AssetPath { get; }
            internal bool Font { get; }
            internal bool Mask { get; }
            internal string SourceSha256 { get; }
            internal string License { get; }
            internal string LicenseSha256 { get; }
        }
    }
}
