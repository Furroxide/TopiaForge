using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Presence on a listen host (WS3.10). It gives every participant a presence index, takes each joined player's poses
    /// and states and checks them by the host's rules (<see cref="PresencePlausibility"/>, <see cref="PresenceHealth"/>):
    /// a pose that breaks one is corrected, and a player that keeps breaking them is removed. It replaces a player's talking
    /// claim with what its own voice relay heard, relays every accepted pose to the other players within each one's byte
    /// budget (the other part of this class), and places each player on the spawn ring when it arrives in the host's world.
    /// It takes a pose only from the player it belongs to: the channel names the sender, and a player's payload in the
    /// host's format, which would speak for others, is refused. Main thread only.
    /// </summary>
    internal sealed partial class PresenceHost
    {
        /// <summary>What a player removed for breaking the rules reads. It names no rule, so it gives a cheat nothing to tune by.</summary>
        internal const string KickMessage = "The host removed this player: its movement broke the session's rules.";

        // How far a sender's clock may stray from the host's before its poses are mapped afresh: a jump, not drift.
        private const long ClockJumpMicros = 1_000_000;

        private readonly MultiplayerPresence presence;
        private readonly NetBitReader bits = new NetBitReader();
        private readonly WorldWireReader reader = new WorldWireReader();
        private readonly WorldWireWriter writer = new WorldWireWriter(PresenceMessages.MaximumMessageBytes);
        private readonly SpawnSlotPlanner planner;
        private readonly HashSet<int> occupied = new HashSet<int>();
        private bool active;

        internal PresenceHost(MultiplayerPresence presence)
        {
            this.presence = presence ?? throw new ArgumentNullException(nameof(presence));
            planner = new SpawnSlotPlanner(from => presence.Backend.FindGround(from), feet => presence.Backend.IsClear(feet));
        }

        internal bool IsActive => active;

        internal void Activate()
        {
            active = true;
            nextRelayMicros = 0;
            nextStatesMicros = 0;
            rosterSent = -1;
            generationSeen = 0;
        }

        internal void Deactivate() => active = false;

        /// <summary>A participant appeared in the session: it takes an index, and a joined player the host's checks.</summary>
        internal void Joined(PresencePeer peer, long nowMicros)
        {
            peer.Index = presence.Roster.TryAssign(peer.Id, nowMicros, out var index) ? index : (byte)0;
            if (peer.IsLocal) return;
            peer.Plausibility = new PresencePlausibility(presence.Policy, nowMicros);
            peer.HealthCheck = new PresenceHealth(presence.Policy);
            peer.Scheduler = new ReplicationScheduler(RelayBudgetBytesPerTick);
            peer.NeedsCatchUp = true;
        }

        /// <summary>A participant left the session for good: its index rests, and its slot frees with it.</summary>
        internal void Left(PresencePeer peer, long nowMicros) => presence.Roster.Release(peer.Id, nowMicros);

        /// <summary>A player's link went or came back. Back, it may have moved meanwhile, so the next pose is measured afresh.</summary>
        internal void LinkChanged(PresencePeer peer, bool connected, long nowMicros)
        {
            if (connected) peer.Plausibility?.Restart(nowMicros);
        }

        /// <summary>Frame step 2: what the players sent since the last frame.</summary>
        internal void Receive(long nowMicros)
        {
            var open = presence.Channel;
            if (!active || open == null) return;
            while (open.IsOpen && open.TryReceive(out var message))
            {
                if (!presence.TryGetPeer(message.Sender, out var peer) || peer == null || peer.IsLocal || peer.Removed)
                {
                    presence.Counters.Forged++;
                    continue;
                }

                Handle(peer, message.Payload, nowMicros);
            }

            var bounds = presence.Backend.TryGetWorldBounds(out var world) ? world : PresenceBounds.Everywhere;
            while (open.IsOpen && open.TryReceiveDatagram(out var datagram))
            {
                presence.Counters.PosesReceived++;
                if (!presence.TryGetPeer(datagram.Sender, out var peer) || peer == null || peer.IsLocal || peer.Removed || peer.Plausibility == null)
                {
                    presence.Counters.Forged++;
                    continue;
                }

                if (!PresenceWire.TryReadDatagramType(datagram.Payload, bits, out var type))
                {
                    presence.Counters.Malformed++;
                    continue;
                }

                if (type != PresenceMessages.Pose)
                {
                    // Relayed poses come from the host alone; a player that sends them would move other players.
                    if (type == PresenceMessages.Poses) presence.Counters.Forged++;
                    else presence.Counters.Malformed++;
                    continue;
                }

                if (!PresenceWire.TryReadPose(bits, out var pose))
                {
                    presence.Counters.Malformed++;
                    continue;
                }

                TakePose(peer, pose, nowMicros, bounds);
            }
        }

        private void Handle(PresencePeer peer, byte[] payload, long nowMicros)
        {
            if (!PresenceWire.TryReadMessageType(reader, payload, out var type))
            {
                presence.Counters.Malformed++;
                return;
            }

            switch (type)
            {
                case PresenceMessages.State:
                    if (PresenceWire.TryReadState(reader, out var state)) TakeState(peer, state, nowMicros);
                    else presence.Counters.Malformed++;
                    return;
                case PresenceMessages.Arrived:
                    if (PresenceWire.TryReadArrived(reader, out var world)) Arrive(peer, world, nowMicros);
                    else presence.Counters.Malformed++;
                    return;
                case PresenceMessages.Roster:
                case PresenceMessages.States:
                case PresenceMessages.Place:
                case PresenceMessages.Correct:
                    // The host's own messages: a player that sends one speaks for the host.
                    presence.Counters.Forged++;
                    return;
                default:
                    // A newer peer may add a type; an unknown one is dropped.
                    presence.Counters.Malformed++;
                    return;
            }
        }

        private void TakePose(PresencePeer peer, in PresencePoseRecord pose, long nowMicros, in PresenceBounds bounds)
        {
            var mapped = MapClock(peer, pose.Millis, nowMicros);
            if (peer.HasPose && mapped <= peer.PoseMicros)
            {
                presence.Counters.PosesStale++;
                return;
            }

            var check = peer.Plausibility!.Check(pose.Position, pose.Rotation, pose.Flags, pose.Correction, nowMicros, bounds);
            switch (check.Verdict)
            {
                case PresenceVerdict.Accepted:
                    var flags = check.Flags;
                    if ((pose.Flags & PresenceFlags.Away) != 0 && (flags & PresenceFlags.Away) == 0) presence.Counters.FlagsRefused++;

                    // Whether a player talks is what the host's own voice relay heard, never what the player says.
                    var heard = presence.IsHeard(peer);
                    if ((flags & PresenceFlags.Talking) != 0 && !heard) presence.Counters.FlagsRefused++;
                    flags = heard ? flags | PresenceFlags.Talking : flags & ~PresenceFlags.Talking;
                    peer.SetPose(mapped, pose.Position, pose.Rotation, pose.Velocity, pose.EyeCentimeters, flags);
                    MultiplayerPresence.FeedBuffer(peer, mapped, nowMicros);
                    presence.Counters.PosesAccepted++;
                    return;
                case PresenceVerdict.Ignored:
                    presence.Counters.PosesIgnored++;
                    return;
                case PresenceVerdict.Corrected:
                    presence.Counters.Corrections++;
                    Correct(peer, check);
                    return;
                default:
                    Kick(peer);
                    return;
            }
        }

        /// <summary>Sends the player back to its last validated position, under the correction's number.</summary>
        private void Correct(PresencePeer peer, in PresenceCheck check)
        {
            NetVector3 target;
            if (check.HasCorrection)
            {
                target = NetQuantization.DequantizePosition(check.CorrectTo);
            }
            else
            {
                // Nothing of the player's was ever valid here: it goes to where the world put players, if the host knows.
                var spawn = presence.RunningSpawn?.Invoke();
                if (spawn == null) return;
                target = new NetVector3(spawn.Value.Position.X, spawn.Value.Position.Y, spawn.Value.Position.Z);
            }

            peer.CorrectionDue = true;
            peer.CorrectionNumber = check.Correction;
            peer.CorrectionTarget = target;
        }

        private void Kick(PresencePeer peer)
        {
            peer.Removed = true;
            presence.Counters.Kicks++;
            presence.Log.Warn("Multiplayer: a player was removed because its movement kept breaking the session's rules.");
            try
            {
                presence.Control?.Kick(peer.Id, KickMessage);
            }
            catch (Exception exception)
            {
                presence.Log.Error("Multiplayer: removing a player failed (" + exception.GetType().Name + "); its poses are ignored from now.");
            }
        }

        private void TakeState(PresencePeer peer, in PresenceStateRecord state, long nowMicros)
        {
            var health = peer.HealthCheck!;
            if (!health.Take(state.Health, state.Maximum, nowMicros)) presence.Counters.StatesRefused++;
            peer.SetState(health.HasValue ? health.Health : 0f, health.HasValue ? health.Maximum : 0f, state.HeldItem);
        }

        /// <summary>
        /// A player runs the host's world session now: the host measures it afresh from its next pose in that world, and
        /// places it on the spawn ring, once per world.
        /// </summary>
        private void Arrive(PresencePeer peer, string world, long nowMicros)
        {
            var running = presence.RunningWorld?.Invoke();
            if (running == null || !string.Equals(world, running, StringComparison.Ordinal) || string.Equals(peer.PlacedWorld, world, StringComparison.Ordinal)) return;
            peer.PlacedWorld = world;
            peer.Slot = -1;
            peer.PlacementDue = false;
            peer.Plausibility?.Restart(nowMicros);
            var spawn = presence.RunningSpawn?.Invoke();
            if (spawn == null) return;
            occupied.Clear();
            var all = presence.Peers;
            for (var index = 0; index < all.Count; index++)
            {
                var other = all[index];
                if (!ReferenceEquals(other, peer) && other.Slot >= 0 && string.Equals(other.PlacedWorld, world, StringComparison.Ordinal)) occupied.Add(other.Slot);
            }

            var point = new NetVector3(spawn.Value.Position.X, spawn.Value.Position.Y, spawn.Value.Position.Z);
            SpawnSlot slot;
            try
            {
                if (!point.IsFinite || !planner.TryPlan(point, occupied, out slot)) return;
            }
            catch (Exception exception)
            {
                presence.Log.Error("Multiplayer: the spawn ring could not be planned (" + exception.GetType().Name + "); the player stays where the world put it.");
                return;
            }

            peer.Slot = slot.Index;
            peer.PlacementDue = true;
            peer.PlacementFeet = slot.Position;
            var rotation = spawn.Value.Rotation;
            peer.PlacementYaw = RemotePlayerAvatar.YawOf(new NetQuaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
            peer.Plausibility?.ExpectPlacement(NetQuantization.QuantizePosition(slot.Position), nowMicros);
            presence.Counters.Placements++;
        }

        /// <summary>
        /// Maps a sender's clock onto the host's: the offset follows the quickest arrival at once and drifts slowly the other
        /// way, so the mapped time never runs ahead of the host's clock, never goes back, and a sender can move its own poses
        /// within a second of their arrival at most.
        /// </summary>
        private static long MapClock(PresencePeer peer, uint millis, long nowMicros)
        {
            var sender = (long)millis * 1000;
            var offset = nowMicros - sender;
            if (!peer.HasClockOffset || Math.Abs(offset - peer.ClockOffsetMicros) > ClockJumpMicros)
            {
                peer.HasClockOffset = true;
                peer.ClockOffsetMicros = offset;
            }
            else if (offset < peer.ClockOffsetMicros)
            {
                peer.ClockOffsetMicros = offset;
            }
            else
            {
                peer.ClockOffsetMicros += (offset - peer.ClockOffsetMicros) / 256;
            }

            var mapped = sender + peer.ClockOffsetMicros;
            return mapped > nowMicros ? nowMicros : mapped;
        }
    }
}
