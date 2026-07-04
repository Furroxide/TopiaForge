using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Robotopia
{
    /// <summary>
    /// Builds the QuantumWorks brand AssetBundle consumed by Robotopia.Mods.UnityUi.
    /// Run headless via `robotopia unity build-ui-bundle`, or in-editor via the menu item.
    /// The output bundle is copied into src/Robotopia.Mods.UnityUi/Assets/ where the
    /// kit csproj embeds it into the DLL as an EmbeddedResource.
    /// </summary>
    public static class UiBundleBuilder
    {
        private const string BundleName = "quantumworks-ui";
        private const string BundleFileName = "quantumworks-ui.bundle";
        private const string OutputDir = "Build/Bundles";
        private const string ManifestAssetPath = "Assets/UiBundleManifest.json";

        // Assets that MUST be labeled into the bundle. Font assets are baked by
        // BakeFontAssets (idempotent — skipped when the committed .asset files exist);
        // this list is the contract the build verifies before producing a bundle.
        // Bold text uses TMP faux-bold (the variable TTF only imports its default
        // instance), so no separate bold asset is baked.
        private static readonly string[] RequiredAssets =
        {
            QuicksandAssetPath,
            AudiowideAssetPath,
            ManifestAssetPath,
        };

        private const string QuicksandAssetPath = "Assets/FontAssets/QuantumWorks-Quicksand SDF.asset";
        private const string AudiowideAssetPath = "Assets/FontAssets/QuantumWorks-Audiowide SDF.asset";
        private const string CharacterSet =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
            " ¡¢£¤¥¦§¨©ª«¬­®¯°±²³´µ¶·¸¹º»¼½¾¿ÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖ×ØÙÚÛÜÝÞßàáâãäåæçèéêëìíîïðñòóôõö÷øùúûüýþÿ" +
            "ĀāĂăĄąĆćĈĉĊċČčĎďĐđĒēĔĕĖėĘęĚěĜĝĞğĠġĢģĤĥĦħĨĩĪīĬĭĮįİıĲĳĴĵĶķĸĹĺĻļĽľĿŀŁłŃńŅņŇňŉŊŋŌōŎŏŐőŒœŔŕŖŗŘřŚśŜŝŞşŠšŢţŤťŦŧŨũŪūŬŭŮůŰűŲųŴŵŶŷŸŹźŻżŽž" +
            "–—‘’‚“”„…•·‰′″‹›€™✓";

        [MenuItem("QuantumWorks/Build UI Bundle")]
        public static void BuildFromMenu()
        {
            try
            {
                BuildInternal();
                EditorUtility.DisplayDialog("QuantumWorks", "UI bundle built and copied into src/Robotopia.Mods.UnityUi/Assets.", "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("QuantumWorks", "UI bundle build failed: " + ex.Message, "OK");
                throw;
            }
        }

        /// <summary>Batch-mode entry point (invoked by -executeMethod Robotopia.UiBundleBuilder.Build).</summary>
        public static void Build()
        {
            try
            {
                BuildInternal();
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("[UiBundleBuilder] Build failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        private static void BuildInternal()
        {
            StampManifest();
            AssetDatabase.Refresh();
            BakeFontAssets();

            var missing = RequiredAssets
                .Where(path => AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets) == string.Empty)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    "Missing required assets (bake the TMP font assets first — see README.md): " + string.Join(", ", missing));
            }

            var unlabeled = RequiredAssets
                .Where(path => AssetImporter.GetAtPath(path) is { } importer && importer.assetBundleName != BundleName)
                .ToList();
            foreach (var path in unlabeled)
            {
                var importer = AssetImporter.GetAtPath(path);
                importer.assetBundleName = BundleName;
                importer.SaveAndReimport();
                Debug.Log("[UiBundleBuilder] Labeled " + path + " into bundle '" + BundleName + "'.");
            }

            // Optional sprite assets ride along automatically when labeled; verify nothing
            // else accidentally joined the bundle.
            var labeled = AssetDatabase.GetAssetPathsFromAssetBundle(BundleName);
            Debug.Log("[UiBundleBuilder] Bundle contents:\n  " + string.Join("\n  ", labeled));

            Directory.CreateDirectory(OutputDir);
            var manifest = BuildPipeline.BuildAssetBundles(
                OutputDir,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.DeterministicAssetBundle,
                BuildTarget.StandaloneWindows64);
            if (manifest == null)
            {
                throw new InvalidOperationException("BuildPipeline.BuildAssetBundles returned null.");
            }

            var built = Path.Combine(OutputDir, BundleName);
            if (!File.Exists(built))
            {
                throw new InvalidOperationException("Expected bundle output not found: " + built);
            }

            var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
            var targetDir = Path.Combine(repoRoot, "src", "Robotopia.Mods.UnityUi", "Assets");
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, BundleFileName);
            File.Copy(built, target, overwrite: true);

            var sha256 = ComputeSha256(target);
            WriteProvenance(targetDir, labeled, sha256);
            Debug.Log("[UiBundleBuilder] Wrote " + target + " (SHA256 " + sha256 + ").");
        }

        /// <summary>
        /// Bakes the static SDF TMP font assets from the brand TTFs. Idempotent: skips
        /// any asset that already exists, so committed bakes are reused byte-identical.
        /// </summary>
        private static void BakeFontAssets()
        {
            EnsureTmpEssentials();
            Directory.CreateDirectory("Assets/FontAssets");
            BakeFontAsset("Assets/Fonts/Quicksand-VariableFont_wght.ttf", QuicksandAssetPath, "QuantumWorks-Quicksand SDF", 1024);
            BakeFontAsset("Assets/Fonts/Audiowide-Regular.ttf", AudiowideAssetPath, "QuantumWorks-Audiowide SDF", 512);
        }

        private const string EssentialsPackage = "Packages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage";

        /// <summary>
        /// Batch entry for the first-run phase: imports the TMP essential resources
        /// (settings + SDF shaders) that CreateFontAsset depends on. ImportPackage is
        /// asynchronous, so this run exits from the completion callback instead of
        /// using -quit.
        /// </summary>
        public static void ImportEssentials()
        {
            if (AssetDatabase.IsValidFolder("Assets/TextMesh Pro"))
            {
                Debug.Log("[UiBundleBuilder] TMP essentials already present.");
                EditorApplication.Exit(0);
                return;
            }

            if (!File.Exists(Path.GetFullPath(EssentialsPackage)))
            {
                Debug.LogError("[UiBundleBuilder] TMP essential resources package not found at " + EssentialsPackage);
                EditorApplication.Exit(1);
                return;
            }

            AssetDatabase.importPackageCompleted += _ =>
            {
                AssetDatabase.SaveAssets();
                Debug.Log("[UiBundleBuilder] TMP essentials imported.");
                EditorApplication.Exit(0);
            };
            AssetDatabase.importPackageFailed += (_, message) =>
            {
                Debug.LogError("[UiBundleBuilder] TMP essentials import failed: " + message);
                EditorApplication.Exit(1);
            };

            Debug.Log("[UiBundleBuilder] Importing TMP Essential Resources.");
            AssetDatabase.ImportPackage(EssentialsPackage, interactive: false);
        }

        /// <summary>Build-phase guard: the essentials phase must have run first.</summary>
        private static void EnsureTmpEssentials()
        {
            if (!AssetDatabase.IsValidFolder("Assets/TextMesh Pro"))
            {
                throw new InvalidOperationException(
                    "TMP essentials are missing - run the ImportEssentials phase first ('robotopia unity build-ui-bundle' does this automatically).");
            }
        }

        private static void BakeFontAsset(string ttfPath, string assetPath, string assetName, int atlasSize)
        {
            if (AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets) != string.Empty)
            {
                return;
            }

            var font = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
            if (font == null)
            {
                throw new InvalidOperationException("Source font not found: " + ttfPath);
            }

            Debug.Log("[UiBundleBuilder] probe: font '" + font.name + "' loaded; sdf shader=" +
                (Shader.Find("TextMeshPro/Distance Field") != null) + " mobile=" +
                (Shader.Find("TextMeshPro/Mobile/Distance Field") != null) + " tmpSettings=" +
                (Resources.Load<TMP_Settings>("TMP Settings") != null));

            TMP_FontAsset? fontAsset;
            try
            {
                fontAsset = TMP_FontAsset.CreateFontAsset(
                    font,
                    samplingPointSize: 90,
                    atlasPadding: 9,
                    renderMode: GlyphRenderMode.SDFAA,
                    atlasWidth: atlasSize,
                    atlasHeight: atlasSize,
                    atlasPopulationMode: AtlasPopulationMode.Dynamic,
                    enableMultiAtlasSupport: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("CreateFontAsset threw for " + ttfPath + ": " + ex, ex);
            }

            if (fontAsset == null)
            {
                throw new InvalidOperationException("TMP_FontAsset.CreateFontAsset returned null for " + ttfPath);
            }

            Debug.Log("[UiBundleBuilder] probe: fontAsset created; material=" + (fontAsset.material != null) +
                " atlasTextures=" + (fontAsset.atlasTextures != null ? fontAsset.atlasTextures.Length.ToString() : "null"));

            if (!fontAsset.TryAddCharacters(CharacterSet, out var missing) && !string.IsNullOrEmpty(missing))
            {
                Debug.LogWarning("[UiBundleBuilder] " + assetName + " is missing glyphs for: " + missing);
            }

            // Freeze to a static atlas so the committed asset is deterministic and the
            // runtime never rasterizes.
            fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
            fontAsset.name = assetName;

            AssetDatabase.CreateAsset(fontAsset, assetPath);
            if (fontAsset.material != null)
            {
                fontAsset.material.name = assetName + " Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            foreach (var texture in fontAsset.atlasTextures)
            {
                if (texture != null)
                {
                    texture.name = assetName + " Atlas";
                    AssetDatabase.AddObjectToAsset(texture, fontAsset);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);
            Debug.Log("[UiBundleBuilder] Baked " + assetName + " (" + atlasSize + "px atlas) from " + ttfPath + ".");
        }

        private static void StampManifest()
        {
            var payload = new StringBuilder();
            payload.AppendLine("{");
            payload.AppendLine("  \"bundle\": \"" + BundleName + "\",");
            payload.AppendLine("  \"editorVersion\": \"" + Application.unityVersion + "\",");
            payload.AppendLine("  \"builtUtc\": \"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",");
            payload.AppendLine("  \"assets\": [");
            payload.AppendLine(string.Join(",\n", RequiredAssets.Select(a => "    \"" + a + "\"")));
            payload.AppendLine("  ]");
            payload.AppendLine("}");
            File.WriteAllText(ManifestAssetPath, payload.ToString());
        }

        private static void WriteProvenance(string targetDir, IEnumerable<string> labeled, string sha256)
        {
            var payload = new StringBuilder();
            payload.AppendLine("{");
            payload.AppendLine("  \"bundle\": \"" + BundleFileName + "\",");
            payload.AppendLine("  \"editorVersion\": \"" + Application.unityVersion + "\",");
            payload.AppendLine("  \"builtUtc\": \"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",");
            payload.AppendLine("  \"sha256\": \"" + sha256 + "\",");
            payload.AppendLine("  \"assets\": [");
            payload.AppendLine(string.Join(",\n", labeled.Select(a => "    \"" + a + "\"")));
            payload.AppendLine("  ]");
            payload.AppendLine("}");
            File.WriteAllText(Path.Combine(targetDir, "quantumworks-ui.manifest.json"), payload.ToString());
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
