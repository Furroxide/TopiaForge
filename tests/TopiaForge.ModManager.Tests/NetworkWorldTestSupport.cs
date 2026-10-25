using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Runs the network world suites (WS3.6), and authority routing's over it (WS3.7).</summary>
    internal static class NetworkWorldTests
    {
        internal static void Run()
        {
            SceneObjectIndexTests.Run();
            SceneKeyCaptureTests.Run();
            NativeAuthoritySuppressorTests.Run();
            NetworkWorldWireTests.Run();
            NetworkWorldCensusTests.Run();
            WorldBaselineSyncTests.Run();
            WorldBaselinePacingTests.Run();
            ReplicaTransformDriverTests.Run();
            NetworkWorldLifecycleTests.Run();
            NetworkWorldPrivacyTests.Run();
            BlockerTests.Run();
            AuthorityTests.Run();
            Console.WriteLine("Network world tests passed.");
        }
    }

    /// <summary>A clock a test moves by hand, in microseconds.</summary>
    internal sealed class ManualClock
    {
        internal long Now { get; set; } = 1_000_000;

        internal long Read() => Now;

        internal void Advance(long micros) => Now += micros;
    }

    /// <summary>One peer as ModRuntime composes it: a composition root over a loopback control, its scene, and its network world.</summary>
    internal sealed class WorldPeer
    {
        internal WorldPeer(LoopbackControl control, ManualClock clock, string name)
        {
            Control = control;
            Name = name;
            Log = new RecordingRuntimeLog();
            Runtime = new MultiplayerRuntime(() => Control, new MultiplayerRuntimeHost("2545", "0.2.0", Log), Log) { SessionPhaseSource = () => Phase };
            Replication = new MultiplayerReplication(Runtime);
            Scene = new FakeWorldScene();
            World = NetworkWorld.Install(Runtime, Replication, Scene, clock.Read, FindAssets);
            Lifetime = new OwnerModLifetime();
            Assets = new FakeWorldAssets(Scene, Lifetime);
            Factories = new RecordingFactory(Scene);
        }

        internal string Name { get; }

        internal LoopbackControl Control { get; }

        internal RecordingRuntimeLog Log { get; }

        internal MultiplayerRuntime Runtime { get; }

        internal MultiplayerReplication Replication { get; }

        internal FakeWorldScene Scene { get; }

        internal NetworkWorld World { get; }

        internal OwnerModLifetime Lifetime { get; }

        /// <summary>Gets the asset service of the test mod, which a joined player builds asset replicas through.</summary>
        internal FakeWorldAssets Assets { get; }

        /// <summary>Gets or sets an asset service the test mod's replicas load through instead of <see cref="Assets"/>.</summary>
        internal IAssetService? ModAssets { get; set; }

        internal RecordingFactory Factories { get; }

        /// <summary>Gets the scenes this peer's world was told had loaded.</summary>
        internal HashSet<int> LoadedScenes { get; } = new HashSet<int>();

        internal SessionPhase Phase { get; set; } = SessionPhase.Running;

        /// <summary>Gets or sets whether this peer's runtime runs frames; a stalled game runs none.</summary>
        internal bool Paused { get; set; }

        internal NetworkWorldClient Client => World.Client;

        internal NetworkWorldHost Host => World.Host;

        internal const string ModId = "io.example.world";

        /// <summary>
        /// Tells this peer's world of every scene its engine holds that it has not heard of yet, as the runtime's scene hook
        /// does inside each load: the world keys the scene's objects now, as they loaded.
        /// </summary>
        internal void LoadScenes()
        {
            foreach (var scene in Scene.Scenes)
            {
                if (LoadedScenes.Add(scene.Handle)) World.SceneLoaded(scene.Handle);
            }
        }

        /// <summary>Unloads a scene, telling the world first as the runtime's scene hook does.</summary>
        internal void UnloadScene(FakeSceneData scene)
        {
            World.SceneUnloaded(scene.Handle);
            Scene.UnloadScene(scene);
            LoadedScenes.Remove(scene.Handle);
        }

        internal IAssetService? FindAssets(string modId) => string.Equals(modId, ModId, StringComparison.Ordinal) ? ModAssets ?? Assets : null;

        /// <summary>Binds a replication seam for the test mod, with <paramref name="context"/>.</summary>
        internal RuntimeReplication Seam(ModExecutionContext context = ModExecutionContext.Process, IModLifetime? lifetime = null) =>
            Replication.Bind(ModId, new ModNetProfile(ModNetworkClass.Shared, context), lifetime ?? Lifetime);

        /// <summary>One frame, as ModRuntime runs it: pump, world events, mods, sends and flush, then LateUpdate.</summary>
        internal void Frame()
        {
            if (Paused) return;
            Runtime.BeforeModsUpdate(0.016f);
            Runtime.AfterModsUpdate(0.016f);
            Runtime.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));
        }

        /// <summary>Spawns a crate through the test mod's asset service, as a mod's spawn reaches the seam.</summary>
        internal ISpawnedEntity SpawnCrate(float x, string assetName = "Crate", RuntimeReplication? seam = null)
        {
            var request = new AssetSpawnRequest(new FakePrefab("assets/world.bundle", assetName), new TransformState(new Vec3(x, 1, 0), Quat.Identity, new Vec3(1, 1, 1)));
            var spawned = Assets.Spawn(request).Value!;
            (seam ?? Seam()).RecordAssetSpawn(spawned, request);
            return spawned;
        }
    }

    /// <summary>A listen host and its players, each with its own scene and clock reading, over one loopback network.</summary>
    internal sealed class WorldRig
    {
        internal WorldRig()
        {
            Network = new LoopbackNetPair();
            Host = new WorldPeer(Network.Host, Clock, "host");
            Host.Runtime.Connect();
        }

        internal ManualClock Clock { get; } = new ManualClock();

        internal LoopbackNetPair Network { get; }

        internal WorldPeer Host { get; }

        internal List<WorldPeer> Players { get; } = new List<WorldPeer>();

        /// <summary>Gives the host and every later player the same scene: what each loads from the same scene asset.</summary>
        internal Action<FakeWorldScene>? BuildScene { get; set; }

        /// <summary>Loads <paramref name="build"/>'s scene for the host now, and for every player added later.</summary>
        internal void UseScene(Action<FakeWorldScene> build)
        {
            BuildScene = build ?? throw new ArgumentNullException(nameof(build));
            build(Host.Scene);
            Host.LoadScenes();
        }

        internal WorldPeer AddPlayer(string id)
        {
            var player = new WorldPeer(Network.AddPlayer(id), Clock, id) { Phase = SessionPhase.Idle };
            BuildScene?.Invoke(player.Scene);
            player.LoadScenes();
            player.Runtime.Connect();
            Players.Add(player);
            return player;
        }

        /// <summary>Opens the host's running session to players and runs until it hosts.</summary>
        internal void OpenToMultiplayer()
        {
            var opened = Host.Runtime.OpenToMultiplayer(new InternalHostSettings("World", 4));
            if (!opened.Succeeded) throw new InvalidOperationException("Network world test failed: hosting starts (" + opened + ")");
            Frames(2);
        }

        internal void Join(WorldPeer player) => Network.Join(player.Control);

        /// <summary>Runs one frame on every peer, host first, a frame of 16 ms apart.</summary>
        internal void Frame()
        {
            Host.Frame();
            foreach (var player in Players) player.Frame();
            Clock.Advance(16_000);
        }

        internal void Frames(int count)
        {
            for (var index = 0; index < count; index++) Frame();
        }

        /// <summary>Runs frames until <paramref name="condition"/> holds, at most <paramref name="maximum"/>.</summary>
        internal bool Run(Func<bool> condition, int maximum = 600)
        {
            for (var index = 0; index < maximum; index++)
            {
                if (condition()) return true;
                Frame();
            }

            return condition();
        }

        /// <summary>Joins a player and runs until its world is in place.</summary>
        internal void JoinAndSynchronize(WorldPeer player)
        {
            Join(player);
            if (!Run(() => player.World.Progress.Phase == NetworkWorldPhase.Synchronized && !player.World.IsHoldingReadiness))
            {
                throw new InvalidOperationException("Network world test failed: " + player.Name + " synchronizes (" + player.World.Progress + "; " + Describe(player) + ")");
            }
        }

        /// <summary>Describes where a player's world and its host's stand, for a failure message.</summary>
        internal string Describe(WorldPeer player)
        {
            var host = Host.World.Counters;
            var mine = player.World.Counters;
            return "host active " + Host.Host.IsActive + ", requests " + host.Requests + "/" + host.RequestsRefused + ", baselines " + host.BaselinesStarted + "/" +
                   host.BaselinesCompleted + ", host channel handled " + Host.World.Channel.Handled + " discarded " + Host.World.Channel.Discarded + " sent " +
                   Host.World.Channel.Sent + " refused " + Host.World.Channel.Refused + "; player role " + player.Control.Role + ", active " + player.Client.IsActive +
                   ", requests sent " + mine.RequestsSent + ", baselines " + mine.BaselinesReceived + "/" + mine.BaselinesApplied + ", malformed " + mine.MalformedMessages +
                   ", handled " + player.World.Channel.Handled + " discarded " + player.World.Channel.Discarded + " sent " + player.World.Channel.Sent + " refused " +
                   player.World.Channel.Refused + ", holds " + player.Control.Holds;
        }
    }

    /// <summary>A replica factory that builds fake objects and records what it built and released.</summary>
    internal sealed class RecordingFactory : IInternalReplicaFactory
    {
        private readonly FakeWorldScene scene;

        internal RecordingFactory(FakeWorldScene scene)
        {
            this.scene = scene;
        }

        internal List<InternalSpawnDescriptor> Built { get; } = new List<InternalSpawnDescriptor>();

        internal List<IEntity> Released { get; } = new List<IEntity>();

        internal bool Refuse { get; set; }

        public OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor)
        {
            if (Refuse) return OperationResult<IEntity>.Failure(ModErrorCode.Unavailable, "refused");
            Built.Add(descriptor);
            var target = scene.ActiveScene ?? scene.AddScene("Replicas");
            var position = descriptor.Transform.Position;
            var built = scene.Add(target, descriptor.Kind, null, true, false, new NetVector3(position.X, position.Y, position.Z));
            return OperationResult<IEntity>.Success(built.Entity);
        }

        public void ReleaseReplica(IEntity replica)
        {
            Released.Add(replica);
            if (replica is FakeWorldEntity fake) fake.Object.Destroy();
        }
    }

    /// <summary>Assertions the network world suites share.</summary>
    internal static class WorldAssert
    {
        internal static void That(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Network world test failed: " + message);
        }

        internal static void Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Network world test failed: " + message + " (no " + typeof(TException).Name + ")");
        }

        /// <summary>Whether two positions agree within the position grid's largest error, twice for a round trip.</summary>
        internal static bool Near(NetVector3 left, NetVector3 right, double tolerance = 2.0 / 512.0) =>
            Math.Abs(left.X - right.X) <= tolerance && Math.Abs(left.Y - right.Y) <= tolerance && Math.Abs(left.Z - right.Z) <= tolerance;

        /// <summary>
        /// Counts bytes a warmed, non-inlined loop allocates, the least of three passes, so a loop the JIT is still
        /// compiling cannot count against it.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static long Allocated(Action frame, int frames = 2_000)
        {
            for (var index = 0; index < 256; index++) frame();
            var least = long.MaxValue;
            for (var pass = 0; pass < 3; pass++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < frames; index++) frame();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return least;
        }
    }
}
