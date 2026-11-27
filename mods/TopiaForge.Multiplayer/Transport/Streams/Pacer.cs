using System;

namespace TopiaForge.Multiplayer.Transport.Streams
{
    /// <summary>
    /// The outbound budget of one connection as two token buckets, in integer arithmetic so every platform agrees. The
    /// sustained bucket refills at the sustained rate and holds enough for the burst rate to last the burst time; the
    /// peak bucket caps any instant at the burst rate.
    /// </summary>
    internal sealed class Pacer
    {
        private const long MicrosPerSecond = 1_000_000;

        private readonly long sustainedPerSecond;
        private readonly long peakPerSecond;
        private readonly long sustainedDepth;
        private readonly long peakDepth;

        // Tokens are bytes scaled by a million, so a refill of rate x elapsed microseconds stays exact.
        private long sustainedTokens;
        private long peakTokens;
        private long lastRefill;

        internal Pacer(int sustainedBytesPerSecond, int burstBytesPerSecond, long burstMicros, long now)
        {
            sustainedPerSecond = sustainedBytesPerSecond;
            peakPerSecond = burstBytesPerSecond;
            var burstSurplus = (burstBytesPerSecond - sustainedBytesPerSecond) * burstMicros / MicrosPerSecond;
            sustainedDepth = (burstSurplus + TransportOptions.MaximumDatagramBytes) * MicrosPerSecond;
            peakDepth = 2L * TransportOptions.MaximumDatagramBytes * MicrosPerSecond;
            sustainedTokens = sustainedDepth;
            peakTokens = peakDepth;
            lastRefill = now;
        }

        internal bool CanSend(int bytes, long now)
        {
            Refill(now);
            var needed = bytes * MicrosPerSecond;
            return sustainedTokens >= needed && peakTokens >= needed;
        }

        internal void OnSent(int bytes)
        {
            var spent = bytes * MicrosPerSecond;
            sustainedTokens -= spent;
            peakTokens -= spent;
        }

        /// <summary>Gets the earliest time both buckets hold <paramref name="bytes"/>.</summary>
        internal long NextAvailable(int bytes, long now)
        {
            Refill(now);
            var needed = bytes * MicrosPerSecond;
            var wait = Math.Max(Wait(needed - sustainedTokens, sustainedPerSecond), Wait(needed - peakTokens, peakPerSecond));
            return now + wait;
        }

        private void Refill(long now)
        {
            if (now <= lastRefill) return;
            var elapsed = Math.Min(now - lastRefill, 60 * MicrosPerSecond);
            lastRefill = now;
            sustainedTokens = Math.Min(sustainedDepth, sustainedTokens + (elapsed * sustainedPerSecond));
            peakTokens = Math.Min(peakDepth, peakTokens + (elapsed * peakPerSecond));
        }

        private static long Wait(long deficit, long ratePerSecond) =>
            deficit <= 0 ? 0 : (deficit + ratePerSecond - 1) / ratePerSecond;
    }
}
