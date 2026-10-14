using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// Plays a replica's samples back a little in the past, so it moves smoothly between updates. The delay covers one
    /// observed sample interval plus three times the observed jitter, clamped to 80-350 ms, and changes by at most 10%
    /// of elapsed time so playback never jumps. All bookkeeping is integer microseconds.
    /// </summary>
    internal sealed class SnapshotInterpolationBuffer
    {
        public const int Capacity = 8;
        public const long MinimumDelayMicros = 80_000;
        public const long MaximumDelayMicros = 350_000;
        public const long ExtrapolationLimitMicros = 250_000;
        public const long DiscontinuityMicros = 1_000_000;
        public const int JitterMarginMultiplier = 3;
        public const int DelaySlewPercent = 10;
        public const long DefaultNominalIntervalMicros = 100_000;

        /// <summary>Times must be below 2^53 microseconds (about 285 years), which keeps every sum exact.</summary>
        public const long MaximumTimeMicros = 1L << 53;

        private readonly NetTransformSample[] samples = new NetTransformSample[Capacity];
        private readonly long nominalIntervalMicros;
        private int count;
        private bool started;
        private long offsetScaled;
        private long jitterScaled;
        private long intervalScaled;
        private long lastTransit;
        private long currentDelay;
        private bool hasRendered;
        private long lastRenderTime;
        private long lastSampleLocal;

        /// <param name="nominalIntervalMicros">The expected gap between samples, used until gaps are observed.</param>
        public SnapshotInterpolationBuffer(long nominalIntervalMicros = DefaultNominalIntervalMicros)
        {
            if (nominalIntervalMicros < 1_000 || nominalIntervalMicros > DiscontinuityMicros)
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

        public long TargetDelayMicros
        {
            get
            {
                var delay = IntervalMicros + JitterMarginMultiplier * JitterMicros;
                return Math.Max(MinimumDelayMicros, Math.Min(MaximumDelayMicros, delay));
            }
        }

        public long CurrentDelayMicros => currentDelay;

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
            count = 0;
            started = false;
            offsetScaled = 0;
            jitterScaled = 0;
            intervalScaled = nominalIntervalMicros << 3;
            lastTransit = 0;
            currentDelay = TargetDelayMicros;
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
        /// Evaluates the replica at <paramref name="localNowMicros"/>. The render time never moves backwards between
        /// resets, and with a full buffer it never falls behind the oldest sample.
        /// </summary>
        public bool TrySample(long localNowMicros, out InterpolatedTransform result)
        {
            RequireTime(localNowMicros, nameof(localNowMicros));
            if (count == 0)
            {
                result = new InterpolatedTransform(NetVector3.Zero, NetQuaternion.Identity, InterpolationMode.Empty, 0);
                return false;
            }

            var target = TargetDelayMicros;
            if (!hasRendered)
            {
                currentDelay = target;
                lastSampleLocal = localNowMicros;
            }
            else if (localNowMicros > lastSampleLocal)
            {
                var step = Math.Min(localNowMicros - lastSampleLocal, DiscontinuityMicros) * DelaySlewPercent / 100;
                currentDelay = currentDelay < target
                    ? Math.Min(target, currentDelay + step)
                    : Math.Max(target, currentDelay - step);
                lastSampleLocal = localNowMicros;
            }

            var render = localNowMicros + OffsetMicros - currentDelay;
            if (hasRendered && render < lastRenderTime) render = lastRenderTime;
            if (count == Capacity && render < samples[0].ServerTimeMicros) render = samples[0].ServerTimeMicros;
            hasRendered = true;
            lastRenderTime = render;
            result = Evaluate(render);
            return true;
        }

        internal static void RequireTime(long value, string parameterName)
        {
            if (value < 0 || value >= MaximumTimeMicros) throw new ArgumentOutOfRangeException(parameterName);
        }

        private void Start(in NetTransformSample sample, long offset)
        {
            Array.Clear(samples, 0, Capacity);
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
                Array.Copy(samples, 1, samples, 0, Capacity - 1);
                count--;
            }

            var position = count;
            while (position > 0 && samples[position - 1].ServerTimeMicros > sample.ServerTimeMicros)
            {
                samples[position] = samples[position - 1];
                position--;
            }

            samples[position] = sample;
            count++;
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
