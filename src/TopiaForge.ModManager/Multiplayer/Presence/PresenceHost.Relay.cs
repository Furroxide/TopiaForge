using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class PresenceHost
    {
        /// <summary>
        /// Each player's budget for other players' poses: 4 KiB a second, half the link's 64 kbit/s realtime share, which
        /// leaves room for motion ownership's samples. At 22 bytes a pose that is nine other players at the full 20 Hz; past
        /// that, the nearest take the larger share and none starves.
        /// </summary>
        internal const int DownlinkBytesPerSecond = 4096;

        /// <summary>How often the host relays poses: 20 a second, the rate players send at.</summary>
        internal const long RelayIntervalMicros = PresenceLocalPlayer.SendIntervalMicros;

        /// <summary>One relay tick's share of <see cref="DownlinkBytesPerSecond"/>.</summary>
        internal const int RelayBudgetBytesPerTick = (int)(DownlinkBytesPerSecond * RelayIntervalMicros / 1_000_000);

        /// <summary>How often the host relays states that changed: at most ten times a second.</summary>
        internal const long StatesIntervalMicros = 100_000;

        // Within this distance a player is near enough to take the larger share of the budget.
        private const double NearMeters = 150;

        private readonly NetBitWriter relay = new NetBitWriter(InternalSystemLimits.MaximumDatagramBytes);
        private readonly List<ReplicationCandidate> candidates = new List<ReplicationCandidate>();
        private readonly List<NetworkEntityId> selected = new List<NetworkEntityId>();
        private readonly List<PresencePeer> picks = new List<PresencePeer>();
        private readonly List<PresenceStateRecord> states = new List<PresenceStateRecord>();
        private readonly List<ParticipantId> skipped = new List<ParticipantId>();
        private long nextRelayMicros;
        private long nextStatesMicros;
        private int rosterSent = -1;
        private ulong generationSeen;

        /// <summary>
        /// Frame step 4: the host's own pose and state, what each player is owed (the roster and every state after its stream
        /// starts, a correction, a placement), the states that changed, and every relay tick each player's share of the
        /// others' poses.
        /// </summary>
        internal void Send(long nowMicros)
        {
            if (!active) return;
            TakeOwnPose(nowMicros);
            var open = presence.Channel;
            if (open == null) return;
            FollowStreams(open, nowMicros);
            if (presence.Roster.Version != rosterSent) BroadcastRoster(open);
            var all = presence.Peers;
            for (var index = 0; index < all.Count; index++)
            {
                var peer = all[index];
                if (peer.IsLocal || peer.Removed) continue;
                if (peer.NeedsCatchUp) CatchUp(open, peer);
                if (peer.CorrectionDue) SendCorrection(open, peer);
                if (peer.PlacementDue) SendPlacement(open, peer);
            }

            if (nowMicros >= nextStatesMicros)
            {
                nextStatesMicros = nowMicros + StatesIntervalMicros;
                BroadcastStates(open, nowMicros);
            }

            if (nowMicros >= nextRelayMicros)
            {
                // The same 50 ms beat as players' poses, whatever the frame rate; a host that fell a whole beat behind starts again.
                nextRelayMicros = nowMicros - nextRelayMicros >= RelayIntervalMicros ? nowMicros + RelayIntervalMicros : nextRelayMicros + RelayIntervalMicros;
                Relay(open, nowMicros);
            }
        }

        /// <summary>The host's own player is canonical: its pose and state stand as sampled, with no check.</summary>
        private void TakeOwnPose(long nowMicros)
        {
            var local = presence.LocalParticipant;
            if (local == null || !presence.TryGetPeer(local.Value, out var own) || own == null) return;
            var sampler = presence.Local;
            if (sampler.TryTakePose(nowMicros, 0, out var pose))
            {
                own.SetPose(nowMicros, pose.Position, pose.Rotation, pose.Velocity, pose.EyeCentimeters, pose.Flags & ~PresenceFlags.Teleported);
            }

            if (sampler.TryTakeState(nowMicros)) own.SetState(sampler.Health, sampler.MaximumHealth, sampler.HeldItem);
        }

        /// <summary>A stream that started or restarted owes its player the roster and every state; a player back on its link starts afresh.</summary>
        private void FollowStreams(IInternalSystemChannel open, long nowMicros)
        {
            var generation = open.Generation;
            if (generation == generationSeen) return;
            generationSeen = generation;
            var all = presence.Peers;
            for (var index = 0; index < all.Count; index++)
            {
                var peer = all[index];
                if (peer.IsLocal) continue;
                var stream = open.GetGeneration(peer.Id);
                if (stream == peer.CaughtUpGeneration) continue;
                peer.CaughtUpGeneration = stream;
                if (stream == 0) continue;
                peer.NeedsCatchUp = true;
                peer.Scheduler = new ReplicationScheduler(RelayBudgetBytesPerTick);
                peer.Plausibility?.Restart(nowMicros);
            }
        }

        private void BroadcastRoster(IInternalSystemChannel open)
        {
            rosterSent = presence.Roster.Version;
            PresenceWire.WriteRoster(writer, presence.Roster.List());
            Broadcast(open);
        }

        private void BroadcastStates(IInternalSystemChannel open, long nowMicros)
        {
            states.Clear();
            var all = presence.Peers;
            for (var index = 0; index < all.Count; index++)
            {
                var peer = all[index];
                var health = peer.HealthCheck;
                if (health != null && health.Advance(nowMicros)) peer.SetState(health.Health, health.Maximum, peer.HeldItem);
                if (!peer.StateChanged) continue;
                peer.StateChanged = false;
                if (peer.HasState && peer.Index != 0) states.Add(new PresenceStateRecord(peer.Index, peer.Health, peer.MaximumHealth, peer.HeldItem));
            }

            if (states.Count == 0) return;
            PresenceWire.WriteStates(writer, states);
            Broadcast(open);
        }

        /// <summary>Sends what the writer holds to every player; one whose window was full gets everything again once it has room.</summary>
        private void Broadcast(IInternalSystemChannel open)
        {
            skipped.Clear();
            open.Send(writer.Segment, InternalSystemTarget.Everyone, skipped);
            for (var index = 0; index < skipped.Count; index++)
            {
                if (presence.TryGetPeer(skipped[index], out var peer) && peer != null) peer.NeedsCatchUp = true;
            }
        }

        /// <summary>The roster and every state, to one player, once its window takes both.</summary>
        private void CatchUp(IInternalSystemChannel open, PresencePeer peer)
        {
            var target = InternalSystemTarget.Only(peer.Id);
            PresenceWire.WriteRoster(writer, presence.Roster.List());
            if (!open.CanSend(target, writer.Length) || !open.Send(writer.Segment, target).Succeeded) return;
            states.Clear();
            var all = presence.Peers;
            for (var index = 0; index < all.Count; index++)
            {
                var other = all[index];
                if (other.HasState && other.Index != 0 && !ReferenceEquals(other, peer)) states.Add(new PresenceStateRecord(other.Index, other.Health, other.MaximumHealth, other.HeldItem));
            }

            if (states.Count > 0)
            {
                PresenceWire.WriteStates(writer, states);
                if (!open.CanSend(target, writer.Length) || !open.Send(writer.Segment, target).Succeeded) return;
            }

            peer.NeedsCatchUp = false;
        }

        private void SendCorrection(IInternalSystemChannel open, PresencePeer peer)
        {
            PresenceWire.WriteCorrect(writer, peer.CorrectionNumber, peer.CorrectionTarget);
            if (SendTo(open, peer)) peer.CorrectionDue = false;
        }

        private void SendPlacement(IInternalSystemChannel open, PresencePeer peer)
        {
            var world = peer.PlacedWorld;
            if (world == null || peer.Slot < 0)
            {
                peer.PlacementDue = false;
                return;
            }

            PresenceWire.WritePlace(writer, world, (byte)peer.Slot, peer.PlacementFeet, peer.PlacementYaw);
            if (SendTo(open, peer)) peer.PlacementDue = false;
        }

        private bool SendTo(IInternalSystemChannel open, PresencePeer peer)
        {
            var target = InternalSystemTarget.Only(peer.Id);
            return open.CanSend(target, writer.Length) && open.Send(writer.Segment, target).Succeeded;
        }

        /// <summary>
        /// One relay tick: for each player, its scheduler picks the other players' poses that fit its budget, nearest first
        /// under pressure, every one in turn, and a player at rest after three confirmations costs nothing, so a session at
        /// rest sends nothing. A pose the player itself sent never comes back to it.
        /// </summary>
        private void Relay(IInternalSystemChannel open, long nowMicros)
        {
            var all = presence.Peers;
            for (var index = 0; index < all.Count; index++)
            {
                var peer = all[index];
                peer.MovedThisTick = peer.PoseVersion != peer.TickVersion;
                peer.TickVersion = peer.PoseVersion;
            }

            var hostMillis = unchecked((uint)(nowMicros / 1000));
            for (var index = 0; index < all.Count; index++)
            {
                var recipient = all[index];
                var scheduler = recipient.Scheduler;
                if (recipient.IsLocal || recipient.Removed || !recipient.IsConnected || scheduler == null || open.GetGeneration(recipient.Id) == 0) continue;
                candidates.Clear();
                for (var other = 0; other < all.Count; other++)
                {
                    var candidate = all[other];
                    if (ReferenceEquals(candidate, recipient) || !candidate.HasPose || candidate.Index == 0 || candidate.Removed) continue;
                    candidates.Add(new ReplicationCandidate(new NetworkEntityId(candidate.Index), PriorityOf(recipient, candidate),
                        PresenceMessages.RelayRecordBytes, !candidate.MovedThisTick));
                }

                scheduler.Schedule(candidates, selected);
                picks.Clear();
                for (var pick = 0; pick < selected.Count && picks.Count < PresenceMessages.MaximumRecords; pick++)
                {
                    if (presence.Roster.TryGetParticipant((byte)selected[pick].Value, out var id) && presence.TryGetPeer(id, out var shown) && shown != null)
                    {
                        picks.Add(shown);
                    }
                }

                if (picks.Count == 0) continue;
                PresenceWire.BeginPoses(relay, hostMillis, picks.Count);
                for (var pick = 0; pick < picks.Count; pick++)
                {
                    var shown = picks[pick];
                    var record = new PresencePoseRecord
                    {
                        Index = shown.Index,
                        AgeMillis = (ushort)Math.Min(ushort.MaxValue, Math.Max(0, (nowMicros - shown.PoseMicros) / 1000)),
                        Flags = shown.Flags,
                        EyeCentimeters = shown.EyeCentimeters,
                        Position = shown.Position,
                        Rotation = shown.Rotation,
                        Velocity = shown.Velocity
                    };
                    PresenceWire.WriteRelay(relay, record);
                }

                var payload = relay.AsSegment();
                var sent = open.SendDatagram(payload, InternalSystemTarget.Only(recipient.Id));
                if (!sent.Succeeded || sent.Value == 0) continue;
                presence.Counters.RelaysSent++;
                presence.Counters.RelayedPoses += picks.Count;
                presence.Counters.DatagramBytes += payload.Count;
            }
        }

        /// <summary>
        /// A nearer player claims more of the budget: 1,000 beside the recipient, falling to 100 at 150 m and beyond. The floor
        /// keeps a far player's claim a tenth of a near one's, so when the budget is short it still moves about once a second.
        /// </summary>
        internal static int PriorityOf(PresencePeer recipient, PresencePeer candidate)
        {
            if (!recipient.HasPose) return 500;
            var distance = Math.Sqrt(recipient.Position.DistanceSquaredTo(candidate.Position)) / NetQuantization.PositionUnitsPerMeter;
            var nearness = Math.Max(0.0, NearMeters - distance) / NearMeters;
            return 100 + (int)(900 * nearness);
        }
    }
}
