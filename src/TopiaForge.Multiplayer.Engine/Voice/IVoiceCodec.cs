using System;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// The codec seam. The engine never names a codec: the provider supplies Opus, and the tests a deterministic fake,
    /// so the engine and the Testing package stay free of third-party code. A codec works in fixed frames of mono
    /// 16-bit audio. Its packets come from the network, so every decoder method must cope with any bytes at all.
    /// </summary>
    internal interface IVoiceCodec
    {
        /// <summary>Gets the sample rate of every frame, in hertz.</summary>
        int SampleRate { get; }

        /// <summary>Gets the samples in one frame.</summary>
        int FrameSamples { get; }

        /// <summary>Gets the largest packet one frame encodes to.</summary>
        int MaximumPacketBytes { get; }

        /// <summary>
        /// Gets whether <paramref name="packet"/> has a header this codec accepts, checked without decoding: a host runs
        /// it on every frame it relays, so nobody can make the other players decode something else.
        /// </summary>
        bool IsValidPacket(ReadOnlySpan<byte> packet);

        /// <summary>Gets whether a valid packet is a discontinuous-transmission (silence) packet.</summary>
        bool IsDiscontinuous(ReadOnlySpan<byte> packet);

        /// <summary>Creates an encoder for one talker's stream.</summary>
        IVoiceEncoder CreateEncoder();

        /// <summary>Creates a decoder for one speaker's stream.</summary>
        IVoiceDecoder CreateDecoder();
    }

    /// <summary>Encodes one talker's frames. One thread owns it.</summary>
    internal interface IVoiceEncoder
    {
        /// <summary>
        /// Encodes exactly one frame into <paramref name="packet"/>, which has room for
        /// <see cref="IVoiceCodec.MaximumPacketBytes"/>, and returns the packet length.
        /// </summary>
        int Encode(ReadOnlySpan<short> frame, Span<byte> packet);

        /// <summary>Starts a new stream.</summary>
        void Reset();
    }

    /// <summary>
    /// Decodes one speaker's frames. Every method fills exactly one frame of output, and none throws on what the network
    /// sent. One thread owns it.
    /// </summary>
    internal interface IVoiceDecoder
    {
        /// <summary>Decodes a packet; returns false, with concealment in the frame, when the packet is invalid.</summary>
        bool Decode(ReadOnlySpan<byte> packet, Span<short> frame);

        /// <summary>Fills the frame with packet-loss concealment for a frame that never arrived.</summary>
        void Conceal(Span<short> frame);

        /// <summary>
        /// Rebuilds the frame before <paramref name="nextPacket"/> from the forward error correction that packet carries,
        /// or conceals where it carries none. The caller decodes <paramref name="nextPacket"/> afterwards as usual.
        /// Returns false, with concealment in the frame, when the next packet is invalid.
        /// </summary>
        bool Recover(ReadOnlySpan<byte> nextPacket, Span<short> frame);

        /// <summary>Starts a new stream, as after a talker reconnects.</summary>
        void Reset();
    }
}
