using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        // A mod that only loads: presence needs nothing from a mod, and a quiet one keeps every game's log clean.
        private const string PresenceModEntry = "TopiaForge.ValidTestMod.RuntimeDependentMod";

        /// <summary>
        /// Presence (WS3.10) in the real runtime: two games, then three, over the loopback network, each over a fake engine.
        /// Players see each other as avatars that move smoothly where the players walk; a third player sees both others; a
        /// lost link keeps a player's avatar through its grace; a speed hack is corrected and then kicked; and a player that
        /// leaves takes its avatar with it. Every game reads one clock the test moves, 16 ms a frame.
        /// </summary>
        private static void TestPresenceAcrossRuntimes(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new PresenceGame(NewFixture(Path.Combine(root, "presence-host"), "presence", PresenceModEntry), network.Host, clock, "Bolt");
            var first = new PresenceGame(NewFixture(Path.Combine(root, "presence-first"), "presence", PresenceModEntry), network.AddPlayer("p1"), clock, "Sprocket");
            var second = new PresenceGame(NewFixture(Path.Combine(root, "presence-second"), "presence", PresenceModEntry), network.AddPlayer("p2"), clock, "Widget");
            var games = new List<PresenceGame> { host, first };
            try
            {
                foreach (var game in new[] { host, first, second }) game.Start();
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Presence", 4)).Succeeded, "the host opens its game");
                PresenceFrames(games, clock, 3);
                Assert(first.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "the first player joins");
                PresenceFrames(games, clock, 12);

                // Two games: each sees the other where it stands, the joined player placed on the ring's first slot.
                Assert(first.Backend.Teleports.Count == 1 && NearPoint(first.Backend.Teleports[0].Feet, SpawnSlotPlanner.SlotOffset(0)),
                    "the player is placed on the ring around the host's spawn");
                var firstOnHost = host.AvatarOf(first);
                var hostOnFirst = first.AvatarOf(host);
                Assert(firstOnHost != null && hostOnFirst != null, "each game shows the other's player");
                Assert(NearPoint(firstOnHost!.Feet, first.Backend.Feet) && NearPoint(hostOnFirst!.Feet, host.Backend.Feet), "where it stands");

                // The player walks along +X at 4 m/s: the host's avatar of it follows smoothly, a little behind.
                var start = firstOnHost.Trail.Count;
                for (var frame = 0; frame < 120; frame++)
                {
                    first.Step(0.064f);
                    PresenceFrames(games, clock, 1);
                }

                var steps = firstOnHost.Trail.Skip(start + 20).Zip(firstOnHost.Trail.Skip(start + 21), (before, after) => after.X - before.X).ToList();
                Assert(steps.Count > 60 && steps.All(step => step >= -0.0005f && step <= 0.13f), "the avatar moves smoothly (" + string.Join(", ", steps.Take(12)) + ")");
                var lag = first.Backend.Feet.X - firstOnHost.Feet.X;
                Assert(lag > 0.1f && lag < 1f, "a little behind the player, as interpolation keeps it (" + lag + " m)");
                Assert(PresenceRunUntil(games, clock, () => NearPoint(firstOnHost.Feet, first.Backend.Feet), 60), "and comes to rest where the player stops");

                // A third game joins: it sees both others, and both see it.
                games.Add(second);
                Assert(second.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "the second player joins");
                PresenceFrames(games, clock, 12);
                Assert(second.Backend.Teleports.Count == 1 && NearPoint(second.Backend.Teleports[0].Feet, SpawnSlotPlanner.SlotOffset(1)), "on the ring's next slot");
                foreach (var viewer in games)
                {
                    foreach (var other in games)
                    {
                        if (ReferenceEquals(viewer, other)) continue;
                        Assert(viewer.AvatarOf(other) != null && NearPoint(viewer.AvatarOf(other)!.Feet, other.Backend.Feet), viewer.Name + " sees " + other.Name + " where it stands");
                    }
                }

                Assert(second.Presence.TryGetPose(first.Control.Id, out var pose) && Math.Abs(pose.Position.X - first.Backend.Feet.X) < 0.01f, "and the session's lookups know every player's pose");

                // The second player's link goes: its avatar stays through the grace, and follows it again once it is back.
                var secondOnHost = host.AvatarOf(second)!;
                var secondOnFirst = first.AvatarOf(second)!;
                network.LoseLink(second.Control);
                PresenceFrames(games, clock, 60);
                Assert(!secondOnHost.Destroyed && !secondOnFirst.Destroyed && host.Presence.ValidatedPosition(second.Control.Id) == null,
                    "a lost link keeps the avatar through its grace, and the host vouches for no position meanwhile");
                second.Step(3f);
                network.Restore(second.Control);
                PresenceFrames(games, clock, 30);
                Assert(NearPoint(secondOnHost.Feet, second.Backend.Feet) && NearPoint(secondOnFirst.Feet, second.Backend.Feet) && host.Presence.Counters.Corrections == 0,
                    "back on its link, its avatar goes where it is, and nothing it did meanwhile is taken for a cheat");

                // The first player speeds at twice the limit: corrected, each correction moving it back, then kicked.
                var placed = first.Backend.Teleports.Count;
                var frames = 0;
                while (network.Kicks.Count == 0 && frames++ < 600)
                {
                    first.Step(0.96f);
                    PresenceFrames(games, clock, 1);
                }

                Assert(network.Kicks.Count == 1 && network.Kicks[0].Player.Equals(first.Control.Id) && network.Kicks[0].Message == PresenceHost.KickMessage,
                    "the host removes the speeding player (" + frames + " frames)");
                Assert(host.Presence.Counters.Corrections == 4 && first.Backend.Teleports.Skip(placed).Count(teleport => teleport.KeepFacing) == 4,
                    "after four corrections, each of which moved it back");
                PresenceFrames(games, clock, 3);
                Assert(first.Runtime.Multiplayer.Realm == NetRealm.Offline && host.AvatarOf(first) == null && second.AvatarOf(first) == null,
                    "the removed player is back in single player, and its avatar is gone everywhere");
                games.Remove(first);

                // The second player leaves: its avatar goes, and the host plays on alone.
                Assert(second.Runtime.Multiplayer.Leave().Succeeded, "the second player leaves");
                PresenceFrames(games, clock, 3);
                Assert(secondOnHost.Destroyed && host.Backend.Alive().Count == 0 && second.Backend.Alive().Count == 0, "a player that leaves takes its avatar with it");
                foreach (var game in new[] { host, first, second })
                {
                    Assert(game.Fixture.Logger.Errors.Count == 0, game.Name + " logged no error (" + string.Join(" | ", game.Fixture.Logger.Errors) + ")");
                }
            }
            finally
            {
                foreach (var game in new[] { host, first, second }) game.Runtime.UnloadAll();
            }
        }

        /// <summary>
        /// Presence costs nothing at rest in the real runtime: zero bytes over thousands of frames with no provider, with a solo
        /// one, and online with a host and a player at rest while the clock runs and their heartbeats travel. With a host and
        /// three players moving, the frame's cost is measured and printed.
        /// </summary>
        private static void TestPresenceCostsNothingAtRest(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new PresenceGame(NewFixture(Path.Combine(root, "presence-cost-host"), "presence", PresenceModEntry), network.Host, clock, "Bolt");
            var players = Enumerable.Range(1, 3)
                .Select(index => new PresenceGame(NewFixture(Path.Combine(root, "presence-cost-" + index), "presence", PresenceModEntry), network.AddPlayer("c" + index), clock, "Unit " + index))
                .ToList();
            var all = new List<PresenceGame> { host };
            all.AddRange(players);
            try
            {
                foreach (var game in all) game.Backend.RecordTrails = false;
                host.Runtime.Load(new[] { host.Fixture.Package });
                var offline = MeasurePresenceFrames(new[] { host }, clock, 10_000);
                Assert(offline == 0, "an offline frame allocates nothing (" + offline + " bytes over 10,000 frames)");
                Assert(host.Runtime.Multiplayer.Attach(host.Control), "a solo provider attaches");
                var solo = MeasurePresenceFrames(new[] { host }, clock, 10_000);
                Assert(solo == 0, "and neither does a solo one (" + solo + " bytes over 10,000 frames)");

                foreach (var player in players) player.Start();
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Presence", 4)).Succeeded, "the host opens its game");
                PresenceFrames(all, clock, 3);
                Assert(players[0].Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "a player joins");
                var pair = new[] { host, players[0] };
                PresenceFrames(pair, clock, 120);
                Assert(host.AvatarOf(players[0]) != null && players[0].AvatarOf(host) != null, "the two see each other");
                var heartbeats = players[0].Presence.Counters.PosesSent;
                var online = MeasurePresenceFrames(pair, clock, 10_000);
                Assert(players[0].Presence.Counters.PosesSent > heartbeats + 100, "their heartbeats went on throughout");
                Assert(online == 0, "a host and a player online at rest allocate nothing, the clock running (" + online + " bytes over 10,000 frames)");

                // A host and three players moving: the cost of one frame of all four games, measured.
                for (var index = 1; index < players.Count; index++)
                {
                    Assert(players[index].Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "player " + (index + 1) + " joins");
                    PresenceFrames(all, clock, 12);
                }

                var angles = new double[all.Count];
                void Walk()
                {
                    for (var index = 0; index < all.Count; index++)
                    {
                        angles[index] += 0.08 / 4.0;
                        all[index].Backend.Feet = new NetVector3((float)(index * 10 + 4 * Math.Cos(angles[index])), 0f, (float)(4 * Math.Sin(angles[index])));
                    }
                }

                for (var frame = 0; frame < 120; frame++)
                {
                    Walk();
                    PresenceFrames(all, clock, 1);
                }

                Assert(all.All(viewer => all.All(other => ReferenceEquals(viewer, other) || viewer.AvatarOf(other) != null)), "all four see each other");
                var bytes = MeasurePresenceFrames(all, clock, 2_000, Walk);
                var watch = Stopwatch.StartNew();
                for (var frame = 0; frame < 2_000; frame++)
                {
                    Walk();
                    PresenceFrames(all, clock, 1);
                }

                watch.Stop();
                var perFrame = watch.Elapsed.TotalMilliseconds * 1000 / 2_000;
                Console.WriteLine("ModRuntime presence cost: a host and three players moving, " + (bytes / 2_000.0).ToString("F1", CultureInfo.InvariantCulture) + " bytes and " +
                                  perFrame.ToString("F1", CultureInfo.InvariantCulture) + " us a frame for all four games together, the runtime's own frame included");
                Assert(bytes == 0, "four games moving allocate nothing once every avatar exists (" + bytes + " bytes over 2,000 frames)");
                Assert(host.Presence.Counters.Corrections == 0 && network.Kicks.Count == 0, "and nobody walking is taken for a cheat");
            }
            finally
            {
                foreach (var game in all) game.Runtime.UnloadAll();
            }
        }

        private static bool NearPoint(NetVector3 left, NetVector3 right) =>
            Math.Abs(left.X - right.X) <= 0.01f && Math.Abs(left.Y - right.Y) <= 0.01f && Math.Abs(left.Z - right.Z) <= 0.01f;

        private static void PresenceFrames(IEnumerable<PresenceGame> games, WorldTestClock clock, int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                foreach (var game in games) game.Frame();
                clock.Now += 16_000;
            }
        }

        private static bool PresenceRunUntil(IEnumerable<PresenceGame> games, WorldTestClock clock, Func<bool> condition, int maximum)
        {
            for (var frame = 0; frame < maximum; frame++)
            {
                if (condition()) return true;
                PresenceFrames(games, clock, 1);
            }

            return condition();
        }

        /// <summary>Counts what <paramref name="frames"/> frames of every game allocate, the fewest bytes of three passes after one to warm.</summary>
        private static long MeasurePresenceFrames(IReadOnlyList<PresenceGame> games, WorldTestClock clock, int frames, Action? before = null)
        {
            PresenceFramePass(games, clock, frames, before);
            var allocated = long.MaxValue;
            for (var pass = 0; pass < 3 && allocated != 0; pass++) allocated = Math.Min(allocated, PresenceFramePass(games, clock, frames, before));
            return allocated;
        }

        /// <summary>
        /// Runs frames with a zero frame time, which keeps the runtime's once-a-second capability refresh out, while the clock
        /// presence reads moves 16 ms a frame.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long PresenceFramePass(IReadOnlyList<PresenceGame> games, WorldTestClock clock, int frames, Action? before)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var frame = 0; frame < frames; frame++)
            {
                before?.Invoke();
                for (var index = 0; index < games.Count; index++)
                {
                    games[index].Runtime.DispatchUpdate(0f);
                    games[index].Game.RaiseLateUpdate();
                }

                clock.Now += 16_000;
            }

            return GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }

        /// <summary>One game for presence: a fixture's runtime over a gameplay host whose engine side is a fake, and its loopback control.</summary>
        private sealed class PresenceGame
        {
            internal PresenceGame(Fixture fixture, LoopbackControl control, WorldTestClock clock, string displayName)
            {
                Fixture = fixture;
                Control = control;
                Name = displayName;
                control.DisplayName = displayName;
                Game = new PresenceGameHost();
                Runtime = new RuntimeUnderTest(
                    fixture.Paths,
                    fixture.Logger,
                    new ManifestValidationContext("0.0.2309", "1.0.0", "1.0.0", platform: "windows", architecture: "x64",
                        contentTargets: new[] { "code", "standalonewindows64" }, enforceRuntimeCompatibility: true),
                    fixture.Observer,
                    Game);
                Runtime.NetworkClock = clock.Read;

                // No gamemode runs here, so the test names the world session and its spawn as the session orchestrator would.
                Runtime.Presence.RunningWorld = () => "world-one";
                Runtime.Presence.RunningSpawn = () => new TransformState(new Vec3(0f, 0f, 0f), Quat.Identity, new Vec3(1f, 1f, 1f));
            }

            internal Fixture Fixture { get; }

            internal LoopbackControl Control { get; }

            internal string Name { get; }

            internal PresenceGameHost Game { get; }

            internal RuntimeUnderTest Runtime { get; }

            internal MultiplayerPresence Presence => Runtime.Presence;

            internal FakePresenceBackend Backend => Game.Backend;

            internal void Start()
            {
                Runtime.Load(new[] { Fixture.Package });
                Assert(Runtime.Multiplayer.Attach(Control), "the loopback provider attaches");
            }

            internal void Step(float x) => Backend.Feet = new NetVector3(Backend.Feet.X + x, Backend.Feet.Y, Backend.Feet.Z);

            internal FakeAvatar? AvatarOf(PresenceGame other)
            {
                if (!Presence.TryGetPeer(other.Control.Id, out var peer) || peer?.Avatar == null || !peer.Avatar.IsAlive) return null;
                return Backend.Avatars.FirstOrDefault(avatar => avatar.IsAlive && ReferenceEquals(avatar.Entity, peer.Avatar.Entity));
            }

            internal void Frame()
            {
                Runtime.DispatchUpdate(0.016f);
                Game.RaiseLateUpdate();
            }
        }

        /// <summary>A gameplay host whose only engine side is presence's fake: no assets, entities or scenes beyond it.</summary>
        private sealed class PresenceGameHost : IRuntimeGameplayHost, IMultiplayerPlatform, IPresenceBackendSource
        {
            public event Action<GameTimeSample>? FixedUpdate { add { } remove { } }

            public event Action<GameTimeSample>? LateUpdate;

            internal FakePresenceBackend Backend { get; } = new FakePresenceBackend();

            public IPresenceBackend PresenceBackend => Backend;

            public bool RunInBackground { get; set; }

            public GameplayContextServices Create(
                string ownerModId,
                string packagePath,
                string dataPath,
                IModLifetime lifetime,
                IModLogger logger,
                ModNetProfile profile,
                NativeTransitionAccessSlot? transitionAccess = null) => GameplayContextServices.Unavailable(lifetime);

            public GameTimeSample BeginFrame(float deltaTime) => new GameTimeSample(GameLoopPhase.Frame, deltaTime, deltaTime, 0d, 0);

            internal void RaiseLateUpdate() => LateUpdate?.Invoke(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));

            public void Dispose()
            {
            }
        }
    }
}
