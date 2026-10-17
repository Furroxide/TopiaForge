using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.WorldAssert;
using static TopiaForge.ModManager.Tests.WorldBaselineSyncTests;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The replica driver's timing: host samples feed each replica's interpolation buffer at their arrival, replicas move
    /// in LateUpdate before the mods and never in Update, playback runs a bounded delay behind the host and never
    /// backwards, and a replica built after its samples arrived moves as soon as it exists.
    /// </summary>
    internal static class ReplicaTransformDriverTests
    {
        internal static void Run()
        {
            ReplicasFollowTheHostABoundedDelayBehind();
            ReplicasMoveOnlyInLateUpdate();
            JitteredArrivalsNeverRunPlaybackBackwards();
            AReplicaBuiltLateMovesOnceItExists();
            TheDriverKeepsItsReplicasStraight();
        }

        private static void ReplicasFollowTheHostABoundedDelayBehind()
        {
            var rig = Yard(1);
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            var box = rig.Host.Scene.Scenes[0].Roots[0];
            var replica = player.Scene.Scenes[0].Roots[0];
            var lags = new List<double>();
            var previous = double.NegativeInfinity;
            var backwards = 0;
            for (var frame = 0; frame < 300; frame++)
            {
                // The host's box moves at 2 m/s along x.
                box.Position = new NetVector3(frame * 0.032f, 0, 0);
                rig.Frame();
                if (frame < 120) continue;
                var lag = (box.Position.X - replica.Position.X) / 2.0;
                lags.Add(lag);
                if (replica.Position.X < previous) backwards++;
                previous = replica.Position.X;
            }

            That(replica.Mark == box.Mark && box.Mark != 0, "the moving box is bound on both sides");
            That(backwards == 0, "the replica never moves backwards (" + backwards + " frames did)");
            That(lags.All(lag => lag >= 0.07 && lag <= 0.42),
                "once settled it plays a bounded delay behind the host, 80 to 350 ms and the frame (" + lags.Min().ToString("0.000") + " to " + lags.Max().ToString("0.000") + " s)");
        }

        private static void ReplicasMoveOnlyInLateUpdate()
        {
            var rig = Yard(1);
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            var box = rig.Host.Scene.Scenes[0].Roots[0];
            var replica = player.Scene.Scenes[0].Roots[0];
            for (var frame = 0; frame < 120; frame++)
            {
                box.Position = new NetVector3(frame * 0.05f, 0, 0);
                rig.Host.Frame();
                var before = replica.Writes;
                player.Runtime.BeforeModsUpdate(0.016f);
                player.Runtime.AfterModsUpdate(0.016f);
                // Binding places the replica once, before the mods' Update; from then only LateUpdate moves it.
                if (frame > 20) That(replica.Writes == before, "frame " + frame + ": nothing moves the replica in Update");
                player.Runtime.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));
                rig.Clock.Advance(16_000);
                if (frame > 20) That(replica.Writes == before + 1, "frame " + frame + ": LateUpdate moves it once, before the mods");
            }
        }

        private static void JitteredArrivalsNeverRunPlaybackBackwards()
        {
            // Samples at 10 Hz from a body moving at 1 m/s, arriving 20 to 120 ms late, rendered every 16 ms.
            var driver = new ReplicaTransformDriver();
            var world = new FakeWorldScene();
            var target = world.Add(world.AddScene("S"), "Replica", null, rigidbody: true);
            var replica = new ClientWorldEntity(new NetworkEntityId(1)) { Object = target };
            driver.Track(replica);
            var random = new Random(3605);
            var arrivals = new List<(long At, long Sent)>();
            for (var sent = 0L; sent < 20_000_000; sent += 100_000) arrivals.Add((sent + 20_000 + random.Next(100_000), sent));
            arrivals.Sort((left, right) => left.At.CompareTo(right.At));
            var next = 0;
            var previous = double.NegativeInfinity;
            var extrapolated = 0;
            var frames = 0;
            for (var now = 0L; now < 20_000_000; now += 16_000)
            {
                while (next < arrivals.Count && arrivals[next].At <= now)
                {
                    var (_, sentAt) = arrivals[next++];
                    driver.Offer(replica, 5_000_000 + sentAt, new NetPose(new NetVector3(sentAt / 1_000_000f, 0, 0), NetQuaternion.Identity), now);
                }

                driver.Drive(now);
                if (replica.Buffer == null || replica.Buffer.Count == 0) continue;
                frames++;
                That(target.Position.X >= previous, "playback never runs backwards (" + target.Position.X + " after " + previous + ")");
                previous = target.Position.X;
                if (now > 3_000_000 && replica.Buffer.TrySample(now, out var probe) && probe.Mode != InterpolationMode.Interpolating) extrapolated++;
            }

            That(frames > 1000 && extrapolated < frames / 50, "after the first seconds nearly every frame interpolates (" + extrapolated + " of " + frames + " did not)");
        }

        private static void AReplicaBuiltLateMovesOnceItExists()
        {
            var rig = Yard(0);
            var crate = (FakeSpawnedEntity)rig.Host.SpawnCrate(0);
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            player.Assets.HoldLoads = true;
            rig.Join(player);
            for (var frame = 0; frame < 60; frame++)
            {
                crate.Object.Position = new NetVector3(frame * 0.05f, 1, 0);
                rig.Frame();
            }

            var mirrored = player.Client.Driver;
            That(player.Client.PendingBuilds == 1 && player.Assets.Spawned.Count == 0 && mirrored.Count == 1, "the replica is still loading, and its samples wait");
            player.Assets.CompleteLoads();
            rig.Frame();
            player.Assets.CompleteLoads();
            for (var frame = 60; frame < 90; frame++)
            {
                crate.Object.Position = new NetVector3(frame * 0.05f, 1, 0);
                rig.Frame();
            }

            var replica = player.Assets.Spawned.Single().Object;
            That(replica.Writes > 10 && replica.Position.X > 2.5f && replica.Position.X <= crate.Object.Position.X,
                "once built, the replica moves to the host's samples at once (" + replica.Position.X + ")");
        }

        private static void TheDriverKeepsItsReplicasStraight()
        {
            var driver = new ReplicaTransformDriver();
            var world = new FakeWorldScene();
            var scene = world.AddScene("S");
            var random = new Random(3606);
            var tracked = new List<ClientWorldEntity>();
            for (var round = 0; round < 3000; round++)
            {
                if (tracked.Count == 0 || random.Next(3) != 0)
                {
                    var replica = new ClientWorldEntity(new NetworkEntityId((uint)(round + 1))) { Object = random.Next(4) == 0 ? null : world.Add(scene, "R") };
                    driver.Track(replica);
                    driver.Track(replica);
                    tracked.Add(replica);
                }
                else
                {
                    var gone = tracked[random.Next(tracked.Count)];
                    driver.Untrack(gone);
                    driver.Untrack(gone);
                    tracked.Remove(gone);
                    That(gone.DriverIndex == -1, "an untracked replica forgets its place");
                }

                That(driver.Count == tracked.Count && tracked.All(replica => replica.DriverIndex >= 0 && replica.DriverIndex < driver.Count),
                    "round " + round + ": the driver tracks each replica once, at its own place");
            }

            foreach (var replica in tracked) driver.Offer(replica, 1_000_000, new NetPose(new NetVector3(1, 2, 3), NetQuaternion.Identity), 0);
            var dead = tracked.First(replica => replica.Object != null);
            ((FakeWorldObject)dead.Object!).Destroy();
            driver.Drive(10_000);
            That(driver.MovedLastFrame == tracked.Count(replica => replica.Object != null && replica.Object.IsAlive),
                "a frame moves every replica with an object and a sample, and skips the unbuilt and the destroyed (" + driver.MovedLastFrame + ")");
            driver.Clear();
            That(driver.Count == 0 && tracked.All(replica => replica.DriverIndex == -1 && (replica.Buffer == null || replica.Buffer.Count == 0)), "clearing forgets every replica and sample");
            Throws<ArgumentNullException>(() => driver.Track(null!), "a replica is required");
        }
    }
}
