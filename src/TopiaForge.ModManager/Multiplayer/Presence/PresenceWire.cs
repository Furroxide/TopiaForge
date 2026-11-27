using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// What presence sends on the <c>Avatars</c> system channel (WS3.10). Poses go as datagrams on the channel's realtime
    /// lane, bit-packed with the replication model's quantization; everything that must arrive goes as messages on its
    /// stream. Every payload starts with the version and its type, and a reader refuses whole what it cannot read exactly.
    /// </summary>
    internal static class PresenceMessages
    {
        internal const byte Version = 1;

        /// <summary>Datagram, player to host: the player's own pose. See <see cref="PresenceWire.WritePose"/>.</summary>
        internal const byte Pose = 1;

        /// <summary>Datagram, host to player: other players' poses, each named by presence index. See <see cref="PresenceWire.BeginPoses"/>.</summary>
        internal const byte Poses = 2;

        /// <summary>Message, host to player: which participant each presence index names. <c>[count varuint]([index byte][participant id string])...</c>.</summary>
        internal const byte Roster = 16;

        /// <summary>
        /// Message, player to host: the player's health and what it holds. <c>[health f32][maximum f32][held byte][held item
        /// string, when held is 1]</c>.
        /// </summary>
        internal const byte State = 17;

        /// <summary>Message, host to player: other players' states. <c>[count varuint]([index byte][state as above])...</c>.</summary>
        internal const byte States = 18;

        /// <summary>Message, player to host: the player runs the host's world session now. <c>[world string]</c>.</summary>
        internal const byte Arrived = 19;

        /// <summary>
        /// Message, host to player: where on the spawn ring to stand in that world. <c>[world string][slot byte][x f32][y f32]
        /// [z f32][yaw f32]</c>.
        /// </summary>
        internal const byte Place = 20;

        /// <summary>
        /// Message, host to player: a pose broke the session's rules; go back here and number poses so from now.
        /// <c>[correction byte][x f32][y f32][z f32]</c>.
        /// </summary>
        internal const byte Correct = 21;

        /// <summary>The largest participant id or held item a message carries: 128 characters, at most 512 UTF-8 bytes.</summary>
        internal const int MaximumTextBytes = 512;

        /// <summary>The largest world token: a session id.</summary>
        internal const int MaximumWorldBytes = 128;

        /// <summary>The most poses one datagram carries; far fewer fit the per-player budget in a tick.</summary>
        internal const int MaximumRecords = 32;

        /// <summary>The most entries a roster or a state message lists: every presence index.</summary>
        internal const int MaximumEntries = 255;

        /// <summary>The largest message presence writes.</summary>
        internal const int MaximumMessageBytes = 16 * 1024;

        /// <summary>A pose datagram's size: the header and 187 bits, 26 bytes.</summary>
        internal const int PoseBytes = (16 + PoseBits + 7) / 8;

        /// <summary>The bits of one relayed pose: index, age, flags, eyes, position, rotation and velocity.</summary>
        internal const int RelayRecordBits = 8 + 16 + 8 + 8 + 3 * NetQuantization.PositionAxisBits + NetQuantization.RotationBits + 3 * NetQuantization.VelocityAxisBits;

        /// <summary>One relayed pose's share of a datagram, rounded up, which the host's scheduler budgets by.</summary>
        internal const int RelayRecordBytes = (RelayRecordBits + 7) / 8;

        /// <summary>A relayed datagram's header: the version, the type, the host's time and the count.</summary>
        internal const int RelayHeaderBits = 16 + 32 + 8;

        /// <summary>The flags a pose may set; any other bit makes it malformed.</summary>
        internal const PresenceFlags KnownFlags =
            PresenceFlags.Crouching | PresenceFlags.Talking | PresenceFlags.Typing | PresenceFlags.Away | PresenceFlags.Teleported;

        private const int PoseBits = 32 + 8 + 8 + 8 + 3 * NetQuantization.PositionAxisBits + NetQuantization.RotationBits + 3 * NetQuantization.VelocityAxisBits;
    }

    /// <summary>One pose as the wire carries it.</summary>
    internal struct PresencePoseRecord
    {
        /// <summary>The sender's clock in milliseconds, for a player's own pose.</summary>
        public uint Millis;

        /// <summary>How long before the datagram's host time the host accepted it, for a relayed pose.</summary>
        public ushort AgeMillis;

        /// <summary>The presence index of the player it belongs to, for a relayed pose.</summary>
        public byte Index;

        /// <summary>The host correction the sender had taken when it sampled the pose.</summary>
        public byte Correction;

        public PresenceFlags Flags;

        /// <summary>How high the player's eyes are above its feet, in centimetres.</summary>
        public byte EyeCentimeters;

        public NetQuantizedPosition Position;

        public uint Rotation;

        public NetQuantizedVelocity Velocity;
    }

    /// <summary>One player's state as the wire carries it.</summary>
    internal readonly struct PresenceStateRecord
    {
        public PresenceStateRecord(byte index, float health, float maximum, string? heldItem)
        {
            Index = index;
            Health = health;
            Maximum = maximum;
            HeldItem = heldItem;
        }

        public byte Index { get; }

        public float Health { get; }

        public float Maximum { get; }

        public string? HeldItem { get; }
    }

    /// <summary>Writes and reads presence's payloads. Writing reuses its caller's buffers; reading never throws and never trusts.</summary>
    internal static class PresenceWire
    {
        /// <summary>The largest coordinate a placement or a correction names, as session follow's spawn bound.</summary>
        private const float MaximumCoordinate = 1_000_000f;

        /// <summary>Writes a player's own pose datagram: 26 bytes.</summary>
        internal static void WritePose(NetBitWriter writer, in PresencePoseRecord pose)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Reset();
            writer.WriteBits(PresenceMessages.Version, 8);
            writer.WriteBits(PresenceMessages.Pose, 8);
            writer.WriteBits(pose.Millis, 32);
            writer.WriteBits(pose.Correction, 8);
            writer.WriteBits((uint)(pose.Flags & PresenceMessages.KnownFlags), 8);
            writer.WriteBits(pose.EyeCentimeters, 8);
            WriteMotion(writer, pose);
        }

        /// <summary>Starts a host's datagram of <paramref name="count"/> relayed poses, as at <paramref name="hostMillis"/>.</summary>
        internal static void BeginPoses(NetBitWriter writer, uint hostMillis, int count)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (count < 1 || count > PresenceMessages.MaximumRecords) throw new ArgumentOutOfRangeException(nameof(count));
            writer.Reset();
            writer.WriteBits(PresenceMessages.Version, 8);
            writer.WriteBits(PresenceMessages.Poses, 8);
            writer.WriteBits(hostMillis, 32);
            writer.WriteBits((uint)count, 8);
        }

        /// <summary>Appends one relayed pose after <see cref="BeginPoses"/>.</summary>
        internal static void WriteRelay(NetBitWriter writer, in PresencePoseRecord pose)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (pose.Index == 0) throw new ArgumentOutOfRangeException(nameof(pose), "A relayed pose names a presence index from 1.");
            writer.WriteBits(pose.Index, 8);
            writer.WriteBits(pose.AgeMillis, 16);
            writer.WriteBits((uint)(pose.Flags & PresenceMessages.KnownFlags), 8);
            writer.WriteBits(pose.EyeCentimeters, 8);
            WriteMotion(writer, pose);
        }

        /// <summary>Reads a datagram's version and type, leaving the reader after them.</summary>
        internal static bool TryReadDatagramType(ArraySegment<byte> payload, NetBitReader reader, out byte type)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            type = 0;
            if (payload.Array == null || payload.Count < 2 || payload.Count > Mods.Internal.InternalSystemLimits.MaximumDatagramBytes) return false;
            reader.Reset(payload);
            if (!reader.TryReadBits(8, out var version) || version != PresenceMessages.Version || !reader.TryReadBits(8, out var read)) return false;
            type = (byte)read;
            return true;
        }

        /// <summary>Reads a player's pose after its type: exactly its fields, known flags only, nothing after.</summary>
        internal static bool TryReadPose(NetBitReader reader, out PresencePoseRecord pose)
        {
            pose = default;
            if (!reader.TryReadBits(32, out var millis) || !reader.TryReadBits(8, out var correction) ||
                !TryReadFlags(reader, out var flags) || !reader.TryReadBits(8, out var eye) ||
                !TryReadMotion(reader, ref pose) || !reader.IsAtCanonicalEnd)
            {
                pose = default;
                return false;
            }

            pose.Millis = millis;
            pose.Correction = (byte)correction;
            pose.Flags = flags;
            pose.EyeCentimeters = (byte)eye;
            return true;
        }

        /// <summary>Reads a relayed datagram's host time and count after its type.</summary>
        internal static bool TryReadPosesHeader(NetBitReader reader, out uint hostMillis, out int count)
        {
            count = 0;
            if (!reader.TryReadBits(32, out hostMillis) || !reader.TryReadBits(8, out var read) || read < 1 || read > PresenceMessages.MaximumRecords)
            {
                hostMillis = 0;
                return false;
            }

            count = (int)read;
            return true;
        }

        /// <summary>Reads one relayed pose; the caller checks the end once the count is read.</summary>
        internal static bool TryReadRelay(NetBitReader reader, out PresencePoseRecord pose)
        {
            pose = default;
            if (!reader.TryReadBits(8, out var index) || index == 0 || !reader.TryReadBits(16, out var age) ||
                !TryReadFlags(reader, out var flags) || !reader.TryReadBits(8, out var eye) || !TryReadMotion(reader, ref pose))
            {
                pose = default;
                return false;
            }

            pose.Index = (byte)index;
            pose.AgeMillis = (ushort)age;
            pose.Flags = flags;
            pose.EyeCentimeters = (byte)eye;
            return true;
        }

        internal static void BeginMessage(WorldWireWriter writer, byte type)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Reset();
            writer.WriteByte(PresenceMessages.Version);
            writer.WriteByte(type);
        }

        /// <summary>Reads a message's version and type.</summary>
        internal static bool TryReadMessageType(WorldWireReader reader, byte[] payload, out byte type)
        {
            type = 0;
            if (payload == null || payload.Length < 2 || payload.Length > PresenceMessages.MaximumMessageBytes) return false;
            reader.Reset(payload);
            return reader.TryReadByte(out var version) && version == PresenceMessages.Version && reader.TryReadByte(out type);
        }

        internal static void WriteRoster(WorldWireWriter writer, IReadOnlyList<KeyValuePair<byte, ParticipantId>> entries)
        {
            BeginMessage(writer, PresenceMessages.Roster);
            writer.WriteVarUInt((ulong)entries.Count);
            for (var index = 0; index < entries.Count; index++)
            {
                writer.WriteByte(entries[index].Key);
                writer.WriteString(entries[index].Value.Value);
            }
        }

        /// <summary>Reads a roster after its type into <paramref name="into"/>: indices from 1, each once, and valid ids, each once.</summary>
        internal static bool TryReadRoster(WorldWireReader reader, List<KeyValuePair<byte, ParticipantId>> into)
        {
            into.Clear();
            if (!reader.TryReadInt32(PresenceMessages.MaximumEntries, out var count)) return false;
            for (var entry = 0; entry < count; entry++)
            {
                if (!reader.TryReadByte(out var index) || index == 0 || !reader.TryReadString(PresenceMessages.MaximumTextBytes, out var id) ||
                    !IsIdentity(id))
                {
                    into.Clear();
                    return false;
                }

                var participant = new ParticipantId(id);
                for (var seen = 0; seen < into.Count; seen++)
                {
                    if (into[seen].Key == index || into[seen].Value.Equals(participant))
                    {
                        into.Clear();
                        return false;
                    }
                }

                into.Add(new KeyValuePair<byte, ParticipantId>(index, participant));
            }

            if (reader.AtEnd) return true;
            into.Clear();
            return false;
        }

        internal static void WriteState(WorldWireWriter writer, float health, float maximum, string? heldItem)
        {
            BeginMessage(writer, PresenceMessages.State);
            WriteStateBody(writer, health, maximum, heldItem);
        }

        /// <summary>Reads a player's state after its type. Health is checked by the host's rules, not here; the held item's text is.</summary>
        internal static bool TryReadState(WorldWireReader reader, out PresenceStateRecord state)
        {
            state = default;
            if (!TryReadStateBody(reader, 0, out state) || !reader.AtEnd) return false;
            return true;
        }

        internal static void WriteStates(WorldWireWriter writer, IReadOnlyList<PresenceStateRecord> states)
        {
            BeginMessage(writer, PresenceMessages.States);
            writer.WriteVarUInt((ulong)states.Count);
            for (var index = 0; index < states.Count; index++)
            {
                writer.WriteByte(states[index].Index);
                WriteStateBody(writer, states[index].Health, states[index].Maximum, states[index].HeldItem);
            }
        }

        /// <summary>Reads the host's states whole, refusing health that cannot form a usable public pose.</summary>
        internal static bool TryReadStates(WorldWireReader reader, List<PresenceStateRecord> into)
        {
            into.Clear();
            if (!reader.TryReadInt32(PresenceMessages.MaximumEntries, out var count)) return false;
            for (var entry = 0; entry < count; entry++)
            {
                if (!reader.TryReadByte(out var index) || index == 0 || !TryReadStateBody(reader, index, out var state) || !IsHealth(state))
                {
                    into.Clear();
                    return false;
                }

                into.Add(state);
            }

            if (reader.AtEnd) return true;
            into.Clear();
            return false;
        }

        internal static void WriteArrived(WorldWireWriter writer, string world)
        {
            BeginMessage(writer, PresenceMessages.Arrived);
            writer.WriteString(world);
        }

        internal static bool TryReadArrived(WorldWireReader reader, out string world) =>
            reader.TryReadString(PresenceMessages.MaximumWorldBytes, out world) && IsWorld(world) && reader.AtEnd;

        internal static void WritePlace(WorldWireWriter writer, string world, byte slot, NetVector3 feet, float yawDegrees)
        {
            BeginMessage(writer, PresenceMessages.Place);
            writer.WriteString(world);
            writer.WriteByte(slot);
            writer.WriteSingle(feet.X);
            writer.WriteSingle(feet.Y);
            writer.WriteSingle(feet.Z);
            writer.WriteSingle(yawDegrees);
        }

        internal static bool TryReadPlace(WorldWireReader reader, out string world, out byte slot, out NetVector3 feet, out float yawDegrees)
        {
            feet = default;
            yawDegrees = 0f;
            slot = 0;
            if (!reader.TryReadString(PresenceMessages.MaximumWorldBytes, out world) || !IsWorld(world) || !reader.TryReadByte(out slot) ||
                slot >= SpawnSlotPlanner.SlotCount || !TryReadPoint(reader, out feet) || !reader.TryReadSingle(out yawDegrees) ||
                !IsCoordinate(yawDegrees) || !reader.AtEnd)
            {
                world = string.Empty;
                return false;
            }

            return true;
        }

        internal static void WriteCorrect(WorldWireWriter writer, byte correction, NetVector3 feet)
        {
            BeginMessage(writer, PresenceMessages.Correct);
            writer.WriteByte(correction);
            writer.WriteSingle(feet.X);
            writer.WriteSingle(feet.Y);
            writer.WriteSingle(feet.Z);
        }

        internal static bool TryReadCorrect(WorldWireReader reader, out byte correction, out NetVector3 feet)
        {
            feet = default;
            return reader.TryReadByte(out correction) && TryReadPoint(reader, out feet) && reader.AtEnd;
        }

        /// <summary>Gets whether <paramref name="value"/> may name a participant: what <see cref="ParticipantId"/> takes.</summary>
        internal static bool IsIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
            foreach (var character in value)
            {
                if (char.IsControl(character)) return false;
            }

            return true;
        }

        /// <summary>Gets whether <paramref name="value"/> may name a held item: a catalog id of at most 128 characters, no control characters.</summary>
        internal static bool IsHeldItem(string value) => IsIdentity(value);

        /// <summary>Gets whether <paramref name="value"/> may name a world session: printable ASCII of 1 to 64 characters.</summary>
        internal static bool IsWorld(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (var character in value)
            {
                if (character < 0x21 || character > 0x7E) return false;
            }

            return true;
        }

        private static void WriteMotion(NetBitWriter writer, in PresencePoseRecord pose)
        {
            NetQuantization.WritePosition(writer, pose.Position);
            writer.WriteBits(pose.Rotation, NetQuantization.RotationBits);
            NetQuantization.WriteVelocity(writer, pose.Velocity);
        }

        private static bool TryReadMotion(NetBitReader reader, ref PresencePoseRecord pose)
        {
            if (!NetQuantization.TryReadPosition(reader, out var position) ||
                !reader.TryReadBits(NetQuantization.RotationBits, out var rotation) ||
                !NetQuantization.TryReadVelocity(reader, out var velocity))
            {
                return false;
            }

            pose.Position = position;
            pose.Rotation = rotation;
            pose.Velocity = velocity;
            return true;
        }

        private static bool TryReadFlags(NetBitReader reader, out PresenceFlags flags)
        {
            flags = PresenceFlags.None;
            if (!reader.TryReadBits(8, out var bits) || (bits & ~(uint)PresenceMessages.KnownFlags) != 0) return false;
            flags = (PresenceFlags)bits;
            return true;
        }

        private static void WriteStateBody(WorldWireWriter writer, float health, float maximum, string? heldItem)
        {
            writer.WriteSingle(health);
            writer.WriteSingle(maximum);
            writer.WriteByte(heldItem == null ? (byte)0 : (byte)1);
            if (heldItem != null) writer.WriteString(heldItem);
        }

        private static bool TryReadStateBody(WorldWireReader reader, byte index, out PresenceStateRecord state)
        {
            state = default;
            if (!reader.TryReadSingle(out var health) || !reader.TryReadSingle(out var maximum) || !reader.TryReadByte(out var held) || held > 1)
            {
                return false;
            }

            string? item = null;
            if (held == 1)
            {
                if (!reader.TryReadString(PresenceMessages.MaximumTextBytes, out var text) || !IsHeldItem(text)) return false;
                item = text;
            }

            state = new PresenceStateRecord(index, health, maximum, item);
            return true;
        }

        // Zero health and maximum are the normal sentinel when the host cannot sample health.
        private static bool IsHealth(in PresenceStateRecord state) =>
            !float.IsNaN(state.Health) && !float.IsInfinity(state.Health) &&
            !float.IsNaN(state.Maximum) && !float.IsInfinity(state.Maximum) &&
            state.Health >= 0f && state.Maximum >= state.Health;

        private static bool TryReadPoint(WorldWireReader reader, out NetVector3 point)
        {
            point = default;
            if (!reader.TryReadSingle(out var x) || !reader.TryReadSingle(out var y) || !reader.TryReadSingle(out var z)) return false;
            if (!IsCoordinate(x) || !IsCoordinate(y) || !IsCoordinate(z)) return false;
            point = new NetVector3(x, y, z);
            return true;
        }

        private static bool IsCoordinate(float value) => !float.IsNaN(value) && value >= -MaximumCoordinate && value <= MaximumCoordinate;
    }
}
