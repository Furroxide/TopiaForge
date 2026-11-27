using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// A token bucket in integer micro-tokens, so every platform makes the same decision at the same microsecond.
    /// It starts full, refills at <see cref="TokensPerSecond"/> up to <see cref="Capacity"/>, and ignores time that
    /// runs backwards.
    /// </summary>
    internal sealed class TokenBucket
    {
        public const long MicroTokensPerToken = 1_000_000;
        public const int MaximumCapacity = 1_000_000;
        public const int MaximumTokensPerSecond = 1_000_000;

        private readonly long capacityMicroTokens;
        private long level;
        private long lastMicros;

        public TokenBucket(int capacity, int tokensPerSecond, long nowMicros)
        {
            if (capacity < 1 || capacity > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (tokensPerSecond < 0 || tokensPerSecond > MaximumTokensPerSecond)
            {
                throw new ArgumentOutOfRangeException(nameof(tokensPerSecond));
            }

            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
            Capacity = capacity;
            TokensPerSecond = tokensPerSecond;
            capacityMicroTokens = capacity * MicroTokensPerToken;
            level = capacityMicroTokens;
            lastMicros = nowMicros;
        }

        public int Capacity { get; }

        /// <summary>The refill rate; zero makes a fixed allowance that never refills.</summary>
        public int TokensPerSecond { get; }

        /// <summary>The level after refilling to <paramref name="nowMicros"/>.</summary>
        public long AvailableMicroTokens(long nowMicros)
        {
            Refill(nowMicros);
            return level;
        }

        /// <summary>Takes <paramref name="cost"/> whole tokens if they are all there; otherwise takes nothing.</summary>
        public bool TryTake(long nowMicros, int cost = 1)
        {
            RequireCost(cost);
            Refill(nowMicros);
            var needed = cost * MicroTokensPerToken;
            if (level < needed) return false;
            level -= needed;
            return true;
        }

        /// <summary>
        /// How long until <paramref name="cost"/> tokens will be available: zero if they are now, or
        /// <see cref="long.MaxValue"/> if the bucket never refills.
        /// </summary>
        public long MicrosUntilAvailable(long nowMicros, int cost = 1)
        {
            RequireCost(cost);
            Refill(nowMicros);
            var missing = cost * MicroTokensPerToken - level;
            if (missing <= 0) return 0;
            if (TokensPerSecond == 0) return long.MaxValue;
            return (missing + TokensPerSecond - 1) / TokensPerSecond;
        }

        // One token per second is one micro-token per microsecond, so the refill is elapsed * rate.
        private void Refill(long nowMicros)
        {
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
            if (nowMicros <= lastMicros) return;
            var elapsed = nowMicros - lastMicros;
            lastMicros = nowMicros;
            var missing = capacityMicroTokens - level;
            if (missing <= 0 || TokensPerSecond == 0) return;
            var useful = (missing + TokensPerSecond - 1) / TokensPerSecond;
            level = Math.Min(capacityMicroTokens, level + Math.Min(elapsed, useful) * TokensPerSecond);
        }

        private void RequireCost(int cost)
        {
            if (cost < 1 || cost > Capacity) throw new ArgumentOutOfRangeException(nameof(cost));
        }
    }
}
