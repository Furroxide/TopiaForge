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
    /// slew, and neither the render clock nor the replica jumps (WS3.5). Each stream has 50 ms of transit before or after
    /// the change and is rendered at 60 Hz; each case asserts the range of playback rates it ran at.
    /// </summary>
    internal static class MultiplayerInterpolationLagTests
    {
        private const long Start = 1_000_000;
        private const long Frame = 16_667;

        /// <summary>The delay above which catching up speeds up: the 350 ms cap plus the 50 ms dead band.</summary>
        private const long CatchUpFrom = SnapshotInterpolationBuffer.MaximumDelayMicros + SnapshotInterpolationBuffer.CatchUpDeadBandMicros;

        /// <summary>The delay below which falling back speeds up: the 80 ms floor less its 20 ms dead band.</summary>
        private const long FallBackFrom = SnapshotInterpolationBuffer.MinimumDelayMicros - SnapshotInterpolationBuffer.FallBackDeadBandMicros;

        public static void Run()
        {
            ATimelineStepIsCaughtUpWithoutAJump();
            ALateFirstSampleIsCaughtUpWithoutAJump();
            LatencyDropsAreCaughtUpWithoutAJump();
            ADropInsideTheDeadBandNeverSpeedsUp();
            ALatencyRiseFallsBackAtABoundedRate();
            ASmallRiseFallsBackBelowTheFloor();
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

            ExpectCaughtUp(Play(arrivals, 100_000), "a 900 ms timeline step", 1.85);
        }

        private static void ALateFirstSampleIsCaughtUpWithoutAJump()
        {
            // The first sample arrives 900 ms late and the stream starts a second later, so the first offset is wrong
            // by 900 ms. The render used to jump 17.2 frames' worth when the late sample left the buffer.
            var arrivals = new List<Arrival> { new Arrival(Start, 0, Start + 950_000) };
            for (var index = 10; index < 150; index++) arrivals.Add(new Arrival(Start + index * 100_000L, index, Start + index * 100_000L + 50_000));
            ExpectCaughtUp(Play(arrivals, 100_000), "a late first sample", 1.85);
        }

        private static void LatencyDropsAreCaughtUpWithoutAJump()
        {
            // 950 to 50 ms at 10 Hz used to jump 12.5 frames' worth, with 6 frames outside 0.9 to 1.1; at 20 Hz a drop of
            // 600 ms jumped 16.2 frames' worth.
            ExpectCaughtUp(Play(Latency(10, 950_000, 50_000, 30), 100_000), "950 to 50 ms at 10 Hz", 1.6);
            ExpectCaughtUp(Play(Latency(20, 650_000, 50_000, 60), 50_000), "650 to 50 ms at 20 Hz", 1.5);
        }

        private static void ADropInsideTheDeadBandNeverSpeedsUp()
        {
            // A 300 ms drop at 20 Hz takes the delay to 369 ms, past the cap but inside the dead band, so playback stays
            // within 0.9 to 1.1 times real time. Without the dead band it reached 1.20.
            var played = Play(Latency(20, 350_000, 50_000, 60), 50_000);
            ExpectEveryFrame(played, "350 to 50 ms at 20 Hz");
            Equal(0, played.Rate.Corrections, "350 to 50 ms at 20 Hz: no frame needed more than the normal slew");
            True(played.Rate.FastestRatio > 1.09 && played.Rate.FastestRatio <= 1.1,
                "it caught up at the normal 1.1 times real time: " + Format(played.Rate.FastestRatio));
            True(played.Rate.SlowestRatio >= 0.9, "and never slowed: " + Format(played.Rate.SlowestRatio));
            True(played.Rate.LargestDelay > SnapshotInterpolationBuffer.MaximumDelayMicros && played.Rate.LargestDelay <= CatchUpFrom,
                "the delay went past the cap but stayed inside the dead band: " + played.Rate.LargestDelay);
            Settled(played, "350 to 50 ms at 20 Hz");
        }

        private static void ALatencyRiseFallsBackAtABoundedRate()
        {
            // 50 to 950 ms at 10 Hz: the samples stop for 900 ms, then arrive later than the render. At 0.9 times real
            // time the render stayed ahead of them, extrapolating, for about nine seconds.
            var played = Play(Latency(10, 50_000, 950_000, 30), 100_000);
            ExpectEveryFrame(played, "50 to 950 ms at 10 Hz", smooth: false);
            True(played.Rate.SlowestRatio < 0.6, "the render fell back faster than the normal slew: " + Format(played.Rate.SlowestRatio));
            True(played.Rate.SlowestRatio >= 0.5, "but never below half real time");
            True(played.Rate.FastestRatio <= 1.1, "and never sped past the normal slew: " + Format(played.Rate.FastestRatio));
            True(played.LastExtrapolatingMicros < Start + 8_000_000, "it is back inside the samples within five seconds of the rise");
            Settled(played, "50 to 950 ms at 10 Hz");
        }

        private static void ASmallRiseFallsBackBelowTheFloor()
        {
            // 50 to 150 ms at 30 Hz: the first late sample moves the offset estimate 12.5 ms at once, and the estimate
            // keeps falling faster than 0.9 times real time can follow, so the delay must dip below the 80 ms floor. The
            // fall-back used to wait for the delay to go negative: it sat at 38 ms at 0.9 times real time for 58 frames,
            // and 20 frames extrapolated. Falling back faster from the floor itself kept the dip to 66 ms, but it slowed
            // light-jitter streams held at the floor below 0.9 times real time. From 60 ms, the floor less its dead band,
            // the delay dips to 52 ms, ends 44 frames below the floor, all within 0.75 s, and 14 frames extrapolate. At
            // 20 Hz it dips to 56 ms, for 49 frames within 0.85 s, and 23 frames extrapolate.
            foreach (var (hertz, worstDelay, extrapolating, framesBelow, window) in new[] { (30, 52_000L, 14, 44, 750_000L), (20, 55_500L, 23, 49, 850_000L) })
            {
                var name = "50 to 150 ms at " + hertz + " Hz";
                var played = Play(Latency(hertz, 50_000, 150_000, hertz * 3), 1_000_000 / hertz);
                ExpectEveryFrame(played, name);
                True(played.Rate.SlowestRatio < 0.85 && played.Rate.SlowestRatio >= 0.5,
                    name + ": below the fall-back's dead band it fell back faster than the normal slew: " + Format(played.Rate.SlowestRatio));
                True(played.Rate.FastestRatio <= 1.0, name + ": and never sped up: " + Format(played.Rate.FastestRatio));
                True(played.Rate.SmallestDelay >= worstDelay, name + ": the delay dipped only to " + played.Rate.SmallestDelay);
                True(played.FramesBelowFloor > 0 && played.FramesBelowFloor <= framesBelow, name + ": " + played.FramesBelowFloor + " frames ended below the floor");
                True(played.LastBelowFloorMicros - played.FirstBelowFloorMicros < window,
                    name + ": all within " + (played.LastBelowFloorMicros - played.FirstBelowFloorMicros) + " us");
                True(played.ExtrapolatingFrames <= extrapolating, name + ": " + played.ExtrapolatingFrames + " frames extrapolated");
                Settled(played, name);
            }
        }

        /// <param name="slowestCatchUp">The least the fastest frame must reach: the correction has to be a real catch-up.</param>
        private static void ExpectCaughtUp(Played played, string name, double slowestCatchUp)
        {
            ExpectEveryFrame(played, name);
            True(played.Rate.Corrections > 0, name + ": the render had to catch up");
            True(played.Rate.FastestRatio >= slowestCatchUp && played.Rate.FastestRatio <= 2.0,
                name + ": it caught up at up to twice real time: " + Format(played.Rate.FastestRatio));
            True(played.Rate.SlowestRatio >= 0.9, name + ": never slower than the normal slew: " + Format(played.Rate.SlowestRatio));
            True(played.Rate.LargestDelay <= CatchUpFrom + 100_000,
                name + ": the delay stayed within 100 ms of where catching up speeds up, at most " + played.Rate.LargestDelay);
            Equal(0, played.ExtrapolatingFrames, name + ": the render never ran out of samples");
            True(played.LastCorrectionMicros < Start + 8_000_000, name + ": the correction was over within seconds");
            Settled(played, name);
        }

        /// <summary>
        /// The written slew was checked on every frame from the first sample on. A rise past the extrapolation cap holds
        /// the replica where the cap left it, so only the other cases also check that it never jumps.
        /// </summary>
        private static void ExpectEveryFrame(Played played, string name, bool smooth = true)
        {
            True(played.Rate.Frames > 650, name + ": every frame from the first sample on was checked");
            if (smooth) True(played.LargestStep <= 2.05, name + ": the replica never jumps, at most " + Format(played.LargestStep) + " frames' worth");
        }

        private static void Settled(Played played, string name)
        {
            Equal(played.Buffer.TargetDelayMicros, played.Buffer.CurrentDelayMicros, name + ": playback settled on the target");
            True(played.Buffer.CurrentDelayMicros >= SnapshotInterpolationBuffer.MinimumDelayMicros &&
                 played.Buffer.CurrentDelayMicros <= SnapshotInterpolationBuffer.MaximumDelayMicros, name + ": between the floor and the cap");
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
                if (delayBefore > CatchUpFrom || delayBefore < FallBackFrom) played.LastCorrectionMicros = now;

                // The frame's own correction counts: a frame that ends below the floor could not climb back in time.
                if (buffer.CurrentDelayMicros < SnapshotInterpolationBuffer.MinimumDelayMicros)
                {
                    if (played.FramesBelowFloor++ == 0) played.FirstBelowFloorMicros = now;
                    played.LastBelowFloorMicros = now;
                }

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

        private static string Format(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

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

            /// <summary>Frames that ended with the delay below the 80 ms floor.</summary>
            public int FramesBelowFloor { get; set; }

            public long FirstBelowFloorMicros { get; set; }

            public long LastBelowFloorMicros { get; set; }

            /// <summary>The largest change of position in one frame, in frames' worth of smooth motion.</summary>
            public double LargestStep { get; set; }
        }
    }
}
