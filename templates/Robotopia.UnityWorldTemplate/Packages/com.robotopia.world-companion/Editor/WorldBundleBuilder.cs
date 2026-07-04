using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Robotopia.WorldCompanion.Editor
{
    /// <summary>
    /// Builds the world prefab into an AssetBundle and copies it into the paired mod's AssetBundles/
    /// folder. The pairing lives in robotopia.world.json at the Unity project root (written by
    /// `robotopia world link`); the CLI's headless build overrides fields via command-line args.
    /// Run in-editor via the menu, or headless via
    /// `-executeMethod Robotopia.WorldCompanion.Editor.WorldBundleBuilder.Build`.
    /// </summary>
    public static class WorldBundleBuilder
    {
        private const string ConfigFileName = "robotopia.world.json";
        private const string OutputDir = "Build/WorldBundles";

        [Serializable]
        private class WorldConfig
        {
            public int schemaVersion = 1;
            public string worldId = "";
            public string bundleName = "";
            public string worldPrefab = "Assets/World/World.prefab";
            public string modPath = "";
        }

        [MenuItem("Robotopia/Build World Bundle")]
        public static void BuildFromMenu()
        {
            try
            {
                var target = BuildInternal();
                EditorUtility.DisplayDialog("Robotopia", "World bundle built:\n" + target, "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Robotopia", "World bundle build failed: " + ex.Message, "OK");
                throw;
            }
        }

        /// <summary>Batch entry point (never pass -quit; this exits explicitly).</summary>
        public static void Build()
        {
            try
            {
                BuildInternal();
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("[WorldBundleBuilder] Build failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        private static string BuildInternal()
        {
            var config = LoadConfig();
            ApplyCommandLineOverrides(config);

            if (string.IsNullOrWhiteSpace(config.bundleName))
            {
                throw new InvalidOperationException(
                    "No bundle name: set bundleName in " + ConfigFileName + " (robotopia world link) or pass -robotopiaBundleName.");
            }

            var modPath = ResolveModPath(config.modPath);

            var issues = WorldValidator.Validate(config.worldPrefab);
            foreach (var warning in issues.Warnings)
            {
                Debug.LogWarning("[WorldBundleBuilder] " + warning);
            }

            if (issues.Errors.Count > 0)
            {
                throw new InvalidOperationException(
                    "World prefab validation failed:\n  " + string.Join("\n  ", issues.Errors));
            }

            // Label the prefab; its dependencies (meshes, materials, textures) ride along automatically.
            var importer = AssetImporter.GetAtPath(config.worldPrefab);
            if (importer == null)
            {
                throw new InvalidOperationException("Could not open importer for " + config.worldPrefab);
            }

            if (importer.assetBundleName != config.bundleName)
            {
                importer.assetBundleName = config.bundleName;
                importer.SaveAndReimport();
            }

            var labeled = AssetDatabase.GetAssetPathsFromAssetBundle(config.bundleName);
            Debug.Log("[WorldBundleBuilder] Bundle contents:\n  " + string.Join("\n  ", labeled));

            Directory.CreateDirectory(OutputDir);
            var manifest = BuildPipeline.BuildAssetBundles(
                OutputDir,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.DeterministicAssetBundle,
                BuildTarget.StandaloneWindows64);
            if (manifest == null)
            {
                throw new InvalidOperationException("BuildPipeline.BuildAssetBundles returned null.");
            }

            var built = Path.Combine(OutputDir, config.bundleName);
            if (!File.Exists(built))
            {
                throw new InvalidOperationException("Expected bundle output not found: " + built);
            }

            var targetDir = Path.Combine(modPath, "AssetBundles");
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, config.bundleName + ".bundle");
            File.Copy(built, target, overwrite: true);

            var sha256 = ComputeSha256(target);
            WriteProvenance(targetDir, config, labeled, sha256);
            Debug.Log("[WorldBundleBuilder] Wrote " + target + " (SHA256 " + sha256 + ").");
            return target;
        }

        private static WorldConfig LoadConfig()
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ConfigFileName));
            if (!File.Exists(path))
            {
                // Headless overrides can still supply everything; start from defaults.
                return new WorldConfig();
            }

            var config = JsonUtility.FromJson<WorldConfig>(File.ReadAllText(path));
            return config ?? new WorldConfig();
        }

        // The standard Unity batch pattern: our own -robotopia* args ride on the editor command line.
        private static void ApplyCommandLineOverrides(WorldConfig config)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index++)
            {
                switch (args[index])
                {
                    case "-robotopiaModPath":
                        config.modPath = args[index + 1];
                        break;
                    case "-robotopiaBundleName":
                        config.bundleName = args[index + 1];
                        break;
                    case "-robotopiaWorldPrefab":
                        config.worldPrefab = args[index + 1];
                        break;
                }
            }
        }

        private static string ResolveModPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException(
                    "No paired mod: set modPath in " + ConfigFileName + " (robotopia world link) or pass -robotopiaModPath.");
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var resolved = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(projectRoot, raw));
            if (!File.Exists(Path.Combine(resolved, "robotopia.mod.json")))
            {
                throw new InvalidOperationException(resolved + " is not a mod directory (no robotopia.mod.json).");
            }

            return resolved;
        }

        private static void WriteProvenance(string targetDir, WorldConfig config, string[] labeled, string sha256)
        {
            var payload = new StringBuilder();
            payload.AppendLine("{");
            payload.AppendLine("  \"bundle\": \"" + config.bundleName + ".bundle\",");
            payload.AppendLine("  \"worldPrefab\": \"" + config.worldPrefab + "\",");
            payload.AppendLine("  \"editorVersion\": \"" + Application.unityVersion + "\",");
            payload.AppendLine("  \"builtUtc\": \"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",");
            payload.AppendLine("  \"sha256\": \"" + sha256 + "\",");
            payload.AppendLine("  \"assets\": [");
            payload.AppendLine(string.Join(",\n", labeled.Select(asset => "    \"" + asset + "\"")));
            payload.AppendLine("  ]");
            payload.AppendLine("}");
            File.WriteAllText(Path.Combine(targetDir, config.bundleName + ".manifest.json"), payload.ToString());
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
