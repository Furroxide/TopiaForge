using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace TopiaForge
{
    /// <summary>Bounded pinned-Editor entry point; loads only the staged developer fixture.</summary>
    public static class SandboxEditorAutomation
    {
        private const string Pending = "TopiaForge.SandboxEditorAutomation.Pending";
        private static IEnumerator checks;
        private static int lastFrame = -1;
        private static double deadline;
        public static void Run()
        {
            SetViewport();
            SessionState.SetBool(Pending, true);
            Resume();
            if (!EditorApplication.isPlaying) EditorApplication.EnterPlaymode();
        }
        private static void SetViewport()
        {
            // Editor-only reflection adapter: no production actuation or reflection seam.
            var assembly = typeof(EditorWindow).Assembly;
            var sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
            var sizes = sizesType.BaseType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType", true);
            var group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { Enum.Parse(groupType, "Standalone") });
            var sizeType = assembly.GetType("UnityEditor.GameViewSize", true);
            var modeType = assembly.GetType("UnityEditor.GameViewSizeType", true);
            var size = Activator.CreateInstance(sizeType, new object[] { Enum.Parse(modeType, "FixedResolution"), 1920, 1080, "Sandbox automation 1920x1080" });
            var index = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            var viewType = assembly.GetType("UnityEditor.GameView", true);
            var view = EditorWindow.GetWindow(viewType);
            viewType.GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, index);
        }
        [InitializeOnLoadMethod]
        private static void ResumeAfterReload() { if (SessionState.GetBool(Pending, false)) Resume(); }
        private static void Resume()
        {
            EditorApplication.playModeStateChanged -= Playing;
            EditorApplication.playModeStateChanged += Playing;
            if (EditorApplication.isPlaying) Start();
        }
        private static void Playing(PlayModeStateChange state) { if (state == PlayModeStateChange.EnteredPlayMode) Start(); }
        private static void Start()
        {
            EditorApplication.playModeStateChanged -= Playing;
            SessionState.SetBool(Pending, false);
            try
            {
                if (Application.unityVersion != "6000.0.23f1") throw new InvalidOperationException("Editor pin mismatch.");
                var directory = Argument("-topiaforgeSandboxAssemblies");
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                {
                    var name = new AssemblyName(args.Name).Name;
                    if (name == null || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                    var path = Path.Combine(directory, name + ".dll");
                    return File.Exists(path) ? Load(path) : null;
                };
                // Existing project supplies Unity/TMP; the input backend is staged from the current reference inventory.
                var input = Path.Combine(directory, "Unity.InputSystem.dll");
                if (File.Exists(input)) Load(input);
                var fixture = Load(Path.Combine(directory, "TopiaForge.SandboxAutomation.Unity.dll"));
                checks = (IEnumerator)fixture.GetType("TopiaForge.SandboxAutomation.Unity.EditorWorkbenchChecks", true)
                    .GetMethod("Run").Invoke(null, new object[] { Argument("-topiaforgeSandboxEvidence") });
                deadline = EditorApplication.timeSinceStartup + 180;
                EditorApplication.update += Step;
            }
            catch (Exception exception) { Fail(exception); }
        }
        private static Assembly Load(string path)
        {
            var identity = AssemblyName.GetAssemblyName(path);
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => AssemblyName.ReferenceMatchesDefinition(value.GetName(), identity))
                ?? Assembly.Load(UiSmokeAssemblyFileIo.ReadStableBytes(path, 64 * 1024 * 1024, "Sandbox Editor fixture"));
        }
        private static void Step()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Editor fixture exceeded 180 seconds.");
                if (Time.frameCount == lastFrame) return;
                lastFrame = Time.frameCount;
                if (checks.MoveNext()) return;
                EditorApplication.update -= Step;
                (checks as IDisposable)?.Dispose();
                Debug.Log("[SandboxEditorAutomation] Production UI and seeded-negative checks completed; no native game proof.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Fail(exception); }
        }
        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            if (index < 0 || index == args.Length - 1) throw new ArgumentException("Required argument missing: " + name);
            return Path.GetFullPath(args[index + 1]);
        }
        private static void Fail(Exception exception)
        {
            EditorApplication.update -= Step;
            try { (checks as IDisposable)?.Dispose(); } catch (Exception cleanup) { Debug.LogException(cleanup); }
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }
}
