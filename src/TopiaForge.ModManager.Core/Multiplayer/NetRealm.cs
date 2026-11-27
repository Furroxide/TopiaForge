using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>The network role of this process.</summary>
    internal enum NetRealm
    {
        /// <summary>Single player: no session links, and this process is its own authority.</summary>
        Offline = 0,

        /// <summary>A player-hosted listen server: client and server in one process.</summary>
        ListenHost = 1,

        /// <summary>Joined to another player's listen server, following its world.</summary>
        Client = 2
    }

    /// <summary>The answer to a requested realm change.</summary>
    internal enum NetRealmTransitionVerdict
    {
        /// <summary>The process is already in the requested realm.</summary>
        Unchanged = 0,

        /// <summary>The change may happen now; <see cref="NetRealmTransition.Effect"/> says what it does.</summary>
        Allowed = 1,

        /// <summary>
        /// The running local session must end first ("Leave current game?"). Ask, stop it, then ask again from Idle.
        /// </summary>
        RequiresLeave = 2,

        /// <summary>A session phase change is in flight. Ask again once it settles at Idle or Running.</summary>
        Busy = 3,

        /// <summary>An enabled standalone mod refuses every multiplayer session.</summary>
        StandaloneRefused = 4,

        /// <summary>The active world has no multiplayer loading contract yet.</summary>
        WorldRefused = 5
    }

    /// <summary>What an allowed realm change does to the local session.</summary>
    internal enum NetRealmTransitionEffect
    {
        None = 0,

        /// <summary>Start hosting with no running world; the launcher's "start as host" path.</summary>
        StartAsHost = 1,

        /// <summary>
        /// "Open to multiplayer": the running solo session becomes the host's session in place. The world takes its
        /// scene census now and baselines earlier spawns from the descriptors it recorded at spawn time.
        /// </summary>
        PromoteInPlace = 2,

        /// <summary>Join: the client session replaces the local one, which is idle.</summary>
        ReplaceLocalSession = 3,

        /// <summary>Leave or stop hosting: the online session ends. Leaving is never refused.</summary>
        EndOnlineSession = 4
    }

    /// <summary>A realm-change decision: a verdict and, when allowed, its effect.</summary>
    internal readonly struct NetRealmTransition : IEquatable<NetRealmTransition>
    {
        public NetRealmTransition(NetRealmTransitionVerdict verdict, NetRealmTransitionEffect effect)
        {
            Verdict = verdict;
            Effect = effect;
        }

        public NetRealmTransitionVerdict Verdict { get; }

        public NetRealmTransitionEffect Effect { get; }

        public bool IsAllowed => Verdict == NetRealmTransitionVerdict.Allowed;

        public bool Equals(NetRealmTransition other) => Verdict == other.Verdict && Effect == other.Effect;

        public override bool Equals(object? obj) => obj is NetRealmTransition other && Equals(other);

        public override int GetHashCode() => ((int)Verdict * 31) ^ (int)Effect;

        public override string ToString() => Verdict + "/" + Effect;
    }

    /// <summary>
    /// Realm rules. Offline to ListenHost may happen mid-session; Offline to Client only from Idle, because joining
    /// replaces the local session; leaving an online realm is always allowed.
    /// </summary>
    internal static class NetRealms
    {
        public static bool IsDefined(NetRealm realm) => realm >= NetRealm.Offline && realm <= NetRealm.Client;

        public static bool IsOnline(NetRealm realm)
        {
            Require(realm);
            return realm != NetRealm.Offline;
        }

        /// <summary>Decides whether the process may move from one realm to another.</summary>
        /// <param name="from">The current realm.</param>
        /// <param name="to">The requested realm.</param>
        /// <param name="phase">The current phase of the local session lifecycle.</param>
        /// <param name="standaloneModEnabled">Whether any enabled mod is standalone.</param>
        public static NetRealmTransition EvaluateTransition(
            NetRealm from,
            NetRealm to,
            SessionPhase phase,
            bool standaloneModEnabled)
        {
            Require(from);
            Require(to);
            if (phase < SessionPhase.Idle || phase > SessionPhase.Stopping) throw new ArgumentOutOfRangeException(nameof(phase));
            if (from == to) return Decide(NetRealmTransitionVerdict.Unchanged);

            if (to == NetRealm.Offline) return Allow(NetRealmTransitionEffect.EndOnlineSession);

            // Between two online realms the current session must end first: a host cannot hand its session over,
            // and a client must leave before it can host.
            if (from != NetRealm.Offline) return Decide(NetRealmTransitionVerdict.RequiresLeave);

            if (standaloneModEnabled) return Decide(NetRealmTransitionVerdict.StandaloneRefused);
            if (phase != SessionPhase.Idle && phase != SessionPhase.Running) return Decide(NetRealmTransitionVerdict.Busy);

            if (to == NetRealm.ListenHost)
            {
                return Allow(phase == SessionPhase.Idle
                    ? NetRealmTransitionEffect.StartAsHost
                    : NetRealmTransitionEffect.PromoteInPlace);
            }

            return phase == SessionPhase.Idle
                ? Allow(NetRealmTransitionEffect.ReplaceLocalSession)
                : Decide(NetRealmTransitionVerdict.RequiresLeave);
        }

        internal static void Require(NetRealm realm)
        {
            if (!IsDefined(realm)) throw new ArgumentOutOfRangeException(nameof(realm));
        }

        private static NetRealmTransition Allow(NetRealmTransitionEffect effect) =>
            new NetRealmTransition(NetRealmTransitionVerdict.Allowed, effect);

        private static NetRealmTransition Decide(NetRealmTransitionVerdict verdict) =>
            new NetRealmTransition(verdict, NetRealmTransitionEffect.None);
    }
}
