using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Presence's payloads (WS3.10): poses round-trip through the replication model's quantization within its stated
    /// errors, relayed datagrams carry several, the messages that must arrive round-trip exactly, and every payload that
    /// is cut, bent, padded or names what it may not is refused whole.
    /// </summary>
    internal static class PresenceWireTests
    {
        internal static void Run()
        {
            PosesRoundTripWithinTheQuantizationErrors();
            RelayedPosesRoundTrip();
            BentDatagramsAreRefusedWhole();
            MessagesRoundTrip();
            BentMessagesAreRefusedWhole();
            Console.WriteLine("PresenceWireTests passed.");
        }

        private static PresencePoseRecord RandomPose(SeededRandom random, out NetVector3 feet, out NetQuaternion view, out NetVector3 velocity)
        {
            feet = new NetVector3((float)random.NextDouble(-4000, 4000), (float)random.NextDouble(-500, 500), (float)random.NextDouble(-4000, 4000));
            var x = random.NextDouble(-1, 1);
            var y = random.NextDouble(-1, 1);
            var z = random.NextDouble(-1, 1);
            var w = random.NextDouble(-1, 1);
            var norm = Math.Sqrt(x * x + y * y + z * z + w * w);
            view = new NetQuaternion((float)(x / norm), (float)(y / norm), (float)(z / norm), (float)(w / norm));
            velocity = new NetVector3((float)random.NextDouble(-60, 60), (float)random.NextDouble(-60, 60), (float)random.NextDouble(-60, 60));
            return new PresencePoseRecord
            {
                Millis = (uint)random.NextUInt64(),
                Correction = (byte)random.NextInt(0, 256),
                Flags = (PresenceFlags)random.NextInt(0, 32),
                EyeCentimeters = (byte)random.NextInt(0, 256),
                Position = NetQuantization.QuantizePosition(feet),
                Rotation = NetQuantization.QuantizeRotation(view),
                Velocity = NetQuantization.QuantizeVelocity(velocity),
                Index = (byte)random.NextInt(1, 256),
                AgeMillis = (ushort)random.NextInt(0, 65536)
            };
        }

        private static void PosesRoundTripWithinTheQuantizationErrors()
        {
            var random = new SeededRandom(0x5EED_3_10);
            var writer = new NetBitWriter(InternalSystemLimits.MaximumDatagramBytes);
            var reader = new NetBitReader();
            for (var sample = 0; sample < 2000; sample++)
            {
                var sent = RandomPose(random, out var feet, out var view, out var velocity);
                PresenceWire.WritePose(writer, sent);
                Equal(PresenceMessages.PoseBytes, writer.ByteLength, "a pose is 26 bytes");
                True(PresenceWire.TryReadDatagramType(writer.AsSegment(), reader, out var type) && type == PresenceMessages.Pose, "it reads as a pose");
                True(PresenceWire.TryReadPose(reader, out var read), "and reads back (" + sample + ")");
                True(read.Millis == sent.Millis && read.Correction == sent.Correction && read.Flags == sent.Flags && read.EyeCentimeters == sent.EyeCentimeters,
                    "time, correction, flags and eyes are exact");
                True(read.Position.Equals(sent.Position) && read.Rotation == sent.Rotation && read.Velocity.Equals(sent.Velocity), "the quantized motion is exact");
                var back = NetQuantization.DequantizePosition(read.Position);
                Near(feet.X, back.X, NetQuantization.PositionMaximumErrorMeters, "x within half a grid step");
                Near(feet.Y, back.Y, NetQuantization.PositionMaximumErrorMeters, "y within half a grid step");
                Near(feet.Z, back.Z, NetQuantization.PositionMaximumErrorMeters, "z within half a grid step");
                var angle = MultiplayerQuantizationTests.AngleDegrees(view, NetQuantization.DequantizeRotation(read.Rotation));
                True(angle <= NetQuantization.RotationMaximumErrorDegrees, "the view within " + NetQuantization.RotationMaximumErrorDegrees + " degrees (" + angle + ")");
                var moving = NetQuantization.DequantizeVelocity(read.Velocity);
                Near(velocity.X, moving.X, NetQuantization.VelocityMaximumErrorPerSecond, "velocity within half a step");
            }
        }

        private static void RelayedPosesRoundTrip()
        {
            var random = new SeededRandom(77);
            var writer = new NetBitWriter(InternalSystemLimits.MaximumDatagramBytes);
            var reader = new NetBitReader();
            for (var count = 1; count <= PresenceMessages.MaximumRecords; count++)
            {
                var sent = new List<PresencePoseRecord>();
                PresenceWire.BeginPoses(writer, 123_456u + (uint)count, count);
                for (var index = 0; index < count; index++)
                {
                    var pose = RandomPose(random, out _, out _, out _);
                    sent.Add(pose);
                    PresenceWire.WriteRelay(writer, pose);
                }

                True(writer.ByteLength <= (PresenceMessages.RelayHeaderBits + count * PresenceMessages.RelayRecordBits + 7) / 8, "a relay is its header and its records, packed");
                True(writer.ByteLength <= InternalSystemLimits.MaximumDatagramBytes, "and fits a datagram");
                True(PresenceWire.TryReadDatagramType(writer.AsSegment(), reader, out var type) && type == PresenceMessages.Poses, "it reads as relayed poses");
                True(PresenceWire.TryReadPosesHeader(reader, out var millis, out var read) && millis == 123_456u + (uint)count && read == count, "with its time and count");
                for (var index = 0; index < count; index++)
                {
                    True(PresenceWire.TryReadRelay(reader, out var pose), "record " + index + " reads");
                    var expected = sent[index];
                    True(pose.Index == expected.Index && pose.AgeMillis == expected.AgeMillis && pose.Flags == expected.Flags && pose.EyeCentimeters == expected.EyeCentimeters &&
                         pose.Position.Equals(expected.Position) && pose.Rotation == expected.Rotation && pose.Velocity.Equals(expected.Velocity), "exactly");
                }

                True(reader.IsAtCanonicalEnd, "and nothing follows");
            }

            Equal(22, PresenceMessages.RelayRecordBytes, "a relayed pose costs 22 bytes of the budget");
            Throws<ArgumentOutOfRangeException>(() => PresenceWire.BeginPoses(writer, 0, 0), "a relay carries one pose at least");
            Throws<ArgumentOutOfRangeException>(() => PresenceWire.BeginPoses(writer, 0, PresenceMessages.MaximumRecords + 1), "and 32 at most");
            Throws<ArgumentOutOfRangeException>(() => PresenceWire.WriteRelay(writer, new PresencePoseRecord { Index = 0 }), "a relayed pose names an index");
        }

        private static void BentDatagramsAreRefusedWhole()
        {
            var writer = new NetBitWriter(InternalSystemLimits.MaximumDatagramBytes);
            var reader = new NetBitReader();
            var pose = RandomPose(new SeededRandom(5), out _, out _, out _);
            PresenceWire.WritePose(writer, pose);
            var bytes = writer.ToArray();
            for (var length = 0; length < bytes.Length; length++)
            {
                var cut = new ArraySegment<byte>(bytes, 0, length);
                var refused = !PresenceWire.TryReadDatagramType(cut, reader, out _) || !PresenceWire.TryReadPose(reader, out _);
                True(refused, "a pose cut to " + length + " bytes is refused");
            }

            var padded = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
            True(PresenceWire.TryReadDatagramType(new ArraySegment<byte>(padded), reader, out _) && !PresenceWire.TryReadPose(reader, out _), "a byte after the pose is refused");
            var spare = (byte[])bytes.Clone();
            spare[spare.Length - 1] |= 0x80;
            True(PresenceWire.TryReadDatagramType(new ArraySegment<byte>(spare), reader, out _) && !PresenceWire.TryReadPose(reader, out _), "a spare bit set is refused");
            var version = (byte[])bytes.Clone();
            version[0] = 2;
            True(!PresenceWire.TryReadDatagramType(new ArraySegment<byte>(version), reader, out _), "another version is refused");

            // Flags are bits 48 to 55 of the stream: the header's 16, the time's 32, then the correction's 8 come first.
            var flagged = (byte[])bytes.Clone();
            flagged[7] |= 0x80;
            True(PresenceWire.TryReadDatagramType(new ArraySegment<byte>(flagged), reader, out _) && !PresenceWire.TryReadPose(reader, out _), "an unknown flag is refused");

            PresenceWire.BeginPoses(writer, 1, 2);
            PresenceWire.WriteRelay(writer, new PresencePoseRecord { Index = 3, Rotation = NetQuantization.IdentityRotationBits });
            var short1 = writer.ToArray();
            True(PresenceWire.TryReadDatagramType(new ArraySegment<byte>(short1), reader, out _) && PresenceWire.TryReadPosesHeader(reader, out _, out var two) && two == 2 &&
                 PresenceWire.TryReadRelay(reader, out _) && !PresenceWire.TryReadRelay(reader, out _), "a relay that promises two poses and carries one is short");
            True(!PresenceWire.TryReadDatagramType(new ArraySegment<byte>(new byte[InternalSystemLimits.MaximumDatagramBytes + 1]), reader, out _), "an oversized payload is refused");
        }

        private static void MessagesRoundTrip()
        {
            var writer = new WorldWireWriter(PresenceMessages.MaximumMessageBytes);
            var reader = new WorldWireReader();
            var roster = new List<KeyValuePair<byte, ParticipantId>>();
            for (var index = 1; index <= 255; index++) roster.Add(new KeyValuePair<byte, ParticipantId>((byte)index, new ParticipantId("player-" + index)));
            PresenceWire.WriteRoster(writer, roster);
            var read = new List<KeyValuePair<byte, ParticipantId>>();
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out var type) && type == PresenceMessages.Roster && PresenceWire.TryReadRoster(reader, read),
                "a roster naming every index round-trips");
            True(read.Count == 255 && read[254].Key == 255 && read[254].Value.Equals(roster[254].Value), "exactly");

            // The largest roster a session makes: every player of the most a session holds, each id as long as an id may be
            // in the widest characters UTF-8 has for one UTF-16 unit.
            roster.Clear();
            for (var index = 1; index <= InternalHostSettings.MaximumPlayers; index++)
            {
                roster.Add(new KeyValuePair<byte, ParticipantId>((byte)index, new ParticipantId(new string('€', 127) + (char)('a' + index))));
            }

            PresenceWire.WriteRoster(writer, roster);
            True(writer.Length < PresenceMessages.MaximumMessageBytes, "the largest roster a session makes fits a message (" + writer.Length + " bytes)");
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out _) && PresenceWire.TryReadRoster(reader, read) &&
                 read.Count == InternalHostSettings.MaximumPlayers && read[15].Value.Equals(roster[15].Value), "and round-trips");

            PresenceWire.WriteState(writer, 42.5f, 100f, "sledgehammer");
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out type) && type == PresenceMessages.State &&
                 PresenceWire.TryReadState(reader, out var state) && state.Health == 42.5f && state.Maximum == 100f && state.HeldItem == "sledgehammer", "a state round-trips");
            PresenceWire.WriteState(writer, 1f, 2f, null);
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out _) && PresenceWire.TryReadState(reader, out state) && state.HeldItem == null, "holding nothing too");

            var states = new List<PresenceStateRecord> { new PresenceStateRecord(1, 10f, 100f, null), new PresenceStateRecord(7, 99f, 100f, "wrench") };
            PresenceWire.WriteStates(writer, states);
            var readStates = new List<PresenceStateRecord>();
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out type) && type == PresenceMessages.States && PresenceWire.TryReadStates(reader, readStates) &&
                 readStates.Count == 2 && readStates[1].Index == 7 && readStates[1].HeldItem == "wrench", "states round-trip");

            PresenceWire.WriteArrived(writer, "0123456789abcdef0123456789abcdef");
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out type) && type == PresenceMessages.Arrived &&
                 PresenceWire.TryReadArrived(reader, out var world) && world == "0123456789abcdef0123456789abcdef", "an arrival round-trips");

            PresenceWire.WritePlace(writer, "world", 23, new NetVector3(1.5f, -2f, 3f), -90f);
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out type) && type == PresenceMessages.Place &&
                 PresenceWire.TryReadPlace(reader, out world, out var slot, out var feet, out var yaw) && world == "world" && slot == 23 &&
                 feet.Equals(new NetVector3(1.5f, -2f, 3f)) && yaw == -90f, "a placement round-trips");

            PresenceWire.WriteCorrect(writer, 200, new NetVector3(-7f, 8f, 9f));
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out type) && type == PresenceMessages.Correct &&
                 PresenceWire.TryReadCorrect(reader, out var correction, out var target) && correction == 200 && target.Equals(new NetVector3(-7f, 8f, 9f)), "a correction round-trips");
        }

        private static void BentMessagesAreRefusedWhole()
        {
            var writer = new WorldWireWriter(PresenceMessages.MaximumMessageBytes);
            var reader = new WorldWireReader();
            var read = new List<KeyValuePair<byte, ParticipantId>>();

            True(!Roster(writer, reader, read, (0, "zero")), "index zero names nobody");
            True(!Roster(writer, reader, read, (1, "a"), (1, "b")), "an index given twice is refused");
            True(!Roster(writer, reader, read, (1, "a"), (2, "a")), "a participant given two indices is refused");
            True(!Roster(writer, reader, read, (1, "bad\nid")), "an id with a control character is refused");
            True(!Roster(writer, reader, read, (1, new string('y', 129))), "an id past 128 characters is refused");
            True(!Roster(writer, reader, read, (1, "   ")), "a blank id is refused");

            PresenceWire.WriteState(writer, 1f, 2f, "held\u0007item");
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out _) && !PresenceWire.TryReadState(reader, out _), "a held item with a control character is refused");
            PresenceWire.WritePlace(writer, "world", 24, new NetVector3(0f, 0f, 0f), 0f);
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out _) && !PresenceWire.TryReadPlace(reader, out _, out _, out _, out _), "a slot past the ring is refused");
            PresenceWire.WritePlace(writer, "world", 1, new NetVector3(float.NaN, 0f, 0f), 0f);
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out _) && !PresenceWire.TryReadPlace(reader, out _, out _, out _, out _), "a placement that is not a number is refused");
            PresenceWire.WriteCorrect(writer, 1, new NetVector3(0f, 2_000_000f, 0f));
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out _) && !PresenceWire.TryReadCorrect(reader, out _, out _), "a correction a million metres out is refused");
            PresenceWire.WriteArrived(writer, "has space");
            True(PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out _) && !PresenceWire.TryReadArrived(reader, out _), "a world token with a space is refused");

            PresenceWire.WriteCorrect(writer, 1, new NetVector3(0f, 0f, 0f));
            var padded = new byte[writer.Length + 1];
            Buffer.BlockCopy(writer.Segment.Array!, 0, padded, 0, writer.Length);
            True(PresenceWire.TryReadMessageType(reader, padded, out _) && !PresenceWire.TryReadCorrect(reader, out _, out _), "a byte after the last field is refused");
            var bytes = writer.Segment.ToArray();
            for (var length = 0; length < bytes.Length; length++)
            {
                var cut = new byte[length];
                Buffer.BlockCopy(bytes, 0, cut, 0, length);
                True(!PresenceWire.TryReadMessageType(reader, cut, out _) || !PresenceWire.TryReadCorrect(reader, out _, out _), "a correction cut to " + length + " bytes is refused");
            }

            True(!PresenceWire.TryReadMessageType(reader, new byte[] { 2, PresenceMessages.Correct }, out _), "another version is refused");
        }

        private static bool Roster(WorldWireWriter writer, WorldWireReader reader, List<KeyValuePair<byte, ParticipantId>> into, params (int Index, string Id)[] entries)
        {
            PresenceWire.BeginMessage(writer, PresenceMessages.Roster);
            writer.WriteVarUInt((ulong)entries.Length);
            foreach (var entry in entries)
            {
                writer.WriteByte((byte)entry.Index);
                writer.WriteString(entry.Id);
            }

            return PresenceWire.TryReadMessageType(reader, writer.Segment.ToArray(), out _) && PresenceWire.TryReadRoster(reader, into);
        }
    }
}
