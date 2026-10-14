extern alias provider;

using TopiaForge.Multiplayer.Engine;
using OpusToc = provider::TopiaForge.Multiplayer.Voice.OpusToc;
using OpusVoiceCodec = provider::TopiaForge.Multiplayer.Voice.OpusVoiceCodec;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The host's relay policy on its own, against explicit times: each limit, when sustained floods strike and honest
/// backlogs never do, stale and backwards frames, the escalation from strikes to an automatic mute to removal, and the
/// provider's Opus header check through the codec seam.
/// </summary>
internal static class VoiceRelayPolicyTests
{
    private const long Second = 1_000_000;

    internal static void AStreamWithinTheLimitsIsRelayed()
    {
        var (policy, speaker, codec) = Create();
        for (var index = 0; index < 75; index++)
        {
            // 25 datagrams a second, two 50-byte frames each: 20 kbit/s of codec data.
            var admission = policy.Admit(speaker, Datagram(codec, (ulong)(2 * index) + 1, (ulong)(2 * index), pair: true), index * 40_000);
            Assert(admission.Verdict == VoiceVerdict.Relay && admission.Packet.Count == 2, "datagram " + index + " is relayed");
        }

        Assert(speaker.LastSequence == 150 && speaker.LastTimestamp == 149, "the stream position follows the last frame relayed");
    }

    internal static void EachLimitStrikes()
    {
        // Frame rate: the bucket holds a second of burst, thirty datagrams; the 31st at once is dropped, not struck.
        var (policy, speaker, codec) = Create();
        for (var index = 0; index < 30; index++)
        {
            Assert(policy.Admit(speaker, Datagram(codec, (ulong)index + 1, (ulong)index), index * 1_000).Verdict == VoiceVerdict.Relay,
                "thirty datagrams in a burst pass");
        }

        Expect(policy.Admit(speaker, Datagram(codec, 31, 30), 31_000), VoiceDropReason.RateLimited, struck: false, "the 31st at once");
        Assert(policy.Admit(speaker, Datagram(codec, 32, 31), Second + 1_000).Verdict == VoiceVerdict.Relay, "a second later the rate is free again");

        // Bitrate: datagrams of two 160-byte frames at 25 a second are about 66 kbit/s, past the 40 kbit/s bucket.
        (policy, speaker, codec) = Create(new FakeVoiceCodec(160));
        var limited = -1;
        for (var index = 0; index < 50 && limited < 0; index++)
        {
            var admission = policy.Admit(speaker, Datagram(codec, (ulong)(2 * index) + 1, (ulong)(2 * index), pair: true), index * 40_000);
            if (admission.Verdict != VoiceVerdict.Relay) limited = index;
        }

        // About 8.2 KB/s against a 5 KB/s refill drains the one-second bucket in about one and a half seconds.
        Assert(limited > 30 && limited < 45, "the bitrate bucket runs dry once its burst is spent (datagram " + limited + ")");

        // Size, codec header and format.
        (policy, speaker, codec) = Create();
        var oversized = new byte[161];
        oversized[0] = FakeVoiceCodec.AudioHeader;
        Expect(policy.Admit(speaker, Raw(1, 0, oversized), 0), VoiceDropReason.Oversized, struck: true, "a 161-byte frame");
        Expect(policy.Admit(speaker, Raw(2, 1, new byte[] { 0x00, 1, 2, 3, 4, 5 }), 1_000), VoiceDropReason.InvalidPacket, struck: true,
            "a frame whose header the codec refuses");
        Expect(policy.Admit(speaker, new byte[] { 0x01, 0x05 }, 2_000), VoiceDropReason.Malformed, struck: true, "a truncated datagram");
        var relayedKind = Datagram(codec, 3, 2);
        relayedKind[0] = VoicePacket.RelayedKind;
        Expect(policy.Admit(speaker, relayedKind, 3_000), VoiceDropReason.Malformed, struck: true, "a client posing as the relay");
        Assert(policy.Admit(speaker, Datagram(codec, 3, 2), 4_000).Verdict == VoiceVerdict.Relay, "a valid frame still passes after them");

        // Order: a repeat is stale and harmless; time that stands still is malformed.
        Expect(policy.Admit(speaker, Datagram(codec, 3, 2), 5_000), VoiceDropReason.Stale, struck: false, "a repeated datagram");
        Expect(policy.Admit(speaker, Datagram(codec, 2, 1), 6_000), VoiceDropReason.Stale, struck: false, "an older datagram");
        Expect(policy.Admit(speaker, Datagram(codec, 4, 2), 7_000), VoiceDropReason.Malformed, struck: true, "a timestamp that does not advance");
        Assert(policy.Admit(speaker, Datagram(codec, 5, 3), 8_000).Verdict == VoiceVerdict.Relay, "the stream carries on");
        speaker.ResetStream(2);
        Assert(policy.Admit(speaker, Datagram(codec, 1, 0), 9_000).Verdict == VoiceVerdict.Relay && speaker.Stream == 2,
            "a new connection starts its sequence again, in a new stream generation");
    }

    internal static void ABacklogIsAbsorbedAndOnlySustainedExcessStrikes()
    {
        // An honest talker, 25 datagrams a second, behind a host that stalls for two seconds: the backlog arrives at
        // once. A second of it passes, the rest is dropped without strikes, and fresh speech passes as the bucket refills.
        var (policy, speaker, codec) = Create();
        ulong next = 0;
        var relayed = 0;
        var dropped = 0;
        VoiceAdmission Admit(long micros)
        {
            next += 2;
            var admission = policy.Admit(speaker, Datagram(codec, next - 1, next - 2, pair: true), micros);
            if (admission.Verdict == VoiceVerdict.Relay) relayed++;
            else dropped++;
            Assert(!admission.Struck, "datagram " + (next / 2) + " earns no strike");
            return admission;
        }

        for (var index = 0; index < 50; index++) Admit(index * 40_000);
        Assert(relayed == 50, "two seconds of steady speech pass");
        for (var index = 0; index < 50; index++) Admit(4 * Second);
        Assert(relayed == 80 && dropped == 20, "after a two-second stall, a second of the backlog passes and the rest is dropped");
        for (var index = 1; index <= 50; index++) Admit((4 * Second) + (index * 40_000));
        Assert(relayed == 130, "fresh speech after the stall all passes");

        // Every datagram draws on a second bucket that refills at twice the rate and holds five seconds of that. A flood
        // at four times the rate empties it in five seconds; only then do rate-limited datagrams strike, and soon mute.
        (policy, speaker, codec) = Create();
        var verdicts = new List<VoiceAdmission>();
        for (var index = 0; index < 1_200; index++)
        {
            verdicts.Add(policy.Admit(speaker, Datagram(codec, (ulong)index + 1, (ulong)index), index * Second / 120));
            if (verdicts[^1].Verdict == VoiceVerdict.AutoMute) break;
        }

        var firstStrike = verdicts.FindIndex(admission => admission.Struck);
        Assert(firstStrike > 0 && firstStrike * Second / 120 >= 4_900_000 && firstStrike * Second / 120 < 5_500_000,
            "a flood at 120 a second strikes only once it has lasted five seconds (" + (firstStrike * 1_000 / 120) + " ms)");
        Assert(verdicts[^1].Verdict == VoiceVerdict.AutoMute, "and the strikes then mute the speaker");
    }

    internal static void FloodsStrikeOnlyPastTwiceTheirLimit()
    {
        // Each limit keeps a sustained allowance of twice the limit, five seconds deep: 300 datagrams refilled at 60 a
        // second, and 50,000 bytes refilled at 10,000 a second. A datagram over a limit strikes only once that limit's own
        // allowance is spent, so a flood of r datagrams a second is first struck after 300 / (r - 60) seconds, and a flood
        // of b bytes a second after 50,000 / (b - 10,000). Below twice a limit a flood is only cut down to the limit.
        var tiny = new FakeVoiceCodec(20);
        var big = new FakeVoiceCodec(160);
        var cases = new (string What, int PerSecond, FakeVoiceCodec Codec, bool Pair, long FirstStrikeFrom, long FirstStrikeTo, long MutedBy)[]
        {
            ("small datagrams at 60 a second, twice the datagram rate", 60, tiny, false, 0, 0, 0),
            ("small datagrams at 90 a second", 90, tiny, false, 9_900_000, 10_100_000, 10_700_000),
            ("small datagrams at 120 a second", 120, tiny, false, 4_900_000, 5_100_000, 5_400_000),
            ("332-byte datagrams at 25 a second, 66 kbit/s", 25, big, true, 0, 0, 0),
            ("332-byte datagrams at 50 a second, 133 kbit/s", 50, big, true, 7_500_000, 7_700_000, 9_600_000),
            ("332-byte datagrams at 59 a second, 157 kbit/s", 59, big, true, 5_100_000, 5_300_000, 6_700_000),
        };
        foreach (var (what, perSecond, codec, pair, strikeFrom, strikeTo, mutedBy) in cases)
        {
            var options = new VoiceOptions(codec);
            options.Validate();
            var policy = new VoiceRelayPolicy(options);
            var speaker = new VoiceSpeaker(1, options);
            long firstStrike = -1;
            long muted = -1;
            long relayedBytes = 0;
            var dropped = 0;
            for (var index = 0; index < perSecond * 120 && muted < 0; index++)
            {
                var now = index * Second / perSecond;
                var datagram = Datagram(codec, (ulong)(2 * index) + 1, (ulong)(2 * index), pair);
                var admission = policy.Admit(speaker, datagram, now);
                if (admission.Struck && firstStrike < 0) firstStrike = now;
                if (admission.Verdict == VoiceVerdict.AutoMute) muted = now;
                if (admission.Verdict == VoiceVerdict.Relay) relayedBytes += datagram.Length;
                else dropped++;
            }

            if (strikeFrom == 0)
            {
                Assert(firstStrike < 0 && dropped > 0, what + " is cut down to the limit, never struck (first strike at " + firstStrike + ")");
                Assert(relayedBytes <= 121 * options.MaximumBitsPerSecond / 8, what + ": what passes stays within the bitrate");
                continue;
            }

            Assert(firstStrike >= strikeFrom && firstStrike <= strikeTo,
                what + " is first struck between " + (strikeFrom / 1_000) + " and " + (strikeTo / 1_000) + " ms (" + (firstStrike / 1_000) + " ms)");
            Assert(muted > 0 && muted <= mutedBy, what + " is muted within " + (mutedBy / 1_000) + " ms (" + (muted / 1_000) + " ms)");
        }
    }

    internal static void HonestTalkersAreNeverStruckAfterAStall()
    {
        // An honest talker at the edge of the limits: 25 datagrams a second of two 80-byte frames, about 34 kbit/s. The host
        // stalls for 0.5, 1.2, 3 and 6 seconds, then three times for 1.1 seconds two seconds apart, and each time reads the
        // whole backlog at once. Nothing strikes, and fresh speech after each stall passes in full.
        var (policy, speaker, codec) = Create(new FakeVoiceCodec(80));
        ulong next = 0;
        long now = 0;
        var relayed = 0;
        void Admit(long at)
        {
            next += 2;
            var admission = policy.Admit(speaker, Datagram(codec, next - 1, next - 2, pair: true), at);
            Assert(!admission.Struck, "datagram " + (next / 2) + " earns no strike");
            if (admission.Verdict == VoiceVerdict.Relay) relayed++;
        }

        void Talk(long micros)
        {
            for (var end = now + micros; now < end; now += 40_000) Admit(now);
        }

        Talk(5 * Second);
        foreach (var (stall, after) in new[] { (Second / 2, 5 * Second), (1_200_000L, 5 * Second), (3 * Second, 5 * Second), (6 * Second, 5 * Second),
                     (1_100_000L, 2 * Second), (1_100_000L, 2 * Second), (1_100_000L, 5 * Second) })
        {
            // What the talker sent during the stall arrives when it ends; the next datagram comes 40 ms later.
            var backlog = stall / 40_000;
            now += stall;
            for (var index = 0; index < backlog; index++) Admit(now);
            now += 40_000;
            var before = relayed;
            Talk(after);
            Assert(relayed - before == after / 40_000, "after a " + (stall / 1_000) + " ms stall, fresh speech passes in full");
        }
    }

    internal static void StrikesMuteThenRemoveTheSpeaker()
    {
        var (policy, speaker, codec) = Create();
        var garbage = new byte[] { 0x7F };
        for (var strike = 1; strike < 20; strike++)
        {
            Expect(policy.Admit(speaker, garbage, strike * 1_000), VoiceDropReason.Malformed, struck: true, "strike " + strike);
        }

        var muted = policy.Admit(speaker, garbage, 20_000);
        Assert(muted.Verdict == VoiceVerdict.AutoMute && muted.Struck, "the 20th strike within ten seconds mutes the speaker");
        Assert(speaker.IsAutoMuted(20_000) && speaker.IsMuted(20_000), "the speaker is muted automatically");
        Expect(policy.Admit(speaker, Datagram(codec, 1, 0), 21_000), VoiceDropReason.ServerMuted, struck: false,
            "while muted, frames are dropped on arrival and earn no strikes");
        var unmutedAt = 20_000 + (30 * Second);
        Assert(!speaker.IsMuted(unmutedAt), "the automatic mute lasts thirty seconds");
        Assert(policy.Admit(speaker, Datagram(codec, 2, 1), unmutedAt).Verdict == VoiceVerdict.Relay, "then the speaker is heard again");

        // A second and a third automatic mute within ten minutes remove the speaker.
        var now = unmutedAt;
        for (var round = 2; round <= 3; round++)
        {
            VoiceAdmission last = default;
            for (var strike = 0; strike < 20; strike++)
            {
                now += 1_000;
                last = policy.Admit(speaker, garbage, now);
            }

            Assert(last.Verdict == (round == 2 ? VoiceVerdict.AutoMute : VoiceVerdict.Kick), "round " + round + " ends in " + last.Verdict);
            now += 30 * Second;
        }

        // Strikes spread thinner than the window never mute.
        (policy, speaker, _) = Create();
        for (var strike = 0; strike < 100; strike++)
        {
            Assert(policy.Admit(speaker, garbage, strike * 600_000).Verdict == VoiceVerdict.Drop, "an occasional bad frame is only dropped");
        }

        Assert(!speaker.IsMuted(100 * 600_000), "a strike every 0.6 s stays below twenty in ten seconds");
    }

    internal static void AServerMuteDropsOnArrivalWithoutStrikes()
    {
        var (policy, speaker, codec) = Create();
        speaker.ServerMuted = true;
        for (var index = 0; index < 100; index++)
        {
            Expect(policy.Admit(speaker, Datagram(codec, (ulong)index + 1, (ulong)index), index * 100), VoiceDropReason.ServerMuted, struck: false,
                "a muted speaker's datagram " + index);
        }

        speaker.ServerMuted = false;
        Assert(policy.Admit(speaker, Datagram(codec, 1, 0), 20 * Second).Verdict == VoiceVerdict.Relay,
            "unmuted, the speaker is heard; the flood while muted spent none of the rate");
    }

    internal static void OpusHeadersAreCheckedBeforeRelay()
    {
        // The host runs the provider's own TOC check through the seam: only mono Opus packets of one 20 ms frame pass.
        var options = new VoiceOptions(new OpusTocSeam());
        options.Validate();
        var policy = new VoiceRelayPolicy(options);
        var speaker = new VoiceSpeaker(1, options);
        ulong next = 0;
        VoiceAdmission Admit(byte toc, int length = 40)
        {
            var payload = new byte[length];
            payload[0] = toc;
            next++;
            return policy.Admit(speaker, Raw(next, next, payload), (long)next * 40_000);
        }

        foreach (var toc in new byte[] { 0x48, 0x78, 0xF8 })
        {
            Assert(Admit(toc).Verdict == VoiceVerdict.Relay, "TOC 0x" + toc.ToString("X2") + ", one mono 20 ms frame, is relayed");
        }

        Assert(Admit(0x48, length: 1).Verdict == VoiceVerdict.Relay, "so is a TOC-only silence packet");
        foreach (var (toc, what) in new[] { ((byte)0x4C, "a stereo frame"), ((byte)0x49, "two frames in one packet"), ((byte)0x50, "a 40 ms frame"), ((byte)0x00, "a 10 ms frame") })
        {
            Expect(Admit(toc), VoiceDropReason.InvalidPacket, struck: true, what);
        }
    }

    internal static byte[] Datagram(FakeVoiceCodec codec, ulong sequence, ulong timestamp, bool pair = false)
    {
        var first = codec.Packet((short)(timestamp + 1), (short)timestamp);
        var second = pair ? codec.Packet((short)(timestamp + 2), (short)(timestamp + 1)) : Array.Empty<byte>();
        var buffer = new byte[VoicePacket.MaximumBytes(codec.MaximumPacketBytes)];
        var length = VoicePacket.WriteUplink(buffer, sequence, timestamp, VoiceFrameFlags.None, first, VoiceFrameFlags.None, second);
        return buffer.AsSpan(0, length).ToArray();
    }

    private static byte[] Raw(ulong sequence, ulong timestamp, byte[] payload)
    {
        var buffer = new byte[VoicePacket.MaximumBytes(payload.Length)];
        var length = VoicePacket.WriteUplink(buffer, sequence, timestamp, VoiceFrameFlags.None, payload, VoiceFrameFlags.None, ReadOnlySpan<byte>.Empty);
        return buffer.AsSpan(0, length).ToArray();
    }

    private static (VoiceRelayPolicy Policy, VoiceSpeaker Speaker, FakeVoiceCodec Codec) Create(FakeVoiceCodec? codec = null)
    {
        codec ??= new FakeVoiceCodec();
        var options = new VoiceOptions(codec);
        options.Validate();
        return (new VoiceRelayPolicy(options), new VoiceSpeaker(1, options), codec);
    }

    private static void Expect(VoiceAdmission admission, VoiceDropReason reason, bool struck, string what) =>
        Assert(admission.Verdict == VoiceVerdict.Drop && admission.Reason == reason && admission.Struck == struck,
            what + " is dropped as " + reason + (struck ? " with a strike" : " without a strike") +
            " (got " + admission.Verdict + "/" + admission.Reason + "/" + admission.Struck + ")");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    /// <summary>The provider's Opus header rules behind the engine's seam, which only the header check needs here.</summary>
    private sealed class OpusTocSeam : IVoiceCodec
    {
        public int SampleRate => OpusVoiceCodec.SampleRateHz;

        public int FrameSamples => OpusVoiceCodec.FrameSampleCount;

        public int MaximumPacketBytes => OpusVoiceCodec.MaximumPacketLength;

        public bool IsValidPacket(ReadOnlySpan<byte> packet) => OpusToc.IsVoicePacket(packet);

        public bool IsDiscontinuous(ReadOnlySpan<byte> packet) => OpusToc.IsDiscontinuous(packet);

        public IVoiceEncoder CreateEncoder() => throw new NotSupportedException();

        public IVoiceDecoder CreateDecoder() => throw new NotSupportedException();
    }
}
