extern alias provider;

using provider::TopiaForge.Multiplayer.Engine;
using provider::TopiaForge.Multiplayer.Voice;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The talker's side of the voice service, read off the wire at a host that has no voice service of its own: voice
/// activation and its hangover, push-to-talk spurts, stale capture after a stall, and capture at other device rates.
/// </summary>
internal static class VoiceCaptureTests
{
    private const int Second = 48_000;

    internal static void VoiceActivationKeepsAHangover()
    {
        // Speech for one second, a second of silence, speech for 0.6 s, and silence again. The synthetic speech's quietest
        // frames sit at -47 dBFS, so a -50 dBFS threshold makes every frame of it speech and the hangover exact.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost(withVoice: false);
        var a = rig.AddClient("a");
        a.Voice!.Mode = VoiceTransmitMode.VoiceActivation;
        a.Voice.ActivationThresholdDb = -50;
        a.Source = new VoiceSource(Concat(Speech(1, 3), Silence(Second), Speech(0.6, 4), Silence(Second)));
        rig.StepFor(3_800_000);
        var frames = host.Frames.Where(frame => frame.Speaker == "a").ToList();
        var timestamps = frames.Select(frame => frame.Timestamp).ToHashSet();
        Assert(Enumerable.Range(0, 50).Concat(Enumerable.Range(100, 30)).All(ts => timestamps.Contains((ulong)ts)), "every frame of speech is sent");
        Assert(timestamps.All(ts => ts <= 64 || (ts >= 100 && ts <= 144)), "nothing is sent after a 300 ms hangover");
        var ends = frames.Where(frame => (frame.Flags & VoiceFrameFlags.EndOfTalk) != 0).Select(frame => frame.Timestamp).ToList();
        Assert(ends.SequenceEqual(new ulong[] { 64, 144 }), "the hangover's fifteenth frame ends each spurt (" + string.Join(", ", ends) + ")");
        Assert(frames.Where(frame => frame.Timestamp < 50 || (frame.Timestamp >= 100 && frame.Timestamp < 130))
                .All(frame => (frame.Flags & VoiceFrameFlags.Discontinuous) == 0), "speech is never marked as silence");
        var statistics = a.Voice.GetStatistics();
        Assert(statistics.EndsOfTalk == 2 && statistics.Invalid == 0, "two spurts, both ended");

        // A tone below the threshold is measured for the meter but never sent.
        using var quiet = new VoiceServiceRig();
        var quietHost = quiet.AddHost(withVoice: false);
        var whisper = quiet.AddClient("w");
        whisper.Voice!.Mode = VoiceTransmitMode.VoiceActivation;
        whisper.Source = new VoiceSource(Tone(Second, 0.0014));
        quiet.StepFor(500_000);
        Assert(quietHost.Frames.Count == 0, "a whisper under -40 dBFS sends nothing");
        Assert(Math.Abs(whisper.Voice.InputLevelDb - (-60)) < 2, "and the meter reads its level (" + whisper.Voice.InputLevelDb.ToString("F1") + " dBFS)");
    }

    internal static void PushToTalkEndsTheSpurtWhenCaptureStops()
    {
        // The key is held for one second, released for half a second, then held for another half.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost(withVoice: false);
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        b.Listen("a");
        var source = new VoiceSource(Speech(2, 5));
        source.Gaps.Add((Second, Second * 3 / 2));
        a.Source = source;
        rig.StepFor(2_500_000);
        var frames = host.Frames.Where(frame => frame.Speaker == "a").ToList();
        var expected = Enumerable.Range(0, 51).Concat(Enumerable.Range(75, 26)).Select(ts => (ulong)ts);
        Assert(frames.Select(frame => frame.Timestamp).SequenceEqual(expected),
            "each spurt's frames, then one more that ends it; the second spurt starts where the clock had reached");
        var ends = frames.Where(frame => (frame.Flags & VoiceFrameFlags.EndOfTalk) != 0).Select(frame => frame.Timestamp).ToList();
        Assert(ends.SequenceEqual(new ulong[] { 50, 100 }), "releasing the key ends the spurt");
        var last = frames.Single(frame => frame.Timestamp == 49);
        var end = frames.Single(frame => frame.Timestamp == 50);
        Assert(end.Arrival - last.Arrival <= 40_000, "the end of talk follows the last frame within 40 ms");
        var heard = VoiceServiceTests.Counters(b, "a");
        Assert(heard.TalkSpurts == 2 && heard.Concealed == 0 && heard.Faded == 0, "b plays two spurts and conceals nothing at either end");

        // A held key that briefly yields no samples, with zero-sample calls, keeps the spurt open: no end of talk until the
        // key is released. Listeners treat the 100 ms without audio as loss, which is what it is.
        using var held = new VoiceServiceRig();
        var heldHost = held.AddHost(withVoice: false);
        var c = held.AddClient("c");
        var d = held.AddClient("d");
        d.Listen("c");
        var holding = new VoiceSource(Speech(1, 6)) { HoldThroughGaps = true };
        holding.Gaps.Add((Second / 2, Second * 6 / 10));
        c.Source = holding;
        held.StepFor(1_400_000);
        var heldFrames = heldHost.Frames.Where(frame => frame.Speaker == "c").ToList();
        Assert(heldFrames.Count(frame => (frame.Flags & VoiceFrameFlags.EndOfTalk) != 0) == 1, "only the final release ends the spurt");
        var pause = VoiceServiceTests.Counters(d, "c");
        Assert(pause.Decoded >= 44 && pause.Concealed <= 3 && pause.Faded <= 2, "the listener plays both halves around the gap");
    }

    internal static void SilenceUnderAHeldKeyIsOneFrame()
    {
        // The key stays down through a second of silence. The encoder turns silence into one-byte packets; the first goes
        // out marked as silence, the rest stay home, and listeners play the pause as silence, not loss.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost(withVoice: false);
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        b.Listen("a");
        a.Source = new VoiceSource(Concat(Speech(0.5, 9), Silence(Second), Speech(0.5, 10)));
        rig.StepFor(2_300_000);
        var frames = host.Frames.Where(frame => frame.Speaker == "a").ToList();
        var pause = frames.Where(frame => frame.Timestamp >= 25 && frame.Timestamp < 75).ToList();
        Assert(pause.Count <= 10 && pause.Count(frame => (frame.Flags & VoiceFrameFlags.Discontinuous) != 0) >= 1,
            "a second of silence costs a few frames, one marked as silence, not fifty (" + pause.Count + ")");
        Assert(a.Voice!.GetStatistics().DiscontinuousSuppressed >= 35, "the rest of the silence is never sent");
        Assert(frames.Count(frame => frame.Timestamp >= 75 && frame.Timestamp < 100) == 25, "and speech after it is sent in full");
        var heard = VoiceServiceTests.Counters(b, "a");
        Assert(heard.Concealed == 0 && heard.Faded == 0, "b plays the pause as silence, concealing nothing");
    }

    internal static void StaleCaptureIsDroppedAfterAStall()
    {
        // The talker's main thread stalls for half a second, then hands over everything the microphone heard meanwhile.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost(withVoice: false);
        var a = rig.AddClient("a");
        var source = new VoiceSource(Speech(3, 7));
        a.Source = source;
        rig.StepFor(1_000_000);
        var before = source.SamplesSubmitted;
        a.Stalled = true;
        rig.StepFor(500_000);
        a.Stalled = false;
        rig.Step();
        var burst = source.SamplesSubmitted - before;
        rig.StepFor(500_000);
        var statistics = a.Voice!.GetStatistics();
        Assert(burst > 24_000 && statistics.StaleSamplesDropped == burst - 2_880,
            "of the " + burst + " samples handed over at once, only the newest 60 ms are kept (" + statistics.StaleSamplesDropped + " dropped)");
        var timestamps = host.Frames.Where(frame => frame.Speaker == "a").Select(frame => frame.Timestamp).ToList();
        var resumed = timestamps.First(ts => ts > 52);
        Assert(resumed >= 72 && resumed <= 73, "the stream resumes at the frame the clock reached, not where it stopped (" + resumed + ")");
        Assert(!timestamps.Any(ts => ts > 52 && ts < resumed), "nothing captured during the stall is sent late");
        Assert(statistics.StaleFramesDropped == 1, "the one frame encoded as the stall began is dropped rather than sent half a second late");
        Assert(statistics.RateLimited == 0 && statistics.Invalid == 0, "and nothing bursts against the host's limits");
    }

    internal static void CaptureAtOtherRatesIsResampled()
    {
        // The same speech captured at 44.1 and 16 kHz plays back as the 48 kHz original: same pitch, same timing, same level.
        var original = Speech(2, 8);
        foreach (var rate in new[] { 44_100, 16_000 })
        {
            using var rig = new VoiceServiceRig();
            rig.AddHost(withVoice: false);
            var a = rig.AddClient("a");
            var b = rig.AddClient("b");
            b.Listen("a");
            a.Source = new VoiceSource(VoiceSource.AtRate(original, rate), rate);
            rig.StepFor(2_500_000);
            var output = b.Heard("a").ToArray();
            var (delay, correlation) = VoiceAudio.FindDelay(original, output, Second / 2, Second, 14_400);
            var level = VoiceAudio.LevelDecibels(output, (Second / 2) + delay, Second) - VoiceAudio.LevelDecibels(original, Second / 2, Second);
            Assert(correlation >= 0.8, rate + " Hz capture matches the original, sample for sample (correlation " + correlation.ToString("F3") + ")");
            Assert(delay <= 7_680 && Math.Abs(level) <= 3, "with the usual delay and level (" + (delay / 48) + " ms, " + level.ToString("F1") + " dB)");
            var statistics = a.Voice!.GetStatistics();
            Assert(Math.Abs(statistics.FramesCaptured - 101) <= 2, "two seconds make 100 frames and an end (" + statistics.FramesCaptured + ")");
        }
    }

    internal static void CaptureKeepsUpAtLowFrameRates()
    {
        // The game runs at 10, 20, 30 and 60 frames a second: each frame hands over the capture since the last and pumps
        // once. Every rate keeps all of it but the first call's excess over 60 ms, sends every frame it codes, and is heard
        // in full once the listener's jitter buffer has adapted to frames that arrive a game frame at a time.
        foreach (var fps in new[] { 10, 20, 30, 60 })
        {
            using var rig = new VoiceServiceRig();
            rig.AddHost(withVoice: false);
            var a = rig.AddClient("a");
            var b = rig.AddClient("b");
            b.Listen("a");
            var signal = Speech(4, 41);
            var frameMicros = 1_000_000L / fps;
            var start = rig.NowMicros;
            var submitted = 0;
            var lastFrame = -1L;
            a.Voice!.SetTransmitting(true);
            while (rig.NowMicros - start < 4_500_000)
            {
                var frame = (rig.NowMicros - start) / frameMicros;
                var runs = frame != lastFrame;
                if (runs)
                {
                    lastFrame = frame;
                    var due = (int)Math.Min(signal.Length, (rig.NowMicros - start) * 48 / 1_000);
                    if (due > submitted) a.Voice.SubmitCapture(signal.AsSpan(submitted, due - submitted).ToArray(), due - submitted, Second);
                    submitted = Math.Max(submitted, due);
                }

                a.Stalled = !runs;
                rig.Step();
            }

            a.Stalled = false;
            a.Voice.SetTransmitting(false);
            rig.StepFor(300_000);
            var statistics = a.Voice.GetStatistics();
            var firstExcess = Math.Max(0, 48 * (frameMicros - VoiceService.MinimumCaptureBacklogMicros) / 1_000);
            Assert(statistics.StaleSamplesDropped <= firstExcess,
                fps + " frames a second keep all the capture but the first call's excess (" + statistics.StaleSamplesDropped + " samples dropped)");
            Assert(statistics.StaleFramesDropped == 0 && statistics.FramesRefused == 0 && statistics.FramesSent == statistics.FramesEncoded,
                fps + " frames a second send every frame they code (" + statistics.FramesSent + " of " + statistics.FramesEncoded + ")");
            var (delay, correlation) = VoiceAudio.FindDelay(signal, b.Heard("a").ToArray(), (Second * 5) / 2, Second, 24_000);
            Assert(correlation >= 0.9,
                fps + " frames a second are heard in full (" + (delay / 48) + " ms, correlation " + correlation.ToString("F3") + ")");
        }
    }

    internal static void PushToTalkFollowsTheKeyNotTheCaptureCadence()
    {
        // The microphone hands over 1,024 samples at a time, an audio callback's 21.3 ms, while the main thread pumps every
        // 10 ms, so some pumps see no capture at all. The key is held for two seconds and released while the microphone
        // goes on: one spurt, one end of talk, at the release, and nothing sent after it, though the meter still moves.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost(withVoice: false);
        var a = rig.AddClient("a");
        var signal = Speech(3, 48);
        var submitted = 0;
        var start = rig.NowMicros;
        var released = false;
        a.Voice!.SetTransmitting(true);
        while (submitted + 1_024 <= signal.Length)
        {
            if (!released && rig.NowMicros - start >= 2_000_000)
            {
                a.Voice.SetTransmitting(false);
                released = true;
            }

            while (submitted + 1_024 <= (rig.NowMicros - start) * 48 / 1_000)
            {
                a.Voice.SubmitCapture(signal.AsSpan(submitted, 1_024).ToArray(), 1_024, Second);
                submitted += 1_024;
            }

            rig.Step();
        }

        rig.StepFor(300_000);
        var frames = host.Frames.Where(frame => frame.Speaker == "a").ToList();
        var ends = frames.Where(frame => (frame.Flags & VoiceFrameFlags.EndOfTalk) != 0).ToList();
        Assert(ends.Count == 1 && ends[0].Timestamp == frames.Max(frame => frame.Timestamp),
            "one end of talk, the last frame sent, at the release (" + ends.Count + " ends in " + frames.Count + " frames)");
        Assert(Math.Abs(frames.Count - 100) <= 2, "two seconds held make a hundred frames (" + frames.Count + ")");
        Assert(a.Voice.InputLevelDb > -50 && a.Voice.GetStatistics().FramesCaptured > 140, "and the meter still reads the microphone after the release");
    }

    internal static void CaptureBelongsToTheMainThread()
    {
        // Capture and transmission belong to the main thread, the engine's: from any other thread they are refused at once,
        // so a caller hands them over there. After the service is disposed they are ignored.
        using var rig = new VoiceServiceRig();
        rig.AddHost(withVoice: false);
        var a = rig.AddClient("a");
        var voice = a.Voice!;
        var refused = 0;
        var calls = new Action[] { () => voice.SubmitCapture(new float[480], 480, Second), () => voice.SetTransmitting(true), () => voice.Enabled = false };
        var thread = new Thread(() =>
        {
            foreach (var call in calls)
            {
                try
                {
                    call();
                }
                catch (InvalidOperationException)
                {
                    refused++;
                }
            }
        });
        thread.Start();
        thread.Join();
        Assert(refused == calls.Length && voice.Enabled && voice.GetStatistics().SubmittedSamples == 0, "every call from another thread is refused, and changes nothing");
        voice.Dispose();
        voice.SetTransmitting(true);
        voice.SubmitCapture(new float[480], 480, Second);
        Assert(voice.GetStatistics().SubmittedSamples == 0, "after the service is disposed, capture is ignored");
    }

    internal static float[] Tone(int samples, double amplitude) => VoiceTestSignal.Tone(samples, Second, 440, amplitude);

    internal static float[] Speech(double seconds, uint seed) => VoiceSource.Speech(seconds, seed).Signal;

    internal static float[] Silence(int samples) => new float[samples];

    internal static float[] Concat(params float[][] parts) => parts.SelectMany(part => part).ToArray();

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
