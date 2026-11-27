using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Snapshot interpolation under heavy jitter at the rates the runtime streams: noise in the offset estimate inside the
    /// catch-up dead band never speeds playback up, and twenty samples cover the cap at 30 Hz, so a replica moving on a
    /// curve stays on it (WS3.5). Every stream has 50 ms of transit plus uniform extra transit and is rendered at 60 Hz,
    /// and every frame's rate is checked against the written slew. The first three seconds are the buffer learning the
    /// jitter, so the steady-state claims start after them.
    /// </summary>
    internal static class MultiplayerInterpolationJitterTests
    {
        private const long Start = 1_000_000;
        private const long Frame = 16_667;
        private const long Transit = 50_000;
        private const long Settled = Start + 3_000_000;
        private const double Radius = 5.0;
        private const double TurnSeconds = 2.0;
        private const long CatchUpFrom = SnapshotInterpolationBuffer.MaximumDelayMicros + SnapshotInterpolationBuffer.CatchUpDeadBandMicros;
        private const long FallBackFrom = SnapshotInterpolationBuffer.MinimumDelayMicros - SnapshotInterpolationBuffer.FallBackDeadBandMicros;

        public static void Run()
        {
            LightJitterAtTheFloorNeverSlowsPlayback();
            JitterInsideTheDeadBandNeverSpeedsUp();
            HeavierJitterCatchesUpRarelyAndGently();
            ACurveStaysOnItsPathUnderHeavyJitter();
            TheCapacityCoversTheCapAtTheFastestStream();
            Console.WriteLine("MultiplayerInterpolationJitterTests passed.");
        }

        private static void LightJitterAtTheFloorNeverSlowsPlayback()
        {
            // 30 to 50 ms of extra transit at 20 and 30 Hz holds the target at the 80 ms floor, and the offset estimate's
            // noise moves the delay a few milliseconds either side of it. Falling back from the floor itself turned that
            // noise into slow playback: with 40 ms of jitter at 30 Hz, 901 of 17,100 settled frames ran slower than 0.9
            // times real time, down to 0.87. With the fall-back's dead band no frame leaves 0.9 to 1.1 times real time,
            // and once settled none needs more than the normal slew.
            foreach (var hertz in new[] { 20, 30 })
            {
                foreach (var jitter in new[] { 30_000L, 40_000L, 50_000L })
                {
                    var name = jitter / 1_000 + " ms of jitter at " + hertz + " Hz";
                    var totals = Jittered(hertz, jitter, name);
                    Equal(0, totals.SlowFrames, name + ": no frame ran slower than 0.9 times real time");
                    True(totals.Slowest >= 0.9, name + ": the slowest frame ran at " + Format(totals.Slowest));
                    Equal(0, totals.FastFrames, name + ": and none faster than 1.1");
                    Equal(0, totals.SteadyCorrections, name + ": once settled, no frame needed more than the normal slew");
                    True(totals.SteadySmallestDelay < SnapshotInterpolationBuffer.MinimumDelayMicros && totals.SteadySmallestDelay >= FallBackFrom,
                        name + ": the delay dipped under the floor but stayed inside the dead band, at least " + totals.SteadySmallestDelay);
                }
            }
        }

        private static void JitterInsideTheDeadBandNeverSpeedsUp()
        {
            // Up to 250 ms of extra transit pins the delay at the cap, and the offset estimate wanders around it. Catching
            // up as soon as the delay passed the cap used to run 13 to 15% of frames faster than 1.1 times real time,
            // peaking at 1.38, while the delay stayed past the cap anyway. Inside the dead band no frame runs faster than
            // 1.1, and once the jitter is learnt the delay stays between the floor and the band's 400 ms with no frame
            // needing more than the normal slew.
            foreach (var hertz in new[] { 10, 20, 30 })
            {
                var name = "250 ms of jitter at " + hertz + " Hz";
                var totals = Jittered(hertz, 250_000, name);
                Equal(0, totals.FastFrames, name + ": no frame ran faster than 1.1 times real time");
                True(totals.Fastest <= 1.1, name + ": the fastest frame ran at " + Format(totals.Fastest));
                Equal(0, totals.SteadyCorrections, name + ": once settled, no frame needed more than the normal slew");
                True(totals.SteadySmallestDelay >= SnapshotInterpolationBuffer.MinimumDelayMicros,
                    name + ": the delay stayed above the floor, at least " + totals.SteadySmallestDelay);
                True(totals.SteadyLargestDelay <= CatchUpFrom, name + ": and inside the dead band, at most " + totals.SteadyLargestDelay);
                True(totals.SteadyLargestDelay > SnapshotInterpolationBuffer.MaximumDelayMicros, name + ": which it did reach past the cap");
            }
        }

        private static void HeavierJitterCatchesUpRarelyAndGently()
        {
            // 400 ms of extra transit, more than the cap can hide, used to run a fifth to a quarter of all frames faster
            // than 1.1 times real time, peaking at 1.71. Past the dead band catching up is genuine and rare.
            foreach (var hertz in new[] { 10, 20, 30 })
            {
                var name = "400 ms of jitter at " + hertz + " Hz";
                var totals = Jittered(hertz, 400_000, name);
                var fastShare = totals.FastFrames / (double)totals.Frames;
                True(fastShare < 0.01, name + ": " + Format(100 * fastShare) + "% of frames ran faster than 1.1 times real time");
                True(totals.Fastest <= 1.45, name + ": never faster than 1.45 times real time: " + Format(totals.Fastest));
            }
        }

        private static void TheCapacityCoversTheCapAtTheFastestStream()
        {
            // The render can sit the cap and the dead band behind the estimated sender clock. That estimate is the mean
            // transit, and the newest sample, the quickest to arrive, leads it by about half the jitter: some 158 ms when
            // jitter just pins the delay at the cap at 30 Hz (an interval plus three times a third of the jitter). The
            // buffer also needs the sample before the render and room for the next one. Twenty samples at 30 Hz leave
            // 600 ms for those 558 ms; sixteen left 467 ms and eight 200 ms.
            var interval = SnapshotInterpolationBuffer.MinimumNominalIntervalMicros;
            Equal(1_000_000L / SnapshotInterpolationBuffer.MaximumStreamRateHz, interval, "the shortest nominal interval is one sample at the fastest rate");
            var pinningJitter = SnapshotInterpolationBuffer.MaximumDelayMicros - interval;
            var needed = CatchUpFrom + pinningJitter / 2;
            True((SnapshotInterpolationBuffer.Capacity - 2) * interval >= needed,
                "twenty samples at 30 Hz cover the cap, the dead band and the newest sample's lead: " + needed + " us");
            True((16 - 2) * interval < needed, "sixteen did not");
        }

        private static void ACurveStaysOnItsPathUnderHeavyJitter()
        {
            // A body circles 5 m out, one turn every 2 s, under jitter that holds the delay at the cap. Every frame
            // interpolates between two neighbouring samples, so the replica never strays from the circle further than
            // the chord between them sags. With eight samples at 30 Hz, the held-body rate, 300 ms of jitter left a mean
            // error of 0.125 m and a worst of 0.545 m; with sixteen, three frames in twenty seeds still cut across a
            // dropped sample, four times the sag.
            foreach (var (hertz, jitter) in new[] { (20, 300_000L), (30, 300_000L), (30, 350_000L) })
            {
                var angleStep = 2 * Math.PI / (TurnSeconds * hertz);
                var chordSag = Radius * (1 - Math.Cos(angleStep / 2));
                var name = "a 5 m circle at " + hertz + " Hz with " + jitter / 1_000 + " ms of jitter";
                var error = new ErrorTotals();
                for (ulong seed = 1; seed <= 10; seed++) Circle(hertz, jitter, seed, error);
                var mean = error.Sum / error.Frames;
                True(error.Frames > 15_000, name + ": every frame after the first two seconds was measured");
                True(error.Worst <= chordSag * 1.05,
                    name + ": the worst error " + Format(error.Worst) + " m stays within one chord's sag, " + Format(chordSag) + " m");
                True(mean <= chordSag * 0.7, name + ": the mean error is " + Format(mean) + " m");
                Equal(0, error.Extrapolating, name + ": no frame ran past the newest sample");
            }
        }

        /// <summary>Five seeds of a minute each, with every frame checked and the steady state measured after three seconds.</summary>
        private static Totals Jittered(int hertz, long jitter, string name)
        {
            var totals = new Totals();
            var interval = 1_000_000L / hertz;
            for (ulong seed = 1; seed <= 5; seed++)
            {
                var random = new SeededRandom(seed * 7_919 + (ulong)hertz);
                var arrivals = new List<(long Server, long Local, float X)>();
                for (var index = 0; index < hertz * 60; index++)
                {
                    var server = Start + index * interval;
                    arrivals.Add((server, server + Transit + (long)random.NextDouble(0, jitter), index));
                }

                arrivals.Sort((left, right) => left.Local != right.Local ? left.Local.CompareTo(right.Local) : left.Server.CompareTo(right.Server));
                var buffer = new SnapshotInterpolationBuffer(interval);
                var rate = new PlaybackRate();
                var next = 0;
                for (var now = Start; now < Start + 60_000_000; now += Frame)
                {
                    while (next < arrivals.Count && arrivals[next].Local <= now)
                    {
                        var arrival = arrivals[next++];
                        buffer.Add(new NetTransformSample(arrival.Server, new NetVector3(arrival.X, 0f, 0f), NetQuaternion.Identity), arrival.Local);
                    }

                    var delayBefore = buffer.CurrentDelayMicros;
                    if (!rate.Sample(buffer, now, out _) || now < Settled) continue;
                    if (delayBefore > CatchUpFrom || delayBefore < FallBackFrom) totals.SteadyCorrections++;
                    totals.SteadySmallestDelay = Math.Min(totals.SteadySmallestDelay, buffer.CurrentDelayMicros);
                    totals.SteadyLargestDelay = Math.Max(totals.SteadyLargestDelay, buffer.CurrentDelayMicros);
                }

                True(rate.Frames > 3_500, name + ", seed " + seed + ": every frame from the first sample on was checked");
                totals.Add(rate);
            }

            return totals;
        }

        private static void Circle(int hertz, long jitter, ulong seed, ErrorTotals error)
        {
            var interval = 1_000_000L / hertz;
            var random = new SeededRandom(seed * 104_729 + (ulong)hertz);
            var arrivals = new List<(long Server, long Local)>();
            for (var index = 0; index < hertz * 30; index++)
            {
                var server = Start + index * interval;
                arrivals.Add((server, server + Transit + (long)random.NextDouble(0, jitter)));
            }

            arrivals.Sort((left, right) => left.Local != right.Local ? left.Local.CompareTo(right.Local) : left.Server.CompareTo(right.Server));
            var buffer = new SnapshotInterpolationBuffer(interval);
            var rate = new PlaybackRate();
            var next = 0;
            for (var now = Start; now < Start + 30_000_000; now += Frame)
            {
                while (next < arrivals.Count && arrivals[next].Local <= now)
                {
                    var arrival = arrivals[next++];
                    buffer.Add(new NetTransformSample(arrival.Server, OnCircle(arrival.Server), NetQuaternion.Identity), arrival.Local);
                }

                if (!rate.Sample(buffer, now, out var result) || now < Start + 2_000_000) continue;
                if (result.Mode == InterpolationMode.Extrapolating || result.Mode == InterpolationMode.ExtrapolationCapped) error.Extrapolating++;
                var truth = OnCircle(result.RenderTimeMicros);
                var dx = (double)result.Position.X - truth.X;
                var dz = (double)result.Position.Z - truth.Z;
                var distance = Math.Sqrt(dx * dx + dz * dz);
                error.Sum += distance;
                error.Worst = Math.Max(error.Worst, distance);
                error.Frames++;
            }
        }

        private static NetVector3 OnCircle(long serverTime)
        {
            var angle = 2 * Math.PI * ((serverTime - Start) / 1_000_000.0) / TurnSeconds;
            return new NetVector3((float)(Radius * Math.Cos(angle)), 0f, (float)(Radius * Math.Sin(angle)));
        }

        private static string Format(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

        private sealed class Totals
        {
            public int Frames { get; private set; }

            public int FastFrames { get; private set; }

            public double Fastest { get; private set; }

            public int SlowFrames { get; private set; }

            public double Slowest { get; private set; } = double.MaxValue;

            public int SteadyCorrections { get; set; }

            public long SteadyLargestDelay { get; set; } = long.MinValue;

            public long SteadySmallestDelay { get; set; } = long.MaxValue;

            public void Add(PlaybackRate rate)
            {
                Frames += rate.Frames;
                FastFrames += rate.FastFrames;
                Fastest = Math.Max(Fastest, rate.FastestRatio);
                SlowFrames += rate.SlowFrames;
                Slowest = Math.Min(Slowest, rate.SlowestRatio);
            }
        }

        private sealed class ErrorTotals
        {
            public int Frames { get; set; }

            public int Extrapolating { get; set; }

            public double Sum { get; set; }

            public double Worst { get; set; }
        }
    }
}
