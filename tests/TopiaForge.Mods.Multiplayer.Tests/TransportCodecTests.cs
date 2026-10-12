extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using ProtocolException = provider::TopiaForge.Multiplayer.Engine.ProtocolException;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The handshake datagrams, the protected frame codec and the encrypted handshake payloads.</summary>
internal static class TransportCodecTests
{
    internal static void HandshakeDatagramsKeepTheirExactLayouts()
    {
        var writer = new PacketWriter();
        var message1 = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var cookie = Enumerable.Range(100, 16).Select(value => (byte)value).ToArray();
        foreach (var withCookie in new[] { false, true })
        {
            var datagram = new byte[HandshakePackets.InitiationSize];
            writer.Reset(datagram, 0, datagram.Length);
            HandshakePackets.WriteInitiation(writer, 0x1122334455667788UL, withCookie ? cookie : null, message1);
            That(writer.Written == 1200 && datagram[0] == 0xE0 && datagram[1] == 1, "an Initiation fills exactly one datagram");
            That(HandshakePackets.TryReadInitiation(datagram, 0, datagram.Length, out var version, out var id, out var readCookie, out var readMessage) &&
                 version == 1 && id == 0x1122334455667788UL && readMessage.SequenceEqual(message1) &&
                 (withCookie ? readCookie!.SequenceEqual(cookie) : readCookie == null),
                "an Initiation reads back its id, cookie and message");
            That(!HandshakePackets.TryReadInitiation(datagram, 0, 1199, out _, out _, out _, out _), "a short Initiation is refused");
            datagram[10] = 15;
            That(!HandshakePackets.TryReadInitiation(datagram, 0, datagram.Length, out _, out _, out _, out _), "a cookie must be absent or 16 bytes");
        }

        var retry = new byte[HandshakePackets.RetrySize];
        writer.Reset(retry, 0, retry.Length);
        HandshakePackets.WriteRetry(writer, 42, cookie);
        That(HandshakePackets.TryReadRetry(retry, 0, retry.Length, out var retryId, out var retryCookie) && retryId == 42 && retryCookie.SequenceEqual(cookie),
            "a Retry echoes the initiator's id and carries the cookie");
        That(!HandshakePackets.TryReadRetry(retry, 0, retry.Length - 1, out _, out _), "a Retry has one exact size");
        That(!HandshakePackets.TryReadAddressed(new byte[] { 0xE2, 1, 2, 3, 4, 5, 6, 7, 8 }, 0, 9, PacketType.Response, out _, out _),
            "an addressed datagram must carry a message");
        That(PacketType.IsForeign(0x00) && PacketType.IsForeign(0x03) && PacketType.IsForeign(0x40) && PacketType.IsForeign(0x4F) &&
             !PacketType.IsForeign(0x04) && !PacketType.IsForeign(0xE0) && !PacketType.IsForeign(0x50),
            "STUN and TURN ChannelData first bytes are recognised");
        That(HandshakePackets.Prologue(1).Length == 22 + 8, "the prologue is the label and the initiator's id");
    }

    internal static void HandshakePayloadsParseStrictly()
    {
        var response = HandshakePayloads.WriteResponse(0xABCDEF0123456789UL, 2 * 1024 * 1024);
        That(HandshakePayloads.TryReadResponse(response, out var id, out var window) && id == 0xABCDEF0123456789UL && window == 2 * 1024 * 1024,
            "the Response payload carries the responder's id and window");
        That(!HandshakePayloads.TryReadResponse(response.Concat(new byte[] { 0 }).ToArray(), out _, out _), "trailing bytes are refused");
        That(!HandshakePayloads.TryReadResponse(response.Take(8).ToArray(), out _, out _), "a missing window is refused");
        var completion = HandshakePayloads.WriteCompletion(70_000);
        That(HandshakePayloads.TryReadCompletion(completion, out window) && window == 70_000, "the Completion payload carries the window");
        That(!HandshakePayloads.TryReadCompletion(new byte[] { 0x80, 0x00 }, out _), "a non-minimal varint is refused");
        That(!HandshakePayloads.TryReadCompletion(Array.Empty<byte>(), out _), "an empty Completion payload is refused");
    }

    internal static void FramesRoundTripThroughTheReader()
    {
        var buffer = new byte[ProtectedPacket.MaximumFramesBytes];
        var writer = new PacketWriter();
        writer.Reset(buffer, 0, buffer.Length);
        var ranges = new List<PacketRange> { new(90, 100), new(50, 60), new(0, 10) };
        FrameWriter.WriteAck(writer, ranges, 4_000);
        writer.WriteByte(FrameType.Ping);
        FrameWriter.WriteWindow(writer, 3 * 1024 * 1024);
        FrameWriter.WriteStreamHeader(writer, 1_000_000, 3);
        writer.WriteBytes(new byte[] { 7, 8, 9 }, 0, 3);
        FrameWriter.WriteDatagram(writer, 1, 77, new byte[] { 1, 2 });
        FrameWriter.WriteClose(writer, CloseCode.FlowControl);
        That(FrameWriter.AckSize(ranges, 4_000) == 1 + 1 + 2 + 1 + 1 + 1 + 1 + 1 + 1, "ACK sizing matches what it writes");
        var sink = new RecordingFrameSink();
        var read = FrameReader.Read(buffer, 0, writer.Written, sink, new List<PacketRange>());
        That(read == (FrameRead.AckEliciting | FrameRead.NonProbing), "a packet with a PING asks for an acknowledgement and uses its path");
        That(sink.LastAck.Select(range => (range.Smallest, range.Largest)).SequenceEqual(new[] { (90UL, 100UL), (50UL, 60UL), (0UL, 10UL) }),
            "ACK ranges read back newest first");
        That(sink.Pings == 1 && sink.Windows.SequenceEqual(new[] { 3UL * 1024 * 1024 }) && sink.Closes.SequenceEqual(new[] { CloseCode.FlowControl }),
            "PING, WINDOW and CLOSE read back");
        That(sink.Streams.Count == 1 && sink.Streams[0].Offset == 1_000_000 && sink.Streams[0].Data.SequenceEqual(new byte[] { 7, 8, 9 }),
            "STREAM reads back its offset and bytes");
        That(sink.Datagrams.Count == 1 && sink.Datagrams[0].Channel == 1 && sink.Datagrams[0].Sequence == 77, "DATAGRAM reads back");

        writer.Reset(buffer, 0, buffer.Length);
        FrameWriter.WriteAck(writer, new List<PacketRange> { new(5, 5) }, 0);
        FrameWriter.WriteClose(writer, CloseCode.Normal);
        That(FrameReader.Read(buffer, 0, writer.Written, new RecordingFrameSink(), new List<PacketRange>()) == FrameRead.NonProbing,
            "ACK and CLOSE alone ask for nothing");

        writer.Reset(buffer, 0, buffer.Length);
        var challenge = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        FrameWriter.WritePathFrame(writer, FrameType.PathChallenge, challenge);
        FrameWriter.WritePathFrame(writer, FrameType.PathResponse, challenge.Reverse().ToArray());
        writer.WriteZeros(40);
        var paths = new RecordingFrameSink();
        That(FrameReader.Read(buffer, 0, writer.Written, paths, new List<PacketRange>()) == FrameRead.None,
            "path frames and padding only probe: they ask for nothing and move nothing");
        That(paths.Challenges.Single().SequenceEqual(challenge) && paths.Responses.Single().SequenceEqual(challenge.Reverse()),
            "PATH_CHALLENGE and PATH_RESPONSE read back their eight bytes");
        writer.Reset(buffer, 0, buffer.Length);
        FrameWriter.WriteClose(writer, (CloseCode)99);
        var unknownCode = new RecordingFrameSink();
        FrameReader.Read(buffer, 0, writer.Written, unknownCode, new List<PacketRange>());
        That(unknownCode.Closes.SequenceEqual(new[] { CloseCode.ProtocolViolation }), "an unknown close code reads as a protocol violation");
    }

    internal static void MalformedFramesAreProtocolErrors()
    {
        var cases = new (string Name, byte[] Bytes)[]
        {
            ("an empty packet", Array.Empty<byte>()),
            ("an unknown frame type", new byte[] { 0x7F }),
            ("a truncated path challenge", new byte[] { FrameType.PathChallenge, 1, 2, 3 }),
            ("a reserved frame type", new byte[] { 0x32 }),
            ("a truncated window", new byte[] { FrameType.Window, 0x80 }),
            ("a stream frame past its bytes", new byte[] { FrameType.Stream, 0, 5, 1, 2 }),
            ("a stream frame larger than a datagram", new byte[] { FrameType.Stream, 0, 0xB1, 0x09 }),
            ("a stream frame past the end of the stream", new byte[] { FrameType.Stream, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 1, 9 }),
            ("an ACK range wider than its largest", new byte[] { FrameType.Ack, 5, 0, 0, 6 }),
            ("an ACK gap below zero", new byte[] { FrameType.Ack, 5, 0, 1, 0, 4, 0 }),
            ("an ACK with too many ranges", new byte[] { FrameType.Ack, 100, 0, 32, 0 }),
            ("a datagram without a sequence", new byte[] { FrameType.Datagram, 1 })
        };
        foreach (var (name, bytes) in cases)
        {
            Throws<ProtocolException>(
                () => FrameReader.Read(bytes, 0, bytes.Length, new RecordingFrameSink(), new List<PacketRange>()),
                name + " is a protocol error");
        }
    }
}
