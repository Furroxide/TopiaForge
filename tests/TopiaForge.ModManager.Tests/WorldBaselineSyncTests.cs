using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Late joiners and their baselines: the whole world arrives, what happens while it arrives is kept, a stream that
    /// restarts starts again from a baseline, readiness and the overlay follow the progress, and a player a broadcast
    /// skipped gets a new baseline.
    /// </summary>
    internal static class WorldBaselineSyncTests
    {
        internal const string RobotKind = "robotkit.robot";

        internal static void Run()
        {
            ALateJoinerGetsTheWholeWorld();
            TheWorldMayArriveBeforeItsScene();
            WhatHappensDuringABaselineIsKept();
            AGenerationChangeStartsAgainFromABaseline();
            ANewBaselineRebuildsNothingThePlayerHas();
            AReplacementStartsEveryPlayerAgain();
            ProgressFeedsTheHoldAndTheOverlay();
            ALoadThatNeverLandsGivesUp();
            APlayerABroadcastSkippedGetsANewBaseline();
            AHostWhoseProcessRestartedServesAnotherWorld();
        }

        private static void ALateJoinerGetsTheWholeWorld()
        {
            var rig = Yard(30);
            var host = rig.Host;
            var boxes = host.Scene.Scenes[0].Roots;

            // Single player, before the game opens: boxes 0 to 4 are pushed about and come to rest, and mods spawn.
            for (var index = 0; index < 5; index++) boxes[index].Position = new NetVector3(index, 3, 7);
            var crates = Enumerable.Range(0, 5).Select(index => host.SpawnCrate(100 + index)).ToList();
            var robots = Enumerable.Range(0, 3).Select(index => SpawnRobot(host, 200 + index, 64)).ToList();
            rig.OpenToMultiplayer();

            // Hosting: boxes 5 to 9 move, and boxes 20 and 21 are destroyed.
            for (var index = 5; index < 10; index++) boxes[index].Position = new NetVector3(index, 3, 7);
            boxes[20].Destroy();
            boxes[21].Destroy();
            rig.Frames(10);

            var player = AddPlayer(rig, "p1");
            var phases = new List<NetworkWorldPhase>();
            rig.Join(player);
            That(rig.Run(() =>
            {
                phases.Add(player.World.Progress.Phase);
                return player.World.Progress.Phase == NetworkWorldPhase.Synchronized && !player.World.IsHoldingReadiness;
            }), "the player synchronizes (" + player.World.Progress + ")");

            That(SameWorld(host, player), "the player mirrors exactly the host's entities (" + Ids(host) + " / " + Ids(player) + ")");
            var playerBoxes = player.Scene.Scenes[0].Roots;
            for (var index = 0; index < 5; index++)
            {
                That(playerBoxes[index].Mark == 0 && boxes[index].Mark == 0 && Near(playerBoxes[index].Position, new NetVector3(index, 3, 7)),
                    "box " + index + ", moved before the game opened, stands where the host's does without an entity");
            }

            for (var index = 5; index < 10; index++)
            {
                That(playerBoxes[index].Mark == boxes[index].Mark && boxes[index].Mark != 0 && Near(playerBoxes[index].Position, new NetVector3(index, 3, 7)),
                    "box " + index + ", moved while hosting, is bound to the host's entity and stands where the host's does");
            }

            That(Enumerable.Range(10, 20).Where(index => index != 20 && index != 21).All(index =>
                    playerBoxes[index].Mark == 0 && playerBoxes[index].Active && Near(playerBoxes[index].Position, new NetVector3(index * 2, 0, 0))),
                "boxes that never moved stay as the scene loaded them");
            That(!playerBoxes[20].Active && !playerBoxes[21].Active, "the boxes the host destroyed are hidden");
            That(player.World.Counters.ScenePosesPlaced == 23 && player.World.Counters.SceneKeysUnbound == 0,
                "the baseline placed every unbound box the promotion censused, and every key it named was found (" + player.World.Counters.ScenePosesPlaced + ")");
            That(player.Assets.Spawned.Count == 5 && player.Assets.Spawned.All(replica => replica.Object.Kinematic && replica.Object.Mark != 0),
                "every asset spawn has a kinematic, marked replica");
            That(player.Assets.BundleLoads == 1 && player.Assets.PrefabLoads == 1, "built from one shared bundle and prefab load");
            That(player.Factories.Built.Count == 3 && player.Factories.Built.All(descriptor => descriptor.Kind == RobotKind && descriptor.Payload.Length == 64),
                "every provider spawn is built by its kind's factory, with its description");
            // A world this small arrives within one frame; a larger one shows its baseline arriving (below).
            That(phases.Contains(NetworkWorldPhase.Requesting) && phases.Last() == NetworkWorldPhase.Synchronized,
                "the overlay saw the player ask and synchronize (" + string.Join(",", phases.Distinct()) + ")");
            That(player.Control.HoldsTaken >= 1 && player.Control.Holds == 0, "readiness was held for the world, and let go");

            // A box placed by the baseline binds when it next moves, from where the host had it.
            boxes[0].Position = new NetVector3(0, 3, 12);
            That(rig.Run(() => playerBoxes[0].Mark != 0 && Near(playerBoxes[0].Position, boxes[0].Position), 60),
                "a box moved before the game opened binds when it moves again, and follows the host's");
            That(SameWorld(host, player), "the player still mirrors exactly the host's entities");
            GC.KeepAlive(crates);
            GC.KeepAlive(robots);
        }

        /// <summary>
        /// A player that joins from its menu may have the host's world before it loads the scene: what the baseline named in
        /// that scene waits for it, and lands once it loads, as session follow (WS3.8) loads it.
        /// </summary>
        private static void TheWorldMayArriveBeforeItsScene()
        {
            var rig = Yard(12);
            var host = rig.Host;
            var boxes = host.Scene.Scenes[0].Roots;
            for (var index = 0; index < 3; index++) boxes[index].Position = new NetVector3(index, 4, 4);
            rig.OpenToMultiplayer();
            for (var index = 3; index < 6; index++) boxes[index].Position = new NetVector3(index, 5, 5);
            boxes[11].Destroy();
            rig.Frames(10);

            var yard = rig.BuildScene!;
            rig.BuildScene = null;
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            That(player.Client.EntityCount == 3 && Ids(player).All(id => player.Client.BindingOf(new NetworkEntityId(id)) == ClientEntityBinding.None),
                "the boxes the host moved while hosting are mirrored, and wait for their scene");

            yard(player.Scene);
            player.LoadScenes();
            var mine = player.Scene.Scenes[0].Roots;
            That(Enumerable.Range(0, 3).All(index => mine[index].Mark == 0 && Near(mine[index].Position, new NetVector3(index, 4, 4))),
                "once it loads, the boxes moved before the game opened stand where the host's do");
            That(Enumerable.Range(3, 3).All(index => mine[index].Mark == boxes[index].Mark && mine[index].Mark != 0 && Near(mine[index].Position, new NetVector3(index, 5, 5))),
                "the boxes moved while hosting are bound and stand where the host's do");
            That(!mine[11].Active && Enumerable.Range(6, 5).All(index => mine[index].Active && Near(mine[index].Position, new NetVector3(index * 2, 0, 0))),
                "the box the host destroyed is hidden, and the rest stand as the scene put them");
            That(mine.All(box => box.Kinematic), "and the scene's bodies are the host's to move");

            // A scene unloaded and loaded again starts from the scene: its removed box is hidden again.
            player.UnloadScene(player.Scene.Scenes[0]);
            yard(player.Scene);
            player.LoadScenes();
            rig.Frames(5);
            That(!player.Scene.Scenes[0].Roots[11].Active, "a scene loaded again hides what the host removed from it");
        }

        private static void WhatHappensDuringABaselineIsKept()
        {
            var rig = Yard(0);
            var host = rig.Host;
            var robots = Enumerable.Range(0, 400).Select(index => SpawnRobot(host, index, 8 * 1024)).ToList();
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            rig.Join(player);
            That(rig.Run(() => player.World.Progress.Phase == NetworkWorldPhase.ReceivingBaseline && player.World.Progress.EntriesReceived > 0, 60),
                "a large baseline is under way");

            // While it arrives: robots end, new ones spawn, a box moves.
            robots[0].Dispose();
            robots[399].Dispose();
            var late = SpawnRobot(host, 1000, 128);
            That(player.World.Progress.Phase == NetworkWorldPhase.ReceivingBaseline, "the baseline is still under way");
            That(rig.Run(() => player.World.Progress.Phase == NetworkWorldPhase.Synchronized, 600), "the baseline completes (" + player.World.Progress + ")");
            rig.Frames(10);
            That(SameWorld(host, player), "the player ends with exactly the host's world, ended and later spawns included (" + Ids(host).Length + " / " + Ids(player).Length + ")");
            That(player.Client.TryGetEntity(IdOf(host, late), out _), "the spawn made during the baseline is there");
            GC.KeepAlive(late);
        }

        private static void AGenerationChangeStartsAgainFromABaseline()
        {
            var rig = Yard(0);
            var host = rig.Host;
            var robots = Enumerable.Range(0, 300).Select(index => SpawnRobot(host, index, 8 * 1024)).ToList();
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            rig.Join(player);
            That(rig.Run(() => player.World.Progress.EntriesReceived > 20, 60), "the first baseline is part way");
            var startedBefore = host.Counters().BaselinesStarted;
            var staleBefore = player.Counters().StaleMessages;

            // The player's link drops and it rejoins within its grace: the session goes on, its stream restarts.
            rig.Network.Rejoin(player.Control);
            host.Frame();
            That(!host.Host.TryGetBaselineProgress(player.Control.Id, out _, out _),
                "the host abandons the baseline under way as soon as the stream restarts, before the player asks again");
            robots[5].Dispose();
            var during = SpawnRobot(host, 2000, 32);
            That(rig.Run(() => player.World.Progress.Phase == NetworkWorldPhase.Synchronized && !player.World.IsHoldingReadiness, 600),
                "after the restart the player synchronizes again (" + player.World.Progress + ")");
            rig.Frames(10);
            That(host.Counters().BaselinesStarted == startedBefore + 1 && host.Counters().BaselinesCompleted == 1,
                "the host started one new baseline on the new stream and abandoned the old one (" + host.Counters().BaselinesStarted + " started)");
            That(player.Counters().StaleMessages == staleBefore,
                "at once: nothing of the old baseline reached the new stream (" + (player.Counters().StaleMessages - staleBefore) + " stale messages)");
            That(SameWorld(host, player), "and the player ends with exactly the host's world");
            That(player.World.Progress.EntriesReceived > 0 && player.World.Progress.EntriesReceived <= player.World.Progress.EntriesExpected &&
                 player.World.Progress.EntriesExpected <= Ids(host).Length + 1,
                "the new baseline was counted from its own start (" + player.World.Progress + ")");

            // A rejoin after the player is in sync starts again too: whatever was in flight is gone.
            var beforeRejoin = host.Counters().BaselinesCompleted;
            rig.Network.Rejoin(player.Control);
            robots[6].Dispose();
            That(rig.Run(() => host.Counters().BaselinesCompleted == beforeRejoin + 1 && player.World.Progress.Phase == NetworkWorldPhase.Synchronized, 600),
                "a rejoin in sync brings a fresh baseline");
            rig.Frames(10);
            That(SameWorld(host, player), "after which the player matches the host again");
            That(player.Control.HoldsTaken >= 2 && player.Control.Holds == 0, "each restart held readiness again, and let it go");
            GC.KeepAlive(during);
        }

        /// <summary>
        /// A baseline after a restart lists what the player already has: those entities keep their replicas, however the
        /// entries before it ran, and only what changed meanwhile is built or let go.
        /// </summary>
        private static void ANewBaselineRebuildsNothingThePlayerHas()
        {
            var rig = Yard(4);
            var host = rig.Host;
            var robots = Enumerable.Range(0, 10).Select(index => SpawnRobot(host, index, 256)).ToList();
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);

            // A box binds while the player is in sync, so the last entry it read before the restart is a scene object's.
            host.Scene.Scenes[0].Roots[0].Position = new NetVector3(1, 2, 3);
            That(rig.Run(() => player.Scene.Scenes[0].Roots[0].Mark != 0, 30), "the box the host moved binds on the player");
            rig.Network.Rejoin(player.Control);
            robots[3].Dispose();
            var later = SpawnRobot(host, 50, 256);
            That(rig.Run(() => player.World.Progress.Phase == NetworkWorldPhase.Synchronized && !player.World.IsHoldingReadiness && SameWorld(host, player), 120),
                "the player is back in sync after the restart");
            That(player.Factories.Built.Count == 11 && player.Factories.Released.Count == 1,
                "only the robot spawned meanwhile was built, and only the one that ended let go (" + player.Factories.Built.Count + " built, " +
                player.Factories.Released.Count + " released)");
            GC.KeepAlive(later);
        }

        private static void AReplacementStartsEveryPlayerAgain()
        {
            var rig = Yard(5);
            var host = rig.Host;
            for (var index = 0; index < 40; index++) SpawnRobot(host, index, 512);
            rig.OpenToMultiplayer();
            var first = AddPlayer(rig, "p1");
            var second = AddPlayer(rig, "p2");
            rig.JoinAndSynchronize(first);
            rig.Join(second);
            rig.Frames(2);
            var started = host.Counters().BaselinesStarted;
            That(host.Network().Host.ReplaceSession().Succeeded, "the host replaces the session");
            That(rig.Run(() => first.World.Progress.Phase == NetworkWorldPhase.Synchronized && second.World.Progress.Phase == NetworkWorldPhase.Synchronized &&
                               !first.World.IsHoldingReadiness && !second.World.IsHoldingReadiness, 600), "both players synchronize after the replacement");
            rig.Frames(5);
            That(host.Counters().BaselinesStarted >= started + 2 && SameWorld(host, first) && SameWorld(host, second),
                "each started again from a baseline and matches the host");
        }

        private static void ProgressFeedsTheHoldAndTheOverlay()
        {
            var rig = Yard(0);
            var host = rig.Host;
            for (var index = 0; index < 6; index++) host.SpawnCrate(index);
            for (var index = 0; index < 200; index++) SpawnRobot(host, index, 4096);
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            player.Assets.HoldLoads = true;
            rig.Join(player);
            var received = new List<int>();
            var fractions = new List<double>();
            That(rig.Run(() =>
            {
                var progress = player.World.Progress;
                if (progress.Phase == NetworkWorldPhase.ReceivingBaseline)
                {
                    received.Add(progress.EntriesReceived);
                    fractions.Add(progress.Fraction);
                }

                return progress.Phase == NetworkWorldPhase.BuildingReplicas;
            }, 600), "the baseline arrives while the asset replicas still load (" + player.World.Progress + ")");
            That(received.Count > 1 && received.Zip(received.Skip(1), (before, after) => after >= before).All(rising => rising) &&
                 fractions.Zip(fractions.Skip(1), (before, after) => after >= before).All(rising => rising) && fractions.All(value => value < 1),
                "received entries and the overlay's fraction only rise while the baseline arrives");
            That(player.World.IsHoldingReadiness && player.Control.IsReadinessHeld && player.World.Progress.ReplicasPending == 6,
                "readiness waits for the replicas still loading (" + player.World.Progress + ")");
            var reportsBefore = player.Control.ProgressReports;
            player.Assets.CompleteLoads();
            rig.Frames(1);
            player.Assets.CompleteLoads();
            That(rig.Run(() => !player.World.IsHoldingReadiness, 30) && player.World.Progress.Phase == NetworkWorldPhase.Synchronized &&
                 player.World.Progress.Fraction == 1d && player.Control.ProgressReports >= reportsBefore + 6,
                "each finished replica reports progress, and readiness goes once they are all built (" + player.Control.ProgressReports + " reports)");
        }

        /// <summary>
        /// An asset replica whose load never lands gives up after <see cref="NetworkWorldClient.BuildTimeoutMicros"/>: it
        /// counts as unbuildable and the player's readiness goes, rather than holding the player until the hold's own limit
        /// ends its join.
        /// </summary>
        private static void ALoadThatNeverLandsGivesUp()
        {
            var rig = Yard(0);
            for (var index = 0; index < 2; index++) rig.Host.SpawnCrate(index);
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            player.Assets.HoldLoads = true;
            rig.Join(player);
            That(rig.Run(() => player.World.Progress.Phase == NetworkWorldPhase.BuildingReplicas, 300) && player.World.IsHoldingReadiness,
                "the player waits for the crates' loads (" + player.World.Progress + ")");
            rig.Clock.Advance(NetworkWorldClient.BuildTimeoutMicros / 2);
            rig.Frames(2);
            That(player.World.IsHoldingReadiness && player.Counters().ReplicasUnbuildable == 0, "half the bound later it still waits");
            rig.Clock.Advance(NetworkWorldClient.BuildTimeoutMicros / 2 + 1_000_000);
            That(rig.Run(() => !player.World.IsHoldingReadiness, 30) && player.Counters().ReplicasUnbuildable == 2 &&
                 player.World.Progress.Phase == NetworkWorldPhase.Synchronized,
                "past it, both crates count as unbuildable and readiness goes (" + player.World.Progress + ", " + player.Counters().ReplicasUnbuildable + " unbuildable)");
        }

        private static void APlayerABroadcastSkippedGetsANewBaseline()
        {
            var rig = Yard(12);
            var host = rig.Host;
            rig.Network.HostWindowMessages = 24;
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            var boxes = host.Scene.Scenes[0].Roots;

            // The player's game stalls; the host's world keeps moving, so its broadcasts fill the player's window.
            player.Paused = true;
            for (var frame = 0; frame < 400; frame++)
            {
                foreach (var box in boxes) box.Position = new NetVector3(box.Position.X + 0.05f, 0, 0);
                rig.Frame();
            }

            That(host.Counters().Rebaselines > 0, "a broadcast skipped the stalled player (" + host.Counters().Rebaselines + ")");
            var gone = SpawnRobot(host, 5, 64);
            var kept = SpawnRobot(host, 6, 64);
            rig.Frames(15);
            var goneId = IdOf(host, gone);
            gone.Dispose();
            rig.Frames(15);
            var completed = host.Counters().BaselinesCompleted;
            player.Paused = false;
            That(rig.Run(() => host.Counters().BaselinesCompleted > completed && player.World.Progress.Phase == NetworkWorldPhase.Synchronized, 600),
                "once it recovers it receives a new baseline");
            rig.Frames(20);
            That(SameWorld(host, player), "and matches the host, though it missed messages meanwhile");
            That(player.Client.TryGetEntity(IdOf(host, kept), out _) && goneId.IsValid && !player.Client.Knows(goneId),
                "what spawned during the stall is there, and what spawned and ended during it is not");
            var playerBoxes = player.Scene.Scenes[0].Roots;
            var apart = Enumerable.Range(0, 12).Where(index => !Near(boxes[index].Position, playerBoxes[index].Position, 0.05)).ToList();
            That(apart.Count == 0, "and every box stands where the host's does again (" + string.Join(", ", apart.Select(index =>
                index + ": " + boxes[index].Position.X + " / " + playerBoxes[index].Position.X)) + ")");
        }

        /// <summary>
        /// The host's process restarts under the same identity, and the player's link finds it again within the grace, as
        /// the engine allows: the new process serves another world, whose entities are numbered from one again and which
        /// has what the first one destroyed. The player starts that world from nothing: what the first world hid shows
        /// again, an id the first world ended is the new world's to use, and each scene object binds to the new world's
        /// entity. A rejoin to the same process keeps everything, as above.
        /// </summary>
        private static void AHostWhoseProcessRestartedServesAnotherWorld()
        {
            var rig = Yard(6);
            var host = rig.Host;
            rig.OpenToMultiplayer();
            var robot = SpawnRobot(host, 40, 32);
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            var boxes = host.Scene.Scenes[0].Roots;
            boxes[1].Position = new NetVector3(30, 1, 1);
            boxes[2].Remove();
            rig.Frames(10);
            robot.Dispose();
            rig.Frames(10);
            var mine = player.Scene.Scenes[0].Roots;
            That(!mine[2].Active && mine[1].Mark != 0 && player.Client.EntityCount == 1, "the player hid the box the first process destroyed, and bound the one it moved");

            // The first process goes without a word, and a second one starts under the same identity, loads the yard and hosts.
            host.Runtime.Disconnect();
            var restarted = new WorldPeer(rig.Network.Host, rig.Clock, "host again");
            rig.BuildScene!(restarted.Scene);
            restarted.LoadScenes();
            That(restarted.Runtime.Connect(), "the second process's runtime attaches");
            restarted.Frame();
            That(restarted.Host.IsActive, "and takes its census");
            rig.Network.Rejoin(player.Control);
            var theirs = restarted.Scene.Scenes[0].Roots;
            theirs[1].Position = new NetVector3(8, 1, 1);
            theirs[2].Position = new NetVector3(7, 1, 1);
            var synchronized = false;
            for (var frame = 0; frame < 600 && !synchronized; frame++)
            {
                restarted.Frame();
                player.Frame();
                rig.Clock.Advance(16_000);
                synchronized = player.World.Progress.Phase == NetworkWorldPhase.Synchronized && !player.World.IsHoldingReadiness && theirs[2].Mark != 0 &&
                               SameWorld(restarted, player) && mine[2].Mark == theirs[2].Mark;
            }

            That(synchronized && player.Counters().WorldsReplaced == 1, "the player mirrors the second process's world (" + player.World.Progress + ", " +
                 player.Counters().WorldsReplaced + " replaced; " + string.Join(",", Ids(restarted)) + " / " + string.Join(",", Ids(player)) + ")");
            That(mine[2].Active && Near(mine[2].Position, theirs[2].Position), "the box the first process destroyed is back, where the second has it");
            That(mine[1].Mark == theirs[1].Mark && theirs[1].Mark != 0 && Near(mine[1].Position, theirs[1].Position),
                "the box both moved follows the second process's entity, under the id the first gave the robot it ended");
            That(new[] { 0, 3, 4, 5 }.All(index => mine[index].Mark == 0 && mine[index].Active), "and the rest stand as the yard loaded them");
        }

        /// <summary>A rig whose host and players load a yard of <paramref name="boxes"/> boxes in a row, and whose players build robots.</summary>
        internal static WorldRig Yard(int boxes)
        {
            var rig = new WorldRig();
            rig.UseScene(scene =>
            {
                var yard = scene.AddScene("Yard");
                for (var index = 0; index < boxes; index++) scene.Add(yard, "Box", null, rigidbody: true, position: new NetVector3(index * 2, 0, 0));
            });
            return rig;
        }

        internal static WorldPeer AddPlayer(WorldRig rig, string id)
        {
            var player = rig.AddPlayer(id);
            player.Seam().RegisterReplicaFactory(RobotKind, player.Factories);
            return player;
        }

        /// <summary>Spawns a robot through a provider's spawn request, with a description of <paramref name="payloadBytes"/>.</summary>
        internal static ISpawnedEntity SpawnRobot(WorldPeer host, float x, int payloadBytes)
        {
            var payload = new byte[payloadBytes];
            for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index * 31 + (int)x);
            var transform = new TransformState(new Vec3(x, 0, 5), Quat.Identity, new Vec3(1, 1, 1));
            ISpawnedEntity? spawned = null;
            var result = host.Seam().RequestSpawn(new InternalSpawnDescriptor(RobotKind, transform, payload), () =>
            {
                var made = host.Assets.Spawn(new AssetSpawnRequest(new FakePrefab("assets/robots.bundle", "Robot"), transform));
                spawned = made.Value;
                return OperationResult<IEntity>.Success(made.Value!);
            });
            That(result.Succeeded && spawned != null, "the host spawns a robot");
            return spawned!;
        }

        internal static NetworkEntityId IdOf(WorldPeer host, ISpawnedEntity spawned) =>
            host.Scene.TryGetNetworkId(spawned, out var id) ? id : NetworkEntityId.None;

        internal static bool SameWorld(WorldPeer host, WorldPeer player) => Ids(host).SequenceEqual(Ids(player));

        internal static uint[] Ids(WorldPeer peer)
        {
            var ids = new List<uint>();
            if (peer.Host.IsActive) peer.Host.CollectIds(ids);
            else peer.Client.CollectIds(ids);
            return ids.ToArray();
        }

        internal static LoopbackNetPair Network(this WorldPeer host) => host.Control.Network;
    }
}
