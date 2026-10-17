using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;
using static TopiaForge.ModManager.Tests.WorldBaselineSyncTests;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The network world's life with the realm: leaving and detaching release everything and restore every native
    /// setting, the session's lookups go through the registry and name only this session's entities, the steady state
    /// allocates nothing of its own, and failures stay where they happen.
    /// </summary>
    internal static class NetworkWorldLifecycleTests
    {
        internal static void Run()
        {
            LeavingReleasesEverything();
            DetachingWhileJoinedRestoresEverything();
            LookupsGoThroughTheRegistry();
            TheSteadyStateAllocatesNothingOfItsOwn();
            FailuresStayWhereTheyHappen();
        }

        private static void LeavingReleasesEverything()
        {
            var rig = Yard(8);
            var host = rig.Host;
            NativeAuthoritySuppressorTests.Robots(host.Scene, 2);
            var boxes = host.Scene.Scenes[0].Roots;
            var crates = Enumerable.Range(0, 3).Select(index => host.SpawnCrate(index)).ToList();
            for (var index = 0; index < 2; index++) SpawnRobot(host, index, 32);
            rig.OpenToMultiplayer();
            boxes[0].Position = new NetVector3(5, 5, 5);
            boxes[1].Destroy();
            rig.Frames(10);
            rig.BuildScene = scene =>
            {
                var yard = scene.AddScene("Yard");
                for (var index = 0; index < 8; index++) scene.Add(yard, "Box", null, rigidbody: true, position: new NetVector3(index * 2, 0, 0));
                NativeAuthoritySuppressorTests.Robots(scene, 2);
            };
            var player = AddPlayer(rig, "p1");
            var switches = NativeAuthoritySuppressorTests.AllSwitches(player.Scene);
            var before = switches.Select(item => item.Value).ToArray();
            rig.JoinAndSynchronize(player);
            var mine = player.Scene.Scenes[0].Roots;
            That(mine[0].Mark != 0 && !mine[1].Active && player.Assets.Spawned.Count == 3 && player.Factories.Built.Count == 2,
                "the player has the host's world (" + rig.Describe(player) + ")");

            player.Control.Leave();
            rig.Frames(2);
            That(player.Runtime.Realm == NetRealm.Offline && !player.World.IsOnline, "the player is back in single player");
            That(player.Assets.Spawned.All(replica => !replica.IsAlive) && player.Factories.Released.Count == 2, "every replica is released");
            That(mine.Where(item => item.IsAlive).All(item => item.Mark == 0) && mine[1].Active, "every scene object is unbound, and the hidden box is back");
            That(switches.Select(item => item.Value).SequenceEqual(before), "every native setting is back as it was");
            That(player.Control.Holds == 0 && player.Client.EntityCount == 0 && player.Client.Driver.Count == 0, "nothing is held or mirrored");
            That(player.Control.IdentitySource == player.World && !player.World.TryGetEntity(new NetworkObjectId("world-1"), out _),
                "the session's lookups stay set, and name nothing once offline");
            GC.KeepAlive(crates);
        }

        private static void DetachingWhileJoinedRestoresEverything()
        {
            var rig = Yard(4);
            rig.OpenToMultiplayer();
            rig.BuildScene = scene =>
            {
                var yard = scene.AddScene("Yard");
                for (var index = 0; index < 4; index++) scene.Add(yard, "Box", null, rigidbody: true, kinematic: false, position: new NetVector3(index * 2, 0, 0));
            };
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            var bodies = player.Scene.Scenes[0].Roots;
            That(bodies.All(body => body.Kinematic), "a joined player's scene bodies are kinematic");

            // Shutdown detaches multiplayer first, while still joined.
            player.Runtime.Disconnect();
            That(bodies.All(body => !body.Kinematic) && player.Control.Holds == 0 && player.Control.IdentitySource == null && player.Control.Channel(InternalSystemChannelKind.WorldReplication) == null,
                "detaching restores every body, lets readiness go, removes the lookups and closes the channel");
            That(player.Runtime.Control == null && !player.World.IsOnline, "and the world is offline");
        }

        private static void LookupsGoThroughTheRegistry()
        {
            var rig = Yard(0);
            var host = rig.Host;
            var yard = host.Scene.Scenes[0];
            var cart = host.Scene.Add(yard, "Cart", null, rigidbody: true);
            var wheel = host.Scene.Add(yard, "Wheel", cart);
            var crate = host.SpawnCrate(3);
            rig.OpenToMultiplayer();
            rig.BuildScene = scene =>
            {
                var copy = scene.AddScene("Yard");
                var theirCart = scene.Add(copy, "Cart", null, rigidbody: true);
                scene.Add(copy, "Wheel", theirCart);
            };
            var player = AddPlayer(rig, "p1");
            cart.Position = new NetVector3(1, 0, 0);
            rig.JoinAndSynchronize(player);
            rig.Run(() => player.Scene.Scenes[0].Roots[0].Mark != 0, 30);

            var source = host.Control.IdentitySource!;
            That(source.TryGetNetworkObjectId(crate, out var crateId) && crateId.Value == "world-1", "the host names its spawn (" + crateId + ")");
            var wheelNamed = source.TryGetNetworkObjectId(wheel.Entity, out var wheelId);
            var cartNamed = source.TryGetNetworkObjectId(cart.Entity, out var cartId);
            That(wheelNamed && cartNamed && wheelId.Equals(cartId), "a part of an object resolves to its network root (" + wheelId + ")");
            That(source.TryGetEntity(cartId, out var resolved) && ReferenceEquals(resolved, cart.Entity), "and the id resolves to the root's entity");

            var theirs = player.Control.IdentitySource!;
            var theirCartObject = player.Scene.Scenes[0].Roots[0];
            var theirWheel = theirCartObject.Children[0];
            That(theirs.TryGetNetworkObjectId(theirWheel.Entity, out var mirroredId) && mirroredId.Equals(cartId) &&
                 theirs.TryGetEntity(cartId, out var theirResolved) && ReferenceEquals(theirResolved, theirCartObject.Entity),
                "a joined player names the same object by the same id");
            That(theirs.TryGetEntity(crateId, out var replica) && ReferenceEquals(replica, player.Assets.Spawned.Single().Object.Entity),
                "and the host's spawn by its replica");

            var seam = player.Seam();
            That(seam.TryCreateRef(theirWheel.Entity, out var reference) && reference.Kind == InternalNetRefKind.Entity && reference.Value == cartId.Value &&
                 seam.TryResolveRef(reference, out var referenced) && ReferenceEquals(referenced, theirCartObject.Entity),
                "a reference names the entity on every peer");
            That(!seam.TryResolveRef(new InternalNetRef(InternalNetRefKind.Player, "world-1"), out _), "players are not the network world's to name");
            foreach (var malformed in new[] { "world-0", "world-01", "world-", "world-4294967296", "net-object-1", "world-+1", "world- 1", "WORLD-1" })
            {
                That(!theirs.TryGetEntity(new NetworkObjectId(malformed), out _), "'" + malformed + "' names nothing");
            }

            That(!source.TryGetNetworkObjectId(host.Scene.Add(yard, "Loose").Entity, out _), "an object that is no network root has no id");
            player.Control.Leave();
            rig.Frames(2);
            That(!theirs.TryGetNetworkObjectId(theirWheel.Entity, out _) && !theirs.TryGetEntity(cartId, out _), "after leaving, nothing is named");
        }

        private static void TheSteadyStateAllocatesNothingOfItsOwn()
        {
            var rig = Yard(60);
            var host = rig.Host;
            for (var index = 0; index < 20; index++) SpawnRobot(host, index, 64);
            var boxes = host.Scene.Scenes[0].Roots;
            rig.OpenToMultiplayer();

            // Half the yard moves once while hosting, so the world at rest holds thirty bound boxes as well as its spawns.
            for (var index = 0; index < 30; index++) boxes[index].Position = new NetVector3(index, 9, 0);
            var players = new[] { AddPlayer(rig, "p1"), AddPlayer(rig, "p2") };
            foreach (var player in players) rig.JoinAndSynchronize(player);

            // Everything at rest: after the rest confirmations nothing is sent, and neither side allocates.
            rig.Frames(120);
            var resting = Allocated(() => rig.Frame(), 600);
            That(resting == 0, "a host and two players with a world at rest allocate nothing per frame (" + resting + " bytes over 600 frames)");

            // One box moving: each snapshot costs the channel its copy and its result, and nothing else.
            var moving = boxes[0];
            var x = 0f;
            var perFrame = Allocated(() =>
            {
                x += 0.01f;
                moving.Position = new NetVector3(x, 9, 0);
                rig.Frame();
            }, 600) / 600.0;
            That(perFrame < 200, "with one body moving the world allocates about one small message per tick (" + perFrame.ToString("0.0", CultureInfo.InvariantCulture) + " bytes a frame)");
            Console.WriteLine("Network world steady state: " + resting + " bytes at rest, " + perFrame.ToString("0.0", CultureInfo.InvariantCulture) + " bytes a frame with one body moving (loopback channel).");
        }

        private static void FailuresStayWhereTheyHappen()
        {
            var rig = Yard(3);
            var host = rig.Host;
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            player.Factories.Refuse = true;
            SpawnRobot(host, 1, 16);
            rig.JoinAndSynchronize(player);
            That(player.Counters().ReplicasUnbuildable == 1 && player.Client.EntityCount == 1 && player.Client.BindingOf(new NetworkEntityId(1)) == ClientEntityBinding.None,
                "a replica its factory refuses is mirrored, counted and left unbuilt; the join goes on");
            player.Factories.Refuse = false;

            // A missing bundle, a mod this player lacks and an unknown kind are each unbuildable, never a fault.
            player.Assets.MissingBundle = "assets/missing.bundle";
            var request = new AssetSpawnRequest(new FakePrefab("assets/missing.bundle", "Ghost"), TransformState.Identity);
            var ghost = host.Assets.Spawn(request).Value!;
            host.Seam().RecordAssetSpawn(ghost, request);
            var strangers = host.Replication.Bind("io.example.stranger", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), host.Lifetime);
            var foreign = host.Assets.Spawn(new AssetSpawnRequest(new FakePrefab("assets/world.bundle", "Crate"), TransformState.Identity)).Value!;
            strangers.RecordAssetSpawn(foreign, new AssetSpawnRequest(new FakePrefab("assets/world.bundle", "Crate"), TransformState.Identity));
            var unknown = host.Assets.Spawn(new AssetSpawnRequest(new FakePrefab("assets/world.bundle", "Crate"), TransformState.Identity)).Value!;
            host.Seam().RegisterCanonical(unknown, new InternalSpawnDescriptor("nobody.builds", TransformState.Identity));
            That(rig.Run(() => player.Counters().ReplicasUnbuildable == 4, 60), "each is unbuildable (" + player.Counters().ReplicasUnbuildable + ")");
            That(player.Log.Lines.All(line => !line.Contains("error", StringComparison.OrdinalIgnoreCase)), "and none is an error in the log");

            // A scene census that throws on the host is logged once, and the host's frame goes on.
            var failing = new WorldRig();
            failing.Host.Scene.AddScene("Yard");
            var thrower = new ThrowingBackend(failing.Host.Scene);
            var log = new RecordingRuntimeLog();
            var runtime = new MultiplayerRuntime(() => failing.Network.Host, new MultiplayerRuntimeHost("2545", "0.2.0", log), log) { SessionPhaseSource = () => SessionPhase.Running };
            var replication = new MultiplayerReplication(runtime);
            failing.Host.Runtime.Disconnect();
            NetworkWorld.Install(runtime, replication, thrower, failing.Clock.Read, _ => null);
            runtime.Connect();
            runtime.OpenToMultiplayer(new InternalHostSettings("Broken", 4));
            for (var frame = 0; frame < 5; frame++)
            {
                runtime.BeforeModsUpdate(0.016f);
                runtime.AfterModsUpdate(0.016f);
                failing.Clock.Advance(16_000);
            }

            That(log.Lines.Count(line => line.Contains("census at promotion failed", StringComparison.Ordinal)) == 1 && runtime.Realm == NetRealm.ListenHost,
                "a census that throws is logged once, and the host keeps hosting (" + string.Join(" | ", log.Lines) + ")");
            That(log.Lines.All(line => !line.Contains("203.0.113.9", StringComparison.Ordinal)), "and its log line holds no address");
        }

        /// <summary>A backend whose scene census throws, carrying an address in its message, as a broken engine side might.</summary>
        private sealed class ThrowingBackend : INetworkWorldBackend
        {
            private readonly FakeWorldScene inner;

            internal ThrowingBackend(FakeWorldScene inner)
            {
                this.inner = inner;
            }

            public void CollectSceneObjects(int? sceneHandle, List<NetworkSceneObject> into) =>
                throw new InvalidOperationException("the census of 203.0.113.9:27250 failed");

            public void CollectNativeAuthority(int? sceneHandle, List<NativeAuthorityTarget> into) => inner.CollectNativeAuthority(sceneHandle, into);

            public INetworkWorldObject? Adopt(IEntity entity) => inner.Adopt(entity);

            public bool TryGetNetworkId(IEntity entity, out NetworkEntityId id) => inner.TryGetNetworkId(entity, out id);

            public void PrepareReplica(INetworkWorldObject replica) => inner.PrepareReplica(replica);

            public INativeAuthoritySwitch? PresenceSwitch(INetworkWorldObject sceneObject) => inner.PresenceSwitch(sceneObject);
        }
    }
}
