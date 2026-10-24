using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using RuntimeUnderTest = TopiaForge.ModManager.ModRuntime;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private const string WorldModEntry = "TopiaForge.ValidTestMod.RuntimeWorldSpawningMod";

        /// <summary>
        /// The network world in the real runtime: three games over the loopback network, each loading the same mod. The
        /// host's scene objects and its mod's spawns appear on a joined player and move there under interpolation, a late
        /// joiner gets all of it in its baseline before it has even loaded the scene, and after leaving nothing native stays
        /// suppressed. Every game reads one clock the test moves, 16 ms a frame.
        /// </summary>
        private static void TestNetworkWorldAcrossRuntimes(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new WorldGame(NewFixture(Path.Combine(root, "world-host"), "network-world", WorldModEntry), network.Host, clock);
            var player = new WorldGame(NewFixture(Path.Combine(root, "world-player"), "network-world", WorldModEntry), network.AddPlayer("p1"), clock);
            var late = new WorldGame(NewFixture(Path.Combine(root, "world-late"), "network-world", WorldModEntry), network.AddPlayer("p2"), clock);
            var games = new[] { host, player, late };
            var trace = Path.Combine(root, "network-world-trace.txt");
            Environment.SetEnvironmentVariable("TOPIAFORGE_RUNTIME_TEST_TRACE", trace);
            host.MarkSpawner();
            try
            {
                foreach (var game in games) game.Start();
                AssertTrace(trace, "world-spawner", "world-watcher", "world-watcher");

                // Single player: the host loads the yard and its mod spawns a crate there, recorded for a later promotion.
                var hostYard = host.LoadScene("Yard", true);
                Assert(host.PackageAssets.Spawned.Count == 1 && host.Runtime.Multiplayer.Spawns.Live().Count == 1, "the host's mod spawns a crate offline, and it is recorded");
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("World", 4)).Succeeded, "the host opens its game");
                WorldFrames(games, clock, 3);
                Assert(host.Runtime.Multiplayer.Realm == NetRealm.ListenHost && host.World.Progress.Phase == NetworkWorldPhase.Hosting &&
                       host.PackageAssets.Spawned[0].Object.Mark != 0, "the host hosts, and its earlier spawn is a network entity");

                // A player joins from its menu, and loads the same yard as session follow will have it do.
                var natives = NativeSwitches(player.Game.Scene, () => player.BuildYard());
                Assert(player.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "the player joins");
                WorldFrames(games, clock, 1);
                var playerYard = player.LoadScene("Yard", false);
                Assert(WorldRunUntil(games, clock, () => player.World.Progress.Phase == NetworkWorldPhase.Synchronized && !player.World.IsHoldingReadiness, 120),
                    "the player synchronizes (" + player.World.Progress + ")");
                var replica = player.PackageAssets.Spawned.Single();
                var crate = host.PackageAssets.Spawned[0];
                Assert(replica.Object.Mark == crate.Object.Mark && replica.Object.Kinematic && Near(replica.Object.Position, crate.Object.Position),
                    "the host's crate has a kinematic replica on the player, built through the player's own copy of the mod, where the host's is");
                Assert(natives.All(item => item.Value == item.Switch.SuppressedValue), "the player's yard is the host's to move: its bodies kinematic, its robot's systems off");

                // Hosting: the host's mod spawns as another scene loads, and a box is pushed along.
                host.LoadScene("Annex", true);
                var box = hostYard.Roots[0];
                var mine = playerYard.Roots[0];
                var trail = new List<float>();
                for (var frame = 1; frame <= 90; frame++)
                {
                    box.Position = new NetVector3(frame * 0.05f, 0f, 0f);
                    WorldFrames(games, clock, 1);
                    trail.Add(mine.Position.X);
                }

                Assert(mine.Mark == box.Mark && mine.Mark != 0, "the box was bound to a network entity when it first moved");
                var steps = trail.Skip(45).Zip(trail.Skip(46), (before, after) => after - before).ToList();
                Assert(trail.Last() < box.Position.X - 0.2f && trail.Last() > box.Position.X - 1.5f,
                    "under interpolation the player's box trails the host's a little (" + trail.Last() + " / " + box.Position.X + ")");
                Assert(steps.All(step => step >= 0f && step < 0.09f), "and moves smoothly, a frame's motion at a time (" + string.Join(", ", steps) + ")");
                Assert(WorldRunUntil(games, clock, () => Near(mine.Position, box.Position), 60), "then comes to rest where the host's does");
                Assert(player.PackageAssets.Spawned.Count == 2, "the crate the host's mod spawned while hosting has a replica too");

                // A late joiner whose yard loads only after the host's world has arrived.
                Assert(late.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "a late player joins");
                Assert(WorldRunUntil(games, clock, () => late.World.Progress.Phase == NetworkWorldPhase.Synchronized && !late.World.IsHoldingReadiness, 120),
                    "the late joiner synchronizes (" + late.World.Progress + ")");
                var lateNatives = NativeSwitches(late.Game.Scene, () => late.BuildYard());
                var lateYard = late.LoadScene("Yard", false);
                WorldFrames(games, clock, 5);
                Assert(WorldIds(late).SequenceEqual(WorldIds(host)) && late.PackageAssets.Spawned.Count == 2, "the late joiner mirrors the host's entities, both crates included");
                Assert(lateYard.Roots[0].Mark == box.Mark && Near(lateYard.Roots[0].Position, box.Position) &&
                       lateYard.Roots.Skip(1).Where(item => item.Name == "Box").All(item => item.Mark == 0),
                    "and the box the host moved stands where the host's does, bound, while the others are as the scene put them");
                Assert(lateNatives.All(item => item.Value == item.Switch.SuppressedValue), "its yard is the host's to move too");

                // Leaving, and shutting down while joined: nothing native stays suppressed, and every replica goes.
                Assert(player.Runtime.Multiplayer.Leave().Succeeded, "the player leaves");
                WorldFrames(games, clock, 2);
                Assert(player.Runtime.Multiplayer.Realm == NetRealm.Offline && natives.All(item => item.Value == item.Before),
                    "after leaving, every native setting on the player is back as it was");
                Assert(player.PackageAssets.Spawned.All(item => !item.IsAlive) && playerYard.Roots.Where(item => item.IsAlive).All(item => item.Mark == 0),
                    "its replicas are gone and its scene objects unmarked");
                late.Runtime.UnloadAll();
                network.Leave(late.Control);
                Assert(lateNatives.All(item => item.Value == item.Before) && late.PackageAssets.Spawned.All(item => !item.IsAlive),
                    "a player that shuts down while joined restores everything too");

                Assert(host.Runtime.Multiplayer.Leave().Succeeded, "the host stops hosting");
                WorldFrames(new[] { host, player }, clock, 2);
                Assert(host.Runtime.Multiplayer.Realm == NetRealm.Offline && hostYard.Roots.All(item => item.Mark == 0) && host.PackageAssets.Spawned.All(item => item.Object.Mark == 0),
                    "and the host plays on alone, nothing marked");
                foreach (var game in games)
                {
                    Assert(game.Fixture.Logger.Errors.Count == 0, "no game logged an error (" + string.Join(" | ", game.Fixture.Logger.Errors) + ")");
                }
            }
            finally
            {
                foreach (var game in games) game.Runtime.UnloadAll();
            }
        }

        /// <summary>
        /// The real runtime keys each scene inside its load, offline as much as online, and its frames pay nothing for the
        /// keys: zero bytes over 10,000 frames with a keyed scene, with no provider and with a solo one.
        /// </summary>
        private static void TestSceneKeysCostOnlyTheLoad(string root)
        {
            var network = new LoopbackNetPair();
            var game = new WorldGame(NewFixture(Path.Combine(root, "world-offline"), "network-world", WorldModEntry), network.Host, new WorldTestClock());
            try
            {
                game.Runtime.Load(new[] { game.Fixture.Package });
                var yard = game.LoadScene("Yard", true);
                Assert(game.World.SceneKeys.TryGet(yard.Handle, out var keys) && keys.Count == 9 && game.World.SceneKeys.Captures == 1 && !game.World.IsOnline,
                    "the yard is keyed as it loads, offline, without a provider (" + keys.Count + " objects)");
                var offline = MeasureWorldFrames(game);
                Assert(offline == 0, "an offline frame with a keyed scene allocates nothing (" + offline + " bytes over 10,000 frames)");
                Assert(game.Runtime.Multiplayer.Attach(network.Host), "a solo provider attaches");
                var solo = MeasureWorldFrames(game);
                Assert(solo == 0, "and so does a solo frame (" + solo + " bytes over 10,000 frames)");
                Assert(game.Runtime.DispatchSceneUnloaded(yard.Handle, "Yard", true, SceneLoadMode.Additive) && !game.World.SceneKeys.TryGet(yard.Handle, out _),
                    "unloading the yard forgets its keys");
            }
            finally
            {
                game.Runtime.UnloadAll();
            }
        }

        /// <summary>Counts what 10,000 frames allocate, the fewest bytes of up to three passes after one to warm, as the other runtime checks do.</summary>
        private static long MeasureWorldFrames(WorldGame game)
        {
            WorldFramePass(game, 10_000);
            var allocated = long.MaxValue;
            for (var pass = 0; pass < 3 && allocated != 0; pass++) allocated = Math.Min(allocated, WorldFramePass(game, 10_000));
            return allocated;
        }

        /// <summary>Runs frames with a zero frame time, which keeps the runtime's once-a-second capability refresh out.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long WorldFramePass(WorldGame game, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++)
            {
                game.Runtime.DispatchUpdate(0f);
                game.Game.RaiseLateUpdate();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private static List<NativeSwitchState> NativeSwitches(FakeWorldScene scene, Action build)
        {
            build();
            var all = new List<NativeSwitchState>();
            foreach (var item in scene.AllObjects())
            {
                if (item.HasRigidbody && item.Name != "Crate") all.Add(new NativeSwitchState(item.KinematicSwitch));
                foreach (var native in item.Natives) all.Add(new NativeSwitchState(native));
            }

            return all;
        }

        private static uint[] WorldIds(WorldGame game)
        {
            var ids = new List<uint>();
            if (game.World.Host.IsActive) game.World.Host.CollectIds(ids);
            else game.World.Client.CollectIds(ids);
            return ids.ToArray();
        }

        private static bool Near(NetVector3 left, NetVector3 right) =>
            Math.Abs(left.X - right.X) <= 0.01f && Math.Abs(left.Y - right.Y) <= 0.01f && Math.Abs(left.Z - right.Z) <= 0.01f;

        private static void WorldFrames(IEnumerable<WorldGame> games, WorldTestClock clock, int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                foreach (var game in games) game.Frame();
                clock.Now += 16_000;
            }
        }

        private static bool WorldRunUntil(IEnumerable<WorldGame> games, WorldTestClock clock, Func<bool> condition, int maximum)
        {
            for (var frame = 0; frame < maximum; frame++)
            {
                if (condition()) return true;
                WorldFrames(games, clock, 1);
            }

            return condition();
        }

        /// <summary>One native setting as a test saw it before the game joined.</summary>
        private sealed class NativeSwitchState
        {
            internal NativeSwitchState(FakeSwitch native)
            {
                Switch = native;
                Before = native.Value;
            }

            internal FakeSwitch Switch { get; }

            internal bool Before { get; }

            internal bool Value => Switch.Value;
        }

        /// <summary>The clock every game's network world reads, in microseconds.</summary>
        private sealed class WorldTestClock
        {
            internal long Now { get; set; } = 1_000_000;

            internal long Read() => Now;
        }

        /// <summary>One game: a fixture's runtime over a gameplay host with a fake engine, and its loopback control.</summary>
        private sealed class WorldGame
        {
            internal WorldGame(Fixture fixture, LoopbackControl control, WorldTestClock clock)
            {
                Fixture = fixture;
                Control = control;
                Game = new WorldGameHost();
                Runtime = new RuntimeUnderTest(
                    fixture.Paths,
                    fixture.Logger,
                    new ManifestValidationContext("0.0.2309", "1.0.0", "1.0.0", platform: "windows", architecture: "x64",
                        contentTargets: new[] { "code", "standalonewindows64" }, enforceRuntimeCompatibility: true),
                    fixture.Observer,
                    Game);
                Runtime.NetworkClock = clock.Read;
            }

            internal Fixture Fixture { get; }

            internal LoopbackControl Control { get; }

            internal WorldGameHost Game { get; }

            internal RuntimeUnderTest Runtime { get; }

            internal NetworkWorld World => Runtime.NetworkWorld;

            /// <summary>Gets the asset service the gameplay host made for the mod's own package context.</summary>
            internal FakeWorldAssets PackageAssets => Game.Assets.First(item => item.Owner == Fixture.Manifest.Id && !item.Scoped).Assets;

            /// <summary>Gives this game's mod the data file that makes it spawn.</summary>
            internal void MarkSpawner()
            {
                var data = Fixture.Paths.GetDataPath(Fixture.Manifest.Id);
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "spawn-crates.txt"), "yes");
            }

            internal void Start()
            {
                Runtime.Load(new[] { Fixture.Package });
                Assert(Runtime.Multiplayer.Attach(Control), "the loopback provider attaches");
            }

            /// <summary>Builds the yard every game loads from the same scene asset: eight loose boxes and a robot.</summary>
            internal FakeSceneData BuildYard()
            {
                var existing = Game.Scene.Scenes.FirstOrDefault(scene => scene.Name == "Yard");
                if (existing != null) return existing;
                var yard = Game.Scene.AddScene("Yard");
                for (var index = 0; index < 8; index++) Game.Scene.Add(yard, "Box", null, rigidbody: true, kinematic: false, position: new NetVector3(index * 2f, 0f, 3f));
                var robot = Game.Scene.Add(yard, "Robot", null, rigidbody: true, kinematic: false, position: new NetVector3(0f, 0f, 9f));
                robot.AddNative(NativeAuthorityKind.RobotBrain);
                robot.AddNative(NativeAuthorityKind.Locomotion);
                robot.AddNative(NativeAuthorityKind.Spawner);
                return yard;
            }

            /// <summary>Loads a scene, as the engine reports it to the runtime: the yard, or an empty one.</summary>
            internal FakeSceneData LoadScene(string name, bool active)
            {
                var scene = name == "Yard" ? BuildYard() : Game.Scene.AddScene(name);
                Assert(Runtime.DispatchSceneLoaded(scene.Handle, name, true, SceneLoadMode.Additive, active), "the runtime takes the scene load of " + name);
                return scene;
            }

            internal void Frame()
            {
                Runtime.DispatchUpdate(0.016f);
                Game.RaiseLateUpdate();
            }
        }

        /// <summary>
        /// A gameplay host over a fake engine: its scenes are the network world's engine side, each context's asset service
        /// loads fake bundles and spawns into them, as the Unity host's does, its entity service moves and destroys them, and
        /// its UI service records each toast.
        /// </summary>
        private sealed class WorldGameHost : IRuntimeGameplayHost, IMultiplayerPlatform, INetworkWorldBackendSource
        {
            public event Action<GameTimeSample>? FixedUpdate { add { } remove { } }

            public event Action<GameTimeSample>? LateUpdate;

            internal FakeWorldScene Scene { get; } = new FakeWorldScene();

            internal List<(string Owner, bool Scoped, FakeWorldAssets Assets)> Assets { get; } = new List<(string, bool, FakeWorldAssets)>();

            /// <summary>Gets the toasts each mod showed, as the kit would render them.</summary>
            internal List<(string Owner, string Message, UiTone Tone)> Toasts { get; } = new List<(string, string, UiTone)>();

            public bool RunInBackground { get; set; }

            public INetworkWorldBackend NetworkWorldBackend => Scene;

            /// <summary>Gets or sets what hears of each session scope's lifetime as its services are made.</summary>
            internal Action<IModLifetime>? ScopeCreated { get; set; }

            public GameplayContextServices Create(
                string ownerModId,
                string packagePath,
                string dataPath,
                IModLifetime lifetime,
                IModLogger logger,
                ModNetProfile profile,
                NativeTransitionAccessSlot? transitionAccess = null)
            {
                if (transitionAccess != null) ScopeCreated?.Invoke(lifetime);
                var assets = new FakeWorldAssets(Scene, lifetime);
                Assets.Add((ownerModId, transitionAccess != null, assets));
                var other = GameplayContextServices.Unavailable(lifetime);
                return new GameplayContextServices(other.Input, other.LocalPlayer, new WorldEntities(), other.Physics, other.Time, other.Scheduler,
                    other.Scenes, other.Interactions, other.Items, assets, other.Audio, new WorldToasts(this, ownerModId, other.Ui), other.UnityInterop, other.SceneTransitions);
            }

            public GameTimeSample BeginFrame(float deltaTime) => new GameTimeSample(GameLoopPhase.Frame, deltaTime, deltaTime, 0d, 0);

            internal void RaiseLateUpdate() => LateUpdate?.Invoke(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));

            public void Dispose()
            {
            }
        }
    }
}
