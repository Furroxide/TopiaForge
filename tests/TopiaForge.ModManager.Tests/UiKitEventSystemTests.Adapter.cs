using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitEventSystemTests
    {
        // Compile the production adapter unchanged against a small Unity contract double. This tests its actual
        // scheduling, discovery and teardown code; UI Toolkit routing and native Unity allocation still need a game run.
        private static void TheProductionAdapterMeetsItsContract()
        {
            var sources = new[]
            {
                Read("src", "TopiaForge.Mods.UnityUi", "Core", "TopiaForgeEventSystemPolicy.cs"),
                Read("src", "TopiaForge.Mods.UnityUi", "Runtime", "TopiaForgeEventSystems.cs"),
                EventSystemUnityContract,
                EventSystemAdapterScenarios,
            };
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("EventSystemAdapterProbe",
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            Check(result.Success, "The event-system adapter probe must compile: " + string.Join("\n", result.Diagnostics));
            var probe = Assembly.Load(bytes.ToArray()).GetType("AdapterScenarios", throwOnError: true)!;
            var failures = new List<string>();
            foreach (var name in new[]
            {
                "CanvasLifecycle", "HiddenSurface", "ElapsedTime", "SceneRechecks", "PersistentOther", "IdleAllocations",
                "ScansKeepNoReferences", "SameFrameFocus", "RetireAtSceneLoadKeepsPanelHandlers",
                "RetireAtCheckKeepsPanelHandlers", "ShutdownKeepsPanelHandlers", "Reset",
            })
            {
                var run = (Action)probe.GetMethod(name)!.CreateDelegate(typeof(Action));
                try { run(); }
                catch (Exception exception) { failures.Add(name + ": " + exception.Message); }
            }

            Check(failures.Count == 0, string.Join("\n", failures));
        }

        private const string EventSystemAdapterScenarios = """
            using System;
            using System.Runtime.CompilerServices;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.SceneManagement;
            using UnityEngine.UI;

            public static class AdapterScenarios
            {
                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException(message);
                }

                private static GraphicRaycaster Start(bool interactive = true)
                {
                    TopiaForgeEventSystems.Reset();
                    SceneManager.Reset();
                    EventSystem.All.Clear();
                    Time.unscaledTimeAsDouble = 0;
                    var raycaster = new GameObject("Kit canvas").AddComponent<GraphicRaycaster>();
                    raycaster.enabled = interactive;
                    TopiaForgeEventSystems.Track(raycaster);
                    return raycaster;
                }

                public static void CanvasLifecycle()
                {
                    var raycaster = Start(false);
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current == null, "A disabled HUD raycaster must not create an EventSystem.");
                    raycaster.enabled = true;
                    raycaster.gameObject.SetActive(false);
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current == null, "A hidden canvas must not create an EventSystem.");
                    raycaster.gameObject.SetActive(true);
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current != null, "An interactive canvas needs a system on the next frame.");
                    TopiaForgeEventSystems.Reset();
                    UnityEngine.Object.Destroy(raycaster.gameObject);
                    TopiaForgeEventSystems.Track(raycaster);
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current == null, "A destroyed raycaster must not recreate the system.");
                }

                public static void HiddenSurface()
                {
                    var raycaster = Start();
                    var content = new GameObject("Surface content");
                    content.SetActive(false);
                    // Replace the canvas registration: its raycaster stays enabled while only its content is hidden.
                    TopiaForgeEventSystems.Track(raycaster, content);
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current == null, "Hidden content must not request a system through its active parent canvas.");
                    content.SetActive(true);
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current != null, "Showing the content must acquire an EventSystem on the next frame.");
                }

                public static void ElapsedTime()
                {
                    foreach (var fps in new[] { 15, 30, 144 })
                    {
                        Start();
                        TopiaForgeEventSystems.Tick();
                        var own = EventSystem.current!;
                        var other = new GameObject("Game system").AddComponent<EventSystem>();
                        for (var frame = 1; frame <= fps; frame++)
                        {
                            Time.unscaledTimeAsDouble = (double)frame / fps;
                            TopiaForgeEventSystems.Tick();
                        }
                        Check(EventSystem.current == other && own.gameObject.Destroyed,
                            "The kit must yield within one unscaled second at " + fps + " FPS.");
                        Check(other.isActiveAndEnabled && !other.gameObject.Destroyed, "The game's system must stay untouched.");
                        other.enabled = false;
                        TopiaForgeEventSystems.Tick();
                        Check(EventSystem.current != null && EventSystem.current != other,
                            "Disabling the game's system must restore kit input on the next frame.");
                    }
                }

                public static void SceneRechecks()
                {
                    for (var arrival = 0; arrival <= 2; arrival++)
                    {
                        Start();
                        TopiaForgeEventSystems.Tick();
                        EventSystem? other = arrival == 0 ? new GameObject("Scene system").AddComponent<EventSystem>() : null;
                        SceneManager.Load();
                        if (arrival == 0) Check(EventSystem.current == other, "Scene load must yield immediately.");
                        for (var frame = 1; frame <= 2; frame++)
                        {
                            if (arrival == frame) other = new GameObject("Late scene system").AddComponent<EventSystem>();
                            TopiaForgeEventSystems.Tick();
                            if (frame >= arrival) Check(EventSystem.current == other, "Both post-load frames must recheck.");
                        }
                    }
                }

                public static void PersistentOther()
                {
                    Start();
                    TopiaForgeEventSystems.Tick();
                    var other = new GameObject("Persistent system").AddComponent<EventSystem>();
                    UnityEngine.Object.DontDestroyOnLoad(other.gameObject);
                    Time.unscaledTimeAsDouble = 1;
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current == other, "Discovery must include the persistent scene.");
                }

                public static void IdleAllocations()
                {
                    Start();
                    TopiaForgeEventSystems.Tick();
                    var disabled = new GameObject("Disabled system").AddComponent<EventSystem>();
                    disabled.enabled = false;
                    for (var frame = 1; frame <= 120; frame++)
                    {
                        Time.unscaledTimeAsDouble = frame;
                        TopiaForgeEventSystems.Tick();
                    }
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var frame = 121; frame <= 240; frame++)
                    {
                        Time.unscaledTimeAsDouble = frame;
                        TopiaForgeEventSystems.Tick();
                    }
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Check(allocated == 0, "Steady adapter checks allocated " + allocated + " bytes.");
                    Check(EventSystem.current != null, "A disabled foreign system must not retire the kit's own.");
                }

                public static void ScansKeepNoReferences()
                {
                    Start();
                    TopiaForgeEventSystems.Tick();
                    // The persistent scene is scanned last, so its root and that root's disabled system are what a scan that
                    // kept its lists would still hold.
                    var root = PersistentRoot();
                    Time.unscaledTimeAsDouble = 1;
                    TopiaForgeEventSystems.Tick();
                    Remove(root);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Check(!root.IsAlive, "A scan must keep no reference to the objects it walked once it returns.");
                }

                // A dialog selects its default button in the frame it appears, before the driver's next frame.
                public static void SameFrameFocus()
                {
                    Start();
                    var dropped = new GameObject("Default button").AddComponent<Selectable>();
                    dropped.Select();
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current != null && EventSystem.current.currentSelectedGameObject == null,
                        "Unity drops a selection made before the kit's own exists, so the next frame alone comes too late.");

                    Start();
                    var kept = new GameObject("Default button").AddComponent<Selectable>();
                    TopiaForgeEventSystems.EnsureEventSystem();
                    kept.Select();
                    var own = EventSystem.current;
                    Check(own != null && own.currentSelectedGameObject == kept.gameObject,
                        "Focus must find an event system in the frame it is asked for.");
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current == own && own!.currentSelectedGameObject == kept.gameObject,
                        "The next frame keeps that event system and the focus.");

                    Start();
                    var game = new GameObject("Game system").AddComponent<EventSystem>();
                    TopiaForgeEventSystems.EnsureEventSystem();
                    Check(EventSystem.current == game && EventSystem.All.Count == 1,
                        "Focus uses the game's own event system and never makes a second one.");
                }

                [MethodImpl(MethodImplOptions.NoInlining)]
                private static WeakReference PersistentRoot()
                {
                    var root = new GameObject("Persistent root");
                    root.AddComponent<EventSystem>().enabled = false;
                    UnityEngine.Object.DontDestroyOnLoad(root);
                    return new WeakReference(root);
                }

                // Once destroyed, its system leaves the list of event systems too, as in Unity.
                [MethodImpl(MethodImplOptions.NoInlining)]
                private static void Remove(WeakReference reference)
                {
                    var root = (GameObject)reference.Target!;
                    EventSystem.All.RemoveAll(system => ReferenceEquals(system.gameObject, root));
                    UnityEngine.Object.Destroy(root);
                }

                // Unity parents a UI Toolkit panel's input handler (PanelEventHandler and PanelRaycaster) under the event
                // system that made it, which is the kit's own when a panel comes up while the kit's is the only one: before
                // the menu's own is enabled, or in a level without one. Retiring the kit's own must leave those working.
                public static void RetireAtSceneLoadKeepsPanelHandlers()
                {
                    // A surface shown before the first scene, then the menu loads with its own event system.
                    var handlers = KitSystemWithPanelHandlers();
                    var menu = new GameObject("Menu system").AddComponent<EventSystem>();
                    SceneManager.Load();
                    Check(EventSystem.current == menu, "The menu's own takes over at its load.");
                    CheckWorking(handlers, "a scene load");
                }

                public static void RetireAtCheckKeepsPanelHandlers()
                {
                    // The game's own comes back outside a scene load.
                    var handlers = KitSystemWithPanelHandlers();
                    var game = new GameObject("Game system").AddComponent<EventSystem>();
                    Time.unscaledTimeAsDouble = 1;
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current == game, "The game's own takes over within a second.");
                    CheckWorking(handlers, "a periodic check");
                }

                public static void ShutdownKeepsPanelHandlers()
                {
                    var handlers = KitSystemWithPanelHandlers();
                    TopiaForgeEventSystems.Reset();
                    CheckWorking(handlers, "shutdown");
                }

                private static GameObject[] KitSystemWithPanelHandlers()
                {
                    Start();
                    TopiaForgeEventSystems.Tick();
                    var handlers = new[] { new GameObject("RishPanels"), new GameObject("Another panel") };
                    foreach (var handler in handlers) handler.transform.SetParent(EventSystem.current!.transform);
                    return handlers;
                }

                private static void CheckWorking(GameObject[] handlers, string when)
                {
                    foreach (var handler in handlers)
                    {
                        Check(!handler.Destroyed && handler.activeInHierarchy && handler.transform.parent == null
                            && ReferenceEquals(handler.scene.Data, SceneManager.Persistent.Data),
                            "Retiring the kit's own at " + when + " must leave the UI Toolkit panel handler '" + handler.name
                            + "' parented under it alive, active and persistent.");
                    }
                }

                public static void Reset()
                {
                    Start();
                    TopiaForgeEventSystems.Tick();
                    var other = new GameObject("Game system").AddComponent<EventSystem>();
                    TopiaForgeEventSystems.Reset();
                    Check(SceneManager.Subscribers == 0, "Reset must unsubscribe scene callbacks.");
                    Check(EventSystem.current == other && other.isActiveAndEnabled, "Reset may remove only the kit's own.");
                    other.enabled = false;
                    TopiaForgeEventSystems.Tick();
                    Check(EventSystem.current == null, "Reset must forget every kit raycaster.");
                }
            }
            """;
    }
}
