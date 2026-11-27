using System;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// Estimates how much playout delay one speaker's stream needs. Each frame's transit is its arrival time minus its
    /// stream time; the spread between the fastest transit in the window and the 95th percentile is the jitter to
    /// absorb. The target is that jitter plus one frame, clamped to 40 to 200 ms. Clock offsets cancel out, because only
    /// differences between transits count. Until there are enough samples to judge, the target is the floor plus one
    /// frame: a first talk spurt has no history, and frames that arrive in pairs would leave the floor alone no room.
    /// </summary>
    internal sealed class VoiceDelayEstimator
    {
        internal const long MinimumDelayMicros = 40_000;
        internal const long MaximumDelayMicros = 200_000;

        /// <summary>The transits kept: 128 frames, about 2.6 seconds of speech.</summary>
        internal const int Window = 128;

        /// <summary>The fewest samples that give a percentile; until then the target is <see cref="InitialDelayMicros"/>.</summary>
        internal const int MinimumSamples = 8;

        private readonly long[] transits = new long[Window];
        private readonly long[] sorted = new long[Window];
        private readonly long frameMicros;
        private int count;
        private int next;

        internal VoiceDelayEstimator(long frameMicros)
        {
            if (frameMicros < 1) throw new ArgumentOutOfRangeException(nameof(frameMicros));
            this.frameMicros = frameMicros;
            InitialDelayMicros = Math.Min(MaximumDelayMicros, MinimumDelayMicros + frameMicros);
            TargetDelayMicros = InitialDelayMicros;
        }

        /// <summary>Gets the target before there are enough samples: the floor plus one frame, 60 ms for 20 ms frames.</summary>
        internal long InitialDelayMicros { get; }

        /// <summary>Gets the playout delay the last <see cref="Update"/> chose.</summary>
        internal long TargetDelayMicros { get; private set; }

        /// <summary>Gets the 95th-percentile jitter the last <see cref="Update"/> measured.</summary>
        internal long JitterMicros { get; private set; }

        /// <summary>Gets the target delay in whole frames, rounded up.</summary>
        internal int TargetFrames => (int)((TargetDelayMicros + frameMicros - 1) / frameMicros);

        /// <summary>Records the transit of one frame: when it arrived, against where it sits in the stream.</summary>
        internal void Observe(long arrivalMicros, ulong timestamp)
        {
            transits[next] = arrivalMicros - unchecked((long)timestamp * frameMicros);
            next = (next + 1) % Window;
            if (count < Window) count++;
        }

        /// <summary>Recomputes the target from the window. It allocates nothing.</summary>
        internal void Update()
        {
            if (count < MinimumSamples)
            {
                JitterMicros = 0;
                TargetDelayMicros = InitialDelayMicros;
                return;
            }

            Array.Copy(transits, sorted, count);
            Array.Sort(sorted, 0, count);
            var percentile = sorted[(int)Math.Ceiling(count * 0.95) - 1];
            JitterMicros = percentile - sorted[0];
            TargetDelayMicros = Math.Max(MinimumDelayMicros, Math.Min(MaximumDelayMicros, JitterMicros + frameMicros));
        }

        /// <summary>Forgets every sample, as when a talker's stream restarts with new timestamps.</summary>
        internal void Clear()
        {
            count = 0;
            next = 0;
            Update();
        }
    }

    /// <summary>
    /// How much room recent frames had. A frame's slack is how many frames ahead of playout it arrived, negative when it
    /// arrived late. Its 5th percentile over the window steers drift control: at zero, recent frames land just in time
    /// and playout should wait longer; at three, nearly every frame waits needlessly and one can go. A percentile, unlike
    /// the fill level, holds still under jitter, so corrections follow the clocks rather than the noise.
    /// </summary>
    internal sealed class VoiceSlackWindow
    {
        internal const int Window = 64;

        /// <summary>The arrivals a decision needs after the window was cleared: about two thirds of a second.</summary>
        internal const int MinimumSamples = 32;

        private readonly long[] slacks = new long[Window];
        private readonly long[] sorted = new long[Window];
        private int count;
        private int next;

        internal int Count => count;

        internal void Observe(long slack)
        {
            slacks[next] = slack;
            next = (next + 1) % Window;
            if (count < Window) count++;
        }

        /// <summary>Gets the 5th-percentile slack. It allocates nothing.</summary>
        internal long LowPercentile()
        {
            if (count == 0) throw new InvalidOperationException("The slack window is empty.");
            Array.Copy(slacks, sorted, count);
            Array.Sort(sorted, 0, count);
            return sorted[(int)Math.Ceiling(count * 0.05) - 1];
        }

        internal void Clear()
        {
            count = 0;
            next = 0;
        }
    }

    /// <summary>What one jitter buffer has done since it was created.</summary>
    internal sealed class VoiceJitterStatistics
    {
        internal long Received { get; set; }
        internal long Invalid { get; set; }
        internal long Late { get; set; }
        internal long Duplicates { get; set; }
        internal long Decoded { get; set; }
        internal long Recovered { get; set; }
        internal long Concealed { get; set; }
        internal long Faded { get; set; }
        internal long Silent { get; set; }
        internal long DriftDrops { get; set; }
        internal long DriftInserts { get; set; }
        internal long Resyncs { get; set; }
        internal long Restarts { get; set; }
        internal long TalkSpurts { get; set; }
    }
}
