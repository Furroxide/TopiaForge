extern alias provider;

using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The network world (WS3.6) over real providers on the virtual network, each peer composed as the game composes it: a
/// joining player's baseline crosses the real <c>WorldReplication</c> channel while the player's readiness hold keeps it
/// short of Ready, the host's motion reaches it, a rejoin within the grace starts again from a new baseline, and the world's
/// share of a frame's allocation is measured, at rest and with a body moving.
/// </summary>
internal static class NetworkWorldEngineTests
{
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');
    private const string RobotKind = "robotkit.robot";

    internal static void AJoiningPlayersWorldCrossesTheRealChannel()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(3606, Sandbox);
        var host = WorldNode.Compose(rig, rig.Host);
        for (var index = 0; index < 48; index++) host.SpawnRobot(index, 1024);
        rig.StartHosting(new InternalHostSettings("World", 4));
        That(rig.Run(() => host.World.Progress.Phase == NetworkWorldPhase.Hosting), "the host hosts its world");

        var guest = rig.AddPeer("guest", Sandbox);
        guest.Runtime.PreferredDisplayName = "Sam";
        var player = WorldNode.Compose(rig, guest);
        That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the guest joins");
        var heldWhileReceiving = false;
        var readyTooSoon = false;
        That(rig.Run(() =>
            {
                var phase = guest.Control.GetStatus().JoinPhase;
                if (phase == InternalJoinPhase.Synchronizing && player.World.IsHoldingReadiness && guest.Control.IsReadinessHeld) heldWhileReceiving = true;
                if (phase == InternalJoinPhase.InSession && player.World.Progress.Phase != NetworkWorldPhase.Synchronized) readyTooSoon = true;
                return phase == InternalJoinPhase.InSession && rig.Host.Control.IsParticipantReady(rig.Host.IdOf("Sam"));
            }, 60_000_000),
            "the guest's world arrives and it becomes Ready (" + player.World.Progress + "; " + guest.Control.GetStatus().JoinPhase + ")");
        That(heldWhileReceiving && !readyTooSoon, "readiness was held while the world arrived, and the player was Ready only once it was in place");
        That(player.SameWorldAs(host) && player.Factory.Built.Count == 48 && player.Factory.Built.All(item => item.Payload.Length == 1024),
            "every robot arrived whole, in " + host.World.Counters.BaselinesStarted + " baseline(s) of " + player.World.Channel.Handled + " messages");

        // The host pushes a box: it binds on its first motion, and the player's copy follows it.
        var box = host.Yard.Roots[0];
        var mine = player.Yard.Roots[0];
        for (var frame = 1; frame <= 40; frame++)
        {
            box.Position = new NetVector3(frame * 0.05f, 0f, 0f);
            rig.Run(() => false, 16_000, 16_000);
        }

        That(rig.Run(() => mine.Mark == box.Mark && mine.Mark != 0 && Near(mine.Position, box.Position), 5_000_000, 16_000),
            "the player's box follows the host's (" + mine.Position.X + " / " + box.Position.X + ")");

        // The link drops and comes back within the grace: new streams, so the world starts again from a baseline.
        var started = host.World.Counters.BaselinesStarted;
        var applied = player.World.Counters.BaselinesApplied;
        var hostNode = rig.Host.Factory.Networks[^1].Node;
        var guestNode = guest.Factory.Networks[^1].Node;
        rig.Network.Partition(hostNode, guestNode, cut: true);
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Reconnecting, 30_000_000), "a long silence makes the guest reconnect");
        host.SpawnRobot(100, 64);
        rig.Network.Partition(hostNode, guestNode, cut: false);
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession && player.World.Progress.Phase == NetworkWorldPhase.Synchronized &&
                           !player.World.IsHoldingReadiness && player.SameWorldAs(host), 60_000_000),
            "the guest is back with the host's world (" + player.World.Progress + "; " + guest.Control.GetStatus().JoinPhase + ")");
        That(host.World.Counters.BaselinesStarted > started && player.World.Counters.BaselinesApplied > applied && player.Factory.Built.Count == 49,
            "from a new baseline on the new stream, which built only the robot spawned meanwhile (" + (host.World.Counters.BaselinesStarted - started) + " more, " +
            player.Factory.Built.Count + " built)");

        // What the world itself costs a frame: its stages measured on their own, inside whole frames of both peers.
        rig.Run(() => false, 2_000_000, 16_000);
        var resting = Measure(rig, () => { });
        That(resting.World == 0, "at rest the world allocates nothing (" + resting.World + " bytes over 600 frames; the whole frames " + resting.Frames + ")");
        var x = box.Position.X;
        var moving = Measure(rig, () =>
        {
            x += 0.01f;
            box.Position = new NetVector3(x, 0f, 0f);
        });
        Console.WriteLine("Network world over the real provider: " + resting.World / 600 + " bytes a frame at rest and " + moving.World / 600 +
                          " with one body moving, of whole frames of " + resting.Frames / 600 + " and " + moving.Frames / 600 + " bytes for both peers.");
        That(moving.World / 600 < 1024, "with one body moving the world allocates a bounded amount a frame, its snapshots' share of the channel (" + moving.World / 600 + " bytes)");
        That(rig.Host.Log.Lines.Concat(guest.Log.Lines).All(line => !line.StartsWith("error:", StringComparison.Ordinal)),
            "nothing went wrong in either log (" + string.Join(" | ", rig.Host.Log.Lines.Concat(guest.Log.Lines).Where(line => line.StartsWith("error:", StringComparison.Ordinal))) + ")");
    }

    /// <summary>
    /// Runs 600 frames of 16 ms of both peers, each after <paramref name="change"/>, with every world stage wrapped in one
    /// that counts what this thread allocates inside it: the world's own cost, and whatever it asks of the channel. Whole
    /// frames are counted too, for scale: the provider's own work is in them.
    /// </summary>
    private static (long World, long Frames) Measure(ControlRig rig, Action change)
    {
        var peers = new[] { rig.Host, rig.Peers[0] };
        var stages = new List<MeasuredStage>();
        foreach (var peer in peers)
        {
            stages.Add(MeasuredStage.Wrap(peer.Composition.WorldEvents, stage => peer.Composition.WorldEvents = stage));
            stages.Add(MeasuredStage.Wrap(peer.Composition.HostSnapshots, stage => peer.Composition.HostSnapshots = stage));
            stages.Add(MeasuredStage.Wrap(peer.Composition.ReplicaInterpolation, stage => peer.Composition.ReplicaInterpolation = stage));
        }

        try
        {
            for (var frame = 0; frame < 64; frame++) Frame(rig, change, peers);
            foreach (var stage in stages) stage.Allocated = 0;
            var frames = 0L;
            for (var frame = 0; frame < 600; frame++)
            {
                change();
                rig.Network.RunFor(16_000);
                var before = GC.GetAllocatedBytesForCurrentThread();
                foreach (var peer in peers) peer.Frame();
                frames += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            return (stages.Sum(stage => stage.Allocated), frames);
        }
        finally
        {
            foreach (var stage in stages) stage.Unwrap();
        }
    }

    private static void Frame(ControlRig rig, Action change, ControlPeer[] peers)
    {
        change();
        rig.Network.RunFor(16_000);
        foreach (var peer in peers) peer.Frame();
    }

    /// <summary>A frame stage that counts what the stage it wraps allocates on this thread.</summary>
    private sealed class MeasuredStage : IMultiplayerFrameStage
    {
        private readonly IMultiplayerFrameStage inner;
        private readonly Action<IMultiplayerFrameStage?> install;

        private MeasuredStage(IMultiplayerFrameStage inner, Action<IMultiplayerFrameStage?> install)
        {
            this.inner = inner;
            this.install = install;
        }

        internal long Allocated { get; set; }

        internal static MeasuredStage Wrap(IMultiplayerFrameStage? inner, Action<IMultiplayerFrameStage?> install)
        {
            var stage = new MeasuredStage(inner ?? throw new InvalidOperationException("Assertion failed: the world fills every stage"), install);
            install(stage);
            return stage;
        }

        internal void Unwrap() => install(inner);

        public void Run(float deltaTime)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            inner.Run(deltaTime);
            Allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    private static bool Near(NetVector3 left, NetVector3 right) =>
        Math.Abs(left.X - right.X) <= 0.01f && Math.Abs(left.Y - right.Y) <= 0.01f && Math.Abs(left.Z - right.Z) <= 0.01f;

    /// <summary>One peer's network world as the game composes it, over a fake engine with a yard of boxes.</summary>
    private sealed class WorldNode
    {
        private const string ModId = "io.example.sandbox";

        private WorldNode(ControlPeer peer, VirtualDatagramNetwork network)
        {
            Peer = peer;
            Yard = Scene.AddScene("Yard");
            for (var index = 0; index < 6; index++) Scene.Add(Yard, "Box", null, rigidbody: true, kinematic: false, position: new NetVector3(index * 2f, 0f, 3f));
            Replication = new MultiplayerReplication(peer.Composition);
            Assets = new FakeWorldAssets(Scene, Lifetime);
            Factory = new Replicas(Scene);
            World = NetworkWorld.Install(peer.Composition, Replication, Scene, () => network.Now, _ => null);
            Seam = Replication.Bind(ModId, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), Lifetime);
            That(Seam.RegisterReplicaFactory(RobotKind, Factory).Succeeded, "the robot provider registers its replicas");
        }

        internal ControlPeer Peer { get; }

        internal FakeWorldScene Scene { get; } = new FakeWorldScene();

        internal FakeSceneData Yard { get; }

        internal FakeModLifetime Lifetime { get; } = new FakeModLifetime();

        internal MultiplayerReplication Replication { get; }

        internal RuntimeReplication Seam { get; }

        internal FakeWorldAssets Assets { get; }

        internal Replicas Factory { get; }

        internal NetworkWorld World { get; }

        /// <summary>Installs a network world on <paramref name="peer"/>'s composition root, which the rig attached already, and attaches it again so the world opens its channel.</summary>
        internal static WorldNode Compose(ControlRig rig, ControlPeer peer)
        {
            peer.Composition.Disconnect();
            var node = new WorldNode(peer, rig.Network);
            That(peer.Composition.Connect(), "the runtime attaches again to " + peer.Name + ", its world installed");
            return node;
        }

        /// <summary>Spawns a robot as a provider does, with a description of <paramref name="payloadBytes"/>.</summary>
        internal void SpawnRobot(float x, int payloadBytes)
        {
            var payload = new byte[payloadBytes];
            for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index * 7 + (int)x);
            var transform = new TransformState(new Vec3(x, 0f, 5f), Quat.Identity, new Vec3(1f, 1f, 1f));
            var spawned = Seam.RequestSpawn(new InternalSpawnDescriptor(RobotKind, transform, payload), () =>
            {
                var made = Assets.Spawn(new AssetSpawnRequest(new FakePrefab("assets/robots.bundle", "Robot"), transform));
                return OperationResult<IEntity>.Success(made.Value!);
            });
            That(spawned.Succeeded, "the host spawns a robot");
        }

        internal bool SameWorldAs(WorldNode host)
        {
            var mine = new List<uint>();
            var theirs = new List<uint>();
            World.Client.CollectIds(mine);
            host.World.Host.CollectIds(theirs);
            return mine.SequenceEqual(theirs);
        }
    }

    /// <summary>A robot provider's replica factory on a joined player: it builds fake robots and records what it built.</summary>
    private sealed class Replicas : IInternalReplicaFactory
    {
        private readonly FakeWorldScene scene;

        internal Replicas(FakeWorldScene scene)
        {
            this.scene = scene;
        }

        internal List<InternalSpawnDescriptor> Built { get; } = new();

        public OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor)
        {
            Built.Add(descriptor);
            var at = descriptor.Transform.Position;
            var built = scene.Add(scene.ActiveScene ?? scene.AddScene("Replicas"), descriptor.Kind, null, true, false, new NetVector3(at.X, at.Y, at.Z));
            return OperationResult<IEntity>.Success(built.Entity);
        }

        public void ReleaseReplica(IEntity replica)
        {
            if (replica is FakeWorldEntity fake) fake.Object.Destroy();
        }
    }
}
