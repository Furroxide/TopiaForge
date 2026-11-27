using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.PresenceAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Presence's budget and cost (WS3.10): sixteen players moving at once each receive the others within 4 KiB a second,
    /// nobody starved; a session at rest sends nothing but a pose a second from each player; and presence allocates nothing
    /// offline, in single player, or online at rest with the clock running. The cost of a frame with players moving is
    /// measured and printed.
    /// </summary>
    internal static class PresenceBudgetTests
    {
        internal static void Run()
        {
            SixteenPlayersShareTheBudget();
            ASessionAtRestSendsNothing();
            NothingAllocatesAtRest();
            Console.WriteLine("PresenceBudgetTests passed.");
        }

        private static void SixteenPlayersShareTheBudget()
        {
            var rig = new PresenceRig();
            rig.HostWith(15);
            var nodes = rig.All.ToList();
            foreach (var node in nodes) node.Backend.RecordTrails = false;
            var log = new List<(ParticipantId To, byte[] Payload)>();
            rig.Host.Channel.DatagramLog = log;

            // Each player walks a circle of its own at 5 m/s, the circles spread over 120 m by 120 m.
            var angles = new double[nodes.Count];
            rig.BeforeFrame = () =>
            {
                for (var index = 0; index < nodes.Count; index++)
                {
                    var radius = 3.0 + index * 0.2;
                    angles[index] += 0.08 / radius;
                    var centerX = index % 4 * 40.0 - 60.0;
                    var centerZ = index / 4 * 40.0 - 60.0;
                    nodes[index].Feet = new NetVector3((float)(centerX + radius * Math.Cos(angles[index])), 0f, (float)(centerZ + radius * Math.Sin(angles[index])));
                }
            };
            rig.Frames(60);
            log.Clear();
            var lastSeen = new Dictionary<(ParticipantId, byte), int>();
            var longestGap = new Dictionary<(ParticipantId, byte), int>();
            var bytes = new Dictionary<ParticipantId, long>();
            var reader = new NetBitReader();
            const int Frames = 625;
            for (var frame = 0; frame < Frames; frame++)
            {
                rig.Frame();
                foreach (var (to, payload) in log)
                {
                    bytes[to] = (bytes.TryGetValue(to, out var total) ? total : 0) + payload.Length;
                    var count = 0;
                    That(PresenceWire.TryReadDatagramType(new ArraySegment<byte>(payload), reader, out var type) && type == PresenceMessages.Poses &&
                         PresenceWire.TryReadPosesHeader(reader, out _, out count), "every relay reads back");
                    for (var record = 0; record < count; record++)
                    {
                        That(PresenceWire.TryReadRelay(reader, out var pose), "every record reads back");
                        var key = (to, pose.Index);
                        if (lastSeen.TryGetValue(key, out var seen)) longestGap[key] = Math.Max(longestGap.TryGetValue(key, out var gap) ? gap : 0, frame - seen);
                        lastSeen[key] = frame;
                    }
                }

                log.Clear();
            }

            var seconds = Frames * 0.016;
            foreach (var recipient in rig.Players)
            {
                var rate = bytes.TryGetValue(recipient.Id, out var total) ? total / seconds : 0;
                That(rate <= PresenceHost.DownlinkBytesPerSecond, recipient.Name + " receives within its budget (" + rate.ToString("F0", CultureInfo.InvariantCulture) + " B/s)");
                That(rate >= 0.85 * PresenceHost.DownlinkBytesPerSecond, recipient.Name + " and uses most of it while fifteen others move (" + rate.ToString("F0", CultureInfo.InvariantCulture) + " B/s)");
                foreach (var other in nodes)
                {
                    if (ReferenceEquals(other, recipient)) continue;
                    var key = (recipient.Id, rig.Host.PeerOf(other)!.Index);
                    That(lastSeen.ContainsKey(key), recipient.Name + " hears " + other.Name);
                    var gap = longestGap.TryGetValue(key, out var frames) ? frames * 16 : 0;
                    That(gap <= 1_200, recipient.Name + " never waits longer than 1.2 s for " + other.Name + " (" + gap + " ms)");
                }
            }

            foreach (var viewer in nodes)
            {
                That(nodes.All(other => ReferenceEquals(other, viewer) || viewer.AvatarOf(other) != null), viewer.Name + " sees all fifteen others");
            }

            That(rig.Host.Counters.Corrections == 0 && rig.Network.Kicks.Count == 0, "and nobody walking is taken for a cheat");
            var worst = rig.Players.Max(player => bytes[player.Id]) / seconds;
            var longest = longestGap.Values.Max() * 16;
            Console.WriteLine("  presence budget: 16 players moving, at most " + worst.ToString("F0", CultureInfo.InvariantCulture) + " B/s to a player, longest wait " + longest + " ms");
        }

        private static void ASessionAtRestSendsNothing()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            rig.Frames(60);
            var relays = rig.Host.Counters.RelaysSent;
            var hostDatagrams = rig.Host.Channel.DatagramsSent;
            var poses = rig.Players.Select(player => player.Counters.PosesSent).ToList();
            var messages = rig.Host.Channel.SentMessages;
            rig.Frames(320);
            That(rig.Host.Counters.RelaysSent == relays && rig.Host.Channel.DatagramsSent == hostDatagrams, "a host at rest relays nothing");
            That(rig.Host.Channel.SentMessages == messages, "and sends no message");
            for (var index = 0; index < rig.Players.Count; index++)
            {
                var sent = rig.Players[index].Counters.PosesSent - poses[index];
                That(sent >= 4 && sent <= 6, "a player at rest sends a pose a second (" + sent + " in 5 s)");
            }
        }

        private static void NothingAllocatesAtRest()
        {
            // Offline: no provider attached, then one in single player.
            var offline = new PresenceRig();
            offline.Host.Backend.RecordTrails = false;
            var alone = MeasureFrames(() => offline.Frame());
            That(alone == 0, "presence allocates nothing in single player (" + alone + " bytes over 10,000 frames)");

            // Online at rest, the clock running: heartbeats travel, nothing is relayed, and nothing allocates.
            var rig = new PresenceRig();
            rig.HostWith(2);
            foreach (var node in rig.All) node.Backend.RecordTrails = false;
            rig.Frames(120);
            var heartbeats = rig.Players[0].Counters.PosesSent;
            var built = rig.All.Sum(node => node.Control.SnapshotsBuilt);
            var online = MeasureFrames(() => rig.Frame());
            That(rig.Players[0].Counters.PosesSent > heartbeats + 100, "heartbeats went on throughout (" + (rig.Players[0].Counters.PosesSent - heartbeats) + ")");
            That(online == 0, "presence online at rest allocates nothing, the host and both players together (" + online + " bytes over 10,000 frames)");
            That(rig.All.Sum(node => node.Control.SnapshotsBuilt) == built, "and reconciling with the session builds no snapshot at rest");

            // Moving: the frame's cost, measured and printed, and nothing allocated either once every avatar exists.
            var moving = new PresenceRig();
            moving.HostWith(3);
            var nodes = moving.All.ToList();
            foreach (var node in nodes) node.Backend.RecordTrails = false;
            var angles = new double[nodes.Count];
            moving.BeforeFrame = () =>
            {
                for (var index = 0; index < nodes.Count; index++)
                {
                    angles[index] += 0.08 / 4.0;
                    nodes[index].Feet = new NetVector3((float)(index * 10 + 4 * Math.Cos(angles[index])), 0f, (float)(4 * Math.Sin(angles[index])));
                }
            };
            moving.Frames(120);
            var bytes = MeasureFrames(() => moving.Frame(), 2_000);
            var watch = Stopwatch.StartNew();
            for (var index = 0; index < 2_000; index++) moving.Frame();
            watch.Stop();
            var microseconds = watch.Elapsed.TotalMilliseconds * 1000 / 2_000;
            Console.WriteLine("  presence cost: a host and three players moving, " + (bytes / 2_000.0).ToString("F1", CultureInfo.InvariantCulture) + " bytes and " +
                              microseconds.ToString("F1", CultureInfo.InvariantCulture) + " us a frame for all four games together, test doubles included");
            That(bytes == 0, "four games moving allocate nothing once their avatars exist (" + bytes + " bytes over 2,000 frames)");
        }

        /// <summary>Counts what <paramref name="frames"/> frames allocate: the fewest bytes of three passes after one to warm.</summary>
        private static long MeasureFrames(Action frame, int frames = 10_000)
        {
            Pass(frame, frames);
            var allocated = long.MaxValue;
            for (var pass = 0; pass < 3 && allocated != 0; pass++) allocated = Math.Min(allocated, Pass(frame, frames));
            return allocated;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long Pass(Action frame, int frames)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < frames; index++) frame();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
