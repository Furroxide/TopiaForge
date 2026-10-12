using System.Collections.Generic;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Transport.Handshake
{
    /// <summary>
    /// Handshake budgets: a sliding second per source and one for the whole endpoint, checked before any
    /// Diffie-Hellman work. It also meters every Initiation, so the endpoint knows when it is under load and should
    /// demand cookies. The per-source table is bounded: once full of active sources, new ones are refused.
    /// </summary>
    internal sealed class HandshakeLimiter
    {
        internal const int MaximumSources = 4096;
        private const long WindowMicros = 1_000_000;

        private readonly Dictionary<TransportPath, Source> sources = new Dictionary<TransportPath, Source>();
        private readonly SlidingWindowRateLimiter global;
        private readonly SlidingWindowRateLimiter load;
        private readonly int perSource;

        internal HandshakeLimiter(int perSource, int perEndpoint)
        {
            this.perSource = perSource;
            global = new SlidingWindowRateLimiter(perEndpoint, WindowMicros);
            load = new SlidingWindowRateLimiter(System.Math.Max(1, perEndpoint / 2), WindowMicros);
        }

        /// <summary>Counts one Initiation and returns whether recent ones exceed half the endpoint's budget.</summary>
        internal bool MeterInitiation(long now) => !load.TryAcquire(now);

        /// <summary>Spends one handshake from both budgets; false means the Initiation must be dropped.</summary>
        internal bool TryAdmit(TransportPath path, long now)
        {
            if (!sources.TryGetValue(path, out var source))
            {
                if (sources.Count >= MaximumSources && !Evict(now)) return false;
                source = new Source(new SlidingWindowRateLimiter(perSource, WindowMicros));
                sources.Add(path, source);
            }

            source.LastSeen = now;
            return source.Limiter.TryAcquire(now) && global.TryAcquire(now);
        }

        private bool Evict(long now)
        {
            var stale = new List<TransportPath>();
            foreach (var entry in sources)
            {
                if (now - entry.Value.LastSeen >= WindowMicros) stale.Add(entry.Key);
            }

            foreach (var path in stale) sources.Remove(path);
            return sources.Count < MaximumSources;
        }

        private sealed class Source
        {
            internal Source(SlidingWindowRateLimiter limiter)
            {
                Limiter = limiter;
            }

            internal SlidingWindowRateLimiter Limiter { get; }

            internal long LastSeen { get; set; }
        }
    }
}
