using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;
using static TopiaForge.ModManager.Tests.WorldBaselineSyncTests;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// How a baseline is paced: chunks within the channel's bounds, a window it waits for and resumes from exactly where
    /// it stopped without crowding out the broadcasts behind it, requests a player cannot repeat at will, and players that
    /// never hold each other back.
    /// </summary>
    internal static class WorldBaselinePacingTests
    {
        internal static void Run()
        {
            ChunksStayWithinTheChannelsBounds();
            ABaselineWaitsForItsWindowAndResumesWhereItStopped();
            RequestsWithinAStreamAreBounded();
            PlayersNeverHoldEachOtherBack();
            LeavingPlayersAreForgotten();
        }

        private static void ChunksStayWithinTheChannelsBounds()
        {
            var rig = Yard(50);
            var host = rig.Host;
            for (var index = 0; index < 300; index++) SpawnRobot(host, index, index % 3 == 0 ? InternalReplicationLimits.MaximumPayloadBytes : 700);
            rig.OpenToMultiplayer();
            var log = new List<(ParticipantId To, byte[] Payload)>();
            host.Control.Channel(InternalSystemChannelKind.WorldReplication)!.Log = log;
            var player = AddPlayer(rig, "p1");
            var perFrame = new List<int>();
            rig.Join(player);
            var seen = 0;
            That(rig.Run(() =>
            {
                perFrame.Add(log.Skip(seen).Count(item => Type(item.Payload) == WorldMessages.BaselineEntries));
                seen = log.Count;
                return player.World.Progress.Phase == NetworkWorldPhase.Synchronized;
            }), "the player synchronizes");
            var chunks = log.Where(item => Type(item.Payload) == WorldMessages.BaselineEntries).Select(item => item.Payload).ToList();
            That(chunks.Count > 10 && chunks.All(chunk => chunk.Length <= WorldMessages.MaximumMessageBytes),
                "a large baseline goes in many chunks, each within the message bound (" + chunks.Count + " chunks, largest " + chunks.Max(chunk => chunk.Length) + " bytes)");
            That(perFrame.All(count => count <= NetworkWorldHost.BaselineMessagesPerFrame), "and never more than the frame's share at once (" + perFrame.Max() + ")");
            That(SameWorld(host, player) && player.Factories.Built.Count == 300 && player.Factories.Built.Count(descriptor => descriptor.Payload.Length == InternalReplicationLimits.MaximumPayloadBytes) == 100,
                "every entry arrived whole, the largest descriptions included");
        }

        private static void ABaselineWaitsForItsWindowAndResumesWhereItStopped()
        {
            var rig = Yard(20);
            var host = rig.Host;
            for (var index = 0; index < 200; index++) SpawnRobot(host, index, 4096);
            rig.Network.HostWindowBytes = 96 * 1024;
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            rig.Join(player);
            That(rig.Run(() => player.World.Progress.EntriesReceived > 30, 120), "the baseline starts");

            // The player's game stalls mid-baseline: the host stops at the window's edge and keeps its broadcasts flowing.
            player.Paused = true;
            var boxes = host.Scene.Scenes[0].Roots;
            for (var frame = 0; frame < 60; frame++)
            {
                boxes[0].Position = new NetVector3(frame * 0.1f, 0, 0);
                rig.Frame();
            }

            var outstanding = host.Control.Channel(InternalSystemChannelKind.WorldReplication)!.Outstanding(player.Control.Id);
            var headroom = NetworkWorldHost.BaselineProbeBytes - NetworkWorldHost.BaselineChunkBytes;
            That(outstanding.Bytes < rig.Network.HostWindowBytes && host.Counters().Rebaselines == 0 && player.World.Progress.Phase == NetworkWorldPhase.ReceivingBaseline,
                "a waiting baseline stops short of the window's last " + headroom + " bytes, so no broadcast skips the player (" + outstanding.Bytes + " bytes outstanding)");
            player.Paused = false;
            That(rig.Run(() => player.World.Progress.Phase == NetworkWorldPhase.Synchronized, 600), "the baseline resumes and completes");
            rig.Frames(10);
            That(SameWorld(host, player) && host.Counters().BaselinesStarted == 1 && player.Factories.Built.Count == 200,
                "from where it stopped: one baseline, every entry once (" + player.Factories.Built.Count + " built)");

            // Its samples from the stall arrived late, all at once: the replica plays them back, a little faster than real
            // time, and comes to rest where the host's box is.
            var mine = player.Scene.Scenes[0].Roots[0];
            That(rig.Run(() => Near(mine.Position, boxes[0].Position, 0.05), 120),
                "and the box the host moved meanwhile comes to stand where the host's does (" + mine.Position.X + " / " + boxes[0].Position.X + ")");
        }

        private static void RequestsWithinAStreamAreBounded()
        {
            var rig = Yard(3);
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            var host = rig.Host;
            var channel = host.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            var request = new byte[] { WorldMessages.Version, WorldMessages.BaselineRequest, 1 };
            var started = host.Counters().BaselinesStarted;
            for (var index = 0; index < 10; index++) channel.Inject(player.Control.Id, request);
            rig.Frames(6);
            That(host.Counters().BaselinesStarted - started <= 2 && host.Counters().RequestsRefused >= 8,
                "a player asking again and again within one stream gets at most the bucket's two (" + (host.Counters().BaselinesStarted - started) + " started)");
            rig.Clock.Advance(11_000_000);
            channel.Inject(player.Control.Id, request);
            rig.Frames(6);
            That(host.Counters().BaselinesStarted - started <= 3 && host.Counters().Requests >= 2, "and one more once ten seconds have passed");
            foreach (var malformed in new[] { new byte[] { WorldMessages.Version, WorldMessages.BaselineRequest }, new byte[] { WorldMessages.Version, WorldMessages.BaselineRequest, 2 },
                         new byte[] { WorldMessages.Version, WorldMessages.BaselineRequest, 0, 0 } })
            {
                channel.Inject(player.Control.Id, malformed);
            }

            var discarded = host.World.Channel.Discarded;
            rig.Frames(2);
            That(host.World.Channel.Discarded == discarded + 3, "a malformed request is discarded");

            // A player that sends host messages is ignored: only a joined player's own world reads them.
            var spoof = new WorldWireWriter();
            spoof.Begin(WorldMessages.Despawn);
            spoof.WriteVarUInt(1);
            spoof.WriteVarUInt(1);
            channel.Inject(player.Control.Id, spoof.Segment.ToArray());
            rig.Frames(2);
            That(host.Host.EntityCount == Ids(host).Length && SameWorld(host, player), "a player cannot despawn the host's entities");
        }

        private static void PlayersNeverHoldEachOtherBack()
        {
            var rig = Yard(10);
            var host = rig.Host;
            for (var index = 0; index < 300; index++) SpawnRobot(host, index, 6000);
            rig.OpenToMultiplayer();
            var slow = AddPlayer(rig, "p1");
            var fast = AddPlayer(rig, "p2");
            rig.Join(slow);
            rig.Join(fast);
            That(rig.Run(() => slow.World.Progress.EntriesReceived > 10, 60), "both baselines start");
            slow.Paused = true;
            That(rig.Run(() => fast.World.Progress.Phase == NetworkWorldPhase.Synchronized, 600), "the other player completes while one stalls");
            That(slow.World.Progress.Phase == NetworkWorldPhase.ReceivingBaseline, "the stalled one is still part way");
            slow.Paused = false;
            That(rig.Run(() => slow.World.Progress.Phase == NetworkWorldPhase.Synchronized, 600), "and completes once it recovers");
            rig.Frames(5);
            That(SameWorld(host, slow) && SameWorld(host, fast), "both match the host");
        }

        private static void LeavingPlayersAreForgotten()
        {
            var rig = Yard(3);
            rig.OpenToMultiplayer();
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            That(rig.Host.Host.PeerCount == 1, "the host baselines one player");
            player.Control.Leave();
            rig.Frames(3);
            That(rig.Host.Host.PeerCount == 0, "a player whose stream ended is forgotten");

            // A request from a player without a stream is refused, and keeps nothing.
            var refused = rig.Host.Counters().RequestsRefused;
            var stranger = rig.AddPlayer("p9");
            rig.Network.Join(stranger.Control);
            rig.Frames(1);
            rig.Network.Leave(stranger.Control);
            rig.Frames(4);
            That(rig.Host.Host.PeerCount == 0 && rig.Host.Counters().RequestsRefused >= refused, "a request whose stream is gone keeps nothing");
        }

        private static byte Type(byte[] payload) => payload.Length >= 2 ? payload[1] : (byte)0;
    }
}
