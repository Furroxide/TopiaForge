using System;
using System.IO;
using System.Linq;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using RuntimeUnderTest = TopiaForge.ModManager.ModRuntime;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        /// <summary>
        /// ModRuntime's multiplayer composition root in the real runtime: the frame order around the mods, the cost of the
        /// offline frame, and attaching, failing and detaching with the runtime's own lifecycle.
        /// </summary>
        private static void TestMultiplayerRuntime(string root)
        {
            TestMultiplayerFrameOrder(root);
            TestMultiplayerOfflineFrameAllocatesNothing(root);
            TestMultiplayerFollowsTheRuntimeLifecycle(root);
            Console.WriteLine("ModRuntime multiplayer composition tests passed.");
        }

        private static void TestMultiplayerFrameOrder(string root)
        {
            var fixture = NewFixture(root, "multiplayer-frame-order", "TopiaForge.ValidTestMod.RuntimeFrameOrderMod");
            var host = new MultiplayerFrameHost();
            var runtime = CreateMultiplayerRuntime(fixture, host);
            runtime.Load(new[] { fixture.Package });
            Assert(runtime.Multiplayer.Control == null, "a runtime without the multiplayer provider attaches to nothing");

            void Trace(string line) => File.AppendAllText(fixture.TracePath, line + Environment.NewLine);
            var control = new FakeMultiplayerControl(Trace) { Role = InternalSessionRole.ListenHost };
            runtime.Multiplayer.WorldEvents = new RecordingFrameStage("stage-world", Trace);
            runtime.Multiplayer.PresenceSend = new RecordingFrameStage("stage-presence", Trace);
            runtime.Multiplayer.HostSnapshots = new RecordingFrameStage("stage-snapshots", Trace);
            runtime.Multiplayer.ReplicaInterpolation = new RecordingFrameStage("stage-replicas", Trace);
            Assert(runtime.Multiplayer.Attach(control) && ReferenceEquals(control.Runtime, runtime.Multiplayer.Host), "the runtime attaches its host to the control");

            runtime.DispatchUpdate(1f / 60f);
            host.RaiseLateUpdate();
            control.Role = InternalSessionRole.Client;
            runtime.DispatchUpdate(1f / 60f);
            host.RaiseLateUpdate();
            control.Role = InternalSessionRole.Solo;
            runtime.DispatchUpdate(1f / 60f);
            host.RaiseLateUpdate();
            runtime.UnloadAll();

            // A listen host pumps, runs world events and the mods, then pose, voice and snapshots, then flushes. A joined
            // player sends no snapshots, and its replicas move in LateUpdate before the mods see them. Single player only
            // pumps. Shutdown detaches multiplayer before the mods unload.
            var hostFrame = new[] { "mp-pump", "stage-world", "mod-update", "stage-presence", "stage-snapshots", "mp-flush", "mod-late" };
            var joinedFrame = new[] { "mp-pump", "stage-world", "mod-update", "stage-presence", "mp-flush", "stage-replicas", "mod-late" };
            var soloFrame = new[] { "mp-pump", "mod-update", "mod-late" };
            var expected = new[] { "load", "mp-attach" }.Concat(hostFrame).Concat(joinedFrame).Concat(soloFrame).Concat(new[] { "mp-detach", "unload" });
            AssertTrace(fixture.TracePath, expected.ToArray());
        }

        private static void TestMultiplayerOfflineFrameAllocatesNothing(string root)
        {
            var fixture = NewFixture(root, "multiplayer-allocation", "TopiaForge.ValidTestMod.ValidMod");
            var host = new MultiplayerFrameHost();
            var runtime = CreateMultiplayerRuntime(fixture, host);
            try
            {
                runtime.Load(Array.Empty<ModPackage>());
                var offline = MeasureFrames(runtime, host);
                Assert(offline == 0, "the frame without a multiplayer provider allocates nothing (" + offline + " bytes)");

                var control = new FakeMultiplayerControl();
                Assert(runtime.Multiplayer.Attach(control), "a solo provider attaches");
                var solo = MeasureFrames(runtime, host);
                Assert(solo == 0 && control.Pumps > 10_000 && control.Flushes == 0,
                    "the solo frame pumps the provider and allocates nothing (" + solo + " bytes, " + control.Flushes + " flushes)");
            }
            finally
            {
                runtime.UnloadAll();
            }
        }

        private static void TestMultiplayerFollowsTheRuntimeLifecycle(string root)
        {
            var fixture = NewFixture(root, "multiplayer-lifecycle", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            var host = new MultiplayerFrameHost { RunInBackground = false };
            var runtime = CreateMultiplayerRuntime(fixture, host);
            runtime.Load(new[] { fixture.Package });
            var descriptor = runtime.Multiplayer.Host.LocalContent.Single();
            Assert(descriptor.Id == fixture.Manifest.Id && descriptor.Mode == InternalContentMode.ImplicitShared && InternalText.IsSha256(descriptor.PackageSha256),
                "each loaded package is described for multiplayer, with its receipt's archive hash");

            var control = new FakeMultiplayerControl { Role = InternalSessionRole.ListenHost, PumpFailure = new InvalidOperationException("broken provider") };
            Assert(runtime.Multiplayer.Attach(control), "a provider attaches");
            runtime.Multiplayer.Host.SetRunInBackground(true);
            Assert(host.RunInBackground, "a provider going online keeps the game running in the background");
            runtime.DispatchUpdate(1f / 60f);
            runtime.DispatchUpdate(1f / 60f);
            Assert(fixture.Logger.Errors.Count(line => line.Contains("pump failed", StringComparison.Ordinal)) == 1,
                "a failing provider pump is logged once (" + string.Join(" | ", fixture.Logger.Errors) + ")");
            runtime.UnloadAll();
            Assert(control.Detaches == 1 && control.Runtime == null && !host.RunInBackground,
                "shutdown detaches multiplayer and gives the game its own background setting back");
            AssertTrace(fixture.TracePath, "load", "update-after-failure", "update-after-failure", "unload", "cleanup-second", "cleanup-first");
        }

        private static RuntimeUnderTest CreateMultiplayerRuntime(Fixture fixture, MultiplayerFrameHost host) =>
            new RuntimeUnderTest(
                fixture.Paths,
                fixture.Logger,
                new ManifestValidationContext(
                    "0.0.2309",
                    "1.0.0",
                    "1.0.0",
                    platform: "windows",
                    architecture: "x64",
                    contentTargets: new[] { "code", "standalonewindows64" },
                    enforceRuntimeCompatibility: true),
                fixture.Observer,
                host);

        private static long MeasureFrames(RuntimeUnderTest runtime, MultiplayerFrameHost host)
        {
            // A zero frame time keeps the runtime's once-a-second capability refresh out of the measurement.
            for (var index = 0; index < 64; index++)
            {
                runtime.DispatchUpdate(0f);
                host.RaiseLateUpdate();
            }

            _ = GC.GetAllocatedBytesForCurrentThread();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 10_000; index++)
            {
                runtime.DispatchUpdate(0f);
                host.RaiseLateUpdate();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>A gameplay host that raises LateUpdate on demand and keeps a background setting, as the Unity one does.</summary>
        private sealed class MultiplayerFrameHost : IRuntimeGameplayHost, IMultiplayerPlatform
        {
            public event Action<GameTimeSample>? FixedUpdate { add { } remove { } }

            public event Action<GameTimeSample>? LateUpdate;

            public bool RunInBackground { get; set; } = true;

            public GameplayContextServices Create(
                string ownerModId,
                string packagePath,
                string dataPath,
                IModLifetime lifetime,
                IModLogger logger,
                NativeTransitionAccessSlot? transitionAccess = null) =>
                GameplayContextServices.Unavailable(lifetime);

            public GameTimeSample BeginFrame(float deltaTime) => new GameTimeSample(GameLoopPhase.Frame, deltaTime, deltaTime, 0d, 0);

            internal void RaiseLateUpdate() => LateUpdate?.Invoke(new GameTimeSample(GameLoopPhase.Late, 0f, 0f, 0d, 0));

            public void Dispose()
            {
            }
        }
    }
}
