extern alias provider;

using System.Security.Cryptography;
using OpusToc = provider::TopiaForge.Multiplayer.Voice.OpusToc;
using OpusVoiceCodec = provider::TopiaForge.Multiplayer.Voice.OpusVoiceCodec;
using OpusVoiceDecoder = provider::TopiaForge.Multiplayer.Voice.OpusVoiceDecoder;
using OpusVoiceEncoder = provider::TopiaForge.Multiplayer.Voice.OpusVoiceEncoder;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The provider's Opus voice codec over the vendored Concentus port: speech quality, packet bounds, loss recovery,
/// silence, a frozen hash of its output, and TOC validation. Concentus runs the integer (PARITY) paths with analysis
/// off, and the test signal uses only basic arithmetic, so every platform produces the same bytes.
/// </summary>
internal static class OpusVoiceCodecTests
{
    internal const int Frame = OpusVoiceCodec.FrameSampleCount;

    // Frozen from the vendored Concentus 2.2.2 subset (commit 6c2328dc). Any change to the vendored code, its edits or the
    // codec settings changes them; update them only alongside a reviewed vendoring or settings change. The default ones
    // moved once, when the bitrate target moved to 20 kbit/s.
    private const string DefaultPacketsSha256 = "5353b041e703b3cdb80ae06c314e20092bca8d2062ce59d4a9c74c6b2c1a72bf";
    private const string LowCostPacketsSha256 = "cb98ffaa54073ab036ad4d8ba7f15d6434850237fa91b6b71dbbfd9d68fee704";
    private const string DefaultPcmSha256 = "73b0870fe4fce7da049eba1c0f3caec7b46ccbf40184d496d3b50d47bbd8ced1";

    // Measured at 9.0 dB for complexity 10 and 9.1 dB for complexity 5. Opus is a perceptual codec, so waveform SNR is
    // modest; a codec that lost the signal or its alignment scores near 0 dB.
    private const double MinimumRoundTripSnrDecibels = 7.0;

    internal static void RoundTripKeepsSyntheticSpeech()
    {
        var signal = VoiceTestSignal.Voiced(VoiceTestSignal.SampleRate * 6);
        foreach (var complexity in new[] { OpusVoiceCodec.DefaultComplexity, OpusVoiceCodec.MinimumComplexity })
        {
            var codec = new OpusVoiceCodec(complexity);
            var (_, snr) = Align(codec, signal, RoundTrip(codec, Encode(codec.CreateEncoder(), signal)));
            Assert(snr >= MinimumRoundTripSnrDecibels,
                "complexity " + complexity + " keeps synthetic speech above " + MinimumRoundTripSnrDecibels + " dB (got " + snr.ToString("F2") + ")");
        }
    }

    internal static void PacketsStayWithinTheirBounds()
    {
        var speech = VoiceTestSignal.Voiced(VoiceTestSignal.SampleRate * 10);
        foreach (var bitrate in new[] { OpusVoiceCodec.MaximumBitrate, OpusVoiceCodec.MinimumBitrate })
        {
            var packets = Encode(new OpusVoiceCodec(bitrateBitsPerSecond: bitrate).CreateEncoder(), speech);
            foreach (var packet in packets)
            {
                Assert(packet.Length >= 2 && packet.Length <= OpusVoiceCodec.MaximumPacketLength, "speech packets hold audio within 160 bytes");
                Assert(OpusToc.IsVoicePacket(packet) && packet[0] >> 3 == 9, "speech is coded as SILK-only wideband 20 ms frames");
            }

            var kilobits = packets.Sum(packet => packet.Length) * 8.0 / 10 / 1000;
            Assert(kilobits >= bitrate * 0.85 / 1000 && kilobits <= bitrate * 1.1 / 1000,
                "VBR stays near its " + bitrate + " bit/s target (got " + kilobits.ToString("F1") + " kbit/s)");
        }

        // Full-scale noise is the hardest input there is for a speech codec; the packet bound still holds.
        foreach (var packet in Encode(new OpusVoiceCodec().CreateEncoder(), VoiceTestSignal.Noise(VoiceTestSignal.SampleRate * 2)))
        {
            Assert(packet.Length <= OpusVoiceCodec.MaximumPacketLength && OpusToc.IsVoicePacket(packet), "noise packets keep the bounds");
        }

        var encoder = new OpusVoiceCodec().CreateEncoder();
        AssertThrows<ArgumentException>(() => encoder.Encode(new short[Frame - 1], new byte[160]), "a short frame is refused");
        AssertThrows<ArgumentException>(() => encoder.Encode(new short[Frame + 1], new byte[160]), "a long frame is refused");
        AssertThrows<ArgumentException>(() => encoder.Encode(new short[Frame], new byte[159]), "a small packet buffer is refused");
        var decoder = new OpusVoiceCodec().CreateDecoder();
        AssertThrows<ArgumentException>(() => decoder.Decode(new byte[] { 0x48 }, new short[Frame - 1]), "decoding needs a whole frame");
        AssertThrows<ArgumentException>(() => decoder.Conceal(new short[Frame + 1]), "concealment needs a whole frame");
        AssertThrows<ArgumentException>(() => decoder.Recover(new byte[] { 0x48 }, new short[0]), "recovery needs a whole frame");
    }

    internal static void ForwardErrorCorrectionBeatsConcealment()
    {
        var speech = VoiceTestSignal.Voiced(VoiceTestSignal.SampleRate * 10);
        var hinted = LossTrial(speech, OpusVoiceCodec.DefaultExpectedLossPercent);
        var unhinted = LossTrial(speech, expectedLossPercent: 0);
        Assert(hinted.FecMean >= hinted.ConcealedMean + 2.0,
            "with a loss hint, frames recovered from the next packet's FEC beat concealment by 2 dB or more (FEC " +
            hinted.FecMean.ToString("F2") + " dB, PLC " + hinted.ConcealedMean.ToString("F2") + " dB)");
        Assert(hinted.FecBetter * 2 >= hinted.Lost, "FEC improves at least half of the lost frames (" + hinted.FecBetter + " of " + hinted.Lost + ")");
        Assert(Math.Abs(unhinted.FecMean - unhinted.ConcealedMean) < 0.5,
            "without a loss hint the encoder adds no FEC, so recovery is no better than concealment");
        Assert(hinted.FecMean >= unhinted.FecMean + 2.0, "the loss hint is what pays for the recovery");
    }

    internal static void SilenceBecomesDiscontinuousTransmission()
    {
        var encoder = new OpusVoiceCodec().CreateEncoder();
        var decoder = new OpusVoiceCodec().CreateDecoder();
        var packet = new byte[OpusVoiceCodec.MaximumPacketLength];
        var silence = new short[Frame];
        var frame = new short[Frame];
        var lengths = new List<int>();
        var discontinuous = 0;
        var loudestComfortNoise = 0;
        for (var index = 0; index < 60; index++)
        {
            var length = encoder.Encode(silence, packet);
            lengths.Add(length);
            Assert(OpusToc.IsVoicePacket(packet.AsSpan(0, length)), "silence packets are valid voice packets");
            Assert(decoder.Decode(packet.AsSpan(0, length), frame), "silence packets decode");
            if (index < 10) continue;
            if (OpusToc.IsDiscontinuous(packet.AsSpan(0, length))) discontinuous++;
            foreach (var sample in frame) loudestComfortNoise = Math.Max(loudestComfortNoise, Math.Abs((int)sample));
        }

        Assert(lengths[0] > 1, "the first silent frame is still coded, as the hangover before DTX");
        Assert(discontinuous >= 45, "after the hangover, sustained silence is TOC-only DTX packets (got " + discontinuous + " of 50)");
        Assert(loudestComfortNoise <= 64, "DTX packets decode to near-silent comfort noise (peak " + loudestComfortNoise + ")");
        var speech = VoiceTestSignal.Voiced(Frame * 4);
        for (var start = 0; start < speech.Length; start += Frame)
        {
            Assert(encoder.Encode(speech.AsSpan(start, Frame), packet) > 20, "speech after silence is coded at once");
        }
    }

    internal static void EncodedOutputMatchesItsFrozenHash()
    {
        var speech = VoiceTestSignal.Voiced(VoiceTestSignal.SampleRate * 5);
        var packets = Encode(new OpusVoiceCodec().CreateEncoder(), speech);
        var lowCost = Encode(new OpusVoiceCodec(OpusVoiceCodec.MinimumComplexity, OpusVoiceCodec.MinimumBitrate, 20).CreateEncoder(), speech);
        var decoder = new OpusVoiceCodec().CreateDecoder();
        var pcm = new byte[packets.Count * Frame * 2];
        var frame = new short[Frame];
        for (var index = 0; index < packets.Count; index++)
        {
            Assert(decoder.Decode(packets[index], frame), "every frozen packet decodes");
            for (var sample = 0; sample < Frame; sample++)
            {
                BitConverter.TryWriteBytes(pcm.AsSpan(((index * Frame) + sample) * 2), frame[sample]);
            }
        }

        // All three are compared before failing, so one run shows every value that moved.
        var mismatches = new List<string>();
        CompareHash(PacketHash(packets), DefaultPacketsSha256, "the default settings' packets", mismatches);
        CompareHash(PacketHash(lowCost), LowCostPacketsSha256, "the packets at complexity 5, 20 kbit/s and a 20% loss hint", mismatches);
        CompareHash(Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant(), DefaultPcmSha256, "the decoded audio", mismatches);
        Assert(mismatches.Count == 0, "the codec output matches its frozen SHA-256 values: " + string.Join("; ", mismatches));
    }

    internal static void TocValidationAcceptsOnlyMonoTwentyMillisecondFrames()
    {
        var accepted = Enumerable.Range(0, 256).Where(value => OpusToc.IsVoiceToc((byte)value)).ToArray();
        Assert(accepted.SequenceEqual(new[] { 0x08, 0x28, 0x48, 0x68, 0x78, 0x98, 0xB8, 0xD8, 0xF8 }),
            "exactly the nine mono, one-frame, 20 ms configurations pass: " + string.Join(",", accepted.Select(value => value.ToString("x2"))));
        Assert(!OpusToc.IsVoiceToc(0x49) && !OpusToc.IsVoiceToc(0x4A) && !OpusToc.IsVoiceToc(0x4B), "frame-count codes 1 to 3 are refused");
        Assert(!OpusToc.IsVoiceToc(0x4C), "stereo is refused");
        Assert(!OpusToc.IsVoiceToc(0x40) && !OpusToc.IsVoiceToc(0x50) && !OpusToc.IsVoiceToc(0x58), "10, 40 and 60 ms SILK frames are refused");
        Assert(!OpusToc.IsVoiceToc(0x70) && !OpusToc.IsVoiceToc(0xF0), "10 ms hybrid and 10 ms CELT frames are refused");
        Assert(!OpusToc.IsVoicePacket(ReadOnlySpan<byte>.Empty), "an empty packet is refused");
        Assert(OpusToc.IsVoicePacket(new byte[] { 0x48 }) && OpusToc.IsDiscontinuous(new byte[] { 0x48 }), "a TOC-only packet is DTX");
        Assert(!OpusToc.IsDiscontinuous(new byte[] { 0x48, 0x00 }) && !OpusToc.IsDiscontinuous(new byte[] { 0x49 }), "only a valid TOC alone is DTX");
        var largest = new byte[OpusVoiceCodec.MaximumPacketLength];
        largest[0] = 0x48;
        Assert(OpusToc.IsVoicePacket(largest), "a 160-byte packet passes");
        var oversized = new byte[OpusVoiceCodec.MaximumPacketLength + 1];
        oversized[0] = 0x48;
        Assert(!OpusToc.IsVoicePacket(oversized), "a 161-byte packet is refused");
        var codec = new OpusVoiceCodec();
        Assert(codec.IsValidPacket(largest) && !codec.IsValidPacket(oversized), "the codec validates through the same check");
        Assert(codec.IsDiscontinuous(new byte[] { 0x48 }) && !codec.IsDiscontinuous(largest), "the codec spots DTX through the same check");

        // A refused packet never reaches Concentus: the decoder conceals instead and says so.
        var decoder = codec.CreateDecoder();
        var frame = new short[Frame];
        Array.Fill(frame, (short)1234);
        Assert(!decoder.Decode(new byte[] { 0x50, 0x01, 0x02 }, frame) && frame.All(sample => sample != 1234), "a 40 ms packet is concealed");
        Assert(!decoder.Recover(new byte[] { 0x4C, 0x01 }, frame), "a stereo next packet is not used for recovery");
    }

    internal static List<byte[]> Encode(OpusVoiceEncoder encoder, short[] signal)
    {
        var packets = new List<byte[]>(signal.Length / Frame);
        var packet = new byte[OpusVoiceCodec.MaximumPacketLength];
        for (var start = 0; start + Frame <= signal.Length; start += Frame)
        {
            var length = encoder.Encode(signal.AsSpan(start, Frame), packet);
            packets.Add(packet.AsSpan(0, length).ToArray());
        }

        return packets;
    }

    /// <summary>Decodes <paramref name="packets"/> with a new decoder, in order and without loss.</summary>
    internal static short[] RoundTrip(OpusVoiceCodec codec, List<byte[]> packets)
    {
        var decoder = codec.CreateDecoder();
        var decoded = new short[packets.Count * Frame];
        for (var index = 0; index < packets.Count; index++)
        {
            Assert(decoder.Decode(packets[index], decoded.AsSpan(index * Frame, Frame)), "every packet decodes");
        }

        return decoded;
    }

    /// <summary>
    /// Finds how far decoded audio lags its input, searching near the encoder's look-ahead, and the SNR at that lag
    /// over everything after the first second.
    /// </summary>
    internal static (int Delay, double Snr) Align(OpusVoiceCodec codec, short[] reference, short[] decoded)
    {
        var lookahead = codec.CreateEncoder().LookaheadSamples;
        var from = VoiceTestSignal.SampleRate;
        var best = (Delay: lookahead, Snr: double.MinValue);
        for (var delay = lookahead - 48; delay <= lookahead + 48; delay++)
        {
            var snr = VoiceTestSignal.SnrDecibels(reference, from - delay, decoded.AsSpan(from, decoded.Length - from));
            if (snr > best.Snr) best = (delay, snr);
        }

        return best;
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    internal static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static (int Lost, int FecBetter, double FecMean, double ConcealedMean) LossTrial(short[] speech, int expectedLossPercent)
    {
        var codec = new OpusVoiceCodec(expectedLossPercent: expectedLossPercent);
        var packets = Encode(codec.CreateEncoder(), speech);
        var (delay, _) = Align(codec, speech, RoundTrip(codec, packets));
        var concealing = codec.CreateDecoder();
        var recovering = codec.CreateDecoder();
        var concealed = new short[Frame];
        var recovered = new short[Frame];
        var lost = 0;
        var fecBetter = 0;
        double fecTotal = 0;
        double concealedTotal = 0;
        for (var index = 0; index < packets.Count - 1; index++)
        {
            // A receiver that loses every 20th packet once its stream has settled.
            if (index < 60 || index % 20 != 0)
            {
                concealing.Decode(packets[index], concealed);
                recovering.Decode(packets[index], recovered);
                continue;
            }

            concealing.Conceal(concealed);
            Assert(recovering.Recover(packets[index + 1], recovered), "the next packet is usable for recovery");
            var reference = (index * Frame) - delay;
            var fec = VoiceTestSignal.SnrDecibels(speech, reference, recovered);
            var plc = VoiceTestSignal.SnrDecibels(speech, reference, concealed);
            lost++;
            if (fec > plc + 0.01) fecBetter++;
            fecTotal += fec;
            concealedTotal += plc;
        }

        return (lost, fecBetter, fecTotal / lost, concealedTotal / lost);
    }

    private static string PacketHash(List<byte[]> packets)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[2];
        foreach (var packet in packets)
        {
            BitConverter.TryWriteBytes(length, (ushort)packet.Length);
            hash.AppendData(length);
            hash.AppendData(packet);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CompareHash(string actual, string expected, string what, List<string> mismatches)
    {
        if (actual != expected) mismatches.Add(what + " hash to " + actual);
    }
}
