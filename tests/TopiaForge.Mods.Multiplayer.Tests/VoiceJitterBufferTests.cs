using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The per-speaker jitter buffer, driven frame by frame with the fake codec, whose output names the path that produced
/// it. Times are microseconds; one frame is 20 ms. Frame <c>ts</c> carries id <c>ts + 1</c> and the previous frame's id
/// as FEC, and its sequence is <c>ts + 1</c> unless a test says otherwise.
/// </summary>
internal static class VoiceJitterBufferTests
{
    private const long Frame = 20_000;

    internal static void TheTargetFollowsJitterWithinItsClamps()
    {
        // A steady stream needs only the 40 ms floor, whatever its clock offset.
        var steady = new VoiceDelayEstimator(Frame);
        for (ulong ts = 0; ts < 200; ts++) steady.Observe(((long)ts * Frame) + 123_456_789, ts);
        steady.Update();
        Assert(steady.TargetDelayMicros == VoiceDelayEstimator.MinimumDelayMicros && steady.JitterMicros == 0, "no jitter means the 40 ms floor");

        // Jitter spread evenly over 0 to 80 ms: the target is the window's 95th percentile plus one frame.
        var jittery = new VoiceDelayEstimator(Frame);
        var jitters = new List<long>();
        var state = 11u;
        for (ulong ts = 0; ts < 200; ts++)
        {
            var jitter = (long)(Next(ref state) % 80_001);
            jitters.Add(jitter);
            jittery.Observe(((long)ts * Frame) + jitter, ts);
        }

        var window = jitters.Skip(jitters.Count - VoiceDelayEstimator.Window).OrderBy(value => value).ToArray();
        var expected = window[(int)Math.Ceiling(window.Length * 0.95) - 1] - window[0] + Frame;
        jittery.Update();
        Assert(jittery.TargetDelayMicros == expected, "the target is p95 jitter plus a frame (" + jittery.TargetDelayMicros + " vs " + expected + ")");
        Assert(expected > 80_000 && expected < 101_000, "for 0 to 80 ms of jitter that is close to 100 ms");
        Assert(jittery.TargetFrames == (int)((expected + Frame - 1) / Frame), "in frames, rounded up");

        // Jitter far beyond 200 ms is clamped there.
        var wild = new VoiceDelayEstimator(Frame);
        for (ulong ts = 0; ts < 200; ts++) wild.Observe(((long)ts * Frame) + (ts % 2 == 0 ? 0 : 600_000), ts);
        wild.Update();
        Assert(wild.TargetDelayMicros == VoiceDelayEstimator.MaximumDelayMicros && wild.TargetFrames == 10, "the target never exceeds 200 ms");

        // Too few samples, or a cleared window, fall back to the initial target: the floor plus a frame.
        var few = new VoiceDelayEstimator(Frame);
        for (ulong ts = 0; ts < VoiceDelayEstimator.MinimumSamples - 1; ts++) few.Observe(((long)ts * Frame) + (ts % 2 == 0 ? 0 : 600_000), ts);
        few.Update();
        Assert(few.TargetDelayMicros == 60_000 && few.InitialDelayMicros == 60_000, "seven samples are not enough to judge");
        wild.Clear();
        Assert(wild.TargetDelayMicros == 60_000 && wild.TargetFrames == 3, "clearing returns to the initial target");
    }

    internal static void ASecondSpurtPlaysCleanlyOnceTheTargetHasAdapted()
    {
        // A first spurt with up to 70 ms of jitter teaches the estimator; the second, with the same jitter, starts at
        // the adapted target and loses nothing. The path is sequenced, so a frame never overtakes the one before it.
        var stream = new Stream();
        var state = 5u;
        var sent = new List<(ulong Ts, long Arrival, VoiceFrameFlags Flags)>();
        var previous = 0L;
        foreach (var (from, to) in new[] { (0UL, 150UL), (300UL, 450UL) })
        {
            for (var ts = from; ts < to; ts++)
            {
                previous = Math.Max(previous, ((long)ts * Frame) + (long)(Next(ref state) % 70_001));
                sent.Add((ts, previous, ts == to - 1 ? VoiceFrameFlags.EndOfTalk : VoiceFrameFlags.None));
            }
        }

        var results = stream.Play(sent, pulls: 520);
        var second = results.Where(result => result.Pull >= 300).ToList();
        Assert(stream.Buffer.TargetFrames >= 4, "the target adapted to the jitter (" + stream.Buffer.TargetFrames + " frames)");
        var played = second.Where(result => result.Kind == VoicePlayout.Decoded).Select(result => result.Id).ToList();
        Assert(played.SequenceEqual(Enumerable.Range(301, 150).Select(id => (short)id)), "the second spurt plays every frame, in order");
        Assert(second.All(result => result.Kind is VoicePlayout.Decoded or VoicePlayout.Silence), "and conceals none of them");
    }

    internal static void MissingFramesUseFecThenConcealmentThenFade()
    {
        var stream = new Stream();
        var dropped = new HashSet<ulong> { 10, 20, 21 };
        for (ulong ts = 30; ts < 40; ts++) dropped.Add(ts);
        var sent = Enumerable.Range(0, 50).Select(ts => (ulong)ts).Where(ts => !dropped.Contains(ts))
            .Select(ts => (ts, (long)ts * Frame, VoiceFrameFlags.None)).ToList();
        var results = stream.Play(sent, pulls: 50);
        var fill = results.FindIndex(result => result.Kind != VoicePlayout.Silence);
        Assert(fill == 2, "the first spurt fills to 60 ms before it plays");
        Playout At(int ts) => results[ts + fill];

        Assert(At(10).Kind == VoicePlayout.Recovered && At(10).Id == 11, "one lost frame is rebuilt from the next frame's FEC");
        Assert(At(20).Kind == VoicePlayout.Concealed && At(20).Id == FakeVoiceCodec.ConcealMarker,
            "of two lost frames, the first is concealed: no frame after it has arrived yet");
        Assert(At(21).Kind == VoicePlayout.Recovered && At(21).Id == 22, "and the second is rebuilt from the frame after it");
        Assert(At(30).Kind == VoicePlayout.Concealed && At(31).Kind == VoicePlayout.Concealed && At(32).Kind == VoicePlayout.Concealed,
            "a longer loss conceals three frames at full level");
        Assert(At(33).Kind == VoicePlayout.Faded && At(34).Kind == VoicePlayout.Faded, "then fades over two");
        var firstFade = At(33).Frame;
        var secondFade = At(34).Frame;
        Assert(firstFade[0] > firstFade[959] && firstFade[959] >= secondFade[0] && secondFade[0] > secondFade[959],
            "the fade only falls");
        Assert(firstFade[959] == FakeVoiceCodec.ConcealMarker / 2 && secondFade[959] == 0, "halfway after one frame, silent after two");
        Assert(Enumerable.Range(35, 4).All(ts => At(ts).Kind == VoicePlayout.Silence), "then the spurt is over and stays silent");
        var resumed = results.Skip(fill + 35).First(result => result.Kind == VoicePlayout.Decoded);
        Assert(resumed.Id == 41 && resumed.Pull == 41, "speech resumes from the first frame after the loss, one frame after it arrives");
        Assert(stream.Buffer.Statistics.Recovered == 2 && stream.Buffer.Statistics.Concealed == 4 && stream.Buffer.Statistics.Faded == 2,
            "statistics count each path");
    }

    internal static void DriftIsCorrectedByDroppingOrInsertingFrames()
    {
        foreach (var (period, faster) in new[] { (19_800L, true), (20_200L, false) })
        {
            // The talker's clock runs 1% fast or slow against the listener's, with 30 ms of path delay.
            var stream = new Stream();
            var sent = Enumerable.Range(0, 3_000)
                .Select(ts => ((ulong)ts, (ts * period) + 30_000, ts == 2_999 ? VoiceFrameFlags.EndOfTalk : VoiceFrameFlags.None)).ToList();
            var results = stream.Play(sent, pulls: 3_000);
            var statistics = stream.Buffer.Statistics;
            if (faster)
            {
                Assert(statistics.DriftDrops >= 20, "a fast talker's surplus frames are dropped (" + statistics.DriftDrops + ")");
            }
            else
            {
                Assert(statistics.DriftInserts >= 20, "a slow talker's shortfall is filled with inserted frames (" + statistics.DriftInserts + ")");
            }

            Assert(statistics.Concealed == 0 && statistics.Faded == 0, "drift never empties the buffer into concealment");
            Assert(results.Skip(100).Take(2_800).All(result => result.Depth <= stream.Buffer.TargetFrames + 2), "the fill level stays near its target");
            var decoded = results.Where(result => result.Kind == VoicePlayout.Decoded).Select(result => (int)result.Id).ToList();
            Assert(decoded.Zip(decoded.Skip(1)).All(pair => pair.Second > pair.First), "playback never goes backwards");
        }
    }

    internal static void LateFramesAreDroppedAndOldNumbersStartAgain()
    {
        // The channel is sequenced, so frames come in order; one can still arrive after its turn has passed.
        var stream = new Stream();
        for (ulong ts = 0; ts < 3; ts++) stream.Push(ts, (long)ts * Frame);
        Assert(stream.Pull().Id == 1 && stream.Pull().Id == 2 && stream.Pull().Id == 3, "three frames fill the buffer and play");
        Assert(stream.Pull().Kind == VoicePlayout.Concealed, "frame 3 is missing when its turn comes");
        stream.Push(3, 4 * Frame);
        Assert(stream.Buffer.Statistics.Late == 1 && stream.Buffer.Statistics.Restarts == 0, "frame 3, arriving after its turn, is late and dropped");
        stream.Push(4, 4 * Frame);
        Assert(stream.Pull() is { Kind: VoicePlayout.Decoded, Id: 5 }, "and playback carries on with frame 4");

        // A number at or below one already received can only start a new stream, however short the old one was.
        var decoder = stream.Decoder;
        stream.Push(0, 5 * Frame, sequence: 2);
        Assert(stream.Buffer.Statistics.Restarts == 1 && decoder.Resets == 1 && stream.Buffer.Statistics.Late == 1,
            "an old number restarts the buffer and the decoder instead of counting as late");
        stream.Push(1, 6 * Frame, sequence: 3);
        stream.Push(2, 7 * Frame, sequence: 4);
        Assert(Enumerable.Range(0, 3).Select(_ => stream.Pull()).Select(result => result.Id).SequenceEqual(new short[] { 1, 2, 3 }),
            "and the new stream plays from its first frame");
    }

    internal static void PausesAndTalkSpurtsPlayAsSilence()
    {
        // A pause the sequence numbers show: frames 0 to 4, then frames 10 to 14 with the next sequences.
        var stream = new Stream();
        for (ulong ts = 0; ts < 5; ts++) stream.Push(ts, 0, sequence: ts + 1);
        for (ulong ts = 10; ts < 15; ts++) stream.Push(ts, 0, sequence: ts - 4);
        var kinds = Enumerable.Range(0, 15).Select(_ => stream.Pull()).ToList();
        Assert(kinds.Take(5).Select(result => result.Id).SequenceEqual(new short[] { 1, 2, 3, 4, 5 }), "frames 0 to 4 play at once: the buffer is full");
        Assert(kinds.Skip(5).Take(5).All(result => result.Kind == VoicePlayout.Silence), "the gap is silence, not concealment");
        Assert(kinds.Skip(10).Take(5).Select(result => result.Id).SequenceEqual(new short[] { 11, 12, 13, 14, 15 }), "then speech resumes");
        Assert(stream.Buffer.Statistics.Concealed == 0 && stream.Buffer.Statistics.Recovered == 0, "nothing was concealed or recovered");

        // A pause after a silence frame, before anything newer has arrived.
        var dtx = new Stream();
        dtx.Push(0, 0);
        dtx.Pull();
        dtx.Push(1, Frame, flags: VoiceFrameFlags.Discontinuous);
        dtx.Pull();
        Assert(dtx.Pull().Id == 1, "the last speech frame plays");
        Assert(dtx.Pull().Kind == VoicePlayout.Decoded, "the silence frame plays");
        Assert(Enumerable.Range(0, 20).Select(_ => dtx.Pull()).All(result => result.Kind == VoicePlayout.Silence), "the pause is silent");
        Assert(dtx.Buffer.Statistics.Concealed == 0 && !dtx.Buffer.IsActive, "with no concealment, and ends the spurt");

        // End of talk ends the spurt; the next one fills again before it plays.
        var talk = new Stream();
        for (ulong ts = 0; ts < 3; ts++) talk.Push(ts, (long)ts * Frame, flags: ts == 2 ? VoiceFrameFlags.EndOfTalk : VoiceFrameFlags.None);
        var spurt = Enumerable.Range(0, 6).Select(_ => talk.Pull()).ToList();
        Assert(spurt.Count(result => result.Kind == VoicePlayout.Decoded) == 3 && !talk.Buffer.IsActive, "end of talk ends the spurt");
        talk.Push(50, 50 * Frame);
        Assert(talk.Pull().Kind == VoicePlayout.Silence && talk.Buffer.IsActive, "a new spurt fills before it plays");
        Assert(talk.Buffer.Statistics.TalkSpurts == 2 && talk.Buffer.Statistics.Concealed == 0, "two spurts, nothing concealed");
    }

    internal static void ANewStreamRestartsTheBuffer()
    {
        var stream = new Stream();
        for (ulong ts = 0; ts < 60; ts++)
        {
            stream.Push(ts, (long)ts * Frame);
            stream.Pull();
        }

        var decoder = stream.Decoder;
        stream.Push(0, 61 * Frame, sequence: 1);
        Assert(stream.Buffer.Statistics.Restarts == 1 && decoder.Resets == 1, "a stream that starts over resets the buffer and the decoder");
        stream.Push(1, 62 * Frame, sequence: 2);
        var results = Enumerable.Range(0, 3).Select(_ => stream.Pull()).ToList();
        Assert(results.Any(result => result is { Kind: VoicePlayout.Decoded, Id: 1 }), "and plays the new stream");
    }

    internal static void SteadyPlaybackAllocatesNothing()
    {
        var codec = new FakeVoiceCodec();
        var buffer = new VoiceJitterBuffer(codec);
        var packets = Enumerable.Range(0, 400).Select(ts => codec.Packet((short)(ts + 1), (short)ts)).ToArray();
        var output = new short[960];
        for (var ts = 0; ts < 200; ts++)
        {
            buffer.Push((ulong)ts + 1, (ulong)ts, VoiceFrameFlags.None, packets[ts], ts * Frame);
            buffer.Pull(output);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var ts = 200; ts < 400; ts++)
        {
            if (ts % 7 != 0) buffer.Push((ulong)ts + 1, (ulong)ts, VoiceFrameFlags.None, packets[ts], ts * Frame);
            buffer.Pull(output);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated == 0, "pushing, pulling, recovering and concealing allocate nothing (" + allocated + " bytes)");
    }

    internal static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    internal readonly record struct Playout(int Pull, VoicePlayout Kind, short Id, short[] Frame, int Depth);

    /// <summary>One speaker's stream into one jitter buffer, with a pull every 20 ms.</summary>
    private sealed class Stream
    {
        private readonly FakeVoiceCodec codec = new();

        internal Stream()
        {
            Buffer = new VoiceJitterBuffer(codec);
            Decoder = codec.LastDecoder!;
        }

        internal VoiceJitterBuffer Buffer { get; }

        internal FakeVoiceCodec.Decoder Decoder { get; }

        internal void Push(ulong ts, long arrival, ulong? sequence = null, VoiceFrameFlags flags = VoiceFrameFlags.None) =>
            Buffer.Push(sequence ?? ts + 1, ts, flags, codec.Packet((short)(ts + 1), (short)ts), arrival);

        internal Playout Pull()
        {
            var frame = new short[960];
            var kind = Buffer.Pull(frame);
            return new Playout(0, kind, frame[0], frame, Buffer.Depth);
        }

        /// <summary>Delivers each frame when it arrives and pulls once every 20 ms, starting at time zero.</summary>
        internal List<Playout> Play(List<(ulong Ts, long Arrival, VoiceFrameFlags Flags)> frames, int pulls)
        {
            var ordered = frames.OrderBy(frame => frame.Arrival).ToList();
            var next = 0;
            var results = new List<Playout>(pulls);
            for (var pull = 0; pull < pulls; pull++)
            {
                var now = pull * Frame;
                while (next < ordered.Count && ordered[next].Arrival <= now)
                {
                    var (ts, arrival, flags) = ordered[next++];
                    Push(ts, arrival, flags: flags);
                }

                var frame = new short[960];
                var kind = Buffer.Pull(frame);
                results.Add(new Playout(pull, kind, frame[0], frame, Buffer.Depth));
            }

            return results;
        }
    }
}
