using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>A request a remote player's forwarded intent asks the host to carry out.</summary>
    internal enum NetRequestKind
    {
        Spawn = 0,
        SetTransform = 1,
        Destroy = 2,

        /// <summary>A motion-ownership grab, such as the Gravity Gun's.</summary>
        MotionGrab = 3,

        /// <summary>A forwarded UI callback, interaction or owner command.</summary>
        Interaction = 4
    }

    /// <summary>How the requester relates to the request's target entity.</summary>
    internal enum NetTargetRelation
    {
        /// <summary>The request has no target: only a spawn.</summary>
        None = 0,

        /// <summary>The requester's own spawn.</summary>
        Self = 1,

        /// <summary>A scene, world or gamemode entity the server owns.</summary>
        Server = 2,

        /// <summary>Another participant's spawn.</summary>
        OtherPlayer = 3
    }

    internal enum NetPermission
    {
        Allowed = 0,

        /// <summary>The host's policy does not allow this request on this target.</summary>
        NotPermitted = 1,

        /// <summary>The player sent too many requests of this kind; nothing ran.</summary>
        RateLimited = 2,

        /// <summary>The player already has the most live spawns the host allows.</summary>
        QuotaExceeded = 3
    }

    /// <summary>A token-bucket limit: a burst, then a sustained rate per second.</summary>
    internal readonly struct NetRequestLimit
    {
        public NetRequestLimit(int burst, int perSecond)
        {
            if (burst < 1 || burst > TokenBucket.MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(burst));
            if (perSecond < 0 || perSecond > TokenBucket.MaximumTokensPerSecond)
            {
                throw new ArgumentOutOfRangeException(nameof(perSecond));
            }

            Burst = burst;
            PerSecond = perSecond;
        }

        public int Burst { get; }

        public int PerSecond { get; }
    }

    /// <summary>
    /// The host's rules for remote players' requests: which kinds of request may touch which targets, how many each
    /// player may send, and how many live spawns each player may own. Immutable; the <c>With</c> methods return copies.
    /// </summary>
    internal sealed class PermissionPolicy
    {
        public const int KindCount = 5;
        public const int RelationCount = 4;
        public const int DefaultMaximumLiveSpawnsPerPlayer = 64;

        private readonly bool[,] rules;
        private readonly NetRequestLimit[] limits;

        private PermissionPolicy(bool[,] rules, NetRequestLimit[] limits, int maximumLiveSpawnsPerPlayer)
        {
            this.rules = rules;
            this.limits = limits;
            MaximumLiveSpawnsPerPlayer = maximumLiveSpawnsPerPlayer;
        }

        /// <summary>
        /// Players may spawn, move and remove their own things, grab any body the ownership protocol lets them, and
        /// interact with anything. They may not move or remove the world's entities or other players' spawns.
        /// </summary>
        public static PermissionPolicy Default { get; } = new PermissionPolicy(
            new bool[KindCount, RelationCount]
            {
                // None, Self, Server, OtherPlayer
                { true, false, false, false },  // Spawn
                { false, true, false, false },  // SetTransform
                { false, true, false, false },  // Destroy
                { false, true, true, true },    // MotionGrab
                { false, true, true, true }     // Interaction
            },
            new[]
            {
                new NetRequestLimit(8, 4),    // Spawn
                new NetRequestLimit(60, 30),  // SetTransform
                new NetRequestLimit(8, 4),    // Destroy
                new NetRequestLimit(10, 5),   // MotionGrab
                new NetRequestLimit(40, 20)   // Interaction
            },
            DefaultMaximumLiveSpawnsPerPlayer);

        public int MaximumLiveSpawnsPerPlayer { get; }

        public static bool IsDefined(NetRequestKind kind) => kind >= NetRequestKind.Spawn && kind <= NetRequestKind.Interaction;

        public static bool IsDefined(NetTargetRelation relation) =>
            relation >= NetTargetRelation.None && relation <= NetTargetRelation.OtherPlayer;

        /// <summary>Whether the rules allow a kind of request on a target relation.</summary>
        public bool Permits(NetRequestKind kind, NetTargetRelation relation)
        {
            RequirePair(kind, relation);
            return rules[(int)kind, (int)relation];
        }

        public NetRequestLimit LimitFor(NetRequestKind kind)
        {
            if (!IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            return limits[(int)kind];
        }

        public PermissionPolicy WithRule(NetRequestKind kind, NetTargetRelation relation, bool allowed)
        {
            RequirePair(kind, relation);
            var copy = (bool[,])rules.Clone();
            copy[(int)kind, (int)relation] = allowed;
            return new PermissionPolicy(copy, limits, MaximumLiveSpawnsPerPlayer);
        }

        public PermissionPolicy WithLimit(NetRequestKind kind, NetRequestLimit limit)
        {
            if (!IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (limit.Burst == 0) throw new ArgumentException("A limit needs a burst of at least one.", nameof(limit));
            var copy = (NetRequestLimit[])limits.Clone();
            copy[(int)kind] = limit;
            return new PermissionPolicy(rules, copy, MaximumLiveSpawnsPerPlayer);
        }

        public PermissionPolicy WithMaximumLiveSpawns(int maximum)
        {
            if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
            return new PermissionPolicy(rules, limits, maximum);
        }

        /// <summary>A spawn has no target; every other request has one.</summary>
        internal static void RequirePair(NetRequestKind kind, NetTargetRelation relation)
        {
            if (!IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!IsDefined(relation)) throw new ArgumentOutOfRangeException(nameof(relation));
            if ((kind == NetRequestKind.Spawn) != (relation == NetTargetRelation.None))
            {
                throw new ArgumentException("Only a spawn has no target.", nameof(relation));
            }
        }
    }

    /// <summary>
    /// Applies a <see cref="PermissionPolicy"/> to each remote player. The host's own player is canonical and never
    /// limited. Every remote request spends a token first, so a player who floods forbidden requests is throttled too;
    /// then the target rule, then the spawn quota.
    /// </summary>
    internal sealed class PlayerRequestLimiter
    {
        private readonly Dictionary<int, TokenBucket[]> buckets = new Dictionary<int, TokenBucket[]>();

        public PlayerRequestLimiter(PermissionPolicy policy)
        {
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        public PermissionPolicy Policy { get; }

        public int TrackedParticipants => buckets.Count;

        /// <param name="participant">The requester's participant slot.</param>
        /// <param name="isHostPlayer">Whether the requester is the host's own player.</param>
        /// <param name="kind">The kind of request.</param>
        /// <param name="relation">How the requester relates to the target.</param>
        /// <param name="liveSpawnsOwned">How many live entities the requester owns now; read for spawns.</param>
        /// <param name="nowMicros">The host's session time.</param>
        public NetPermission Evaluate(
            int participant,
            bool isHostPlayer,
            NetRequestKind kind,
            NetTargetRelation relation,
            int liveSpawnsOwned,
            long nowMicros)
        {
            if (participant < 1) throw new ArgumentOutOfRangeException(nameof(participant));
            if (liveSpawnsOwned < 0) throw new ArgumentOutOfRangeException(nameof(liveSpawnsOwned));
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
            PermissionPolicy.RequirePair(kind, relation);
            if (isHostPlayer) return NetPermission.Allowed;
            if (!BucketsFor(participant, nowMicros)[(int)kind].TryTake(nowMicros)) return NetPermission.RateLimited;
            if (!Policy.Permits(kind, relation)) return NetPermission.NotPermitted;
            if (kind == NetRequestKind.Spawn && liveSpawnsOwned >= Policy.MaximumLiveSpawnsPerPlayer)
            {
                return NetPermission.QuotaExceeded;
            }

            return NetPermission.Allowed;
        }

        /// <summary>Drops a departed participant's buckets.</summary>
        public bool Forget(int participant) => buckets.Remove(participant);

        private TokenBucket[] BucketsFor(int participant, long nowMicros)
        {
            if (buckets.TryGetValue(participant, out var existing)) return existing;
            var created = new TokenBucket[PermissionPolicy.KindCount];
            for (var index = 0; index < created.Length; index++)
            {
                var limit = Policy.LimitFor((NetRequestKind)index);
                created[index] = new TokenBucket(limit.Burst, limit.PerSecond, nowMicros);
            }

            buckets.Add(participant, created);
            return created;
        }
    }
}
