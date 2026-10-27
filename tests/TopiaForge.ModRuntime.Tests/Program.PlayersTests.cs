using System;
using System.Collections.Generic;
using System.IO;
using TopiaForge.ModManager;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static int RunPlayersCases()
        {
            UnityMainThreadGuard.CaptureCurrentThread();
            var root = Directory.CreateTempSubdirectory("TopiaForgePlayersTests-").FullName;
            try { TestPlayersAcrossRuntimes(root); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
            finally { TryDelete(root); }
        }

        private static void TestPlayersAcrossRuntimes(string root)
        {
            TestFirstPosePlayersAcrossRuntimes(root);
            var network = new LoopbackNetPair(); var clock = new WorldTestClock();
            var host = new PresenceGame(NewFixture(Path.Combine(root, "players-host"), "players", PresenceModEntry), network.Host, clock, "Host");
            var first = new PresenceGame(NewFixture(Path.Combine(root, "players-first"), "players", PresenceModEntry), network.AddPlayer("p1"), clock, "Sam");
            var second = new PresenceGame(NewFixture(Path.Combine(root, "players-second"), "players", PresenceModEntry), network.AddPlayer("p2"), clock, "Guest");
            var all = new List<PresenceGame> { host, first };
            using var hostServices = new PlayersTestServices(host.Runtime.Replication.Players!, host.Control, host.Backend);
            using var firstServices = new PlayersTestServices(first.Runtime.Replication.Players!, first.Control, first.Backend);
            using var secondServices = new PlayersTestServices(second.Runtime.Replication.Players!, second.Control, second.Backend);
            try
            {
                host.Start(); first.Start(); second.Start();
                Assert(hostServices.Directory.Players.Count == 1, "runtime solo directory contains exactly its local player");
                host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Players", 4)); PresenceFrames(all, clock, 3);
                first.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()); PresenceFrames(all, clock, 25);
                var remote = hostServices.Player(first.Control.Id);
                Assert(remote.Entity != null && remote.TryGetSnapshot(out _) && remote.TryGetHealth(out _), "runtime handles wrap presence's entity, pose and health");
                Assert(remote.Damage(new PlayerDamageRequest(50, "enemy")).Succeeded, "runtime sends player damage");
                PresenceFrames(all, clock, 25); Assert(first.Backend.Health == 50, "second runtime applies damage");
                Assert(remote.Heal(40, "shop").Succeeded && remote.Teleport(new Vec3(80, 0, 30), 45).Succeeded, "runtime sends heal and teleport");
                PresenceFrames(all, clock, 25);
                Assert(first.Backend.Health == 90 && first.Backend.Feet.X == 80 && host.Presence.Counters.Corrections == 0, "second runtime applies both without a plausibility correction");
                var clicked = 0;
                using var shop = remote.Ui.CreateSurface(new UiSurfaceRequest("shop", "Shop", "", content: new UiButton("buy", "Buy", () => clicked++))).Value!;
                PresenceFrames(all, clock, 3); ((UiButton)firstServices.Ui.Surfaces[0].Content!).Activated(); PresenceFrames(all, clock, 3);
                Assert(clicked == 1 && hostServices.Ui.Surfaces.Count == 0, "remote UI callback reaches the first runtime only");

                all.Add(second); second.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()); PresenceFrames(all, clock, 25);
                Assert(hostServices.Directory.Players.Count == 3 && secondServices.Directory.Players.Count == 3 && shop.IsVisible, "three real runtimes keep all handles and the existing window");
                var secondRemote = hostServices.Player(second.Control.Id);
                Assert(secondRemote.Damage(new PlayerDamageRequest(10, "enemy")).Succeeded, "third runtime can be targeted independently");
                PresenceFrames(all, clock, 25); Assert(second.Backend.Health == 90 && first.Backend.Health == 90, "only the selected owner applies it");
                firstServices.Input.SetValue("fire", 1); PresenceFrames(all, clock, 1); firstServices.Input.FinishFrame();
                var pressed = false;
                for (var i = 0; i < 8; i++) { PresenceFrames(all, clock, 1); pressed |= remote.Input.WasPressed("fire"); }
                Assert(pressed && remote.Input.IsHeld("fire") && !secondRemote.Input.IsHeld("fire"), "runtime input is per-player with a press edge");
                firstServices.Input.SetValue("fire", 0); PresenceFrames(all, clock, 1); firstServices.Input.FinishFrame();
                var released = false;
                for (var i = 0; i < 8; i++) { PresenceFrames(all, clock, 1); released |= remote.Input.WasReleased("fire"); }
                Assert(released && !remote.Input.IsHeld("fire"), "runtime reconstructs release");
                shop.Dispose(); PresenceFrames(all, clock, 10);
                void Read()
                {
                    var players = hostServices.Directory.Players;
                    for (var i = 0; i < players.Count; i++)
                    {
                        var player = players[i]; hostServices.Directory.TryGet(player.Id, out _);
                        _ = player.DisplayName; _ = player.IsLocal; _ = player.IsHost; _ = player.Entity;
                        player.TryGetSnapshot(out _); player.TryGetHealth(out _); _ = player.Input.IsHeld("fire");
                    }
                }
                for (var i = 0; i < 1000; i++) Read();
                var before = GC.GetAllocatedBytesForCurrentThread(); for (var i = 0; i < 10000; i++) Read();
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert(bytes == 0, "three-player runtime steady reads allocate zero, got " + bytes);
                var frameBytes = MeasurePresenceFrames(all, clock, 10000);
                Assert(frameBytes == 0, "players runtime at rest allocates zero, got " + frameBytes);
                Console.WriteLine("Players runtime: two and three runtimes passed; steady reads and 10,000 resting frames each allocated 0 B.");
                first.Runtime.Multiplayer.Leave(); PresenceFrames(all, clock, 3);
                Assert(!remote.IsPresent && remote.Heal(1, "old").ErrorCode == ModErrorCode.NotFound, "retained runtime handle retires on leave");
            }
            finally { foreach (var game in new[] { host, first, second }) game.Runtime.UnloadAll(); }
        }
    }
}
