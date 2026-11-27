using System;

namespace TopiaForge.Multiplayer.Transport.Recovery
{
    /// <summary>Round-trip estimation as RFC 9002 section 5 specifies, in microseconds.</summary>
    internal sealed class RttEstimator
    {
        /// <summary>Gets the timer granularity RFC 9002 recommends: 1 ms.</summary>
        internal const long Granularity = 1_000;

        private bool hasSample;

        internal RttEstimator(long initialRtt)
        {
            SmoothedRtt = initialRtt;
            RttVariation = initialRtt / 2;
        }

        internal long LatestRtt { get; private set; }

        internal long SmoothedRtt { get; private set; }

        internal long RttVariation { get; private set; }

        internal long MinimumRtt { get; private set; }

        internal bool HasSample => hasSample;

        /// <summary>
        /// Takes one sample. The peer's ack delay, capped at <paramref name="maximumAckDelay"/>, is subtracted only when
        /// that leaves the sample at or above the minimum.
        /// </summary>
        internal void OnSample(long latestRtt, long ackDelay, long maximumAckDelay)
        {
            if (latestRtt < 0) return;
            LatestRtt = latestRtt;
            if (!hasSample)
            {
                hasSample = true;
                MinimumRtt = latestRtt;
                SmoothedRtt = latestRtt;
                RttVariation = latestRtt / 2;
                return;
            }

            MinimumRtt = Math.Min(MinimumRtt, latestRtt);
            var delay = Math.Min(Math.Max(0, ackDelay), maximumAckDelay);
            var adjusted = latestRtt >= MinimumRtt + delay ? latestRtt - delay : latestRtt;
            RttVariation = ((3 * RttVariation) + Math.Abs(SmoothedRtt - adjusted)) / 4;
            SmoothedRtt = ((7 * SmoothedRtt) + adjusted) / 8;
        }

        /// <summary>Gets the probe timeout before backoff: smoothed RTT, variation or granularity, and the ack delay.</summary>
        internal long ProbeTimeout(long maximumAckDelay) =>
            SmoothedRtt + Math.Max(4 * RttVariation, Granularity) + maximumAckDelay;

        /// <summary>Gets how old an unacknowledged packet may grow before it counts as lost: 9/8 of the larger RTT.</summary>
        internal long LossDelay() => Math.Max(Granularity, Math.Max(SmoothedRtt, LatestRtt) * 9 / 8);
    }
}
