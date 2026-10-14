using System;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// The voice chat codec: Opus, through the vendored Concentus port of libopus 1.1.2, set up for speech. A frame is
    /// 20 ms of 48 kHz mono audio, 960 samples, and encodes to one packet of at most 160 bytes. The encoder uses the VOIP
    /// application, unconstrained VBR at 20 to 24 kbit/s, discontinuous transmission on silence, and in-band forward
    /// error correction sized by an expected-loss hint. Its audio band is capped at wideband, which keeps it in SILK-only
    /// mode: at these bitrates the fullband hybrid mode leaves SILK too few bits to carry any FEC. The measurements
    /// behind these choices are in docs/internal/MultiplayerVoice.md.
    /// </summary>
    /// <remarks>
    /// The codec holds settings only, so it is immutable and safe to share. Each encoder and decoder it creates belongs
    /// to one thread. They write into buffers the caller provides and allocate nothing themselves; the Concentus code
    /// underneath still allocates scratch arrays for every frame, as upstream does.
    /// </remarks>
    internal sealed class OpusVoiceCodec
    {
        /// <summary>The sample rate of every frame, in hertz.</summary>
        internal const int SampleRateHz = 48_000;

        /// <summary>The samples in one 20 ms mono frame.</summary>
        internal const int FrameSampleCount = 960;

        /// <summary>The largest packet the encoder writes, and the largest a host relays.</summary>
        internal const int MaximumPacketLength = 160;

        internal const int DefaultComplexity = 10;
        internal const int MinimumComplexity = 5;
        internal const int MaximumComplexity = 10;
        internal const int DefaultBitrate = 24_000;
        internal const int MinimumBitrate = 20_000;
        internal const int MaximumBitrate = 24_000;
        internal const int DefaultExpectedLossPercent = 10;

        /// <summary>Creates a codec whose encoders start with these settings.</summary>
        /// <param name="complexity">
        /// The encoder complexity, 5 to 10. The Mono spike measured 10 at about 0.42 ms per frame; 5 is the fallback
        /// for slow machines.
        /// </param>
        /// <param name="bitrateBitsPerSecond">The VBR target, 20,000 to 24,000 bit/s.</param>
        /// <param name="expectedLossPercent">The loss hint, 0 to 100 percent; 0 turns in-band FEC off.</param>
        internal OpusVoiceCodec(
            int complexity = DefaultComplexity,
            int bitrateBitsPerSecond = DefaultBitrate,
            int expectedLossPercent = DefaultExpectedLossPercent)
        {
            Complexity = RequireComplexity(complexity, nameof(complexity));
            BitrateBitsPerSecond = RequireBitrate(bitrateBitsPerSecond, nameof(bitrateBitsPerSecond));
            ExpectedLossPercent = RequireLossPercent(expectedLossPercent, nameof(expectedLossPercent));
        }

        internal int SampleRate => SampleRateHz;

        internal int FrameSamples => FrameSampleCount;

        internal int MaximumPacketBytes => MaximumPacketLength;

        internal int Complexity { get; }

        internal int BitrateBitsPerSecond { get; }

        internal int ExpectedLossPercent { get; }

        /// <summary>Gets whether <paramref name="packet"/> is a voice packet a host may relay; see <see cref="OpusToc"/>.</summary>
        internal bool IsValidPacket(ReadOnlySpan<byte> packet) => OpusToc.IsVoicePacket(packet);

        /// <summary>Gets whether <paramref name="packet"/> is a silence packet under discontinuous transmission.</summary>
        internal bool IsDiscontinuous(ReadOnlySpan<byte> packet) => OpusToc.IsDiscontinuous(packet);

        /// <summary>Creates an encoder for one talker's stream, starting from this codec's settings.</summary>
        internal OpusVoiceEncoder CreateEncoder() => new OpusVoiceEncoder(Complexity, BitrateBitsPerSecond, ExpectedLossPercent);

        /// <summary>Creates a decoder for one speaker's stream.</summary>
        internal OpusVoiceDecoder CreateDecoder() => new OpusVoiceDecoder();

        internal static int RequireComplexity(int value, string name)
        {
            if (value < MinimumComplexity || value > MaximumComplexity)
            {
                throw new ArgumentOutOfRangeException(name, value, "Voice encoder complexity is 5 to 10.");
            }

            return value;
        }

        internal static int RequireBitrate(int value, string name)
        {
            if (value < MinimumBitrate || value > MaximumBitrate)
            {
                throw new ArgumentOutOfRangeException(name, value, "The voice bitrate target is 20,000 to 24,000 bit/s.");
            }

            return value;
        }

        internal static int RequireLossPercent(int value, string name)
        {
            if (value < 0 || value > 100) throw new ArgumentOutOfRangeException(name, value, "Expected loss is 0 to 100 percent.");
            return value;
        }

        internal static void RequireFrame(int length, string name)
        {
            if (length != FrameSampleCount) throw new ArgumentException("A voice frame is exactly 960 samples.", name);
        }
    }
}
