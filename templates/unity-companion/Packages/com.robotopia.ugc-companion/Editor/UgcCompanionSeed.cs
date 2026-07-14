using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Robotopia.UgcCompanion.Editor
{
    /// <summary>
    /// Applies a CLI-provided live-sync seed on project load. `robotopia ugc dev` (and the launcher) write
    /// <c>ProjectSettings/RobotopiaUgcCompanion.json</c>; this bootstrap copies its values into the
    /// <see cref="UgcDevDaemon"/> EditorPrefs and opens the window with Live Sync already ON, so the authoring
    /// loop starts with zero manual configuration. The seed is applied once per <c>seededUtc</c> stamp
    /// (recorded as <c>appliedUtc</c> in the same file), so it never clobbers manual changes on later domain
    /// reloads — re-running the CLI re-arms it with a fresh stamp.
    /// </summary>
    internal static class UgcCompanionSeed
    {
        private const string PrefPrefix = "Robotopia.UgcCompanion.";
        private const string SeedPath = "ProjectSettings/RobotopiaUgcCompanion.json";
        private const int MaxSeedBytes = 64 * 1024;

        [Serializable]
        private sealed class Seed
        {
            public string watchFolder = string.Empty;
            public string projectName = string.Empty;
            public string sceneId = string.Empty;
            public string sceneName = string.Empty;
            public string environment = string.Empty;
            public bool liveSync = true;
            public string seededUtc = string.Empty;
            public string appliedUtc = string.Empty;
        }

        [InitializeOnLoadMethod]
        private static void ApplySeedIfPending()
        {
            try
            {
                string json;
                try
                {
                    json = UgcCompanionSeedFileIo.ReadStableUtf8(
                        SeedPath,
                        MaxSeedBytes,
                        "Robotopia UGC companion seed");
                }
                catch (FileNotFoundException)
                {
                    return; // The optional one-shot seed has not been created.
                }

                var seed = JsonUtility.FromJson<Seed>(json);
                if (seed == null || string.IsNullOrEmpty(seed.watchFolder) || string.IsNullOrEmpty(seed.seededUtc))
                {
                    throw new InvalidDataException(
                        "UGC companion seed must contain non-empty watchFolder and seededUtc values.");
                }

                if (!string.IsNullOrEmpty(seed.appliedUtc) && seed.appliedUtc == seed.seededUtc)
                {
                    return; // Already applied; do not override manual changes on later reloads.
                }

                EditorPrefs.SetString(PrefPrefix + "watchFolder", seed.watchFolder);
                if (!string.IsNullOrEmpty(seed.projectName))
                {
                    EditorPrefs.SetString(PrefPrefix + "projectName", seed.projectName);
                }

                if (!string.IsNullOrEmpty(seed.sceneId))
                {
                    EditorPrefs.SetString(PrefPrefix + "sceneId", seed.sceneId);
                }

                if (!string.IsNullOrEmpty(seed.sceneName))
                {
                    EditorPrefs.SetString(PrefPrefix + "sceneName", seed.sceneName);
                }

                if (!string.IsNullOrEmpty(seed.environment))
                {
                    EditorPrefs.SetString(PrefPrefix + "environment", seed.environment);
                }

                EditorPrefs.SetBool(PrefPrefix + "liveSync", seed.liveSync);

                seed.appliedUtc = seed.seededUtc;
                UgcCompanionSeedFileIo.RewriteAtomicUtf8(
                    SeedPath,
                    json,
                    JsonUtility.ToJson(seed, prettyPrint: true),
                    MaxSeedBytes,
                    "Robotopia UGC companion seed");

                // Open after the editor finishes loading so the window exists and reads the seeded prefs.
                EditorApplication.delayCall += UgcDevDaemon.OpenAndReload;
                Debug.Log("[UGC Companion] Applied live-sync seed: watch folder " + seed.watchFolder
                          + (seed.liveSync ? " (Live Sync ON)" : string.Empty));
            }
            catch (Exception ex)
            {
                Debug.LogError("[UGC Companion] Could not apply live-sync seed safely: " + ex);
                throw new InvalidOperationException("UGC companion seed application failed.", ex);
            }
        }
    }
}
