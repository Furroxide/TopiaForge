using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The voice datagram format and the two voice session messages, including everything a peer must refuse.</summary>
internal static class VoicePacketTests
{
    internal static void DatagramsRoundTripOneOrTwoFrames()
    {
        var buffer = new byte[VoicePacket.MaximumBytes(160)];
        var first = new byte[] { 1, 2, 3 };
        var second = new byte[] { 4, 5, 6, 7 };
        var length = VoicePacket.Write(buffer, 0, 300, 7_000, VoiceFrameFlags.Discontinuous, first, VoiceFrameFlags.EndOfTalk, second);
        Assert(VoicePacket.TryReadUplink(buffer.AsSpan(0, length), 160, out var uplink), "an uplink datagram of two frames reads back");
        Assert(uplink.Count == 2 && uplink.First.Speaker == 0, "it holds two frames and no speaker");
        Assert(uplink.First.Sequence == 300 && uplink.First.Timestamp == 7_000 && uplink.First.Flags == VoiceFrameFlags.Discontinuous,
            "the first frame keeps its position and flags");
        Assert(uplink.Second.Sequence == 301 && uplink.Second.Timestamp == 7_001 && uplink.Second.Flags == VoiceFrameFlags.EndOfTalk,
            "the second frame follows on");
        Assert(buffer.AsSpan(uplink.First.PayloadOffset, uplink.First.PayloadLength).SequenceEqual(first) &&
               buffer.AsSpan(uplink.Second.PayloadOffset, uplink.Second.PayloadLength).SequenceEqual(second), "both payloads read back");
        Assert(!VoicePacket.TryReadRelayed(buffer.AsSpan(0, length), 160, out _), "an uplink datagram is not a relayed one");

        length = VoicePacket.Write(buffer, 9, 1, 0, VoiceFrameFlags.None, first, VoiceFrameFlags.None, ReadOnlySpan<byte>.Empty);
        Assert(VoicePacket.TryReadRelayed(buffer.AsSpan(0, length), 160, out var relayed), "a relayed datagram of one frame reads back");
        Assert(relayed.Count == 1 && relayed.First.Speaker == 9 && relayed.First.Sequence == 1 && relayed.First.Timestamp == 0,
            "it carries the host's speaker stamp");
        Assert(!VoicePacket.TryReadUplink(buffer.AsSpan(0, length), 160, out _), "a relayed datagram is not an uplink one");
        Assert(length == 1 + 1 + 1 + 1 + 1 + 1 + 1 + first.Length, "a small relayed frame costs seven bytes of header");
    }

    internal static void MalformedDatagramsAreRefusedWhole()
    {
        var good = Uplink(5, 10, new byte[] { 0xAA });
        Assert(VoicePacket.TryReadUplink(good, 160, out _), "the reference datagram is valid");
        var cases = new (string What, byte[] Bytes)[]
        {
            ("empty", Array.Empty<byte>()),
            ("unknown kind", With(good, 0, 0x03)),
            ("sequence zero", Uplink(0, 10, new byte[] { 0xAA })),
            ("unknown flags", With(good, 4, 0x04)),
            ("frame count zero", With(good, 3, 0)),
            ("frame count three", With(good, 3, 3)),
            ("an empty frame", new byte[] { 1, 5, 10, 1, 0, 0 }),
            ("a frame longer than the datagram", new byte[] { 1, 5, 10, 1, 0, 2, 0xAA }),
            ("trailing bytes", good.Concat(new byte[] { 0 }).ToArray()),
            ("truncated", good.AsSpan(0, good.Length - 1).ToArray()),
            ("a non-minimal varint", new byte[] { 1, 0x85, 0x00, 10, 1, 0, 1, 0xAA }),
            ("an eleven-byte varint", new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }),
            ("a sequence at the top of the range", Uplink(ulong.MaxValue, 10, new byte[] { 0xAA })),
            ("a timestamp at the top of the range", Uplink(5, ulong.MaxValue, new byte[] { 0xAA })),
        };
        foreach (var (what, bytes) in cases)
        {
            Assert(!VoicePacket.TryReadUplink(bytes, 160, out _), "refuses " + what);
        }

        Assert(!VoicePacket.TryReadUplink(Uplink(5, 10, new byte[161]), 160, out _), "a frame above the size limit is refused");
        Assert(!VoicePacket.TryReadRelayed(new byte[] { 2, 0, 5, 10, 1, 0, 1, 0xAA }, 160, out _), "speaker index zero is refused");

        // Whatever arrives, parsing returns an answer and never throws.
        var state = 77u;
        for (var trial = 0; trial < 20_000; trial++)
        {
            var bytes = new byte[Next(ref state) % 48];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)Next(ref state);
            if (bytes.Length > 0) bytes[0] = (byte)(1 + (trial % 2));
            VoicePacket.TryReadUplink(bytes, 160, out _);
            VoicePacket.TryReadRelayed(bytes, 160, out _);
        }
    }

    internal static void VoiceMessagesRoundTripAndRefuseRepeats()
    {
        var frame = new ProtocolWriter();
        var body = new ProtocolWriter();
        VoiceMessages.WriteRoster(frame, body, new[] { new VoiceRosterEntry(1, "host", false), new VoiceRosterEntry(4, "p2", true) });
        var roster = VoiceMessages.ReadRoster(Body(frame, MessageType.VoiceRoster));
        Assert(roster.Count == 2 && roster[1].Speaker == 4 && roster[1].ParticipantId == "p2" && roster[1].ServerMuted, "the roster reads back");

        frame.Reset();
        VoiceMessages.WriteMutes(frame, body, true, new[] { "p3", "p4" });
        VoiceMessages.ReadMutes(Body(frame, MessageType.VoiceMutes), out var muteAll, out var muted);
        Assert(muteAll && muted.SetEquals(new[] { "p3", "p4" }), "the mute set reads back");

        foreach (var (what, entries) in new (string, VoiceRosterEntry[])[]
                 {
                     ("a repeated speaker index", new[] { new VoiceRosterEntry(1, "a", false), new VoiceRosterEntry(1, "b", false) }),
                     ("a repeated participant", new[] { new VoiceRosterEntry(1, "a", false), new VoiceRosterEntry(2, "a", false) }),
                     ("speaker index zero", new[] { new VoiceRosterEntry(0, "a", false) }),
                 })
        {
            frame.Reset();
            VoiceMessages.WriteRoster(frame, body, entries);
            AssertProtocolError(() => VoiceMessages.ReadRoster(Body(frame, MessageType.VoiceRoster)), "a roster with " + what);
        }

        frame.Reset();
        VoiceMessages.WriteMutes(frame, body, false, new[] { "p3", "p3" });
        AssertProtocolError(() => VoiceMessages.ReadMutes(Body(frame, MessageType.VoiceMutes), out _, out _), "a mute set repeating someone");
        AssertProtocolError(() => VoiceMessages.ReadMutes(new ProtocolReader(new byte[] { 2, 0 }), out _, out _), "unknown mute flags");
        AssertProtocolError(() => VoiceMessages.ReadRoster(new ProtocolReader(new byte[] { 1, 1, 1, (byte)'a', 2 })), "unknown roster flags");
    }

    internal static byte[] Uplink(ulong sequence, ulong timestamp, byte[] payload)
    {
        var buffer = new byte[VoicePacket.MaximumBytes(payload.Length + 1)];
        var length = VoicePacket.Write(buffer, 0, sequence, timestamp, VoiceFrameFlags.None, payload, VoiceFrameFlags.None, ReadOnlySpan<byte>.Empty);
        return buffer.AsSpan(0, length).ToArray();
    }

    private static byte[] With(byte[] source, int index, byte value)
    {
        var copy = (byte[])source.Clone();
        copy[index] = value;
        return copy;
    }

    private static ProtocolReader Body(ProtocolWriter frame, byte type)
    {
        var reader = new ProtocolReader(frame.ToArray());
        Assert(reader.ReadVarUInt() == type, "the envelope carries the message type");
        return reader.Slice(reader.ReadLength(ProtocolLimits.MaximumFrameBytes));
    }

    private static void AssertProtocolError(Action action, string what)
    {
        try
        {
            action();
        }
        catch (ProtocolException)
        {
            return;
        }

        throw new InvalidOperationException("Assertion failed: " + what + " is refused");
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
