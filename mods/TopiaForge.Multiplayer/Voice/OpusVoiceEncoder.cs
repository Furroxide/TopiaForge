using System;
using TopiaForge.Multiplayer.ThirdParty.Concentus.Enums;
using TopiaForge.Multiplayer.ThirdParty.Concentus.Structs;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// Encodes one talker's 20 ms frames with the settings of <see cref="OpusVoiceCodec"/>. It writes into the caller's
    /// buffer and allocates nothing itself. One thread owns it.
    /// </summary>
    internal sealed class OpusVoiceEncoder
    {
        private readonly OpusEncoder encoder;

        internal OpusVoiceEncoder(int complexity, int bitrateBitsPerSecond, int expectedLossPercent)
        {
            // Upstream points callers at OpusCodecFactory, which only adds the native library left out here.
#pragma warning disable CS0618
            encoder = new OpusEncoder(OpusVoiceCodec.SampleRateHz, 1, OpusApplication.OPUS_APPLICATION_VOIP);
#pragma warning restore CS0618
            encoder.UseVBR = true;
            encoder.UseConstrainedVBR = false;
            encoder.UseDTX = true;
            encoder.UseInbandFEC = true;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            encoder.MaxBandwidth = OpusBandwidth.OPUS_BANDWIDTH_WIDEBAND;
            Complexity = complexity;
            BitrateBitsPerSecond = bitrateBitsPerSecond;
            ExpectedLossPercent = expectedLossPercent;
        }

        /// <summary>Gets or sets the encoder complexity, 5 to 10. Lower values trade quality for encode time.</summary>
        internal int Complexity
        {
            get => encoder.Complexity;
            set => encoder.Complexity = OpusVoiceCodec.RequireComplexity(value, nameof(value));
        }

        /// <summary>Gets or sets the VBR target, 20,000 to 24,000 bit/s.</summary>
        internal int BitrateBitsPerSecond
        {
            get => encoder.Bitrate;
            set => encoder.Bitrate = OpusVoiceCodec.RequireBitrate(value, nameof(value));
        }

        /// <summary>
        /// Gets or sets the loss the encoder should expect, 0 to 100 percent. In-band FEC spends bits in proportion to
        /// it and stops at 0. The voice service sets it from the loss its transport measures.
        /// </summary>
        internal int ExpectedLossPercent
        {
            get => encoder.PacketLossPercent;
            set => encoder.PacketLossPercent = OpusVoiceCodec.RequireLossPercent(value, nameof(value));
        }

        /// <summary>Gets how many samples of look-ahead the encoder adds to the stream's delay.</summary>
        internal int LookaheadSamples => encoder.Lookahead;

        /// <summary>
        /// Encodes exactly 960 samples into <paramref name="packet"/>, which must have room for 160 bytes, and returns
        /// the packet's length: 1 for silence under discontinuous transmission (see <see cref="OpusToc.IsDiscontinuous"/>),
        /// and never more than 160.
        /// </summary>
        internal int Encode(ReadOnlySpan<short> frame, Span<byte> packet)
        {
            OpusVoiceCodec.RequireFrame(frame.Length, nameof(frame));
            if (packet.Length < OpusVoiceCodec.MaximumPacketLength)
            {
                throw new ArgumentException("A voice packet buffer must have room for 160 bytes.", nameof(packet));
            }

            return encoder.Encode(frame, OpusVoiceCodec.FrameSampleCount, packet, OpusVoiceCodec.MaximumPacketLength);
        }

        /// <summary>Starts a new stream, keeping the settings. A talker's first frame after a reconnect expects this.</summary>
        internal void Reset() => encoder.ResetState();
    }
}
