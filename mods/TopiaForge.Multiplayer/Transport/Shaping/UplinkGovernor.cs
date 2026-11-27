namespace TopiaForge.Multiplayer.Transport.Shaping
{
    /// <summary>
    /// The host-wide outbound budget every connection shares, 4,000 kbit/s by default, and the shedding it drives.
    /// While connections keep finding the budget spent, the level rises a step each second: voice halves, then stops,
    /// then the engine is asked to lower its delta rate. Three quiet seconds lower it a step.
    /// </summary>
    internal sealed class UplinkGovernor
    {
        internal const int MaximumLevel = 3;
        private const long StepUpMicros = 1_000_000;
        private const long StepDownMicros = 3_000_000;

        private readonly TokenBucket bucket;
        private long lastPressureAt = long.MinValue;
        private long levelChangedAt;

        internal UplinkGovernor(long bytesPerSecond, long now)
        {
            // A tenth of a second of the full rate, and never less than two full datagrams.
            bucket = new TokenBucket(bytesPerSecond, System.Math.Max(bytesPerSecond / 10, 2 * TransportOptions.MaximumDatagramBytes), now);
            levelChangedAt = now;
        }

        /// <summary>Gets the shedding level: 0 normal, 1 voice halved, 2 voice stopped, 3 delta rate lowered too.</summary>
        internal int SheddingLevel { get; private set; }

        /// <summary>Gets whether the shared budget holds <paramref name="bytes"/>; a refusal counts as pressure.</summary>
        internal bool CanSend(int bytes, long now)
        {
            if (bucket.CanSpend(bytes, now)) return true;
            lastPressureAt = now;
            return false;
        }

        internal void OnSent(int bytes) => bucket.Spend(bytes);

        internal long NextAvailable(int bytes, long now) => bucket.NextAvailable(bytes, now);

        /// <summary>Moves the shedding level one step when pressure has lasted, or stayed away, long enough.</summary>
        internal void Poll(long now)
        {
            var pressured = lastPressureAt != long.MinValue && now - lastPressureAt < StepUpMicros;
            if (pressured && SheddingLevel < MaximumLevel && now - levelChangedAt >= StepUpMicros)
            {
                SheddingLevel++;
                levelChangedAt = now;
            }
            else if (!pressured && SheddingLevel > 0 && now - levelChangedAt >= StepDownMicros &&
                     (lastPressureAt == long.MinValue || now - lastPressureAt >= StepDownMicros))
            {
                SheddingLevel--;
                levelChangedAt = now;
            }
        }
    }
}
