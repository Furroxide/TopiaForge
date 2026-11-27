using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A deterministic stand-in for Opus that makes every decoder path visible. A test frame holds one id in every sample;
/// its packet carries that id, the previous frame's id as "FEC", and padding to a chosen size. Decoding fills the frame
/// with the id, recovery with the FEC id, and concealment with <see cref="ConcealMarker"/>. A silence frame (id 0)
/// encodes to the one-byte DTX packet.
/// </summary>
internal sealed class FakeVoiceCodec : IVoiceCodec
{
    internal const short ConcealMarker = 30_000;
    internal const byte AudioHeader = 0xF0;
    internal const byte SilenceHeader = 0xF1;
    private const int HeaderBytes = 6;

    internal FakeVoiceCodec(int packetBytes = 50)
    {
        if (packetBytes < HeaderBytes || packetBytes > MaximumPacketBytes) throw new ArgumentOutOfRangeException(nameof(packetBytes));
        PacketBytes = packetBytes;
    }

    public int SampleRate => 48_000;

    public int FrameSamples => 960;

    public int MaximumPacketBytes => 160;

    /// <summary>Gets how long an audio packet is; 50 bytes every 20 ms is 20 kbit/s, like the voice codec.</summary>
    internal int PacketBytes { get; }

    public bool IsValidPacket(ReadOnlySpan<byte> packet) =>
        (packet.Length == 1 && packet[0] == SilenceHeader) ||
        (packet.Length >= HeaderBytes && packet.Length <= MaximumPacketBytes && packet[0] == AudioHeader);

    public bool IsDiscontinuous(ReadOnlySpan<byte> packet) => packet.Length == 1 && packet[0] == SilenceHeader;

    public IVoiceEncoder CreateEncoder() => new Encoder(this);

    public IVoiceDecoder CreateDecoder() => LastDecoder = new Decoder(this);

    /// <summary>Gets the decoder created last, so a test can count what a jitter buffer asked of its own.</summary>
    internal Decoder? LastDecoder { get; private set; }

    /// <summary>Builds the packet of a frame whose id is <paramref name="id"/>, carrying <paramref name="fecId"/> as FEC.</summary>
    internal byte[] Packet(short id, short fecId = 0)
    {
        if (id == 0) return new[] { SilenceHeader };
        var packet = new byte[PacketBytes];
        packet[0] = AudioHeader;
        BitConverter.TryWriteBytes(packet.AsSpan(1), id);
        packet[3] = fecId != 0 ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(packet.AsSpan(4), fecId);
        for (var index = HeaderBytes; index < packet.Length; index++) packet[index] = (byte)(id + index);
        return packet;
    }

    /// <summary>Gets the id a decoded or recovered frame carries, or <see cref="ConcealMarker"/> for concealment.</summary>
    internal static short IdOf(ReadOnlySpan<short> frame) => frame[0];

    internal sealed class Encoder : IVoiceEncoder
    {
        private readonly FakeVoiceCodec codec;
        private short previous;

        internal Encoder(FakeVoiceCodec codec)
        {
            this.codec = codec;
        }

        public int Encode(ReadOnlySpan<short> frame, Span<byte> packet)
        {
            var id = frame[0];
            var bytes = codec.Packet(id, previous);
            previous = id;
            bytes.CopyTo(packet);
            return bytes.Length;
        }

        public void Reset() => previous = 0;
    }

    internal sealed class Decoder : IVoiceDecoder
    {
        private readonly FakeVoiceCodec codec;

        internal Decoder(FakeVoiceCodec codec)
        {
            this.codec = codec;
        }

        internal int Decodes { get; private set; }
        internal int Conceals { get; private set; }
        internal int Recoveries { get; private set; }
        internal int Resets { get; private set; }

        public bool Decode(ReadOnlySpan<byte> packet, Span<short> frame)
        {
            if (!codec.IsValidPacket(packet))
            {
                Conceal(frame);
                return false;
            }

            Decodes++;
            frame.Fill(packet.Length == 1 ? (short)0 : BitConverter.ToInt16(packet.Slice(1, 2)));
            return true;
        }

        public void Conceal(Span<short> frame)
        {
            Conceals++;
            frame.Fill(ConcealMarker);
        }

        public bool Recover(ReadOnlySpan<byte> nextPacket, Span<short> frame)
        {
            if (!codec.IsValidPacket(nextPacket))
            {
                Conceal(frame);
                return false;
            }

            Recoveries++;
            if (nextPacket.Length > 1 && nextPacket[3] == 1) frame.Fill(BitConverter.ToInt16(nextPacket.Slice(4, 2)));
            else frame.Fill(ConcealMarker);
            return true;
        }

        public void Reset() => Resets++;
    }
}
