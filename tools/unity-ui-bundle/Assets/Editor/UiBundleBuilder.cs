using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Robotopia
{
    /// <summary>
    /// Builds the QuantumWorks brand AssetBundle consumed by Robotopia.Mods.UnityUi.
    /// Run headless via tools/build-ui-bundle.ps1, or in-editor via the menu item.
    /// The output bundle is copied into src/Robotopia.Mods.UnityUi/Assets/ where the
    /// kit csproj embeds it into the DLL as an EmbeddedResource.
    /// </summary>
    public static class UiBundleBuilder
    {
        private const string BundleName = "quantumworks-ui";
        private const string BundleFileName = "quantumworks-ui.bundle";
        private const string OutputDir = "Build/Bundles";
        private const string ManifestAssetPath = "Assets/UiBundleManifest.json";

        // Assets that MUST be labeled into the bundle. Font assets are baked interactively
        // (TMP Font Asset Creator) and committed; this list is the contract the build
        // verifies before producing a bundle.
        private static readonly string[] RequiredAssets =
        {
            "Assets/FontAssets/QuantumWorks-Quicksand SDF.asset",
            "Assets/FontAssets/QuantumWorks-Quicksand-Bold SDF.asset",
            "Assets/FontAssets/QuantumWorks-Arista SDF.asset",
            ManifestAssetPath,
        };

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
