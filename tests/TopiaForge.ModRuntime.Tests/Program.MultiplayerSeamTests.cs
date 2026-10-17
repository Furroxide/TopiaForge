using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        /// <summary>
        /// The realm and the factory seam in the real runtime: a loaded mod's context carries the network profile its
        /// manifest gives it, its spawn at load is recorded for a later promotion, offline frames with that record cost
        /// nothing, and the realm rules read the runtime's own session phase.
        /// </summary>
        private static void TestMultiplayerRealmAndFactorySeam(string root)
        {
            var fixture = NewFixture(root, "multiplayer-seam", "TopiaForge.ValidTestMod.RuntimeSpawningMod");
            var host = new MultiplayerFrameHost();
            var runtime = CreateMultiplayerRuntime(fixture, host);
            try
            {
                runtime.Load(new[] { fixture.Package });
                AssertTrace(fixture.TracePath, "spawned");
                var made = host.Made.Single();
                Assert(made.Owner == fixture.Manifest.Id && !made.Scoped &&
                       made.Profile.Equals(new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process)),
                    "a loaded mod's context is made with the profile its manifest gives it (" + made.Profile + ")");
                Assert(runtime.Multiplayer.TryGetNetworkClass(fixture.Manifest.Id, out var networkClass) && networkClass == ModNetworkClass.Shared,
                    "and the runtime knows the loaded mod's class");

                var records = runtime.Multiplayer.Spawns.Live();
                Assert(records.Count == 1, "the mod's spawn at load is recorded (" + records.Count + " records)");
                var record = records[0];
                Assert(record.Description.OwnerModId == fixture.Manifest.Id && record.Description.Kind == MultiplayerSpawnKinds.AssetPrefab &&
                       record.Description.Profile.Equals(made.Profile) && record.Entity.IsAlive &&
                       record.Description.Descriptor is MultiplayerAssetDescriptor descriptor && !descriptor.IsPortable,
                    "the mod's spawn at load is recorded at spawn time, offline, described by its prefab (one without a bundle path stays local)");

                Assert(runtime.Multiplayer.SessionPhaseSource() == SessionPhase.Idle, "without a session runtime the local session is idle");
                var offline = MeasureFrames(runtime, host);
                Assert(offline == 0, "an offline frame with a recorded spawn allocates nothing (" + offline + " bytes)");

                var control = new FakeMultiplayerControl();
                Assert(runtime.Multiplayer.Attach(control), "a solo provider attaches");
                var solo = MeasureFrames(runtime, host);
                Assert(solo == 0, "and the solo frame allocates nothing either (" + solo + " bytes)");
                var join = runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings());
                Assert(join.Succeeded && join.Effect == NetRealmTransitionEffect.ReplaceLocalSession && control.RealmCalls.SequenceEqual(new[] { "join" }),
                    "from the menu a join through the runtime reaches the provider (" + join + ")");
                Assert(runtime.Multiplayer.Leave().Succeeded && control.RealmCalls.Last() == "leave", "and leaving is never refused");
            }
            finally
            {
                runtime.UnloadAll();
            }

            Assert(!runtime.Multiplayer.Spawns.Live().Any(), "unloading the mod ends what it spawned, and its record with it");
        }

        /// <summary>
        /// The realm rules read the runtime's own orchestrator, settled or not: once the player has left the game, the session
        /// is idle but the main menu still loads, and a join asked for meanwhile waits until the menu has loaded, as a launch
        /// does.
        /// </summary>
        private static void TestTheRealmWaitsForTheMainMenu(string root)
        {
            var fixture = NewFixture(root, "multiplayer-menu", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            var runtime = CreateMultiplayerRuntime(fixture, new MultiplayerFrameHost());
            var menuLoad = new TaskCompletionSource<OperationResult<bool>>();
            var menuLoads = 0;
            var sessions = runtime.ActivateSessionRuntime(
                new EffectiveProfile("multiplayer-menu", 1, new[] { new ResolvedPackage(fixture.Manifest.Id, fixture.Manifest.Version, fixture.Manifest) }),
                (_, _) => { menuLoads++; return menuLoad.Task; });
            try
            {
                runtime.Load(new[] { fixture.Package });
                var control = new FakeMultiplayerControl();
                Assert(runtime.Multiplayer.Attach(control), "a provider attaches");
                var menu = sessions.ReturnToMainMenuAsync();
                PumpBindingUntil(runtime.NativeDispatcher, () => menuLoads == 1, "the main menu did not start loading");
                var join = runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings());
                Assert(sessions.Current.Phase == SessionPhase.Idle && join.Verdict == NetRealmTransitionVerdict.Busy && control.RealmCalls.Count == 0,
                    "while the main menu loads, a join waits, though the session is idle (" + join + ")");
                menuLoad.SetResult(OperationResult<bool>.Success(true));
                PumpBinding(runtime.NativeDispatcher, menu);
                var joined = runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings());
                Assert(joined.Succeeded && control.RealmCalls.SequenceEqual(new[] { "join" }), "once the menu has loaded, the join goes through (" + joined + ")");
            }
            finally
            {
                menuLoad.TrySetResult(OperationResult<bool>.Success(true));
                runtime.UnloadAll();
            }
        }

        /// <summary>
        /// An asset service that spawns whatever it is asked to, as a stand-in for the game's: like the game's, it owns each
        /// spawn for the context's lifetime.
        /// </summary>
        private sealed class SpawningAssets : IAssetService
        {
            private readonly IModLifetime lifetime;

            internal SpawningAssets(IModLifetime lifetime)
            {
                this.lifetime = lifetime;
            }

            public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken cancellationToken = default) =>
                Task.FromResult(OperationResult<IAssetBundle>.Failure(ModErrorCode.Unavailable, "no bundles"));

            public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string assetName, CancellationToken cancellationToken = default) =>
                Task.FromResult(OperationResult<IPrefabAsset>.Failure(ModErrorCode.Unavailable, "no prefabs"));

            public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
            {
                var spawned = new SpawnedFixture(request.Transform);
                lifetime.Track(spawned);
                return OperationResult<ISpawnedEntity>.Success(spawned);
            }

            private sealed class SpawnedFixture : ISpawnedEntity
            {
                private static int next;

                internal SpawnedFixture(TransformState initial)
                {
                    InitialTransform = initial;
                    Id = "fixture-" + Interlocked.Increment(ref next);
                }

                public string Id { get; }

                public string Name => "fixture";

                public bool IsAlive { get; private set; } = true;

                public Vec3 Position => Vec3.Zero;

                public TransformState InitialTransform { get; }

                public void Dispose() => IsAlive = false;
            }
        }
    }
}
