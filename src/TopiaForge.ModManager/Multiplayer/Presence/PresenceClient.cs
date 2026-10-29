using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Presence on a joined player (WS3.10). It sends its own player's pose to the host, 20 a second while it moves and one
    /// a second at rest, numbered with the host's latest correction, and its state when it changes; tells the host when it
    /// runs the host's world, so the host places it on the spawn ring; and moves its player where the host's placement or
    /// correction says. It keeps the host's roster and feeds each other player's relayed poses, stamped on the host's
    /// clock, into that player's interpolation buffer. Only the host speaks for other players: a pose in a player's own
    /// format is refused. Main thread only.
    /// </summary>
    internal sealed class PresenceClient
    {
        private readonly MultiplayerPresence presence;
        private readonly NetBitReader bits = new NetBitReader();
        private readonly NetBitWriter pose = new NetBitWriter(PresenceMessages.PoseBytes);
        private readonly WorldWireReader reader = new WorldWireReader();
        private readonly WorldWireWriter writer = new WorldWireWriter(PresenceMessages.MaximumMessageBytes);
        private readonly List<KeyValuePair<byte, ParticipantId>> roster = new List<KeyValuePair<byte, ParticipantId>>();
        private readonly List<PresenceStateRecord> states = new List<PresenceStateRecord>();
        private bool active;
        private byte correction;
        private string? arrivedWorld;
        private ulong generationSeen;
        private bool stateOwed;

        internal PresenceClient(MultiplayerPresence presence)
        {
            this.presence = presence ?? throw new ArgumentNullException(nameof(presence));
        }

        internal bool IsActive => active;

        /// <summary>Gets the host's latest correction, which this player's poses carry.</summary>
        internal byte Correction => correction;

        internal void Activate()
        {
            active = true;
            correction = 0;
            arrivedWorld = null;
            generationSeen = 0;
            stateOwed = false;
        }

        internal void Deactivate() => active = false;

        /// <summary>Frame step 2: what the host sent since the last frame.</summary>
        internal void Receive(long nowMicros)
        {
            var open = presence.Channel;
            if (!active || open == null) return;
            while (open.IsOpen && open.TryReceive(out var message))
            {
                if (presence.Control?.SessionSnapshot.HostParticipantId?.Equals(message.Sender) == true) Handle(message.Payload);
                else presence.Counters.Forged++;
            }
            while (open.IsOpen && open.TryReceiveDatagram(out var datagram))
            {
                if (!PresenceWire.TryReadDatagramType(datagram.Payload, bits, out var type))
                {
                    presence.Counters.Malformed++;
                    continue;
                }

                if (type == PresenceMessages.Poses) TakePoses(nowMicros);
                else if (type == PresenceMessages.Pose) presence.Counters.Forged++;
                else presence.Counters.Malformed++;
            }
        }

        /// <summary>Frame step 4: this player's pose and state, and its arrival in the host's world.</summary>
        internal void Send(long nowMicros)
        {
            if (!active) return;
            var open = presence.Channel;
            var sampler = presence.Local;
            var local = presence.LocalParticipant;
            PresencePeer? own = null;
            if (local != null) presence.TryGetPeer(local.Value, out own);
            if (open != null && open.Generation != generationSeen)
            {
                // A stream that started or restarted has heard nothing yet: the host needs the arrival and the state again.
                generationSeen = open.Generation;
                arrivedWorld = null;
                stateOwed = true;
            }

            if (sampler.TryTakePose(nowMicros, correction, out var taken))
            {
                own?.SetPose(nowMicros, taken.Position, taken.Rotation, taken.Velocity, taken.EyeCentimeters, taken.Flags & ~PresenceFlags.Teleported);
                if (open != null)
                {
                    PresenceWire.WritePose(pose, taken);
                    var payload = pose.AsSegment();
                    if (open.SendDatagram(payload, InternalSystemTarget.Host).Succeeded)
                    {
                        presence.Counters.PosesSent++;
                        presence.Counters.DatagramBytes += payload.Count;
                    }
                }
            }

            if (sampler.TryTakeState(nowMicros)) stateOwed = true;
            if (open == null) return;
            if (stateOwed && sampler.HasPlayer)
            {
                PresenceWire.WriteState(writer, sampler.HasHealth ? sampler.Health : 0f, sampler.HasHealth ? sampler.MaximumHealth : 0f, sampler.HeldItem);
                if (open.CanSend(InternalSystemTarget.Host, writer.Length) && open.Send(writer.Segment, InternalSystemTarget.Host).Succeeded)
                {
                    stateOwed = false;
                    own?.SetState(sampler.HasHealth ? sampler.Health : 0f, sampler.HasHealth ? sampler.MaximumHealth : 0f, sampler.HeldItem);
                }
            }

            var world = presence.RunningWorld?.Invoke();
            if (world != null && !string.Equals(world, arrivedWorld, StringComparison.Ordinal) && PresenceWire.IsWorld(world))
            {
                PresenceWire.WriteArrived(writer, world);
                if (open.CanSend(InternalSystemTarget.Host, writer.Length) && open.Send(writer.Segment, InternalSystemTarget.Host).Succeeded) arrivedWorld = world;
            }
        }

        private void Handle(byte[] payload)
        {
            if (!PresenceWire.TryReadMessageType(reader, payload, out var type))
            {
                presence.Counters.Malformed++;
                return;
            }

            switch (type)
            {
                case PresencePresentationWire.Message:
                    if (payload.Length > PresencePresentationWire.MaximumBytes || !presence.TakePresentation(reader)) presence.Counters.Malformed++;
                    return;
                case PresenceMessages.Roster:
                    if (!PresenceWire.TryReadRoster(reader, roster))
                    {
                        presence.Counters.Malformed++;
                        return;
                    }

                    presence.Roster.Replace(roster);
                    var all = presence.Peers;
                    for (var index = 0; index < all.Count; index++)
                    {
                        all[index].Index = presence.Roster.TryGetIndex(all[index].Id, out var assigned) ? assigned : (byte)0;
                    }

                    return;
                case PresenceMessages.States:
                    if (!PresenceWire.TryReadStates(reader, states))
                    {
                        presence.Counters.Malformed++;
                        return;
                    }

                    for (var index = 0; index < states.Count; index++)
                    {
                        var state = states[index];
                        if (!presence.Roster.TryGetParticipant(state.Index, out var id) || !presence.TryGetPeer(id, out var peer) || peer == null || peer.IsLocal) continue;
                        peer.SetState(state.Health, state.Maximum, state.HeldItem);
                    }

                    return;
                case PresenceMessages.Place:
                    if (PresenceWire.TryReadPlace(reader, out var world, out _, out var feet, out var yaw)) Place(world, feet, yaw);
                    else presence.Counters.Malformed++;
                    return;
                case PresenceMessages.Correct:
                    if (PresenceWire.TryReadCorrect(reader, out var number, out var target)) TakeCorrection(number, target);
                    else presence.Counters.Malformed++;
                    return;
                case PresenceMessages.State:
                case PresenceMessages.Arrived:
                    presence.Counters.Forged++;
                    return;
                default:
                    presence.Counters.Malformed++;
                    return;
            }
        }

        /// <summary>Takes the host's relayed poses: each by its index, stamped on the host's clock and arriving now.</summary>
        private void TakePoses(long nowMicros)
        {
            if (!PresenceWire.TryReadPosesHeader(bits, out var hostMillis, out var count))
            {
                presence.Counters.Malformed++;
                return;
            }

            // Read every record first, so a datagram cut short or with a bit to spare changes nothing at all.
            var start = bits.Position;
            for (var index = 0; index < count; index++)
            {
                if (!PresenceWire.TryReadRelay(bits, out _))
                {
                    presence.Counters.Malformed++;
                    return;
                }
            }

            if (!bits.IsAtCanonicalEnd)
            {
                presence.Counters.Malformed++;
                return;
            }

            bits.Seek(start);
            for (var index = 0; index < count; index++)
            {
                PresenceWire.TryReadRelay(bits, out var record);
                var micros = (long)hostMillis * 1000 - record.AgeMillis * 1000L;
                if (micros < 0 || !presence.Roster.TryGetParticipant(record.Index, out var id) || !presence.TryGetPeer(id, out var peer) || peer == null || peer.IsLocal)
                {
                    continue;
                }

                if (peer.HasPose && micros <= peer.PoseMicros)
                {
                    presence.Counters.PosesStale++;
                    continue;
                }

                peer.SetPose(micros, record.Position, record.Rotation, record.Velocity, record.EyeCentimeters, record.Flags);
                MultiplayerPresence.FeedBuffer(peer, micros, nowMicros);
            }
        }

        /// <summary>The host placed this player on the spawn ring in the world it runs: the player moves there, if still in that world.</summary>
        private void Place(string world, NetVector3 feet, float yawDegrees)
        {
            if (!string.Equals(world, presence.RunningWorld?.Invoke(), StringComparison.Ordinal)) return;
            if (presence.Backend.TeleportLocalPlayer(feet, yawDegrees, keepFacing: false)) presence.Counters.MovesTaken++;
        }

        /// <summary>A pose broke the host's rules: the player goes back where the host says, and numbers its poses so from now.</summary>
        private void TakeCorrection(byte number, NetVector3 target)
        {
            correction = number;
            if (presence.Backend.TeleportLocalPlayer(target, 0f, keepFacing: true)) presence.Counters.MovesTaken++;
        }
    }
}
