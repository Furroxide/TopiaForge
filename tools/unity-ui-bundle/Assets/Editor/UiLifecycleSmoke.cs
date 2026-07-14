using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge
{
    /// <summary>
    /// Exact-editor smoke for repeated TopiaForgeUi host creation and teardown. The SDK is
    /// loaded from its Release output so this project remains only a bundle builder.
    /// </summary>
    public static class UiLifecycleSmoke
    {
        private const int Cycles = 16;
        private const int MaxAssemblyBytes = 64 * 1024 * 1024;
        private const string PendingSessionKey = "TopiaForge.UiLifecycleSmoke.Pending";
        private static Assembly uiAssembly;
        private static Snapshot baseline;
        private static int verificationAttempts;

        public static void Run()
        {
            SessionState.SetBool(PendingSessionKey, true);
            ResumePendingSmoke();
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.EnterPlaymode();
            }
        }

        [InitializeOnLoadMethod]
        private static void ResumeAfterDomainReload()
        {
            if (SessionState.GetBool(PendingSessionKey, false))
            {
                ResumePendingSmoke();
            }
        }

        private static void ResumePendingSmoke()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            if (EditorApplication.isPlaying)
            {
                StartSmoke();
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                StartSmoke();
            }
        }

        private static void StartSmoke()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            SessionState.SetBool(PendingSessionKey, false);
            try
            {
                LoadRuntimeAssemblies();
                baseline = Snapshot.Capture(uiAssembly);
                for (var index = 0; index < Cycles; index++)
                {
                    ExerciseHost(index);
                    Snapshot.Capture(uiAssembly).AssertRuntimeStateEquals(baseline, index);
                }

                var debug = RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeDebugOverlay");
                debug.GetMethod("Toggle", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                debug.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                Snapshot.Capture(uiAssembly).AssertRuntimeStateEquals(baseline, Cycles);

                var toneType = RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeTone");
                RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeToasts")
                    .GetMethod("Show", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, new object[] { "Lifecycle smoke toast", Enum.Parse(toneType, "Success"), 1f });
                RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeUi")
                    .GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, null);
                Snapshot.Capture(uiAssembly).AssertRuntimeStateEquals(baseline, Cycles + 1);

                EditorApplication.update += VerifyDestroyedCanvases;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private static void ExerciseHost(int index)
        {
            var optionsType = RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeUiOptions");
            var options = Activator.CreateInstance(optionsType);
            optionsType.GetProperty("OwnerId").SetValue(options, "io.github.furroxide.topiaforge.lifecycle-smoke-" + index);

            var profileType = RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeAccessibilityProfile");
            var profile = Activator.CreateInstance(
                profileType,
                new object[] { index % 2 == 0, 1.1f, index % 3 == 0, 0.75f });
            optionsType.GetProperty("AccessibilityProfile").SetValue(options, profile);

            var topiaForgeUi = RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeUi");
            var host = topiaForgeUi.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new[] { options });
            var hostType = host.GetType();

            var keyType = RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeKey");
            hostType.GetMethod("Hotkey").Invoke(
                host,
                new object[] { Enum.Parse(keyType, "F8"), (Action)(() => { }) });

            var bandType = RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeLayerBand");
            var schemeType = RequiredType("TopiaForge.Mods.UnityUi.TopiaForgeScheme");
            var layer = hostType.GetMethod("Layer").Invoke(
                host,
                new object[]
                {
                    "smoke-layer",
                    Enum.Parse(bandType, "Window"),
                    Enum.Parse(schemeType, "Paper"),
                    true,
                    false,
                });

            var window = hostType.GetMethod("Window").Invoke(
                host,
                new object[]
                {
                    "smoke-window",
                    "LIFECYCLE SMOKE",
                    320f,
                    220f,
                    Enum.Parse(schemeType, "Paper"),
                    false,
                });
            window.GetType().GetMethod("Show").Invoke(window, null);

            var modals = hostType.GetProperty("Modal").GetValue(host);
            modals.GetType().GetMethod("Confirm").Invoke(
                modals,
                new object[] { "SMOKE", "Teardown must release this modal.", "OK", (Action)(() => { }), "CANCEL" });

            hostType.GetMethod("SetAccessibilityProfile").Invoke(
                host,
                new[] { Activator.CreateInstance(profileType, new object[] { true, 1.25f, true, 0f }) });
            hostType.GetMethod("Clear").Invoke(host, new[] { layer });

            ((IDisposable)host).Dispose();
            ((IDisposable)host).Dispose();
            AssertDisposedCallFails(hostType.GetMethod("Theme"), host, Enum.Parse(schemeType, "Paper"));
        }

        private static void AssertDisposedCallFails(MethodInfo method, object host, object scheme)
        {
            try
            {
                method.Invoke(host, new[] { scheme });
            }
            catch (TargetInvocationException exception)
                when (exception.InnerException is ObjectDisposedException)
            {
                return;
            }

            throw new InvalidOperationException("UiHost.Theme remained usable after Dispose.");
        }

        private static void VerifyDestroyedCanvases()
        {
            try
            {
                verificationAttempts++;
                var current = Snapshot.Capture(uiAssembly);
                if (current.HasPendingDestroyedObjects(baseline) && verificationAttempts < 16)
                {
                    return;
                }

                EditorApplication.update -= VerifyDestroyedCanvases;
                current.AssertEquals(baseline, "post-destroy verification");
                Debug.Log("[UiLifecycleSmoke] PASS: " + Cycles
                    + " create/show/modal/clear/dispose cycles returned every tracked baseline.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                EditorApplication.update -= VerifyDestroyedCanvases;
                Fail(exception);
            }
        }

        private static void LoadRuntimeAssemblies()
        {
            var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
            var managedDir = RequiredArgument("-robotopiaManagedDir");
            var abstractionsPath = Path.Combine(
                repoRoot,
                "src",
                "TopiaForge.Mods.Abstractions",
                "bin",
                "Release",
                "netstandard2.1",
                "TopiaForge.Mods.Abstractions.dll");
            var uiPath = Path.Combine(
                repoRoot,
                "src",
                "TopiaForge.Mods.UnityUi",
                "bin",
                "Release",
                "netstandard2.1",
                "TopiaForge.Mods.UnityUi.dll");

            LoadRequiredAssembly(Path.Combine(managedDir, "Unity.InputSystem.dll"));
            LoadRequiredAssembly(abstractionsPath);
            uiAssembly = LoadRequiredAssembly(uiPath);
        }

        private static Assembly LoadRequiredAssembly(string path)
        {
            AssemblyName name = null;
            var bytes = UiSmokeAssemblyFileIo.ReadStableBytes(
                path,
                MaxAssemblyBytes,
                "Lifecycle smoke assembly",
                stablePath => name = AssemblyName.GetAssemblyName(stablePath));
            if (name == null)
            {
                throw new InvalidDataException("Lifecycle smoke dependency has no assembly identity: " + path);
            }

            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), name));
            return loaded ?? Assembly.Load(bytes);
        }

        private static Type RequiredType(string name)
        {
            return uiAssembly.GetType(name, throwOnError: true);
        }

        private static string RequiredArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], name, StringComparison.Ordinal))
                {
                    return Path.GetFullPath(args[index + 1]);
                }
            }

            throw new ArgumentException("Missing required command-line argument " + name + ".");
        }

        private static void Fail(Exception exception)
        {
            Debug.LogError("[UiLifecycleSmoke] FAIL: " + exception);
            EditorApplication.Exit(1);
        }

        private sealed class Snapshot
        {
            private Snapshot(
                int canvases,
                int tweens,
                int cursorLeases,
                int dismissEntries,
                int hotkeys,
                int themeSubscribers,
                int hosts,
                int toastViews,
                int queuedToasts,
                int runtimeDrivers,
                int eventSystems,
                string layerCapacity)
            {
                CanvasCount = canvases;
                TweenCount = tweens;
                CursorLeases = cursorLeases;
                DismissEntries = dismissEntries;
                Hotkeys = hotkeys;
                ThemeSubscribers = themeSubscribers;
                Hosts = hosts;
                ToastViews = toastViews;
                QueuedToasts = queuedToasts;
                RuntimeDrivers = runtimeDrivers;
                EventSystems = eventSystems;
                LayerCapacity = layerCapacity;
            }

            public int CanvasCount { get; }
            private int TweenCount { get; }
            private int CursorLeases { get; }
            private int DismissEntries { get; }
            private int Hotkeys { get; }
            private int ThemeSubscribers { get; }
            private int Hosts { get; }
            private int ToastViews { get; }
            private int QueuedToasts { get; }
            private int RuntimeDrivers { get; }
            private int EventSystems { get; }
            private string LayerCapacity { get; }

            public static Snapshot Capture(Assembly assembly)
            {
                var theme = assembly.GetType("TopiaForge.Mods.UnityUi.TopiaForgeTheme", true);
                var changed = theme.GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.GetValue(null) as Delegate;
                var registrations = (ICollection)assembly
                    .GetType("TopiaForge.Mods.UnityUi.TopiaForgeHotkeys", true)
                    .GetField("Registrations", BindingFlags.Static | BindingFlags.NonPublic)
                    .GetValue(null);
                var hosts = StaticCollection(assembly, "TopiaForge.Mods.UnityUi.TopiaForgeUi", "Hosts");
                var toastViews = StaticCollection(assembly, "TopiaForge.Mods.UnityUi.TopiaForgeToasts", "Views");
                var queuedToasts = StaticCollection(assembly, "TopiaForge.Mods.UnityUi.TopiaForgeToasts", "Queue");
                var runtimeType = assembly.GetType("TopiaForge.Mods.UnityUi.TopiaForgeRuntime", true);

                return new Snapshot(
                    Resources.FindObjectsOfTypeAll<Canvas>().Length,
                    StaticInt(assembly, "TopiaForge.Mods.UnityUi.TopiaForgeTween", "ActiveCount"),
                    StaticInt(assembly, "TopiaForge.Mods.UnityUi.TopiaForgeCursor", "ActiveLeases"),
                    StaticInt(assembly, "TopiaForge.Mods.UnityUi.TopiaForgeDismissStack", "Count"),
                    registrations.Count,
                    changed?.GetInvocationList().Length ?? 0,
                    hosts.Count,
                    toastViews.Count,
                    queuedToasts.Count,
                    Resources.FindObjectsOfTypeAll(runtimeType).Length,
                    Resources.FindObjectsOfTypeAll<EventSystem>().Length,
                    LayerCapacitySnapshot(assembly));
            }

            public void AssertRuntimeStateEquals(Snapshot expected, int cycle)
            {
                if (TweenCount != expected.TweenCount
                    || CursorLeases != expected.CursorLeases
                    || DismissEntries != expected.DismissEntries
                    || Hotkeys != expected.Hotkeys
                    || ThemeSubscribers != expected.ThemeSubscribers
                    || Hosts != expected.Hosts
                    || ToastViews != expected.ToastViews
                    || QueuedToasts != expected.QueuedToasts
                    || !string.Equals(LayerCapacity, expected.LayerCapacity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Runtime state leaked after lifecycle cycle " + cycle + ".");
                }
            }

            public void AssertEquals(Snapshot expected, string phase)
            {
                AssertRuntimeStateEquals(expected, -1);
                if (HasPendingDestroyedObjects(expected))
                {
                    throw new InvalidOperationException(
                        "Unity object counts did not return to baseline during " + phase
                        + ": canvases " + CanvasCount + "/" + expected.CanvasCount
                        + ", runtime drivers " + RuntimeDrivers + "/" + expected.RuntimeDrivers
                        + ", event systems " + EventSystems + "/" + expected.EventSystems + ".");
                }
            }

            public bool HasPendingDestroyedObjects(Snapshot expected)
            {
                return CanvasCount != expected.CanvasCount
                    || RuntimeDrivers != expected.RuntimeDrivers
                    || EventSystems != expected.EventSystems;
            }

            private static int StaticInt(Assembly assembly, string typeName, string propertyName)
            {
                return (int)assembly.GetType(typeName, true)
                    .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)
                    .GetValue(null);
            }

            private static ICollection StaticCollection(Assembly assembly, string typeName, string fieldName)
            {
                return (ICollection)assembly.GetType(typeName, true)
                    .GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)
                    .GetValue(null);
            }

            private static string LayerCapacitySnapshot(Assembly assembly)
            {
                var layers = assembly.GetType("TopiaForge.Mods.UnityUi.TopiaForgeLayers", true);
                var bands = layers.GetField("Bands", BindingFlags.Static | BindingFlags.NonPublic)
                    .GetValue(null);
                var bandType = assembly.GetType("TopiaForge.Mods.UnityUi.TopiaForgeLayerBand", true);
                var remaining = bands.GetType().GetMethod("Remaining");
                var values = new List<string>();
                foreach (var band in Enum.GetValues(bandType))
                {
                    values.Add(band + "=" + remaining.Invoke(bands, new[] { band }));
                }

                return string.Join(",", values);
            }
        }
    }
}
