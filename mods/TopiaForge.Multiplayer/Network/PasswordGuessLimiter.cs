using System;
using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// How fast a password-protected host lets anyone guess. Wrong proofs count three ways at once:
    /// <list type="bullet">
    /// <item>Per identity: <see cref="PerIdentity"/> in a window, then a wait of <see cref="FirstLockoutMicros"/>.</item>
    /// <item>Per source network, an IPv4 address or an IPv6 /64, whatever ports and identities it uses:
    /// <see cref="PerNetwork"/> in a window, then a lockout.</item>
    /// <item>For the whole host: <see cref="PerHost"/> in a window, then every password from a newcomer is turned away
    /// for a lockout, so many networks together are held as tightly as three.</item>
    /// </list>
    /// A network's or the host's lockout starts at <see cref="FirstLockoutMicros"/> and doubles each time the budget is
    /// spent again, up to <see cref="LongestLockoutMicros"/>; each doubling wears off after <see cref="DecayMicros"/>
    /// without a lockout. Waiting therefore never pays: a guesser who retries the moment a lockout ends and one who
    /// waits both end up with one budget per longest lockout. An identity's lockout never grows, because identities
    /// cost nothing; only the network and host limits hold a guesser back. The defaults are what production uses.
    /// </summary>
    internal sealed class PasswordGuessLimits
    {
        internal const int DefaultPerIdentity = 5;
        internal const int DefaultPerNetwork = 5;
        internal const int DefaultPerHost = 15;
        internal const long DefaultWindowMicros = 60_000_000;
        internal const long DefaultFirstLockoutMicros = 60_000_000;
        internal const long DefaultLongestLockoutMicros = 480_000_000;
        internal const long DefaultDecayMicros = 480_000_000;
        internal const int DefaultTrackedEntries = 1024;

        internal static readonly PasswordGuessLimits Default = new PasswordGuessLimits();

        internal PasswordGuessLimits(
            int perIdentity = DefaultPerIdentity,
            int perNetwork = DefaultPerNetwork,
            int perHost = DefaultPerHost,
            long windowMicros = DefaultWindowMicros,
            long firstLockoutMicros = DefaultFirstLockoutMicros,
            long longestLockoutMicros = DefaultLongestLockoutMicros,
            long decayMicros = DefaultDecayMicros,
            int trackedEntries = DefaultTrackedEntries)
        {
            if (perIdentity < 1 || perNetwork < 1 || perHost < 1) throw new ArgumentOutOfRangeException(nameof(perHost));
            if (windowMicros < 1 || firstLockoutMicros < 1 || longestLockoutMicros < firstLockoutMicros) throw new ArgumentOutOfRangeException(nameof(longestLockoutMicros));
            if (decayMicros < 1) throw new ArgumentOutOfRangeException(nameof(decayMicros));
            if (trackedEntries < 1) throw new ArgumentOutOfRangeException(nameof(trackedEntries));
            PerIdentity = perIdentity;
            PerNetwork = perNetwork;
            PerHost = perHost;
            WindowMicros = windowMicros;
            FirstLockoutMicros = firstLockoutMicros;
            LongestLockoutMicros = longestLockoutMicros;
            DecayMicros = decayMicros;
            TrackedEntries = trackedEntries;
            var levels = 0;
            while (levels < 30 && (firstLockoutMicros << levels) < longestLockoutMicros) levels++;
            DoublingLevels = levels;
        }

        internal int PerIdentity { get; }

        internal int PerNetwork { get; }

        internal int PerHost { get; }

        internal long WindowMicros { get; }

        internal long FirstLockoutMicros { get; }

        internal long LongestLockoutMicros { get; }

        /// <summary>Gets how long a network or the host must go without a lockout for one doubling to wear off.</summary>
        internal long DecayMicros { get; }

        /// <summary>Gets how many identities, and separately how many networks, the host remembers wrong proofs for.</summary>
        internal int TrackedEntries { get; }

        /// <summary>Gets how many doublings take the first lockout to the longest.</summary>
        internal int DoublingLevels { get; }
    }

    /// <summary>Whose budget stops a proof from being checked.</summary>
    internal enum GuessRefusal
    {
        None = 0,
        Identity = 1,
        Network = 2,

        /// <summary>The host's own budget is spent, or it has no room left to remember another guesser.</summary>
        Host = 3
    }

    /// <summary>
    /// Counts wrong password proofs per identity, per source network and for the whole host (<see cref="PasswordGuessLimits"/>).
    /// A proof is checked only when no budget it falls under is locked, so a guesser learns nothing while one is. The
    /// tables are bounded: an entry that no longer limits anything is evicted when room is needed, and when none can be,
    /// the proof is turned away unchecked rather than forgetting a limit still in force. Only verified wrong proofs make
    /// entries, and the host's own budget bounds how many of those there can be, so the tables stay far below their
    /// bound in practice. The limiter sees a network only as an opaque <see cref="SourceNetwork"/>, never an address.
    /// </summary>
    internal sealed class PasswordGuessLimiter
    {
        private readonly PasswordGuessLimits limits;
        private readonly Dictionary<string, Budget> identities = new Dictionary<string, Budget>(StringComparer.Ordinal);
        private readonly Dictionary<SourceNetwork, Budget> networks = new Dictionary<SourceNetwork, Budget>();
        private readonly Budget host = new Budget();

        internal PasswordGuessLimiter(PasswordGuessLimits? limits = null)
        {
            this.limits = limits ?? PasswordGuessLimits.Default;
        }

        internal PasswordGuessLimits Limits => limits;

        internal int TrackedIdentities => identities.Count;

        internal int TrackedNetworks => networks.Count;

        /// <summary>Gets whether every newcomer's password is being turned away for the host's own budget.</summary>
        internal bool HostLocked(long now) => host.IsLocked(now);

        /// <summary>Decides whether a proof from <paramref name="identity"/> on <paramref name="network"/> may be checked now.</summary>
        internal GuessRefusal Check(string identity, SourceNetwork? network, long now)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (host.IsLocked(now)) return GuessRefusal.Host;
            if (network is SourceNetwork source && networks.TryGetValue(source, out var byNetwork) && byNetwork.IsLocked(now)) return GuessRefusal.Network;
            if (identities.TryGetValue(identity, out var byIdentity) && byIdentity.IsLocked(now)) return GuessRefusal.Identity;

            // A wrong proof needs an entry in every table it is new to; without room it is not checked at all.
            if (!HasRoom(identities, identity, now)) return GuessRefusal.Host;
            if (network is SourceNetwork room && !HasRoom(networks, room, now)) return GuessRefusal.Host;
            return GuessRefusal.None;
        }

        /// <summary>Counts one wrong proof, which <see cref="Check"/> allowed, against its identity, its network and the host.</summary>
        internal void RecordWrong(string identity, SourceNetwork? network, long now)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            Entry(identities, identity).Spend(now, limits.PerIdentity, limits, 0);
            if (network is SourceNetwork source) Entry(networks, source).Spend(now, limits.PerNetwork, limits, limits.DoublingLevels);
            host.Spend(now, limits.PerHost, limits, limits.DoublingLevels);
        }

        private bool HasRoom<TKey>(Dictionary<TKey, Budget> table, TKey key, long now) where TKey : notnull
        {
            if (table.Count < limits.TrackedEntries || table.ContainsKey(key)) return true;
            List<TKey>? spent = null;
            foreach (var entry in table)
            {
                if (entry.Value.IsSpent(now, limits)) (spent ??= new List<TKey>()).Add(entry.Key);
            }

            if (spent != null)
            {
                foreach (var item in spent) table.Remove(item);
            }

            return table.Count < limits.TrackedEntries;
        }

        private static Budget Entry<TKey>(Dictionary<TKey, Budget> table, TKey key) where TKey : notnull
        {
            if (!table.TryGetValue(key, out var budget))
            {
                budget = new Budget();
                table.Add(key, budget);
            }

            return budget;
        }

        /// <summary>
        /// One budget: the wrong proofs in the current window, the lockout it is serving, and how many doublings it has
        /// earned. Doublings wear off one per decay period counted from the end of the last lockout.
        /// </summary>
        private sealed class Budget
        {
            private long windowStart;
            private int wrong;
            private bool locked;
            private long lockedUntil;
            private int level;

            internal bool IsLocked(long now) => locked && now < lockedUntil;

            /// <summary>Gets whether the entry limits nothing any more, so a fresh one would behave the same.</summary>
            internal bool IsSpent(long now, PasswordGuessLimits limits) =>
                !IsLocked(now) && (wrong == 0 || now - windowStart >= limits.WindowMicros) && LevelAt(now, limits.DecayMicros) == 0;

            internal void Spend(long now, int limit, PasswordGuessLimits limits, int doublingLevels)
            {
                if (wrong > 0 && now - windowStart >= limits.WindowMicros) wrong = 0;
                if (wrong == 0) windowStart = now;
                if (++wrong < limit) return;

                // The budget is spent: lock out for the current level's lockout, and earn the next doubling.
                var current = LevelAt(now, limits.DecayMicros);
                var lockout = Math.Min(limits.FirstLockoutMicros << current, limits.LongestLockoutMicros);
                locked = true;
                lockedUntil = now + lockout;
                level = Math.Min(current + 1, doublingLevels);
                wrong = 0;
            }

            private int LevelAt(long now, long decay)
            {
                if (level == 0 || !locked || now <= lockedUntil) return level;
                var worn = (now - lockedUntil) / decay;
                return worn >= level ? 0 : level - (int)worn;
            }
        }
    }
}
