using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// Why a joined player's spawn ended before the host adopted it: the host's refusal, as its verdict carries it, or the
    /// player's own reason. Values 1 to 6 travel on the wire; a reason this build does not know reads as
    /// <see cref="Unbuildable"/>.
    /// </summary>
    internal enum SpawnRefusal : byte
    {
        None = 0,

        /// <summary>The player sent spawns faster than the host's limit allows.</summary>
        RateLimited = 1,

        /// <summary>The host's rules do not let players make that object.</summary>
        NotPermitted = 2,

        /// <summary>The player already owns the most live objects the host allows.</summary>
        QuotaExceeded = 3,

        /// <summary>The object comes from a mod the host does not share with players, or of a kind the host cannot make for them.</summary>
        NotShared = 4,

        /// <summary>The host tried and could not make the object: its asset would not load, or the spawn failed.</summary>
        Unbuildable = 5,

        /// <summary>The host has no room for another player's objects now.</summary>
        HostBusy = 6,

        /// <summary>The player's own reason: no verdict came within <see cref="SpawnAuthorityRules.PredictionTimeoutMicros"/>.</summary>
        TimedOut = 7
    }

    /// <summary>Where a joined player's prediction stands: a spawn on its player's intent, made here and asked of the host.</summary>
    internal enum PredictionState : byte
    {
        /// <summary>Made here and frozen; its request waits for the player's request budget or for the channel.</summary>
        Queued = 0,

        /// <summary>Its request went to the host; the verdict has not come.</summary>
        Sent = 1,

        /// <summary>The host adopted it: the instance here is the canonical entity's copy, under the same handle.</summary>
        Adopted = 2,

        /// <summary>Ended: the host refused it, or no verdict came in time. Its handle is dead.</summary>
        Refused = 3,

        /// <summary>Ended because its mod destroyed it, or because the player left. Its handle is dead.</summary>
        Ended = 4
    }

    /// <summary>What happens to a prediction.</summary>
    internal enum PredictionEvent : byte
    {
        /// <summary>Its request left for the host.</summary>
        Sent = 0,

        /// <summary>The host's verdict adopted it.</summary>
        Adopted = 1,

        /// <summary>The host's verdict refused it.</summary>
        Refused = 2,

        /// <summary>No verdict came in time.</summary>
        TimedOut = 3,

        /// <summary>The stream with the host restarted, so a request in flight is lost and goes again.</summary>
        StreamRestarted = 4,

        /// <summary>Its mod destroyed it, or the player left the session, or the host ended the adopted entity.</summary>
        Ended = 5
    }

    /// <summary>Where a joined player's shadow stands: a scripted spawn made here that waits to stand for the host's.</summary>
    internal enum ShadowState : byte
    {
        /// <summary>Made here and frozen; the host's spawn with its key has not arrived.</summary>
        Waiting = 0,

        /// <summary>Bound in place to the host's spawn with its key; the instance here is that entity's copy.</summary>
        Bound = 1,

        /// <summary>Ended: the host's spawn never came, came for another asset, the host ended it, or the player left.</summary>
        Ended = 2
    }

    /// <summary>What happens to a shadow.</summary>
    internal enum ShadowEvent : byte
    {
        /// <summary>The host's spawn with its key arrived, describing the same asset or kind.</summary>
        Matched = 0,

        /// <summary>The host's spawn with its key arrived for another asset or kind, or another copy already stands for it.</summary>
        Mismatched = 1,

        /// <summary>The host's spawn did not come within <see cref="SpawnAuthorityRules.ShadowWaitMicros"/> of this player's synchronized world.</summary>
        Expired = 2,

        /// <summary>The host ended the entity, the mod destroyed its copy, or the player left.</summary>
        Ended = 3
    }

    /// <summary>
    /// The rules authority routing (WS3.7) applies to a joined player's spawns, transform changes and destroys: how a
    /// request relates to its target, which scripted spawns a joined player shadows, how fast an honest player sends, what
    /// a refusal says, and the prediction and shadow state machines. Unity-free; the runtime's authority host and client
    /// apply them. See docs/internal/MultiplayerRuntimeIntegration.md, section 16.
    /// </summary>
    internal static class SpawnAuthorityRules
    {
        /// <summary>
        /// How long a host keeps a departed player's objects, from the moment its stream ends: the 30 seconds the session
        /// engine keeps the player's place, so a reconnect in that window finds them, as it finds its participant id.
        /// </summary>
        internal const long DepartureGraceMicros = 30_000_000;

        /// <summary>How long a host spends making one requested object before it refuses it as unbuildable.</summary>
        internal const long HostBuildTimeoutMicros = 30_000_000;

        /// <summary>How long a joined player waits for a verdict after its request left before it ends the prediction.</summary>
        internal const long PredictionTimeoutMicros = 60_000_000;

        /// <summary>
        /// How long a shadow waits, counted while this player's world is synchronized, for the host's spawn with its key
        /// before it ends.
        /// </summary>
        internal const long ShadowWaitMicros = 10_000_000;

        /// <summary>The shortest time between two refusal toasts; refusals in between are counted, not shown.</summary>
        internal const long RefusalToastIntervalMicros = 3_500_000;

        /// <summary>The most predictions a joined player keeps waiting for verdicts: the host's quota of live objects.</summary>
        internal const int MaximumPendingSpawns = PermissionPolicy.DefaultMaximumLiveSpawnsPerPlayer;

        /// <summary>The most shadows a joined player keeps waiting for the host's spawns.</summary>
        internal const int MaximumWaitingShadows = 4096;

        /// <summary>The largest provisional id a request may carry; zero is never one.</summary>
        internal const uint MaximumProvisionalId = uint.MaxValue;

        /// <summary>The longest bundle path a request may name, as a description carries it.</summary>
        internal const int MaximumBundlePathLength = 512;

        /// <summary>
        /// How a request relates to its target: <see cref="NetTargetRelation.Server"/> for an entity no player owns (owner
        /// slot zero), <see cref="NetTargetRelation.Self"/> for the requester's own, and
        /// <see cref="NetTargetRelation.OtherPlayer"/> for another player's.
        /// </summary>
        internal static NetTargetRelation RelationOf(int ownerSlot, int requesterSlot)
        {
            if (ownerSlot < 0) throw new ArgumentOutOfRangeException(nameof(ownerSlot));
            if (requesterSlot < 1) throw new ArgumentOutOfRangeException(nameof(requesterSlot));
            if (ownerSlot == 0) return NetTargetRelation.Server;
            return ownerSlot == requesterSlot ? NetTargetRelation.Self : NetTargetRelation.OtherPlayer;
        }

        /// <summary>The refusal a limiter's answer gives; <see cref="SpawnRefusal.None"/> when it allows the request.</summary>
        internal static SpawnRefusal RefusalOf(NetPermission permission)
        {
            switch (permission)
            {
                case NetPermission.Allowed:
                    return SpawnRefusal.None;
                case NetPermission.RateLimited:
                    return SpawnRefusal.RateLimited;
                case NetPermission.QuotaExceeded:
                    return SpawnRefusal.QuotaExceeded;
                case NetPermission.NotPermitted:
                    return SpawnRefusal.NotPermitted;
                default:
                    throw new ArgumentOutOfRangeException(nameof(permission));
            }
        }

        /// <summary>Reads a refusal from the wire: a value the host may send, or <see cref="SpawnRefusal.Unbuildable"/> for any other.</summary>
        internal static SpawnRefusal FromWire(byte value) =>
            value >= (byte)SpawnRefusal.RateLimited && value <= (byte)SpawnRefusal.HostBusy ? (SpawnRefusal)value : SpawnRefusal.Unbuildable;

        /// <summary>
        /// Whether a canonical spawn is one a joined player's own run of the same code shadows rather than forwards: what a
        /// joined player's route for the same profile and intent is <see cref="ExecutionRoute.Shadow"/>. That is scripted
        /// code in a context that runs on every peer: process-wide code and the world owner's. A player's own actions, a
        /// participant's code and the gamemode server's never are, and neither is a client-local mod's.
        /// </summary>
        internal static bool IsShadowable(ModNetProfile profile, IntentSource intent) =>
            ExecutionRouting.Route(NetRealm.Client, profile, intent) == ExecutionRoute.Shadow;

        /// <summary>
        /// How fast an honest joined player sends requests of <paramref name="kind"/>: half the host's burst and its full
        /// sustained rate, so requests that arrive bunched never meet the host's limit.
        /// </summary>
        internal static NetRequestLimit ClientPace(NetRequestKind kind)
        {
            var host = PermissionPolicy.Default.LimitFor(kind);
            return new NetRequestLimit(Math.Max(1, host.Burst / 2), host.PerSecond);
        }

        /// <summary>
        /// Whether a bundle path a joined player names is a plain path inside a package: 1 to 512 characters, relative,
        /// with no empty, <c>.</c> or <c>..</c> segment, no drive or scheme, and no control character. No segment ends in a
        /// period or a space, or is shaped like a short name such as <c>WORLD~1.BUN</c>: the game's platform reads each as
        /// another name of a file the mod knows by its plain one, so only case and separators are left for
        /// <see cref="SameBundleFile"/> to tell apart. The asset service checks the path against the package's folder again
        /// before any file is touched.
        /// </summary>
        internal static bool IsSafeBundlePath(string? path)
        {
            if (string.IsNullOrEmpty(path) || path!.Length > MaximumBundlePathLength) return false;
            if (path[0] == '/' || path[0] == '\\' || path.IndexOf(':') >= 0) return false;
            var start = 0;
            for (var index = 0; index <= path.Length; index++)
            {
                if (index < path.Length)
                {
                    var character = path[index];
                    if (char.IsControl(character)) return false;
                    if (character != '/' && character != '\\') continue;
                }

                var length = index - start;
                if (length == 0) return false;
                if (length == 1 && path[start] == '.') return false;
                if (length == 2 && path[start] == '.' && path[start + 1] == '.') return false;

                // The platform drops a period or a space that ends a name, and finds a file by its short name as well.
                var last = path[index - 1];
                if (last == '.' || char.IsWhiteSpace(last) || IsShortName(path, start, length)) return false;
                start = index + 1;
            }

            return true;
        }

        /// <summary>
        /// Whether two bundle paths may name one file of a package on the game's platform, whose file names ignore case and
        /// take either separator. A load the network world made gives way to a mod's own load of the same file however a
        /// player spelled it, since the game refuses a second load of a bundle that is loaded already.
        /// </summary>
        internal static bool SameBundleFile(string? left, string? right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++)
            {
                var one = left[index] == '\\' ? '/' : left[index];
                var other = right[index] == '\\' ? '/' : right[index];
                if (one != other && char.ToUpperInvariant(one) != char.ToUpperInvariant(other)) return false;
            }

            return true;
        }

        /// <summary>
        /// The key that every spelling <see cref="SameBundleFile"/> calls one file shares: each separator as <c>/</c>, each
        /// letter in upper case. The network world keys its loads by it, so one file is one load there, however the players
        /// who named it spelled it.
        /// </summary>
        internal static string BundleFileKey(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var folded = new char[path.Length];
            for (var index = 0; index < path.Length; index++) folded[index] = path[index] == '\\' ? '/' : char.ToUpperInvariant(path[index]);
            return new string(folded);
        }

        /// <summary>
        /// Whether the segment of <paramref name="path"/> at <paramref name="start"/> is shaped like an 8.3 short name: a
        /// <c>~</c> and a digit in at most eight characters before its last period, and at most three after it.
        /// </summary>
        private static bool IsShortName(string path, int start, int length)
        {
            var dot = path.LastIndexOf('.', start + length - 1, length);
            var stem = dot < 0 ? length : dot - start;
            if (stem > 8 || length - stem - 1 > 3) return false;
            for (var index = start; index < start + stem - 1; index++)
            {
                if (path[index] == '~' && path[index + 1] >= '0' && path[index + 1] <= '9') return true;
            }

            return false;
        }

        /// <summary>
        /// What the player reads when a spawn of theirs ends unadopted: what happened, and what to do next. The toast names
        /// no mod, player or object, so it holds nothing another player wrote.
        /// </summary>
        internal static string RefusalText(SpawnRefusal refusal)
        {
            switch (refusal)
            {
                case SpawnRefusal.RateLimited:
                    return "The host removed an object you made: you made too many at once. Wait a moment, then try again.";
                case SpawnRefusal.QuotaExceeded:
                    return "The host removed an object you made: you already have as many as it allows. Remove some, then try again.";
                case SpawnRefusal.NotPermitted:
                    return "The host does not let players make that object, so it was removed.";
                case SpawnRefusal.NotShared:
                    return "That object comes from a mod the host does not share with players, so it was removed.";
                case SpawnRefusal.HostBusy:
                    return "The host had no room for another object, so the one you made was removed. Try again shortly.";
                case SpawnRefusal.TimedOut:
                    return "The host did not answer in time, so an object you made was removed. Try again.";
                default:
                    return "The host could not make an object you made, so it was removed.";
            }
        }

        /// <summary>
        /// The prediction state machine: what <paramref name="happened"/> does to a prediction in <paramref name="state"/>.
        /// Ended and refused predictions stay as they are; a verdict for a prediction that is not waiting for one changes
        /// nothing, and a restarted stream sends a waiting request again.
        /// </summary>
        internal static PredictionState Next(PredictionState state, PredictionEvent happened)
        {
            if (state == PredictionState.Refused || state == PredictionState.Ended) return state;
            switch (happened)
            {
                case PredictionEvent.Sent:
                    return state == PredictionState.Queued ? PredictionState.Sent : state;
                case PredictionEvent.Adopted:
                    return state == PredictionState.Adopted ? state : PredictionState.Adopted;
                case PredictionEvent.Refused:
                case PredictionEvent.TimedOut:
                    return state == PredictionState.Adopted ? state : PredictionState.Refused;
                case PredictionEvent.StreamRestarted:
                    return state == PredictionState.Sent ? PredictionState.Queued : state;
                case PredictionEvent.Ended:
                    return PredictionState.Ended;
                default:
                    throw new ArgumentOutOfRangeException(nameof(happened));
            }
        }

        /// <summary>
        /// The shadow state machine: a waiting shadow binds on a match and ends on anything else; a bound one ends only when
        /// the entity does; an ended one stays ended.
        /// </summary>
        internal static ShadowState Next(ShadowState state, ShadowEvent happened)
        {
            if (state == ShadowState.Ended) return state;
            switch (happened)
            {
                case ShadowEvent.Matched:
                    return state == ShadowState.Waiting ? ShadowState.Bound : state;
                case ShadowEvent.Mismatched:
                case ShadowEvent.Expired:
                    return state == ShadowState.Waiting ? ShadowState.Ended : state;
                case ShadowEvent.Ended:
                    return ShadowState.Ended;
                default:
                    throw new ArgumentOutOfRangeException(nameof(happened));
            }
        }
    }

    /// <summary>
    /// Numbers each mod's shadowable spawns, 1, 2, 3 and so on, in the order the mod makes them. A listen host stamps its
    /// scripted spawns with the number, and a joined player's own run of the same scripts numbers its shadows alike, so a
    /// shadow binds to the host's spawn with the same <c>(mod, number)</c>. The numbering starts over when this process's
    /// world session starts and when it joins a session, since a joined player follows the host's world from then.
    /// Mod ids compare as the runtime's do, ignoring case. It allocates only the first time a mod is numbered after a
    /// start. Main thread only.
    /// </summary>
    internal sealed class ShadowKeyNumbering
    {
        private readonly Dictionary<string, long> counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets how many times the numbering started over.</summary>
        internal int Starts { get; private set; }

        /// <summary>Gives <paramref name="modId"/>'s next number, from 1.</summary>
        internal long Next(string modId)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            counts.TryGetValue(modId, out var count);
            count++;
            counts[modId] = count;
            return count;
        }

        /// <summary>Gets how many numbers <paramref name="modId"/> has been given since the last start.</summary>
        internal long Count(string modId)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            return counts.TryGetValue(modId, out var count) ? count : 0;
        }

        /// <summary>Starts every mod's numbering over.</summary>
        internal void Restart()
        {
            counts.Clear();
            Starts++;
        }
    }
}
