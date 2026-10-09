using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestSessionSceneObserverUsesNormalizedEvents(string root)
        {
            foreach (var packageCount in new[] { 0, 2 })
            {
                var first = NewFixture(root, "session-scenes-first-" + packageCount, "TopiaForge.ValidTestMod.RuntimeSceneLifecycleMod");
                var second = NewFixture(root, "session-scenes-second-" + packageCount, "TopiaForge.SecondTestMod.SceneObserverMod", "TopiaForge.SecondTestMod.dll");
                var runtime = first.CreateRuntimeInstance();
                using var dispatcher = new HostDispatcher();
                var observer = new RecordingSessionSceneObserver { ThrowAfterObservation = true };
                runtime.AttachSessionLifecycle(observer, dispatcher);
                if (packageCount > 0)
                {
                    runtime.Load(new[] { first.Package, second.Package });
                    Assert(runtime.LoadedModIds.Count == 2, "Both scene packages must load: "
                        + runtime.GetLoadFailure(first.Manifest.Id) + " | " + runtime.GetLoadFailure(second.Manifest.Id));
                }
                first.Logger.ThrowOnError = true;
                Assert(runtime.DispatchInitialScene(401, "Menu", isValid: true), "Initial scene is accepted.");
                Assert(!runtime.DispatchSceneLoaded(401, "Menu", isValid: true), "Native replay echo is suppressed.");
                Assert(runtime.DispatchSceneUnloaded(401, "Menu", isValid: true, SceneLoadMode.Single), "Unload is accepted.");
                Assert(observer.Events.Select(scene => scene.Phase).SequenceEqual(new[]
                    { SceneLifecyclePhase.Loaded, SceneLifecyclePhase.Activated, SceneLifecyclePhase.Unloaded }),
                    "Session policy receives one normalized event per phase, independent of loaded package count.");
                Assert(observer.Events.All(scene => scene.SceneInstanceId == 401), "Actual scene identity reaches session policy.");
                Assert(runtime.UnloadAllAsync().GetAwaiter().GetResult().Succeeded, "Completed observer drain permits cleanup.");
                if (packageCount > 0)
                    Assert(File.ReadAllLines(second.TracePath).Count(line => line.StartsWith("scene-lifecycle:", StringComparison.Ordinal)) == 6,
                        "Throwing session observer and diagnostics cannot interrupt either package's event delivery: "
                        + string.Join(" | ", File.ReadAllLines(second.TracePath)));
            }
        }

        private sealed class RecordingSessionSceneObserver : IRuntimeSessionShutdown, IRuntimeSessionSceneObserver
        {
            internal readonly List<SceneLifecycleEvent> Events = new List<SceneLifecycleEvent>();
            internal bool ThrowAfterObservation;
            public void OnSceneLifecycle(SceneLifecycleEvent scene)
            {
                Events.Add(scene);
                if (ThrowAfterObservation) throw new InvalidOperationException("Synthetic observer failure.");
            }
            public Task<OperationResult<bool>> StopOwnerAsync(string packageId) => ShutdownAsync();
            public Task<OperationResult<bool>> ShutdownAsync() => Task.FromResult(OperationResult<bool>.Success(true));
        }
    }
}
