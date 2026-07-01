using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Robotopia.UgcCompanion.Editor
{
    /// <summary>
    /// Editor window that drives the local-dev content loop: pick an export root, point it at the game's UGC
    /// watch folder, and toggle Live Sync. While live, the scene is re-exported on every save and (debounced) on
    /// hierarchy changes, so the running game's Robotopia.UgcLiveSync mod hot-reloads it with no restart.
    /// </summary>
    public sealed class UgcDevDaemon : EditorWindow
    {
        private const string PrefPrefix = "Robotopia.UgcCompanion.";
        private const double DebounceSeconds = 0.4;

        [SerializeField] private Transform exportRoot;
        private string rootPath = string.Empty;
        private string watchFolder = string.Empty;
        private string projectName = "My UGC Level";
        private string sceneId = "main";
        private string sceneName = "Main Scene";
        private string environment = "day";
        private bool liveSync;

        private bool exportPending;
        private double exportDueAt;
        private string status = "Idle.";

        [MenuItem("Robotopia/UGC Live Sync")]
        public static void Open()
        {
            GetWindow<UgcDevDaemon>("UGC Live Sync");
        }

        private void OnEnable()
        {
            watchFolder = EditorPrefs.GetString(PrefPrefix + "watchFolder", string.Empty);
            projectName = EditorPrefs.GetString(PrefPrefix + "projectName", "My UGC Level");
            sceneId = EditorPrefs.GetString(PrefPrefix + "sceneId", "main");
            sceneName = EditorPrefs.GetString(PrefPrefix + "sceneName", "Main Scene");
            environment = EditorPrefs.GetString(PrefPrefix + "environment", "day");
            rootPath = EditorPrefs.GetString(PrefPrefix + "rootPath", string.Empty);
            ResolveRootFromPath();
            SetLiveSync(EditorPrefs.GetBool(PrefPrefix + "liveSync", false));
        }

        private void OnDisable()
        {
            SetLiveSync(false);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Authoring", EditorStyles.boldLabel);
            var newRoot = (Transform)EditorGUILayout.ObjectField("Export root", exportRoot, typeof(Transform), allowSceneObjects: true);
            if (newRoot != exportRoot)
            {
                exportRoot = newRoot;
                rootPath = exportRoot == null ? string.Empty : GetHierarchyPath(exportRoot);
                EditorPrefs.SetString(PrefPrefix + "rootPath", rootPath);
            }

            projectName = Field("Project name", projectName, "projectName");
            sceneId = Field("Scene id", sceneId, "sceneId");
            sceneName = Field("Scene name", sceneName, "sceneName");
            environment = Field("Environment", environment, "environment");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Game watch folder", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            watchFolder = EditorGUILayout.TextField(watchFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                var picked = EditorUtility.OpenFolderPanel("Select the game's UGC watch folder", watchFolder, string.Empty);
                if (!string.IsNullOrEmpty(picked))
                {
                    watchFolder = picked;
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorPrefs.SetString(PrefPrefix + "watchFolder", watchFolder);

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Export now", GUILayout.Height(28)))
            {
                ExportNow();
            }

            var newLive = GUILayout.Toggle(liveSync, liveSync ? "Live Sync: ON" : "Live Sync: OFF", "Button", GUILayout.Height(28));
            if (newLive != liveSync)
            {
                SetLiveSync(newLive);
                if (liveSync)
                {
                    ExportNow();
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(status, MessageType.None);
            EditorGUILayout.HelpBox(
                "Tag GameObjects with Robotopia UGC markers (Add Component → Robotopia UGC). The Export root's "
                + "children with a UGC Entity marker are exported. While Live Sync is ON, saving the scene (or "
                + "editing the hierarchy) re-exports to the watch folder and the running game hot-reloads it.",
                MessageType.Info);
        }

        private string Field(string label, string value, string prefKey)
        {
            var next = EditorGUILayout.TextField(label, value);
            if (next != value)
            {
                EditorPrefs.SetString(PrefPrefix + prefKey, next);
            }

            return next;
        }

        private void SetLiveSync(bool value)
        {
            if (value == liveSync)
            {
                // Still (re)attach handlers on enable when already true.
            }

            liveSync = value;
            EditorPrefs.SetBool(PrefPrefix + "liveSync", liveSync);

            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.update -= OnEditorUpdate;

            if (liveSync)
            {
                EditorSceneManager.sceneSaved += OnSceneSaved;
                EditorApplication.hierarchyChanged += OnHierarchyChanged;
                EditorApplication.update += OnEditorUpdate;
            }
        }

        private void OnSceneSaved(Scene scene)
        {
            ScheduleExport();
        }

        private void OnHierarchyChanged()
        {
            ScheduleExport();
        }

        private void ScheduleExport()
        {
            exportPending = true;
            exportDueAt = EditorApplication.timeSinceStartup + DebounceSeconds;
        }

        private void OnEditorUpdate()
        {
            if (exportPending && EditorApplication.timeSinceStartup >= exportDueAt)
            {
                exportPending = false;
                ExportNow();
            }
        }

        private void ExportNow()
        {
            try
            {
                if (exportRoot == null)
                {
                    ResolveRootFromPath();
                }

                if (exportRoot == null)
                {
                    status = "Set an Export root first.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(watchFolder))
                {
                    status = "Set the game watch folder first.";
                    return;
                }

                var path = UgcProjectExporter.ExportToFolder(exportRoot, watchFolder, projectName, sceneId, sceneName, environment);
                status = "Exported " + DateTime.Now.ToString("HH:mm:ss") + " → " + path;
                Repaint();
            }
            catch (Exception ex)
            {
                status = "Export failed: " + ex.Message;
                Debug.LogError("[UGC Companion] Export failed: " + ex);
            }
        }

        private void ResolveRootFromPath()
        {
            if (exportRoot != null || string.IsNullOrEmpty(rootPath))
            {
                return;
            }

            var go = GameObject.Find(rootPath);
            if (go != null)
            {
                exportRoot = go.transform;
            }
        }

        private static string GetHierarchyPath(Transform t)
        {
            var path = t.name;
            var current = t.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }
    }
}
