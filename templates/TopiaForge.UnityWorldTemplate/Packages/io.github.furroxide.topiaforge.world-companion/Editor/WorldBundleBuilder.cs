using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TopiaForge.WorldCompanion.Editor
{
    /// <summary>
    /// Builds the world prefab into an AssetBundle and copies it into the paired mod's AssetBundles/
    /// folder. The pairing lives in topiaforge.world.json at the Unity project root (written by
    /// `topiaforge world link`); the CLI's headless build overrides fields via command-line args.
    /// Run in-editor via the menu, or headless via
    /// `-executeMethod TopiaForge.WorldCompanion.Editor.WorldBundleBuilder.Build`.
    /// </summary>
    public static class WorldBundleBuilder
    {
        private const string ConfigFileName = "topiaforge.world.json";
        private const string OutputDir = "Build/WorldBundles";
        private const string PipelineAssetPath = "Assets/HDRPDefaultResources/TopiaForgeWorldHDRP.asset";
        private const long MaxConfigBytes = 64 * 1024;

        [Serializable]
        private class WorldConfig
        {
            public int schemaVersion;
            public string worldId = "";
            public string bundleName = "";
            public string worldPrefab = "Assets/World/World.prefab";
            public string modPath = "";
        }

        [MenuItem("TopiaForge/Build World Bundle")]
        public static void BuildFromMenu()
        {
            try
            {
                var target = BuildInternal();
                EditorUtility.DisplayDialog("TopiaForge", "World bundle built:\n" + target, "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("TopiaForge", "World bundle build failed: " + ex.Message, "OK");
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
            EnsureHdrpConfiguration();
            var config = LoadConfig();
            ApplyCommandLineOverrides(config);

            if (string.IsNullOrWhiteSpace(config.bundleName))
            {
                throw new InvalidOperationException(
                    "No bundle name: set bundleName in " + ConfigFileName + " (topiaforge world link) or pass -topiaforgeBundleName.");
            }

            ValidateBundleName(config.bundleName);
            config.worldPrefab = ValidatePrefabPath(config.worldPrefab);

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

            var labeled = AssetDatabase.GetAssetPathsFromAssetBundle(config.bundleName)
                .OrderBy(asset => asset, StringComparer.Ordinal)
                .ToArray();
            Debug.Log("[WorldBundleBuilder] Bundle contents:\n  " + string.Join("\n  ", labeled));

            Directory.CreateDirectory(OutputDir);
            var manifest = BuildPipeline.BuildAssetBundles(
                OutputDir,
                // Unity 5+ always produces deterministic AssetBundles. The old
                // DeterministicAssetBundle flag is obsolete in Unity 6.
                BuildAssetBundleOptions.ChunkBasedCompression,
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
            var provenanceTarget = Path.Combine(targetDir, config.bundleName + ".manifest.json");
            var sha256 = WorldCompanionFileIo.PublishPairAtomic(
                built,
                target,
                provenanceTarget,
                hash => BuildProvenance(config, labeled, hash));
            Debug.Log("[WorldBundleBuilder] Wrote " + target + " (SHA256 " + sha256 + ").");
            return target;
        }

        private static void EnsureHdrpConfiguration()
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(PipelineAssetPath);
            if (pipeline == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PipelineAssetPath));
                pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                pipeline.name = "TopiaForge World HDRP";
                AssetDatabase.CreateAsset(pipeline, PipelineAssetPath);
            }

            if (GraphicsSettings.defaultRenderPipeline != pipeline)
            {
                GraphicsSettings.defaultRenderPipeline = pipeline;
            }

            // Quality levels inherit the single pinned project asset. Per-quality
            // overrides are intentionally absent so authoring and batch builds agree.
            if (QualitySettings.renderPipeline != null)
            {
                QualitySettings.renderPipeline = null;
            }

            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
        }

        private static WorldConfig LoadConfig()
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ConfigFileName));
            string json;
            try
            {
                json = WorldCompanionFileIo.ReadStableUtf8(path, MaxConfigBytes, ConfigFileName);
            }
            catch (FileNotFoundException)
            {
                // Headless overrides can still supply everything; start from defaults only when no path exists.
                return new WorldConfig
                {
                    schemaVersion = WorldCompanionFileIo.CurrentWorldConfigSchemaVersion,
                };
            }

            var config = JsonUtility.FromJson<WorldConfig>(json);
            if (config == null)
            {
                throw new InvalidDataException(ConfigFileName + " must contain a JSON object.");
            }

            WorldCompanionFileIo.RequireCurrentWorldConfigSchema(config.schemaVersion);
            return config;
        }

        private static void ValidateBundleName(string value)
        {
            if (value.Length > 128 || !IsAsciiLetterOrDigit(value[0])
                || !IsAsciiLetterOrDigit(value[value.Length - 1]))
            {
                throw new InvalidOperationException(
                    "Bundle name must be 1-128 characters and start/end with an ASCII letter or digit.");
            }

            foreach (var character in value)
            {
                if (!IsAsciiLetterOrDigit(character) && character != '.' && character != '-' && character != '_')
                {
                    throw new InvalidOperationException(
                        "Bundle name may contain only ASCII letters, digits, '.', '-', and '_'.");
                }
            }
        }

        private static string ValidatePrefabPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\\') >= 0
                || !value.StartsWith("Assets/", StringComparison.Ordinal)
                || !value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "World prefab must be a project-relative Assets/... .prefab path using '/'.");
            }

            var segments = value.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
            {
                throw new InvalidOperationException("World prefab path contains an unsafe segment.");
            }

            return string.Join("/", segments);
        }

        private static bool IsAsciiLetterOrDigit(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z')
                || (value >= '0' && value <= '9');
        }

        // The standard Unity batch pattern: our own -topiaforge* args ride on the editor command line.
        private static void ApplyCommandLineOverrides(WorldConfig config)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index++)
            {
                switch (args[index])
                {
                    case "-topiaforgeModPath":
                        config.modPath = args[index + 1];
                        break;
                    case "-topiaforgeBundleName":
                        config.bundleName = args[index + 1];
                        break;
                    case "-topiaforgeWorldPrefab":
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
                    "No paired mod: set modPath in " + ConfigFileName + " (topiaforge world link) or pass -topiaforgeModPath.");
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var resolved = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(projectRoot, raw));
            if (!File.Exists(Path.Combine(resolved, "topiaforge.mod.json")))
            {
                throw new InvalidOperationException(resolved + " is not a mod directory (no topiaforge.mod.json).");
            }

            return resolved;
        }

        private static string BuildProvenance(WorldConfig config, string[] labeled, string sha256)
        {
            var payload = new StringBuilder();
            payload.AppendLine("{");
            payload.AppendLine("  \"bundle\": " + JsonString(config.bundleName + ".bundle") + ",");
            payload.AppendLine("  \"worldPrefab\": " + JsonString(config.worldPrefab) + ",");
            payload.AppendLine("  \"editorVersion\": " + JsonString(Application.unityVersion) + ",");
            payload.AppendLine("  \"sha256\": " + JsonString(sha256) + ",");
            payload.AppendLine("  \"assets\": [");
            payload.AppendLine(string.Join(",\n", labeled.Select(asset => "    " + JsonString(asset))));
            payload.AppendLine("  ]");
            payload.AppendLine("}");
            return payload.ToString();
        }

        private static string JsonString(string value)
        {
            var escaped = new StringBuilder(value.Length + 2);
            escaped.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            escaped.Append("\\u").Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            escaped.Append(character);
                        }
                        break;
                }
            }

            return escaped.Append('"').ToString();
        }

    }
}
