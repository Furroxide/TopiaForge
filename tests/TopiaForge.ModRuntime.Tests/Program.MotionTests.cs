using System;
using System.IO;
using System.Linq;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestMotionAcrossRuntimes(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            const string entry = "TopiaForge.GravityGun.GravityGunMod";
            const string assembly = "TopiaForge.GravityGun.dll";
            var host = new WorldGame(NewFixture(Path.Combine(root, "motion-host"), "motion", entry, assembly), network.Host, clock);
            var first = new WorldGame(NewFixture(Path.Combine(root, "motion-first"), "motion", entry, assembly), network.AddPlayer("p1"), clock);
            var second = new WorldGame(NewFixture(Path.Combine(root, "motion-second"), "motion", entry, assembly), network.AddPlayer("p2"), clock);
            var games = new[] { host, first, second };
            try
            {
                foreach (var game in games) game.Start();
                host.LoadScene("Yard", true);
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Motion", 4)).Succeeded, "motion's host opens");
                WorldFrames(games, clock, 3);
                foreach (var player in new[] { first, second })
                {
                    Assert(player.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "motion's player joins");
                    Assert(WorldRunUntil(games, clock, () => player.World.Progress.Phase == NetworkWorldPhase.Synchronized, 120), "motion's baseline arrives");
                    player.LoadScene("Yard", false);
                }
                WorldFrames(games, clock, 8);
                foreach (var game in games) AimAtBox(game, 0);
                first.Game.Motion.Hold("grab", true);
                second.Game.Motion.Hold("grab", true);
                WorldFrames(games, clock, 12);
                Assert(first.World.Motion.LocalCount == 1 && second.World.Motion.LocalCount == 0 && second.Game.Toasts.Count == 1,
                    "the actual GravityGun controller grants the first grab and releases the denied second grab through real runtimes");
                first.Game.Motion.Snapshot = new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0.3f, 0, 1)));
                WorldFrames(games, clock, 20);
                Assert(MotionBox(host, 0).Position.X > 0.5f && Near(MotionBox(host, 0).Position, MotionBox(first, 0).Position), "GravityGun's actual FixedUpdate moves the canonical body");
                first.Game.Motion.Hold("throw", true);
                WorldFrames(games, clock, 6);
                Assert(first.World.Motion.LocalCount == 1, "the controller's immediate Dispose after Throw preserves the tail");
                second.Game.Motion.Hold("grab", false);
                WorldFrames(games, clock, 2);
                second.Game.Motion.Hold("grab", true);
                WorldFrames(games, clock, 10);
                Assert(first.World.Motion.LocalCount == 0 && second.World.Motion.LocalCount == 1, "the controller catches a released body and retires the previous owner");
                Assert(first.Game.Toasts.Count == 0, "the thrower whose throw another player caught is shown no refusal");
                second.Game.Motion.Hold("throw", true);
                WorldFrames(games, clock, 75);
                Assert(second.World.Motion.LocalCount == 0 && !MotionBox(host, 0).Kinematic && MotionBox(host, 0).Velocity!.Value.LengthSquared > 1,
                    "the real controller's throw hands its linear velocity back to host physics after one second");
                first.Game.Motion.Hold("grab", false);
                first.Game.Motion.Hold("throw", false);
                AimAtBox(first, 1);
                WorldFrames(games, clock, 2);
                first.Game.Motion.Hold("grab", true);
                WorldFrames(games, clock, 10);
                Assert(MotionBox(host, 1).Kinematic, "the host yields physics to the joined controller");
                network.Leave(first.Control);
                WorldFrames(games, clock, 2);
                Assert(!MotionBox(host, 1).Kinematic, "a disconnected controller's body is reclaimed immediately");
                WorldFrames(games, clock, 200);
                var resting = MeasureWorldFrames(host);
                Assert(resting == 0, "real runtime motion history allocates zero at rest: " + resting);
                Console.WriteLine("GravityGun motion across three real ModRuntime instances passed; host at rest: 0 bytes over 10,000 frames.");
            }
            finally
            {
                foreach (var game in games) game.Runtime.UnloadAll();
            }
        }

        private static FakeWorldObject MotionBox(WorldGame game, int index) => game.Game.Scene.AllObjects().Where(item => item.Name == "Box" && item.IsAlive).ElementAt(index);

        private static void AimAtBox(WorldGame game, int index)
        {
            var box = MotionBox(game, index);
            game.Game.Motion.Hit = new PhysicsHit(box.Entity, new Vec3(box.Position.X, box.Position.Y, box.Position.Z), new Vec3(0, 1, 0), 3);
        }
    }
}
