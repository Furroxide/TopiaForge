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
            foreach (var name in new[] { "CanvasLifecycle", "HiddenSurface", "ElapsedTime", "SceneRechecks", "PersistentOther", "IdleAllocations", "Reset" })
            {
                var run = (Action)probe.GetMethod(name)!.CreateDelegate(typeof(Action));
                try { run(); }
                catch (Exception exception) { failures.Add(name + ": " + exception.Message); }
            }

            Check(failures.Count == 0, string.Join("\n", failures));
        }

        private const string EventSystemAdapterScenarios = """
            using System;
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
