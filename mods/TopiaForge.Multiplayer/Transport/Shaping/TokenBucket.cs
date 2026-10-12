using System;

namespace TopiaForge.Multiplayer.Transport.Shaping
{
    /// <summary>
    /// One token bucket in integer arithmetic, so every platform agrees: bytes scaled by a million, refilled at the
    /// rate per microsecond elapsed. A rate of zero holds everything back for good.
    /// </summary>
    internal sealed class TokenBucket
    {
        private const long Scale = 1_000_000;

        private readonly long depth;
        private long ratePerSecond;
        private long tokens;
        private long lastRefill;

        internal TokenBucket(long bytesPerSecond, long depthBytes, long now)
        {
            ratePerSecond = bytesPerSecond;
            depth = depthBytes * Scale;
            tokens = depth;
            lastRefill = now;
        }

        internal long BytesPerSecond => ratePerSecond;

        internal void SetRate(long bytesPerSecond, long now)
        {
            Refill(now);
            ratePerSecond = bytesPerSecond;
        }

        internal bool CanSpend(int bytes, long now)
        {
            Refill(now);
            return tokens >= bytes * Scale;
        }

        /// <summary>Takes <paramref name="bytes"/>, even past empty: an overdraft delays whatever comes next.</summary>
        internal void Spend(int bytes) => tokens -= bytes * Scale;

        /// <summary>Gets the earliest time the bucket holds <paramref name="bytes"/>, or <see cref="long.MaxValue"/> at rate zero.</summary>
        internal long NextAvailable(int bytes, long now)
        {
            Refill(now);
            var deficit = (bytes * Scale) - tokens;
            if (deficit <= 0) return now;
            if (ratePerSecond == 0) return long.MaxValue;
            return now + ((deficit + ratePerSecond - 1) / ratePerSecond);
        }

        private void Refill(long now)
        {
            if (now <= lastRefill) return;
            var elapsed = Math.Min(now - lastRefill, 60_000_000L);
            lastRefill = now;
            tokens = Math.Min(depth, tokens + (elapsed * ratePerSecond));
        }
    }
}
