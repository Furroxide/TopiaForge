using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Snapshot interpolation when the sender's timeline or the network's latency moves further than the 10% slew can
    /// absorb in time: the render catches up or falls back at a bounded rate, every frame is checked against the written
    /// slew, and neither the render clock nor the replica jumps (WS3.5). Each stream is 10 or 20 Hz with 50 ms of
    /// transit, rendered at 60 Hz.
    /// </summary>
    internal static class MultiplayerInterpolationLagTests
    {
        private const long Start = 1_000_000;
        private const long Frame = 16_667;

        public static void Run()
        {
            ATimelineStepIsCaughtUpWithoutAJump();
            ALateFirstSampleIsCaughtUpWithoutAJump();
            LatencyDropsAreCaughtUpWithoutAJump();
            ALatencyRiseFallsBackAtABoundedRate();
            Console.WriteLine("MultiplayerInterpolationLagTests passed.");
        }

        private static void ATimelineStepIsCaughtUpWithoutAJump()
        {
            // The sender's timeline steps 900 ms forward at sample 30, under the 1 s that resets the buffer. The render
            // used to fall behind the whole buffer, then jump 17.2 frames' worth in one frame, with the delay at 625 ms.
            var arrivals = new List<Arrival>();
            for (var index = 0; index < 150; index++)
            {
                var sent = Start + index * 100_000L;
                arrivals.Add(new Arrival(sent + (index >= 30 ? 900_000 : 0), index, sent + 50_000));
            }

            ExpectCaughtUp(Play(arrivals, 100_000), "a 900 ms timeline step");
        }

        private static void ALateFirstSampleIsCaughtUpWithoutAJump()
        {
            // The first sample arrives 900 ms late and the stream starts a second later, so the first offset is wrong
            // by 900 ms. The render used to jump 17.2 frames' worth when the late sample left the buffer.
            var arrivals = new List<Arrival> { new Arrival(Start, 0, Start + 950_000) };
            for (var index = 10; index < 150; index++) arrivals.Add(new Arrival(Start + index * 100_000L, index, Start + index * 100_000L + 50_000));
            ExpectCaughtUp(Play(arrivals, 100_000), "a late first sample");
        }

        private static void LatencyDropsAreCaughtUpWithoutAJump()
        {
            // 950 to 50 ms at 10 Hz used to jump 12.5 frames' worth, with 6 frames outside 0.9 to 1.1; at 20 Hz a drop of
            // only 300 ms already went past 1.1, and one of 600 ms jumped 16.2 frames' worth.
            ExpectCaughtUp(Play(Latency(10, 950_000, 50_000, 30), 100_000), "950 to 50 ms at 10 Hz");
            ExpectCaughtUp(Play(Latency(20, 350_000, 50_000, 60), 50_000), "350 to 50 ms at 20 Hz", mustCorrect: false);
            ExpectCaughtUp(Play(Latency(20, 650_000, 50_000, 60), 50_000), "650 to 50 ms at 20 Hz");
        }

        private static void ALatencyRiseFallsBackAtABoundedRate()
        {
            // 50 to 950 ms at 10 Hz: the samples stop for 900 ms, then arrive later than the render. At 0.9 times real
            // time the render stayed ahead of them, extrapolating, for about nine seconds.
            var played = Play(Latency(10, 50_000, 950_000, 30), 100_000);
            True(played.Rate.SlowestRatio < 0.9,
                "the render fell back faster than the normal slew: " + played.Rate.SlowestRatio.ToString("F3", CultureInfo.InvariantCulture));
            True(played.Rate.SlowestRatio >= 0.5, "but never below half real time");
            True(played.Rate.FastestRatio <= 1.1, "and never sped up");
            True(played.LastExtrapolatingMicros < Start + 8_000_000, "it is back inside the samples within five seconds of the rise");
            Equal(played.Buffer.TargetDelayMicros, played.Buffer.CurrentDelayMicros, "and settled on the target");
        }

        /// <param name="mustCorrect">Whether the change is large enough to push the delay past the cap.</param>
        private static void ExpectCaughtUp(Played played, string name, bool mustCorrect = true)
        {
            True(played.Rate.Frames > 650, name + ": every frame from the first sample on was checked");
            if (mustCorrect) True(played.Rate.Corrections > 0, name + ": the render had to catch up");
            True(played.Rate.FastestRatio <= 2.0, name + ": never faster than twice real time");
            True(played.Rate.SlowestRatio >= 0.9, name + ": never slower than the normal slew");
            True(played.Rate.LargestDelay <= SnapshotInterpolationBuffer.MaximumDelayMicros + 100_000,
                name + ": the delay stayed within 100 ms of the cap, at most " + played.Rate.LargestDelay);
            True(played.LargestStep <= 2.05,
                name + ": the replica never jumps, at most " + played.LargestStep.ToString("F3", CultureInfo.InvariantCulture) + " frames' worth");
            Equal(0, played.ExtrapolatingFrames, name + ": the render never ran out of samples");
            True(played.LastCorrectionMicros < Start + 8_000_000, name + ": the correction was over within seconds");
            Equal(played.Buffer.TargetDelayMicros, played.Buffer.CurrentDelayMicros, name + ": and playback settled on the target");
            True(played.Buffer.CurrentDelayMicros <= SnapshotInterpolationBuffer.MaximumDelayMicros, name + ": within the cap");
        }

        private static List<Arrival> Latency(int hertz, long before, long after, int switchAt)
        {
            var interval = 1_000_000L / hertz;
            var arrivals = new List<Arrival>();
            for (var index = 0; index < hertz * 15; index++)
            {
                var sent = Start + index * interval;
                arrivals.Add(new Arrival(sent, index, sent + (index < switchAt ? before : after)));
            }

            return arrivals;
        }

        /// <summary>
        /// Renders 12 s at 60 Hz. Samples arriving at the same moment are added oldest first. Positions move one unit
        /// per sample, so a smooth replica moves one unit per interval.
        /// </summary>
        private static Played Play(List<Arrival> arrivals, long interval)
        {
            var pending = new List<Arrival>(arrivals);
            pending.Sort((left, right) => left.Local != right.Local ? left.Local.CompareTo(right.Local) : left.Server.CompareTo(right.Server));
            var buffer = new SnapshotInterpolationBuffer(interval);
            var played = new Played(buffer);
            var nominalStep = Frame / (double)interval;
            var next = 0;
            var lastX = double.NaN;
            for (var now = Start; now < Start + 12_000_000; now += Frame)
            {
                while (next < pending.Count && pending[next].Local <= now)
                {
                    var arrival = pending[next++];
                    buffer.Add(new NetTransformSample(arrival.Server, new NetVector3(arrival.X, 0f, 0f), NetQuaternion.Identity), arrival.Local);
                }

                var delayBefore = buffer.CurrentDelayMicros;
                if (!played.Rate.Sample(buffer, now, out var result)) continue;
                if (delayBefore > SnapshotInterpolationBuffer.MaximumDelayMicros || delayBefore < 0) played.LastCorrectionMicros = now;
                if (result.Mode == InterpolationMode.Extrapolating || result.Mode == InterpolationMode.ExtrapolationCapped)
                {
                    played.ExtrapolatingFrames++;
                    played.LastExtrapolatingMicros = now;
                }

                if (!double.IsNaN(lastX)) played.LargestStep = Math.Max(played.LargestStep, Math.Abs(result.Position.X - lastX) / nominalStep);
                lastX = result.Position.X;
            }

            return played;
        }

        private readonly struct Arrival
        {
            public Arrival(long server, float x, long local)
            {
                Server = server;
                X = x;
                Local = local;
            }

            public long Server { get; }

            public float X { get; }

            public long Local { get; }
        }

        private sealed class Played
        {
            public Played(SnapshotInterpolationBuffer buffer)
            {
                Buffer = buffer;
            }

            public SnapshotInterpolationBuffer Buffer { get; }

            public PlaybackRate Rate { get; } = new PlaybackRate();

            public int ExtrapolatingFrames { get; set; }

            public long LastExtrapolatingMicros { get; set; } = Start;

            public long LastCorrectionMicros { get; set; } = Start;

            /// <summary>The largest change of position in one frame, in frames' worth of smooth motion.</summary>
            public double LargestStep { get; set; }
        }
    }
}
