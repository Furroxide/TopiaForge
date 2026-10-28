using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        private readonly ProtocolWriter privateWriter = new ProtocolWriter();
        private const byte PrivateStateKind = (byte)InternalSystemChannelKind.PrivateState;

        // Each packet is a complete field (under 20 KiB), so no partial-value assembler is needed.
        // Changed fields coalesce while credits are exhausted; only confirmed bytes can enter this stream.
        private bool HasPrivatePending(HostConnection connection)
        {
            if (!connection.IsJoined || connection.Participant == null) return false;
            if (connection.PrivateSent.Count != privateStates.Count) return true;
            if (privateStates.Count == 0) return false;
            if (connection.PrivateGeneration != connection.StreamGeneration) return true;
            foreach (var record in privateStates.Values)
            {
                if (!connection.PrivateSent.TryGetValue(record.Key, out var sent) || !ReferenceEquals(sent.Record, record)) return true;
                if (record.Values.TryGetValue(connection.Participant.Id, out var slot) && slot.ConfirmedVersion != sent.Version) return true;
            }
            return false;
        }

        private void FlushPrivateState(HostConnection connection)
        {
            if (!connection.IsJoined || connection.Participant == null) return;
            var player = connection.Participant.Id;
            if (connection.PrivateGeneration != connection.StreamGeneration)
            {
                if (privateStates.Count == 0 && connection.PrivateSent.Count == 0) return;
                if (!SendPrivate(connection, player, 0, null, null)) return;
                connection.PrivateSent.Clear();
                connection.PrivateGeneration = connection.StreamGeneration;
            }

            connection.PrivateRemoved.Clear();
            var removalsBlocked = false;
            foreach (var sent in connection.PrivateSent)
            {
                if (privateStates.ContainsKey(sent.Key)) continue;
                if (!SendPrivate(connection, player, 2, sent.Key, null)) { removalsBlocked = true; break; }
                connection.PrivateRemoved.Add(sent.Key);
            }
            foreach (var key in connection.PrivateRemoved) connection.PrivateSent.Remove(key);
            // A shorter new field might fit when a long removal does not. Sending it first could exceed
            // the receiver's 64-field inventory even though the host's current inventory is within bounds.
            if (removalsBlocked) return;

            // Finish unsent baseline fields before spending credits on fields already sent and changed again.
            // Otherwise a churning early field could indefinitely starve the tail of a large baseline.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var record in privateStates.Values)
                {
                    var slot = record.Slot(player);
                    var sentBefore = connection.PrivateSent.TryGetValue(record.Key, out var sent) && ReferenceEquals(sent.Record, record);
                    if (sentBefore != (pass == 1)) continue;
                    if (sentBefore)
                    {
                        if (sent.Version == slot.ConfirmedVersion) continue;
                        if (slot.MaximumUpdatesPerSecond > 0 && nowMicros - sent.AtMicros < 1_000_000 / slot.MaximumUpdatesPerSecond) continue;
                    }
                    if (!SendPrivate(connection, player, 1, record.Key, slot)) return;
                    connection.PrivateSent[record.Key] = new PrivateSentValue(record, slot.ConfirmedVersion, nowMicros);
                }
            }
        }

        private bool SendPrivate(HostConnection connection, ParticipantId player, byte operation, WireKey? key, StateRecord? slot)
        {
            privateWriter.Reset();
            privateWriter.WriteByte(1); // Private envelope revision, independent of contract DTO revisions.
            privateWriter.WriteByte(operation);
            privateWriter.WriteString(player.Value);
            if (key.HasValue)
            {
                privateWriter.WriteString(key.Value.Owner);
                privateWriter.WriteString(key.Value.Id);
            }
            if (slot != null)
            {
                privateWriter.WriteVarUInt(slot.ConfirmedVersion);
                privateWriter.WriteBytes(slot.ConfirmedBytes);
            }
            var stream = connection.Streams.Get(PrivateStateKind);
            if (!stream.HasRoom(privateWriter.Length, InternalSystemLimits.HostWindowMessages, InternalSystemLimits.HostWindowBytes))
            {
                stream.MarkBlocked(nowMicros);
                return false;
            }
            var bytes = privateWriter.ToArray();
            stream.CountSent(bytes.Length);
            connection.Enqueue(new SystemItem(epoch, connection.JoinGeneration, PrivateStateKind, bytes));
            return true;
        }

        private void ReceivePrivateState(ParticipantId sender, byte[] payload)
        {
            if (Role != EngineRole.Client || !sender.Equals(SystemHostSender) || !localParticipantId.HasValue)
                throw new ProtocolException("Private state requires an authenticated host and local player.");
            var reader = new ProtocolReader(payload);
            if (reader.ReadByte() != 1) throw new ProtocolException("Unsupported private state revision.");
            var operation = reader.ReadByte();
            if (!string.Equals(reader.ReadIdentity(), localParticipantId.Value.Value, StringComparison.Ordinal))
                throw new ProtocolException("Private state names a different recipient.");
            if (operation == 0)
            {
                if (!reader.AtEnd) throw new ProtocolException("Private state contained trailing bytes.");
                if (privateBaseline) throw new ProtocolException("A private stream restarted without a new generation.");
                privateStaged.Clear();
                privateChangesPending = true;
                privateBaseline = true;
                return;
            }
            if (!privateBaseline) throw new ProtocolException("Private state arrived before its baseline.");
            var key = new WireKey(reader.ReadIdentity(), reader.ReadIdentity());
            if (operation == 2)
            {
                if (!reader.AtEnd) throw new ProtocolException("Private state contained trailing bytes.");
                privateStaged.Remove(key);
                privateChangesPending = true;
                return;
            }
            if (operation != 1) throw new ProtocolException("Unknown private state operation.");
            var version = reader.ReadVarUInt();
            var bytes = reader.ReadBytes(MaximumPrivateValueBytes);
            if (!reader.AtEnd) throw new ProtocolException("Private state contained trailing bytes.");
            if (!privateStaged.ContainsKey(key) && privateStaged.Count >= MaximumPrivateFields)
                throw new ProtocolException("The private field inventory exceeded its bound.");
            if (privateStates.TryGetValue(key, out var record))
            {
                var slot = record.Slot(localParticipantId.Value);
                if (!slot.CanDecode(bytes)) throw new ProtocolException("A private value did not match its codec.");
            }
            privateStaged[key] = new StagedState(key, version, bytes);
            privateChangesPending = true;
        }
    }
}
