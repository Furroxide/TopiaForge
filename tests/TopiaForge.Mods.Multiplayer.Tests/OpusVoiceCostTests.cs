extern alias provider;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using ConcentusApplication = provider::TopiaForge.Multiplayer.ThirdParty.Concentus.Enums.OpusApplication;
using ConcentusBandwidth = provider::TopiaForge.Multiplayer.ThirdParty.Concentus.Enums.OpusBandwidth;
using ConcentusDecoder = provider::TopiaForge.Multiplayer.ThirdParty.Concentus.Structs.OpusDecoder;
using ConcentusEncoder = provider::TopiaForge.Multiplayer.ThirdParty.Concentus.Structs.OpusEncoder;
using ConcentusSignal = provider::TopiaForge.Multiplayer.ThirdParty.Concentus.Enums.OpusSignal;
using OpusToc = provider::TopiaForge.Multiplayer.Voice.OpusToc;
using OpusVoiceCodec = provider::TopiaForge.Multiplayer.Voice.OpusVoiceCodec;
using OpusVoiceDecoder = provider::TopiaForge.Multiplayer.Voice.OpusVoiceDecoder;
using OpusVoiceEncoder = provider::TopiaForge.Multiplayer.Voice.OpusVoiceEncoder;
using SpeexResampler = provider::TopiaForge.Multiplayer.ThirdParty.Concentus.Common.SpeexResampler;
using static TopiaForge.Mods.Multiplayer.Tests.OpusVoiceCodecTests;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// What the voice codec costs and how it treats what it cannot trust: a four-speaker time budget, allocation beyond the
/// vendored codec's own, hostile packets, its settings, resets, and the vendored Speex resampler capture will need.
/// </summary>
internal static class OpusVoiceCostTests
{
    internal static void FourSpeakersCodeWithinRealTime()
    {
        // One second of a four-player session's codec work on one thread: each speaker encodes 50 frames and each
        // listener decodes the other three speakers' 50. The Mono spike measured about 0.43 ms per encode and 0.09 ms
        // per decode, about 0.14 s here; the bound is real time, and the best of three runs counts, so a slow or busy
        // machine still passes while a codec that cannot keep up with speech fails.
        const int Speakers = 4;
        var speech = Enumerable.Range(0, Speakers).Select(speaker => VoiceTestSignal.Voiced(Frame * 60, (uint)(speaker + 1))).ToArray();
        RunSession(speech, frames: 10);
        var best = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < 3 && best >= TimeSpan.FromSeconds(1); attempt++)
        {
            var timer = Stopwatch.StartNew();
            RunSession(speech, frames: 50);
            timer.Stop();
            if (timer.Elapsed < best) best = timer.Elapsed;
        }

        Assert(best < TimeSpan.FromSeconds(1),
            "four speakers' second of speech codes in under a second on one thread (best " + best.TotalMilliseconds.ToString("F0") + " ms)");
    }

    internal static void TheWrapperAllocatesNothingOfItsOwn()
    {
        // The vendored codec allocates scratch arrays for every frame, as upstream does. The wrapper must add nothing:
        // the same frames through the bare Concentus objects, configured as documented, allocate exactly as much and
        // produce exactly the same bytes. A pass codes 60 frames both ways and counts how far the two ways differ.
        var speech = VoiceTestSignal.Voiced(Frame * 60);
        var sideBySide = new SideBySide(new OpusVoiceCodec());
        var (wrappedBytes, bareBytes) = (0L, 0L);
        var difference = AllocationPasses.Fewest(() =>
        {
            (wrappedBytes, bareBytes) = sideBySide.Code(speech);
            return Math.Abs(wrappedBytes - bareBytes);
        });
        Assert(difference == 0,
            "the wrapper allocates nothing beyond the vendored codec (wrapper " + wrappedBytes + " B, bare " + bareBytes + " B over 60 frames)");
    }

    internal static void HostilePacketsAreConcealedNeverThrown()
    {
        var codec = new OpusVoiceCodec();
        var decoder = codec.CreateDecoder();
        var frame = new short[Frame];
        var state = 20261013u;
        var validPackets = 0;
        for (var trial = 0; trial < 4000; trial++)
        {
            var packet = new byte[Next(ref state) % 200];
            for (var index = 0; index < packet.Length; index++) packet[index] = (byte)Next(ref state);
            if (packet.Length > 0 && trial % 2 == 0) packet[0] = (byte)(new[] { 1, 5, 9, 13, 15, 19, 23, 27, 31 }[trial % 9] << 3);
            var valid = OpusToc.IsVoicePacket(packet);
            if (valid) validPackets++;

            // Garbage behind a valid TOC byte decodes to noise, not a failure; anything else is concealed.
            var decoded = trial % 3 == 0 ? decoder.Recover(packet, frame) : decoder.Decode(packet, frame);
            Assert(decoded == valid, "exactly the packets that pass TOC validation decode (trial " + trial + ")");
        }

        Assert(validPackets > 1000, "half the fuzzing passes the TOC check and reaches the codec");

        // Upstream threw on a 1-byte payload, the DTX path, once a decoder had history; the vendored fix covers it.
        foreach (var configuration in new[] { 9, 15, 31 })
        {
            Assert(decoder.Decode(new[] { (byte)(configuration << 3), (byte)0x5A }, frame), "a 2-byte packet decodes as DTX");
        }

        // The same decoder, with no reset, settles back onto real speech within the first second.
        var speech = VoiceTestSignal.Voiced(VoiceTestSignal.SampleRate * 3);
        var packets = Encode(codec.CreateEncoder(), speech);
        var decodedSpeech = new short[speech.Length];
        for (var index = 0; index < packets.Count; index++)
        {
            Assert(decoder.Decode(packets[index], decodedSpeech.AsSpan(index * Frame, Frame)), "real speech decodes after the fuzzing");
        }

        var (_, snr) = Align(codec, speech, decodedSpeech);
        Assert(snr > 7, "speech after the fuzzing decodes as well as ever (got " + snr.ToString("F2") + " dB)");
    }

    internal static void SettingsAreBoundedAndApplied()
    {
        var codec = new OpusVoiceCodec();
        Assert(codec.SampleRate == 48_000 && codec.FrameSamples == 960 && codec.MaximumPacketBytes == 160, "the frame format is fixed");
        Assert(codec.Complexity == 10 && codec.BitrateBitsPerSecond == 20_000 && codec.ExpectedLossPercent == 10,
            "the defaults: complexity 10, the 20 kbit/s target and a 10% loss hint");
        AssertThrows<ArgumentOutOfRangeException>(() => new OpusVoiceCodec(4), "complexity below 5 is refused");
        AssertThrows<ArgumentOutOfRangeException>(() => new OpusVoiceCodec(11), "complexity above 10 is refused");
        AssertThrows<ArgumentOutOfRangeException>(() => new OpusVoiceCodec(bitrateBitsPerSecond: 19_999), "a bitrate below 20 kbit/s is refused");
        AssertThrows<ArgumentOutOfRangeException>(() => new OpusVoiceCodec(bitrateBitsPerSecond: 20_001), "a target above 20 kbit/s is refused");
        AssertThrows<ArgumentOutOfRangeException>(() => new OpusVoiceCodec(expectedLossPercent: -1), "negative loss is refused");
        AssertThrows<ArgumentOutOfRangeException>(() => new OpusVoiceCodec(expectedLossPercent: 101), "loss above 100% is refused");
        var encoder = new OpusVoiceCodec(5, 20_000, 0).CreateEncoder();
        Assert(encoder.Complexity == 5 && encoder.BitrateBitsPerSecond == 20_000 && encoder.ExpectedLossPercent == 0, "encoders start from the codec");
        encoder.Complexity = 10;
        encoder.BitrateBitsPerSecond = 20_000;
        encoder.ExpectedLossPercent = 30;
        Assert(encoder.Complexity == 10 && encoder.BitrateBitsPerSecond == 20_000 && encoder.ExpectedLossPercent == 30, "settings change live");
        AssertThrows<ArgumentOutOfRangeException>(() => encoder.Complexity = 4, "live complexity keeps its bound");
        AssertThrows<ArgumentOutOfRangeException>(() => encoder.BitrateBitsPerSecond = 24_000, "the live bitrate keeps its 20 kbit/s target");
        AssertThrows<ArgumentOutOfRangeException>(() => encoder.ExpectedLossPercent = 101, "the live loss hint keeps its bound");
    }

    internal static void ResetsStartFreshStreams()
    {
        var codec = new OpusVoiceCodec();
        var first = VoiceTestSignal.Voiced(Frame * 30, 3);
        var second = VoiceTestSignal.Voiced(Frame * 20, 4);
        var reused = codec.CreateEncoder();
        Encode(reused, first);
        reused.Reset();
        var afterReset = Encode(reused, second);
        var fresh = Encode(codec.CreateEncoder(), second);
        Assert(afterReset.Zip(fresh).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)), "a reset encoder codes like a new one");

        var reusedDecoder = codec.CreateDecoder();
        var freshDecoder = codec.CreateDecoder();
        var reusedFrame = new short[Frame];
        var freshFrame = new short[Frame];
        foreach (var packet in Encode(codec.CreateEncoder(), first)) reusedDecoder.Decode(packet, reusedFrame);
        reusedDecoder.Reset();
        foreach (var packet in fresh)
        {
            reusedDecoder.Decode(packet, reusedFrame);
            freshDecoder.Decode(packet, freshFrame);
            Assert(reusedFrame.AsSpan().SequenceEqual(freshFrame), "a reset decoder decodes like a new one");
        }
    }

    internal static void TheResamplerConvertsCaptureRates()
    {
        // Microphones commonly run at 44.1 or 16 kHz; capture resamples to the codec's 48 kHz in 10 ms blocks.
        foreach (var inputRate in new[] { 44_100, 16_000 })
        {
#pragma warning disable CS0618 // The upstream factory only adds native-library selection, which is not vendored.
            var resampler = new SpeexResampler(1, inputRate, OpusVoiceCodec.SampleRateHz, 5);
#pragma warning restore CS0618
            var input = VoiceTestSignal.Tone(inputRate, inputRate, 1_000, 0.5);
            var output = new List<float>(OpusVoiceCodec.SampleRateHz + 1024);
            var block = new float[2048];
            var step = inputRate / 100;
            for (var start = 0; start < input.Length; start += step)
            {
                var consumed = Math.Min(step, input.Length - start);
                var produced = block.Length;
                resampler.Process(0, input.AsSpan(start, consumed), ref consumed, block, ref produced);
                Assert(consumed == Math.Min(step, input.Length - start), "every input block is consumed");
                output.AddRange(block.Take(produced));
            }

            Assert(Math.Abs(output.Count - OpusVoiceCodec.SampleRateHz) <= 64, inputRate + " Hz becomes 48,000 samples a second (got " + output.Count + ")");
            var steady = output.Skip(4_800).Take(38_400).ToArray();
            var crossings = 0;
            for (var index = 1; index < steady.Length; index++) if (steady[index - 1] < 0 && steady[index] >= 0) crossings++;
            var hertz = crossings * (double)OpusVoiceCodec.SampleRateHz / steady.Length;
            var rms = Math.Sqrt(steady.Sum(sample => (double)sample * sample) / steady.Length);
            Assert(Math.Abs(hertz - 1_000) <= 5, "a 1 kHz tone stays at 1 kHz (got " + hertz.ToString("F1") + " Hz)");
            Assert(Math.Abs(rms - (0.5 / Math.Sqrt(2))) <= 0.02, "the tone keeps its level (RMS " + rms.ToString("F3") + ")");
        }
    }

    private static void RunSession(short[][] speech, int frames)
    {
        var codec = new OpusVoiceCodec();
        var encoders = speech.Select(_ => codec.CreateEncoder()).ToArray();
        var decoders = speech.Select(_ => speech.Select(_ => codec.CreateDecoder()).ToArray()).ToArray();
        var packets = speech.Select(_ => new byte[OpusVoiceCodec.MaximumPacketLength]).ToArray();
        var lengths = new int[speech.Length];
        var frame = new short[Frame];
        for (var index = 0; index < frames; index++)
        {
            for (var speaker = 0; speaker < speech.Length; speaker++)
            {
                lengths[speaker] = encoders[speaker].Encode(speech[speaker].AsSpan(index * Frame, Frame), packets[speaker]);
            }

            for (var listener = 0; listener < speech.Length; listener++)
            {
                for (var speaker = 0; speaker < speech.Length; speaker++)
                {
                    if (speaker != listener) decoders[listener][speaker].Decode(packets[speaker].AsSpan(0, lengths[speaker]), frame);
                }
            }
        }
    }

    private static ConcentusEncoder BareEncoder()
    {
#pragma warning disable CS0618 // The upstream factory only adds native-library selection, which is not vendored.
        return new ConcentusEncoder(48_000, 1, ConcentusApplication.OPUS_APPLICATION_VOIP)
#pragma warning restore CS0618
        {
            UseVBR = true,
            UseConstrainedVBR = false,
            UseDTX = true,
            UseInbandFEC = true,
            SignalType = ConcentusSignal.OPUS_SIGNAL_VOICE,
            MaxBandwidth = ConcentusBandwidth.OPUS_BANDWIDTH_WIDEBAND,
            Complexity = 10,
            Bitrate = 20_000,
            PacketLossPercent = 10
        };
    }

#pragma warning disable CS0618 // The upstream factory only adds native-library selection, which is not vendored.
    private static ConcentusDecoder BareDecoder() => new(48_000, 1);
#pragma warning restore CS0618

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>The wrapper's encoder and decoder beside bare Concentus ones configured as documented, coding the same frames.</summary>
    private sealed class SideBySide
    {
        private readonly OpusVoiceEncoder wrapped;
        private readonly ConcentusEncoder bare = BareEncoder();
        private readonly OpusVoiceDecoder wrappedDecoder;
        private readonly ConcentusDecoder bareDecoder = BareDecoder();
        private readonly byte[] wrappedPacket = new byte[OpusVoiceCodec.MaximumPacketLength];
        private readonly byte[] barePacket = new byte[OpusVoiceCodec.MaximumPacketLength];
        private readonly short[] wrappedFrame = new short[Frame];
        private readonly short[] bareFrame = new short[Frame];

        internal SideBySide(OpusVoiceCodec codec)
        {
            wrapped = codec.CreateEncoder();
            wrappedDecoder = codec.CreateDecoder();
        }

        /// <summary>
        /// Codes every frame of <paramref name="speech"/> both ways, checking that the two agree byte for byte, and returns
        /// what each way allocated on this thread.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal (long Wrapped, long Bare) Code(short[] speech)
        {
            long wrappedBytes = 0;
            long bareBytes = 0;
            for (var offset = 0; offset < speech.Length; offset += Frame)
            {
                var input = speech.AsSpan(offset, Frame);
                var before = GC.GetAllocatedBytesForCurrentThread();
                var wrappedLength = wrapped.Encode(input, wrappedPacket);
                var middle = GC.GetAllocatedBytesForCurrentThread();
                var bareLength = bare.Encode(input, Frame, barePacket, OpusVoiceCodec.MaximumPacketLength);
                var after = GC.GetAllocatedBytesForCurrentThread();
                Assert(wrappedPacket.AsSpan(0, wrappedLength).SequenceEqual(barePacket.AsSpan(0, bareLength)),
                    "the wrapper applies exactly the documented encoder settings");
                var decodeStart = GC.GetAllocatedBytesForCurrentThread();
                Assert(wrappedDecoder.Decode(wrappedPacket.AsSpan(0, wrappedLength), wrappedFrame), "the wrapped decoder decodes");
                var decodeMiddle = GC.GetAllocatedBytesForCurrentThread();
                bareDecoder.Decode(barePacket.AsSpan(0, bareLength), bareFrame, Frame, false);
                var decodeEnd = GC.GetAllocatedBytesForCurrentThread();
                Assert(wrappedFrame.AsSpan().SequenceEqual(bareFrame), "the wrapped decoder matches the bare one");
                wrappedBytes += (middle - before) + (decodeMiddle - decodeStart);
                bareBytes += (after - middle) + (decodeEnd - decodeMiddle);
            }

            return (wrappedBytes, bareBytes);
        }
    }
}
