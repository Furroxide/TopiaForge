using System;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The promotion census over the spawn ledger: what a new host registers and keys, the spawns announced after it, the
    /// records a scope's end releases, and the joined player that records nothing and binds world content by key.
    /// </summary>
    internal static class NetworkWorldCensusTests
    {
        internal static void Run()
        {
            PromotionRegistersTheRecordedSpawnsInOrder();
            SpawnsAfterTheCensusAreAnnounced();
            AnEndedScopeLeavesNothingReachable();
            OfflineRecordingCostsNothingAndTellsNobody();
            JoinedPlayersRecordNothingAndBindContentByKey();
            StoppingHostingTakesEveryMarkOff();
        }

        private static void PromotionRegistersTheRecordedSpawnsInOrder()
        {
            var rig = new WorldRig();
            var host = rig.Host;
            var yard = host.Scene.AddScene("Yard");
            var prop = host.Scene.Add(yard, "Prop", null, rigidbody: true);
            host.Scene.Player = host.Scene.Add(yard, "Player", null, rigidbody: true);
            host.LoadScenes();

            // What the solo session spawned before anyone thought of hosting.
            var first = host.SpawnCrate(1);
            var gone = host.SpawnCrate(2);
            var third = host.SpawnCrate(3, "Barrel");
            gone.Dispose();
            var hats = host.Replication.Bind("io.example.hats", new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.Process), host.Lifetime);
            host.SpawnCrate(4, "Hat", hats);
            var platform = host.Scene.Add(yard, "Platform", null, rigidbody: true);
            var worlds = host.Seam(ModExecutionContext.WorldOwner);
            That(worlds.RegisterCanonical(platform.Entity, new InternalSpawnDescriptor("worlds.arena", TransformState.Identity, null, InternalSpawnSource.WorldContent, "arena/platform#0")).Succeeded,
                "a world provider registers its content offline");
            That(host.Runtime.Spawns.Live().Count == 3, "offline, the ledger holds the live shared spawns and the content (" + host.Runtime.Spawns.Live().Count + ")");

            rig.OpenToMultiplayer();
            var world = host.Host;
            That(world.IsActive && world.EntityCount == 3, "the census registers every live record, and only those (" + world.EntityCount + ")");
            var objects = new[] { ((FakeSpawnedEntity)first).Object, ((FakeSpawnedEntity)third).Object, platform };
            That(objects.Select(item => item.Mark).SequenceEqual(new uint[] { 1, 2, 3 }), "in the order they were recorded, each marked with its id");
            // The yard is keyed as it loaded: the prop, not the player. Everything spawned since, the client-local hat
            // included, is no scene object.
            That(world.Index.UniqueCount == 1 && world.Index.TryGetBindable(SceneObjectIndexTests.KeyOf("Yard", ("Prop", 0)), out var slot) && ReferenceEquals(slot.Object, prop),
                "the scene census keys the prop as the yard loaded, and leaves out the spawns and the player (" + world.Index.UniqueCount + " keys)");
            That(host.Runtime.Spawns.Observer == world, "and from now on the host hears about later spawns");
            That(world.TryGetEntity(new NetworkEntityId(3), out var entity) && ReferenceEquals(entity, platform.Entity), "the host names its entities by id");
        }

        private static void SpawnsAfterTheCensusAreAnnounced()
        {
            var rig = new WorldRig();
            rig.UseScene(scene => scene.AddScene("Yard"));
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("p1");
            player.Seam().RegisterReplicaFactory("creator.content", player.Factories);
            rig.JoinAndSynchronize(player);

            var crate = rig.Host.SpawnCrate(5);
            That(rig.Run(() => player.Client.EntityCount == 1 && player.Assets.Spawned.Count == 1, 60),
                "a spawn after the census is announced, and the player builds its replica (" + player.Client.EntityCount + ")");
            var replica = player.Assets.Spawned.Single();
            That(replica.Object.Mark == 1 && Near(replica.Object.Position, new NetVector3(5, 1, 0)) && replica.Object.Kinematic && player.Scene.Prepared == 1,
                "the replica is marked with the host's id, placed where the host has it, and kinematic");

            // A provider that registers what it spawned through the asset service describes it again: players rebuild it.
            var described = rig.Host.Seam().RegisterCanonical(crate, new InternalSpawnDescriptor("creator.content", TransformState.Identity, new byte[] { 4, 2 }));
            That(described.Succeeded && rig.Run(() => player.Factories.Built.Count == 1, 60) && !replica.IsAlive && player.Factories.Built[0].Payload.ToArray().SequenceEqual(new byte[] { 4, 2 }),
                "a new description reaches the player, which releases the old replica and builds the new kind");
            crate.Dispose();
            That(rig.Run(() => player.Client.EntityCount == 0, 60) && player.Factories.Released.Count == 1, "and an ended spawn ends there too");
        }

        private static void AnEndedScopeLeavesNothingReachable()
        {
            var rig = new WorldRig();
            var host = rig.Host;
            var scope = new OwnerModLifetime();
            var seam = host.Seam(ModExecutionContext.GamemodeServer, scope);
            var probe = RecordInScope(seam);
            var record = host.Runtime.Spawns.Live().Single();
            That(record.IsLive && record.Owner == seam, "a record knows the seam whose scope made it");
            scope.Dispose();
            That(host.Runtime.Spawns.Count == 0 && !record.IsLive && record.Entity == MultiplayerSpawnRecord.ReleasedEntity.Instance,
                "the scope's end releases its records at once, and they let go of their entity");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            That(!probe.IsAlive, "so the ended scope's spawn is collectable, though the record object is still held");
            GC.KeepAlive(record);

            // A spawn recorded after its scope began to stop is refused rather than kept.
            var stopping = new OwnerModLifetime();
            var late = host.Seam(ModExecutionContext.GamemodeServer, stopping);
            stopping.BeginStop();
            That(late.RegisterCanonical(new FakeWorldScene().Add(new FakeWorldScene().AddScene("x"), "x").Entity, new InternalSpawnDescriptor("x.y", TransformState.Identity)).ErrorCode ==
                 ModErrorCode.Cancelled && host.Runtime.Spawns.Count == 0, "a stopping scope records nothing");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RecordInScope(RuntimeReplication seam)
        {
            var entity = new ScopeEntity();
            seam.RecordAssetSpawn(entity, new AssetSpawnRequest(new FakePrefab("assets/world.bundle", "Crate"), TransformState.Identity));
            return new WeakReference(entity);
        }

        private static void OfflineRecordingCostsNothingAndTellsNobody()
        {
            var rig = new WorldRig();
            var host = rig.Host;
            for (var index = 0; index < 500; index++) host.SpawnCrate(index);
            That(host.Runtime.Spawns.Observer == null && host.Runtime.Spawns.ObserverFailures == 0, "offline, nobody hears about spawns");
            var offline = Allocated(() => host.Frame());
            That(offline == 0, "a solo frame with the network world installed and 500 records allocates nothing (" + offline + " bytes)");

            // Without a provider at all, the frame reads one field.
            var log = new RecordingRuntimeLog();
            var bare = new MultiplayerRuntime(() => null, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            var replication = new MultiplayerReplication(bare);
            NetworkWorld.Install(bare, replication, new FakeWorldScene(), () => 0, _ => null);
            bare.Connect();
            var none = Allocated(() =>
            {
                bare.BeforeModsUpdate(0.016f);
                bare.AfterModsUpdate(0.016f);
                bare.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));
            });
            That(none == 0, "and so does a frame without a provider (" + none + " bytes)");
        }

        private static void JoinedPlayersRecordNothingAndBindContentByKey()
        {
            var rig = new WorldRig();
            var host = rig.Host;
            var hall = host.Scene.AddScene("Hall");
            host.LoadScenes();
            var platform = host.Scene.Add(hall, "Arena", null, rigidbody: true, position: new NetVector3(0, -1, 0));
            host.Seam(ModExecutionContext.WorldOwner).RegisterCanonical(platform.Entity,
                new InternalSpawnDescriptor("worlds.arena", TransformState.Identity, null, InternalSpawnSource.WorldContent, "arena/floor"));
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("p1");
            rig.JoinAndSynchronize(player);
            That(player.Client.EntityCount == 1 && !player.Client.TryGetEntity(new NetworkEntityId(1), out _), "the player mirrors the content, waiting for its own");

            // The player's own world provider builds the same content, and registers it: it binds by key, recorded nowhere.
            var hallCopy = player.Scene.AddScene("Hall");
            player.LoadScenes();
            var ownPlatform = player.Scene.Add(hallCopy, "Arena", null, rigidbody: true);
            var registered = player.Seam(ModExecutionContext.WorldOwner).RegisterCanonical(ownPlatform.Entity,
                new InternalSpawnDescriptor("worlds.arena", TransformState.Identity, null, InternalSpawnSource.WorldContent, "arena/floor"));
            That(registered.Succeeded && player.Runtime.Spawns.Count == 0, "a joined player's registration is recorded nowhere");
            That(player.Client.TryGetEntity(new NetworkEntityId(1), out var bound) && ReferenceEquals(bound, ownPlatform.Entity) && ownPlatform.Mark == 1 &&
                 Near(ownPlatform.Position, new NetVector3(0, -1, 0)), "it binds to the host's content by key, at the host's pose");
            player.Control.Leave();
            rig.Frames(2);
            That(ownPlatform.Mark == 0 && ownPlatform.IsAlive, "leaving unbinds the player's own content and leaves it there");
        }

        private static void StoppingHostingTakesEveryMarkOff()
        {
            var rig = new WorldRig();
            var host = rig.Host;
            var yard = host.Scene.AddScene("Yard");
            var prop = host.Scene.Add(yard, "Prop", null, rigidbody: true);
            host.LoadScenes();
            var crate = (FakeSpawnedEntity)host.SpawnCrate(1);
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("p1");
            rig.JoinAndSynchronize(player);
            prop.Position = new NetVector3(9, 0, 0);
            rig.Run(() => prop.Mark != 0, 30);
            That(prop.Mark != 0 && crate.Object.Mark != 0, "a moved prop and a spawn are marked while hosting");
            var stopped = host.Runtime.Leave();
            rig.Frames(2);
            That(stopped.Succeeded && !host.Host.IsActive && prop.Mark == 0 && crate.Object.Mark == 0 && host.Runtime.Spawns.Observer == null,
                "stopping hosting takes every mark off and stops listening to the ledger");
            That(host.Runtime.Spawns.Live().Count == 1 && !host.World.TryGetEntity(new NetworkObjectId("world-1"), out _),
                "the spawn's record stays for a later promotion, and no id names anything");
            rig.OpenToMultiplayer();
            That(host.Host.EntityCount == 1 && crate.Object.Mark == 3, "a second promotion starts again, with ids that never repeat (" + crate.Object.Mark + ")");
        }

        /// <summary>A spawned entity nothing but the ledger holds.</summary>
        private sealed class ScopeEntity : ISpawnedEntity
        {
            private static int next;

            public string Id { get; } = "scope-" + System.Threading.Interlocked.Increment(ref next);

            public string Name => "scope entity";

            public bool IsAlive => true;

            public Vec3 Position => Vec3.Zero;

            public TransformState InitialTransform => TransformState.Identity;

            public void Dispose()
            {
            }
        }
    }
}
