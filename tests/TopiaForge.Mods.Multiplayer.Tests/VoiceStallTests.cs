namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Voice after a stall, as a scene load or a save causes: whatever stalled, the listener's voice worker, its main
/// thread or the host's, speech plays at its usual delay again within a second, and a reader that paused hears the
/// speaker as they are now, never what they said before.
/// </summary>
internal static class VoiceStallTests
{
    // Speech plays 126 ms late without a stall, as VoiceServiceTests measures; after one it must be back within the same
    // 150 ms bound.
    private const int MaximumDelaySamples = 7_200;

    internal static void StallsLeaveNoLastingDelay()
    {
        foreach (var what in new[] { "the listener's voice worker", "the listener's main thread", "the host's main thread" })
        {
            // a talks for six seconds; at two seconds one thread stalls for a whole second, then catches up at once.
            using var rig = new VoiceServiceRig();
            var host = rig.AddHost();
            var a = rig.AddClient("a");
            var b = rig.AddClient("b");
            b.Listen("a");
            a.Source = VoiceSource.Speech(6, seed: 44);
            rig.StepFor(2_000_000);
            Stall(what, host, b, true);
            rig.StepFor(1_000_000);
            Stall(what, host, b, false);
            rig.StepFor(3_500_000);
            var output = b.Heard("a").ToArray();
            foreach (var second in new[] { 3.6, 5.0 })
            {
                var (delay, correlation) = VoiceAudio.FindDelay(a.Source.Signal, output, (int)(second * 48_000), 9_600, 48_000);
                Assert(correlation >= 0.85 && delay <= MaximumDelaySamples,
                    "after a stall of " + what + ", speech at " + second.ToString("F1") + " s plays " + (delay / 48) + " ms late (correlation " +
                    correlation.ToString("F2") + ")");
            }

            var counters = VoiceServiceTests.Counters(b, "a");
            Assert(counters.Trimmed > 0, "the backlog " + what + " left was trimmed (" + counters.Trimmed + " frames)");
        }
    }

    internal static void APausedReaderHearsNothingStaleWhenItResumes()
    {
        // b's emitter for a stops reading for two seconds, as a paused or disabled audio source does, while a finishes
        // talking and falls silent. When it reads again, it plays what a says now, silence, not the end of a's speech.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        var stream = b.Listen("a");
        a.Source = new VoiceSource(VoiceSource.Speech(2, seed: 40).Signal.Concat(new float[48_000 * 4]).ToArray());
        rig.StepFor(1_900_000);
        b.StopListening("a");
        rig.StepFor(2_100_000);
        Assert(stream.Buffered == 0, "the ring holds nothing once its reader has stopped (" + stream.Buffered + " samples)");
        b.Listen("a");
        rig.StepFor(400_000);
        var heard = b.Heard("a").ToArray();
        Assert(VoiceAudio.LevelDecibels(heard, 0, heard.Length) < -90, "and the resumed reader hears silence, nothing of a's old speech");
    }

    private static void Stall(string what, VoiceServiceRig.Member host, VoiceServiceRig.Member listener, bool stalled)
    {
        if (what.Contains("worker", StringComparison.Ordinal)) listener.WorkerStalled = stalled;
        else if (what.Contains("listener", StringComparison.Ordinal)) listener.Stalled = stalled;
        else host.Stalled = stalled;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
