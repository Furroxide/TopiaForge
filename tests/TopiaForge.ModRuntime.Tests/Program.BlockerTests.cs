using System;
using System.IO;
using System.Linq;
using System.Threading;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestBlockersAcrossRuntimes(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            const string entry = "TopiaForge.ValidTestMod.RuntimeTimeConsumerMod";
            var host = new WorldGame(NewFixture(Path.Combine(root, "box-host"), "blockers", entry), network.Host, clock);
            var player = new WorldGame(NewFixture(Path.Combine(root, "box-player"), "blockers", entry), network.AddPlayer("p1"), clock);
            var late = new WorldGame(NewFixture(Path.Combine(root, "box-late"), "blockers", entry), network.AddPlayer("p2"), clock);
            var games = new[] { host, player, late };
            ModContextScope? scope = null;
            try
            {
                host.Start(); player.Start(); late.Start();
                var contexts = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "TopiaForge.ValidTestMod")
                    .SelectMany(assembly => (IModContext[])assembly.GetType(entry, true)!.GetMethod("TakeLoaded")!.Invoke(null, null)!).ToArray();
                Assert(contexts.Length == 3, "three runtime package contexts loaded");
                var parent = (ModContext)contexts[0];
                var creation = parent.CreateChildScopeAsync("blocker-session", CancellationToken.None, () => { },
                    new NativeTransitionAccessSlot("blocker-scope", "blocker-session", () => !parent.Lifetime.IsStopping),
                    host.Runtime.NativeDispatcher, ModExecutionContext.GamemodeServer);
                PumpBinding(host.Runtime.NativeDispatcher, creation);
                scope = creation.GetAwaiter().GetResult();
                var request = BlockerRequest.Box(new Vec3(0, 1, 0), new Vec3(2, 3, .5f), 25);
                var blocker = scope.Context.Physics.AddBlocker(request).Value!;
                Assert(blocker.IsAlive && host.Runtime.Multiplayer.Spawns.Live().Single().Description.Kind == BlockerDescriptor.Kind,
                    "server context's blocker is recorded offline through ModContext.Physics");
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("World", 4)).Succeeded, "open world with blocker");
                WorldFrames(games, clock, 3);
                Assert(player.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "first player joins blocker world");
                Assert(WorldRunUntil(games, clock, () => player.Game.Scene.Blockers.Count == 1 && !player.World.IsHoldingReadiness, 120),
                    "first player gets blocker baseline");
                var live = scope.Context.Physics.AddBlocker(BlockerRequest.Box(new Vec3(4, 1, 0), new Vec3(1, 2, 1), blocksSight: false)).Value!;
                Assert(WorldRunUntil(games, clock, () => player.Game.Scene.Blockers.Count == 2, 120), "live blocker announced after join");
                Assert(late.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "late player joins blocker world");
                Assert(WorldRunUntil(games, clock, () => late.Game.Scene.Blockers.Count == 2 && !late.World.IsHoldingReadiness, 120),
                    "late joiner gets both blockers without a bundle");
                foreach (var game in games)
                {
                    Assert(game.Game.Scene.Blockers.All(box => box.IsAlive && box.Object.HasRigidbody && box.Object.Kinematic), "all peers have kinematic blockers");
                    Assert(game.Game.Scene.Blockers[0].Size == blocker.Size && game.Game.Scene.Blockers[0].BlocksSight && !game.Game.Scene.Blockers[1].BlocksSight,
                        "every peer has matching geometry and sight flags");
                    Assert(game.PackageAssets.Spawned.Count == 0, "no prefab or renderer-bearing asset was spawned");
                }
                var moved = new TransformState(new Vec3(10, 1, 0), Quat.Identity, new Vec3(1, 1, 1));
                Assert(scope.Context.Entities.SetTransform(blocker, moved).Succeeded, "server moves blocker through context entity service");
                Assert(WorldRunUntil(games, clock, () => games.All(game => Near(game.Game.Scene.Blockers[0].Object.Position, new NetVector3(10, 1, 0))), 150),
                    "both players follow the moved blocker");
                BlockerFrames(games, clock, 300);
                var before = GC.GetAllocatedBytesForCurrentThread();
                BlockerFrames(games, clock, 500);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert(allocated == 0, "three settled runtimes allocate zero bytes over 500 frames (" + allocated + ")");
                PumpBinding(host.Runtime.NativeDispatcher, scope.CloseAsync());
                Assert(WorldRunUntil(games, clock, () => games.All(game => game.Game.Scene.Blockers.All(box => !box.IsAlive)), 120),
                    "ending session scope removes every blocker on all three runtimes");
                Assert(!blocker.IsAlive && !live.IsAlive && host.Runtime.Multiplayer.Spawns.Live().Count == 0, "session ledger is empty");
                foreach (var game in games) Assert(game.Fixture.Logger.Errors.Count == 0, "no runtime logged a blocker error");
                Console.WriteLine("Blocker integration passed: 3 runtimes, baseline, live spawn, late join, move, scope cleanup; 0 bytes / 500 settled frames.");
            }
            finally
            {
                if (scope != null) PumpBinding(host.Runtime.NativeDispatcher, scope.CloseAsync());
                foreach (var game in games) game.Runtime.UnloadAll();
            }
        }

        private static void BlockerFrames(WorldGame[] games, WorldTestClock clock, int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                // As in WorldFramePass, exclude the unrelated periodic capability refresh.
                // Network time still advances, so settled host snapshots and replicas keep running.
                foreach (var game in games)
                {
                    game.Runtime.DispatchUpdate(0f);
                    game.Game.RaiseFixedUpdate();
                    game.Game.RaiseLateUpdate();
                }
                clock.Now += 16_000;
            }
        }
    }
}
