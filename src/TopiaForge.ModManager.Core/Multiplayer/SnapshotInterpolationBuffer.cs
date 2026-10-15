using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// Plays a replica's samples back a little in the past, so it moves smoothly between updates. The delay covers the
    /// longest wait for the next sample (the observed interval, or the longest gap between buffered samples if that is
    /// longer) plus three times the observed jitter, clamped to 80-350 ms. The playback offset, the render time minus
    /// the local time, moves toward the offset estimate minus that delay by at most 10% of elapsed time, so playback
    /// runs between 0.9 and 1.1 times real time. When the render lags the estimated sender clock by more than the cap
    /// plus a dead band it catches up faster, at up to twice real time, and when it comes closer to that clock than the
    /// 80 ms floor it falls back faster, at down to half real time. A full buffer keeps the samples the render is
    /// between, so the render clock never jumps to stay inside it. All bookkeeping is integer microseconds.
    /// </summary>
    internal sealed class SnapshotInterpolationBuffer
    {
        /// <summary>
        /// The render can sit the cap and the dead band behind the estimated sender clock, and the newest sample leads
        /// that estimate by about half the jitter, some 160 ms when jitter just pins the delay at the cap at
        /// <see cref="MaximumStreamRateHz"/>. Twenty samples at that rate span 633 ms: those 560 ms, the interval the
        /// render is in, and one sample to spare. Eight spanned 233 ms and sixteen 500 ms, so a render held at the cap
        /// cut across samples the buffer had dropped.
        /// </summary>
        public const int Capacity = 20;

        /// <summary>The fastest stream the buffer is sized for: a held body's samples, the fastest the runtime sends.</summary>
        public const int MaximumStreamRateHz = 30;

        /// <summary>The shortest nominal interval a buffer accepts: one sample at <see cref="MaximumStreamRateHz"/>.</summary>
        public const long MinimumNominalIntervalMicros = 1_000_000 / MaximumStreamRateHz;

        public const long MinimumDelayMicros = 80_000;
        public const long MaximumDelayMicros = 350_000;
        public const long ExtrapolationLimitMicros = 250_000;
        public const long DiscontinuityMicros = 1_000_000;
        public const int JitterMarginMultiplier = 3;

        /// <summary>The playback offset moves by at most this percentage of elapsed time: 0.9 to 1.1 times real time.</summary>
        public const int PlaybackSlewPercent = 10;

        /// <summary>The fastest catch-up, when the render lags beyond the cap and the dead band: twice real time.</summary>
        public const int CatchUpSlewPercent = 100;

        /// <summary>The fastest fall-back, when the render comes closer to the estimated sender clock than the floor: half real time.</summary>
        public const int FallBackSlewPercent = 50;

        /// <summary>
        /// How far past the cap the delay may go before catching up speeds up. The offset estimate is noisy under heavy
        /// jitter, and 50 ms is about 2.7 standard deviations of it when uniform jitter just pins the delay at the cap.
        /// </summary>
        public const long CatchUpDeadBandMicros = 50_000;

        /// <summary>Each this many microseconds of lag beyond the dead band, or below the floor, add one percent to the slew.</summary>
        public const long CorrectionMicrosPerPercent = 1_000;

        public const long DefaultNominalIntervalMicros = 100_000;

        /// <summary>Times must be below 2^53 microseconds (about 285 years), which keeps every sum exact.</summary>
        public const long MaximumTimeMicros = 1L << 53;

        /// <summary>The delay beyond which catching up speeds up: the cap plus the dead band.</summary>
        private const long CatchUpFromMicros = MaximumDelayMicros + CatchUpDeadBandMicros;

        private readonly NetTransformSample[] samples = new NetTransformSample[Capacity];

        /// <summary>Whether the gap before each sample spans one that a full buffer dropped to keep the render's segment.</summary>
        private readonly bool[] merged = new bool[Capacity];

        private readonly long nominalIntervalMicros;
        private int count;
        private bool started;
        private long offsetScaled;
        private long jitterScaled;
        private long intervalScaled;
        private long lastTransit;
        private long playbackOffset;
        private bool hasRendered;
        private long lastRenderTime;
        private long lastSampleLocal;

        /// <param name="nominalIntervalMicros">
        /// The expected gap between samples, used until gaps are observed: from one sample at
        /// <see cref="MaximumStreamRateHz"/> to one a second.
        /// </param>
        public SnapshotInterpolationBuffer(long nominalIntervalMicros = DefaultNominalIntervalMicros)
        {
            if (nominalIntervalMicros < MinimumNominalIntervalMicros || nominalIntervalMicros > DiscontinuityMicros)
            {
                throw new ArgumentOutOfRangeException(nameof(nominalIntervalMicros));
            }

            this.nominalIntervalMicros = nominalIntervalMicros;
            Reset();
        }

        public int Count => count;

        /// <summary>RFC 3550 interarrival jitter: the mean deviation of transit time, with gain 1/16.</summary>
        public long JitterMicros => (jitterScaled + 8) >> 4;

        /// <summary>The mean gap between successive newest samples, with gain 1/8.</summary>
        public long IntervalMicros => (intervalScaled + 4) >> 3;

        /// <summary>
        /// The longest gap between successive buffered samples, or zero with fewer than two. An uneven cadence, such
        /// as the scheduler's rotation or a lost sample, makes some gaps longer than the mean interval. A gap left by a
        /// sample the buffer dropped to keep the render's segment says nothing about the cadence and does not count.
        /// </summary>
        public long LargestGapMicros
        {
            get
            {
                var largest = 0L;
                for (var index = 1; index < count; index++)
                {
                    if (merged[index]) continue;
                    largest = Math.Max(largest, samples[index].ServerTimeMicros - samples[index - 1].ServerTimeMicros);
                }

                return largest;
            }
        }

        /// <summary>The longest expected wait for the next sample plus a jitter margin, clamped to 80-350 ms.</summary>
        public long TargetDelayMicros
        {
            get
            {
                var wait = Math.Max(IntervalMicros, LargestGapMicros);
                var delay = wait + JitterMarginMultiplier * JitterMicros;
                return Math.Max(MinimumDelayMicros, Math.Min(MaximumDelayMicros, delay));
            }
        }

        /// <summary>
        /// The delay of the last rendered frame behind the estimated sender clock. It follows the target as the
        /// playback offset slews, so a sudden change of estimate shows here first; before rendering it is the target.
        /// </summary>
        public long CurrentDelayMicros => hasRendered ? OffsetMicros - playbackOffset : TargetDelayMicros;

        /// <summary>The last rendered frame's render time minus its local time.</summary>
        public long PlaybackOffsetMicros => playbackOffset;

        /// <summary>The mean of sender time minus local arrival time, with gain 1/8.</summary>
        public long OffsetMicros => (offsetScaled + 4) >> 3;

        public NetTransformSample GetSample(int index)
        {
            if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
            return samples[index];
        }

        /// <summary>Forgets every sample and estimate.</summary>
        public void Reset()
        {
            Array.Clear(samples, 0, Capacity);
            Array.Clear(merged, 0, Capacity);
            count = 0;
            started = false;
            offsetScaled = 0;
            jitterScaled = 0;
            intervalScaled = nominalIntervalMicros << 3;
            lastTransit = 0;
            playbackOffset = 0;
            hasRendered = false;
            lastRenderTime = 0;
            lastSampleLocal = 0;
        }

        /// <summary>Offers a sample that arrived at <paramref name="localArrivalMicros"/> on the local clock.</summary>
        public SnapshotSampleResult Add(in NetTransformSample sample, long localArrivalMicros)
        {
            RequireTime(localArrivalMicros, nameof(localArrivalMicros));
            var serverTime = sample.ServerTimeMicros;
            var offset = serverTime - localArrivalMicros;
            if (!started)
            {
                Start(sample, offset);
                return SnapshotSampleResult.Accepted;
            }

            if (IndexOf(serverTime) >= 0) return SnapshotSampleResult.Duplicate;
            var deviation = offset - OffsetMicros;
            if (deviation > DiscontinuityMicros || deviation < -DiscontinuityMicros)
            {
                if (serverTime <= samples[count - 1].ServerTimeMicros) return SnapshotSampleResult.Stale;
                Start(sample, offset);
                return SnapshotSampleResult.Reset;
            }

            // Every arrival informs the estimators, late ones included: lateness is what the delay must absorb.
            var transit = -offset;
            var difference = Math.Min(Math.Abs(transit - lastTransit), DiscontinuityMicros);
            jitterScaled += difference - ((jitterScaled + 8) >> 4);
            lastTransit = transit;
            offsetScaled += offset - ((offsetScaled + 4) >> 3);

            if (IsStale(serverTime)) return SnapshotSampleResult.Stale;
            var newest = samples[count - 1].ServerTimeMicros;
            if (serverTime > newest)
            {
                var gap = Math.Min(serverTime - newest, DiscontinuityMicros);
                intervalScaled += gap - ((intervalScaled + 4) >> 3);
            }

            Insert(sample);
            return SnapshotSampleResult.Accepted;
        }

        /// <summary>
        /// Evaluates the replica at <paramref name="localNowMicros"/>. The first frame after a reset renders at the
        /// offset estimate minus the target delay; later frames move the playback offset toward that by the slew
        /// <see cref="SlewPercent"/> allows. The render time never moves backwards between resets: a local clock that
        /// steps back holds it, and the playback offset follows.
        /// </summary>
        public bool TrySample(long localNowMicros, out InterpolatedTransform result)
        {
            RequireTime(localNowMicros, nameof(localNowMicros));
            if (count == 0)
            {
                result = new InterpolatedTransform(NetVector3.Zero, NetQuaternion.Identity, InterpolationMode.Empty, 0);
                return false;
            }

            var target = OffsetMicros - TargetDelayMicros;
            if (!hasRendered)
            {
                playbackOffset = target;
                lastSampleLocal = localNowMicros;
            }
            else if (localNowMicros > lastSampleLocal)
            {
                var step = Math.Min(localNowMicros - lastSampleLocal, DiscontinuityMicros) * SlewPercent() / 100;
                playbackOffset = playbackOffset < target
                    ? Math.Min(target, playbackOffset + step)
                    : Math.Max(target, playbackOffset - step);
                lastSampleLocal = localNowMicros;
            }

            var render = localNowMicros + playbackOffset;
            if (hasRendered && render < lastRenderTime) render = lastRenderTime;
            playbackOffset = render - localNowMicros;
            hasRendered = true;
            lastRenderTime = render;
            result = Evaluate(render);
            return true;
        }

        /// <summary>
        /// How fast the playback offset may move, as a percentage of elapsed time. Normally 10%. While the delay the
        /// render is at exceeds the cap plus the dead band, one percent more per millisecond beyond them, up to 100%
        /// (twice real time); while it is below the 80 ms floor, one percent more per millisecond below it, up to 50%
        /// (half real time). The gain is proportional, so small excursions are corrected gently and large ones quickly,
        /// and noise in the offset estimate inside the dead band never speeds playback past 1.1 times real time.
        /// </summary>
        internal long SlewPercent()
        {
            var delay = OffsetMicros - playbackOffset;
            if (delay > CatchUpFromMicros)
            {
                return Math.Min(CatchUpSlewPercent, PlaybackSlewPercent + (delay - CatchUpFromMicros) / CorrectionMicrosPerPercent);
            }

            if (delay < MinimumDelayMicros)
            {
                return Math.Min(FallBackSlewPercent, PlaybackSlewPercent + (MinimumDelayMicros - delay) / CorrectionMicrosPerPercent);
            }

            return PlaybackSlewPercent;
        }

        internal static void RequireTime(long value, string parameterName)
        {
            if (value < 0 || value >= MaximumTimeMicros) throw new ArgumentOutOfRangeException(parameterName);
        }

        private void Start(in NetTransformSample sample, long offset)
        {
            Array.Clear(samples, 0, Capacity);
            Array.Clear(merged, 0, Capacity);
            samples[0] = sample;
            count = 1;
            started = true;
            offsetScaled = offset << 3;
            lastTransit = -offset;
            hasRendered = false;
            lastRenderTime = 0;
        }

        private int IndexOf(long serverTime)
        {
            for (var index = 0; index < count; index++)
            {
                if (samples[index].ServerTimeMicros == serverTime) return index;
            }

            return -1;
        }

        private bool IsStale(long serverTime)
        {
            if (count == Capacity && serverTime < samples[0].ServerTimeMicros) return true;
            if (!hasRendered) return false;
            for (var index = count - 1; index >= 0; index--)
            {
                var bracket = samples[index].ServerTimeMicros;
                if (bracket <= lastRenderTime) return serverTime < bracket;
            }

            return false;
        }

        private void Insert(in NetTransformSample sample)
        {
            if (count == Capacity)
            {
                var evicted = EvictionIndex();
                Array.Copy(samples, evicted + 1, samples, evicted, count - evicted - 1);
                Array.Copy(merged, evicted + 1, merged, evicted, count - evicted - 1);
                count--;
                merged[evicted] = evicted > 0;
                merged[0] = false;
            }

            var position = count;
            while (position > 0 && samples[position - 1].ServerTimeMicros > sample.ServerTimeMicros)
            {
                samples[position] = samples[position - 1];
                merged[position] = merged[position - 1];
                position--;
            }

            samples[position] = sample;
            merged[position] = false;
            count++;
        }

        /// <summary>
        /// Which sample a full buffer drops: the oldest, unless the render still shows it. A render holding the oldest
        /// sample keeps it, and a render between the oldest two keeps both, so the oldest sample outside that segment
        /// goes instead. Dropping a sample then never moves what is on screen, and the render never needs to jump
        /// forward to stay inside the buffer.
        /// </summary>
        private int EvictionIndex()
        {
            if (!hasRendered) return 0;
            if (lastRenderTime < samples[0].ServerTimeMicros) return 1;
            return lastRenderTime < samples[1].ServerTimeMicros ? 2 : 0;
        }

        private InterpolatedTransform Evaluate(long render)
        {
            var oldest = samples[0];
            if (render < oldest.ServerTimeMicros)
            {
                return new InterpolatedTransform(oldest.Position, oldest.Rotation, InterpolationMode.Holding, render);
            }

            var newest = samples[count - 1];
            if (render >= newest.ServerTimeMicros)
            {
                var ahead = render - newest.ServerTimeMicros;
                if (ahead == 0)
                {
                    return new InterpolatedTransform(newest.Position, newest.Rotation, InterpolationMode.Interpolating, render);
                }

                var seconds = Math.Min(ahead, ExtrapolationLimitMicros) / 1_000_000.0;
                ExtrapolationVelocity(out var vx, out var vy, out var vz);
                var position = new NetVector3(
                    (float)(newest.Position.X + vx * seconds),
                    (float)(newest.Position.Y + vy * seconds),
                    (float)(newest.Position.Z + vz * seconds));
                var mode = ahead > ExtrapolationLimitMicros
                    ? InterpolationMode.ExtrapolationCapped
                    : InterpolationMode.Extrapolating;
                return new InterpolatedTransform(position, newest.Rotation, mode, render);
            }

            for (var index = count - 2; index >= 0; index--)
            {
                var from = samples[index];
                if (render < from.ServerTimeMicros) continue;
                var to = samples[index + 1];
                var t = (double)(render - from.ServerTimeMicros) / (to.ServerTimeMicros - from.ServerTimeMicros);
                return new InterpolatedTransform(
                    Lerp(from.Position, to.Position, t),
                    Nlerp(from.Rotation, to.Rotation, t),
                    InterpolationMode.Interpolating,
                    render);
            }

            throw new InvalidOperationException("The sample buffer is not ordered.");
        }

        /// <summary>The newest sample's own velocity, else the slope of the last two samples, else zero.</summary>
        private void ExtrapolationVelocity(out double x, out double y, out double z)
        {
            var newest = samples[count - 1];
            if (newest.HasVelocity)
            {
                x = newest.Velocity.X;
                y = newest.Velocity.Y;
                z = newest.Velocity.Z;
                return;
            }

            if (count < 2)
            {
                x = y = z = 0.0;
                return;
            }

            var previous = samples[count - 2];
            var seconds = (newest.ServerTimeMicros - previous.ServerTimeMicros) / 1_000_000.0;
            x = ((double)newest.Position.X - previous.Position.X) / seconds;
            y = ((double)newest.Position.Y - previous.Position.Y) / seconds;
            z = ((double)newest.Position.Z - previous.Position.Z) / seconds;
        }

        private static NetVector3 Lerp(NetVector3 from, NetVector3 to, double t) =>
            new NetVector3(
                (float)(from.X + ((double)to.X - from.X) * t),
                (float)(from.Y + ((double)to.Y - from.Y) * t),
                (float)(from.Z + ((double)to.Z - from.Z) * t));

        /// <summary>Normalised linear interpolation along the shorter arc: no trigonometry, so it is deterministic.</summary>
        private static NetQuaternion Nlerp(NetQuaternion from, NetQuaternion to, double t)
        {
            double ax = from.X, ay = from.Y, az = from.Z, aw = from.W;
            double bx = to.X, by = to.Y, bz = to.Z, bw = to.W;
            if (ax * bx + ay * by + az * bz + aw * bw < 0.0)
            {
                bx = -bx;
                by = -by;
                bz = -bz;
                bw = -bw;
            }

            var x = ax + (bx - ax) * t;
            var y = ay + (by - ay) * t;
            var z = az + (bz - az) * t;
            var w = aw + (bw - aw) * t;
            var norm = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (!(norm > 0.0)) return to;
            return new NetQuaternion((float)(x / norm), (float)(y / norm), (float)(z / norm), (float)(w / norm));
        }
    }
}
