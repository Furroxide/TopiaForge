using System;
using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// The LAN browser's budget for info queries, the launcher's rule: at most 32 a second overall, and at most 8 to one
    /// sender host in any two seconds, the requery interval. A beacon names its own sender, so the budget bounds how much a
    /// flood of beacons can make the browser send anywhere, and how many of the endpoint's 64 outstanding queries one host
    /// can take. Both windows slide: a query stops counting once its window has passed.
    /// </summary>
    internal sealed class LanQueryBudget
    {
        internal const int MaximumPerSecond = 32;

        /// <summary>Queries to one host per window, enough for a machine running several peers.</summary>
        internal const int MaximumPerHost = 8;

        internal const long HostWindowMicros = LanBrowser.RequeryMicros;
        private const long SecondMicros = 1_000_000;

        /// <summary>
        /// Hosts the budget remembers before it forgets those whose window has passed. At 32 queries a second, no more than
        /// 64 hosts can be inside their window at once, so forgetting the rest always makes room.
        /// </summary>
        private const int MaximumHosts = 128;

        private readonly Queue<long> recent = new Queue<long>();
        private readonly Dictionary<string, Queue<long>> byHost = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);

        /// <summary>Counts a query to <paramref name="host"/> when it fits, and returns whether it did.</summary>
        internal bool TryAdmit(string host, long now)
        {
            Slide(recent, now, SecondMicros);
            if (recent.Count >= MaximumPerSecond) return false;
            if (!byHost.TryGetValue(host, out var times))
            {
                if (byHost.Count >= MaximumHosts) ForgetIdle(now);
                times = new Queue<long>();
                byHost.Add(host, times);
            }

            Slide(times, now, HostWindowMicros);
            if (times.Count >= MaximumPerHost) return false;
            times.Enqueue(now);
            recent.Enqueue(now);
            return true;
        }

        /// <summary>Gets when a query to <paramref name="host"/> next fits: <paramref name="now"/> when it already does.</summary>
        internal long NextRoom(string host, long now)
        {
            var at = now;
            Slide(recent, now, SecondMicros);
            if (recent.Count >= MaximumPerSecond) at = Math.Max(at, recent.Peek() + SecondMicros);
            if (byHost.TryGetValue(host, out var times))
            {
                Slide(times, now, HostWindowMicros);
                if (times.Count >= MaximumPerHost) at = Math.Max(at, times.Peek() + HostWindowMicros);
            }

            return at;
        }

        private void ForgetIdle(long now)
        {
            var idle = new List<string>();
            foreach (var host in byHost)
            {
                Slide(host.Value, now, HostWindowMicros);
                if (host.Value.Count == 0) idle.Add(host.Key);
            }

            foreach (var host in idle) byHost.Remove(host);
        }

        /// <summary>Drops the times whose window has passed; times only grow, so the oldest leave first.</summary>
        private static void Slide(Queue<long> times, long now, long window)
        {
            while (times.Count > 0 && now - times.Peek() >= window) times.Dequeue();
        }
    }
}
