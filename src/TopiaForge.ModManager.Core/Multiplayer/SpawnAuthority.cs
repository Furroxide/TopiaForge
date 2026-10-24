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
        /// another name of a file the mod knows by its plain one, and no reading of the name alone sees a short name, so a
        /// player's spellings of one file differ only by case and separators. No segment is a name Windows keeps for a device,
        /// such as <c>CON</c> or <c>com1.bundle</c> (<see cref="IsDeviceName"/>): opening it opens the device, not a file. The
        /// asset service checks the path against the package's folder again before any file is touched.
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

                // The platform drops a period or a space that ends a name, finds a file by its short name as well, and opens a
                // device, not a file, for a name it keeps for one.
                var last = path[index - 1];
                if (last == '.' || char.IsWhiteSpace(last) || IsShortName(path, start, length) || IsDeviceName(path, start, length)) return false;
                start = index + 1;
            }

            return true;
        }

        /// <summary>
        /// Whether two bundle paths may name one file of a package on the game's platform, read as the asset service
        /// resolves a path there: either separator, an empty or <c>.</c> segment naming nothing, <c>..</c> taking back the
        /// segment before it, whitespace or a period that ends a name dropped, and case ignored. A player's path is a plain
        /// one (<see cref="IsSafeBundlePath"/>), but the mod's own need not be: a mod that loads
        /// <c>./assets/world.bundle</c> holds the file a player names <c>assets/world.bundle</c>. A load the network world
        /// made gives way to a mod's own load of the same file however either spelled it, since the game refuses a second
        /// load of a bundle that is loaded already. It allocates nothing unless a path has a <c>..</c> segment.
        /// </summary>
        internal static bool SameBundleFile(string? left, string? right)
        {
            if (left == null || right == null || EndsInSeparator(left) != EndsInSeparator(right)) return false;
            if (HasParentSegment(left) || HasParentSegment(right)) return string.Equals(BundleFileKey(left), BundleFileKey(right), StringComparison.Ordinal);
            var leftIndex = 0;
            var rightIndex = 0;
            while (true)
            {
                var more = NextName(left, ref leftIndex, out var leftStart, out var leftLength);
                if (more != NextName(right, ref rightIndex, out var rightStart, out var rightLength)) return false;
                if (!more) return true;
                if (leftLength != rightLength) return false;
                for (var offset = 0; offset < leftLength; offset++)
                {
                    var one = left[leftStart + offset];
                    var other = right[rightStart + offset];
                    if (one != other && char.ToUpperInvariant(one) != char.ToUpperInvariant(other)) return false;
                }
            }
        }

        /// <summary>
        /// The key that every spelling <see cref="SameBundleFile"/> calls one file shares: the names the path keeps, each
        /// letter in upper case, joined by <c>/</c>. The network world keys its loads by it, so one file is one load there,
        /// however the players who named it, or the mod that loaded it, spelled it.
        /// </summary>
        internal static string BundleFileKey(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (HasParentSegment(path)) return KeyWithParents(path);
            var length = EndsInSeparator(path) ? 1 : 0;
            var names = 0;
            var index = 0;
            while (NextName(path, ref index, out _, out var nameLength))
            {
                length += nameLength + (names > 0 ? 1 : 0);
                names++;
            }

            var key = new char[length];
            var at = 0;
            index = 0;
            while (NextName(path, ref index, out var start, out var nameLength))
            {
                if (at > 0) key[at++] = '/';
                for (var offset = 0; offset < nameLength; offset++) key[at++] = char.ToUpperInvariant(path[start + offset]);
            }

            if (at < length) key[at] = '/';
            return new string(key);
        }

        /// <summary>The key of a path with a <c>..</c> segment, which takes back the name before it.</summary>
        private static string KeyWithParents(string path)
        {
            var names = new List<string>();
            var index = 0;
            while (NextSegment(path, ref index, out var start, out var end))
            {
                if (end == start || IsDot(path, start, end)) continue;
                var parent = IsParent(path, start, end);
                if (parent && names.Count > 0 && names[names.Count - 1] != "..")
                {
                    names.RemoveAt(names.Count - 1);
                    continue;
                }

                // A ".." with nothing left to take back leaves the package, so it stays in the key. A path read without the
                // package's folder name cannot tell when one comes back in by that name, as ../1.0.0/x does in a folder 1.0.0.
                var name = new char[parent ? 2 : NameLength(path, start, end)];
                for (var offset = 0; offset < name.Length; offset++) name[offset] = char.ToUpperInvariant(path[start + offset]);
                names.Add(new string(name));
            }

            var key = string.Join("/", names);
            return EndsInSeparator(path) ? key + "/" : key;
        }

        /// <summary>
        /// Moves <paramref name="index"/> past the next segment of <paramref name="path"/>, and gives where the segment
        /// starts and where it ends without the whitespace that ends it, which the platform drops; false past the last one.
        /// </summary>
        private static bool NextSegment(string path, ref int index, out int start, out int end)
        {
            start = index;
            end = index;
            if (index > path.Length) return false;
            while (index < path.Length && path[index] != '/' && path[index] != '\\') index++;
            end = index++;
            while (end > start && char.IsWhiteSpace(path[end - 1])) end--;
            return true;
        }

        /// <summary>Moves past the next segment that names a folder or a file, and gives the length of its name as the platform reads it.</summary>
        private static bool NextName(string path, ref int index, out int start, out int length)
        {
            while (NextSegment(path, ref index, out start, out var end))
            {
                if (end == start || IsDot(path, start, end)) continue;
                length = NameLength(path, start, end);
                return true;
            }

            length = 0;
            return false;
        }

        /// <summary>
        /// A name's length without the periods and whitespace that end it, which the platform drops; a name of periods
        /// alone keeps them.
        /// </summary>
        private static int NameLength(string path, int start, int end)
        {
            var trimmed = end;
            while (trimmed > start && (path[trimmed - 1] == '.' || char.IsWhiteSpace(path[trimmed - 1]))) trimmed--;
            return trimmed > start ? trimmed - start : end - start;
        }

        private static bool IsDot(string path, int start, int end) => end - start == 1 && path[start] == '.';

        private static bool IsParent(string path, int start, int end) => end - start == 2 && path[start] == '.' && path[start + 1] == '.';

        private static bool HasParentSegment(string path)
        {
            var index = 0;
            while (NextSegment(path, ref index, out var start, out var end))
            {
                if (IsParent(path, start, end)) return true;
            }

            return false;
        }

        /// <summary>Whether a path names a folder: one that ends in a separator never names the file without it.</summary>
        private static bool EndsInSeparator(string path) => path.Length > 0 && (path[path.Length - 1] == '/' || path[path.Length - 1] == '\\');

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
        /// Whether the segment of <paramref name="path"/> at <paramref name="start"/> is a name Windows keeps for a device:
        /// <c>CON</c>, <c>PRN</c>, <c>AUX</c> or <c>NUL</c>, or <c>COM</c> or <c>LPT</c> and one digit, 0 to 9 or a
        /// superscript one, two or three, which Windows reads as digits there. Case is ignored, and so is everything from the
        /// segment's first period on, with the whitespace before that period, so <c>con</c>, <c>Nul.bundle</c>,
        /// <c>LPT1.tar.gz</c> and <c>aux .bundle</c> all are. Windows reserves these names in every folder, so a mod's files
        /// do not use them, while <c>console.bundle</c> and <c>com10.bundle</c> are ordinary names.
        /// </summary>
        private static bool IsDeviceName(string path, int start, int length)
        {
            var end = path.IndexOf('.', start, length);
            if (end < 0) end = start + length;
            while (end > start && char.IsWhiteSpace(path[end - 1])) end--;
            var stem = end - start;
            if (stem != 3 && stem != 4) return false;
            var first = AsciiUpper(path[start]);
            var second = AsciiUpper(path[start + 1]);
            var third = AsciiUpper(path[start + 2]);
            if (stem == 3)
            {
                return (first == 'C' && second == 'O' && third == 'N') || (first == 'P' && second == 'R' && third == 'N') ||
                       (first == 'A' && second == 'U' && third == 'X') || (first == 'N' && second == 'U' && third == 'L');
            }

            var digit = path[start + 3];
            if ((digit < '0' || digit > '9') && digit != '\u00B9' && digit != '\u00B2' && digit != '\u00B3') return false;
            return (first == 'C' && second == 'O' && third == 'M') || (first == 'L' && second == 'P' && third == 'T');
        }

        /// <summary>An ASCII letter in upper case, and any other character as it is: Windows matches device names by their ASCII letters.</summary>
        private static char AsciiUpper(char character) => character >= 'a' && character <= 'z' ? (char)(character - ('a' - 'A')) : character;

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
