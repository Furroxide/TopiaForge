using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>A host-assigned network identity for one replicated entity. Zero is never assigned.</summary>
    internal readonly struct NetworkEntityId : IEquatable<NetworkEntityId>, IComparable<NetworkEntityId>
    {
        public NetworkEntityId(uint value)
        {
            if (value == 0) throw new ArgumentOutOfRangeException(nameof(value), "Network entity id 0 is reserved.");
            Value = value;
        }

        public static NetworkEntityId None => default;

        public uint Value { get; }

        /// <summary>Reads an id from peer input without throwing; zero is refused.</summary>
        public static bool TryCreate(uint value, out NetworkEntityId id)
        {
            id = value == 0 ? default : new NetworkEntityId(value);
            return value != 0;
        }

        public bool IsValid => Value != 0;

        public int CompareTo(NetworkEntityId other) => Value.CompareTo(other.Value);

        public bool Equals(NetworkEntityId other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is NetworkEntityId other && Equals(other);

        public override int GetHashCode() => (int)Value;

        public override string ToString() => "e" + Value;
    }

    /// <summary>
    /// Who owns an entity's lifetime: the server, or the participant whose scope spawned it and cleans it up on leave.
    /// Participants are the runtime's dense slots, numbered from one. Who simulates a body is a separate question,
    /// answered by <see cref="OwnershipStateMachine"/>.
    /// </summary>
    /// <remarks>
    /// A slot is reused once its participant leaves, so every per-slot structure must be cleared first: remove or
    /// reassign the leaver's entities (<see cref="NetworkEntityTable.CollectOwnedBy"/>), call
    /// <see cref="OwnershipStateMachine.Disconnect"/> on every body's machine, which also forgets the leaver's
    /// cooldown, and <see cref="PlayerRequestLimiter.Forget"/> on the limiter. Otherwise a newcomer given the slot
    /// inherits what the leaver held.
    /// </remarks>
    internal readonly struct NetOwner : IEquatable<NetOwner>
    {
        private NetOwner(int participant)
        {
            Participant = participant;
        }

        public static NetOwner Server => default;

        /// <summary>The participant slot, or zero for the server.</summary>
        public int Participant { get; }

        public bool IsServer => Participant == 0;

        public static NetOwner ForParticipant(int participant)
        {
            if (participant < 1) throw new ArgumentOutOfRangeException(nameof(participant));
            return new NetOwner(participant);
        }

        /// <summary>Reads an owner from peer input without throwing: slot zero is the server, negatives are refused.</summary>
        public static bool TryFromSlot(int slot, out NetOwner owner)
        {
            owner = slot > 0 ? new NetOwner(slot) : Server;
            return slot >= 0;
        }

        public bool Equals(NetOwner other) => Participant == other.Participant;

        public override bool Equals(object? obj) => obj is NetOwner other && Equals(other);

        public override int GetHashCode() => Participant;

        public override string ToString() => IsServer ? "server" : "p" + Participant;
    }

    /// <summary>
    /// The (mod, sequence) a spawn came from. A client's scripted spawn binds as a shadow to the host's entity with
    /// the same origin instead of creating a duplicate.
    /// </summary>
    internal readonly struct NetSpawnOrigin : IEquatable<NetSpawnOrigin>
    {
        public NetSpawnOrigin(string modId, long sequence)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
            ModId = modId;
            Sequence = sequence;
        }

        public string? ModId { get; }

        public long Sequence { get; }

        public bool IsSpecified => ModId != null;

        /// <summary>Reads an origin from peer input without throwing; an empty mod id or a sequence below one is refused.</summary>
        public static bool TryCreate(string? modId, long sequence, out NetSpawnOrigin origin)
        {
            var valid = !string.IsNullOrEmpty(modId) && sequence >= 1;
            origin = valid ? new NetSpawnOrigin(modId!, sequence) : default;
            return valid;
        }

        public bool Equals(NetSpawnOrigin other) =>
            string.Equals(ModId, other.ModId, StringComparison.Ordinal) && Sequence == other.Sequence;

        public override bool Equals(object? obj) => obj is NetSpawnOrigin other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(ModId == null ? 0 : StringComparer.Ordinal.GetHashCode(ModId), Sequence);

        public override string ToString() => IsSpecified ? ModId + "#" + Sequence : "unspecified";
    }

    /// <summary>One live entry of a <see cref="NetworkEntityTable"/>.</summary>
    internal readonly struct NetworkEntityEntry
    {
        public NetworkEntityEntry(NetworkEntityId id, string kind, NetOwner owner, NetSpawnOrigin origin)
        {
            Id = id;
            Kind = kind;
            Owner = owner;
            Origin = origin;
        }

        public NetworkEntityId Id { get; }

        /// <summary>The replica kind that tells peers how to build the entity, such as <c>creator.content</c>.</summary>
        public string Kind { get; }

        public NetOwner Owner { get; }

        public NetSpawnOrigin Origin { get; }

        internal NetworkEntityEntry WithOwner(NetOwner owner) => new NetworkEntityEntry(Id, Kind, owner, Origin);
    }

    internal enum NetworkEntityAddResult
    {
        Added = 0,
        AlreadyLive = 1,

        /// <summary>The id was removed earlier in this session and can never come back.</summary>
        Tombstoned = 2,

        /// <summary>Another live entity already has this spawn origin.</summary>
        DuplicateOrigin = 3,

        /// <summary>The host sent id zero, which is reserved.</summary>
        InvalidId = 4,

        /// <summary>The host sent a kind that is not a lowercase token.</summary>
        InvalidKind = 5
    }

    /// <summary>
    /// Live network entities with their kind, lifetime owner and spawn origin, plus a bounded set of removed ids. The
    /// host allocates ids, increasing and never reused; clients mirror the ids the host sends. Mirrored ids never move
    /// the allocation counter, so a host's announcement cannot exhaust or steer this process's own ids later; allocation
    /// only skips an id that is live or tombstoned here. Peer input is refused with a result, never an exception.
    /// Iteration helpers return ids in ascending order, so callers see the same order on every peer.
    /// </summary>
    /// <remarks>
    /// Lifetime owners are participant slots, which the runtime reuses. Before a leaver's slot goes to someone else,
    /// the runtime must remove or reassign the entities <see cref="CollectOwnedBy"/> reports for it.
    /// </remarks>
    internal sealed class NetworkEntityTable
    {
        public const int DefaultTombstoneCapacity = 8192;
        public const int MaximumKindLength = 64;

        private readonly Dictionary<uint, NetworkEntityEntry> live = new Dictionary<uint, NetworkEntityEntry>();
        private readonly Dictionary<NetSpawnOrigin, uint> origins = new Dictionary<NetSpawnOrigin, uint>();
        private readonly HashSet<uint> tombstones = new HashSet<uint>();
        private readonly Queue<uint> tombstoneOrder = new Queue<uint>();
        private readonly int tombstoneCapacity;
        private uint nextId;
        private bool exhausted;

        /// <param name="tombstoneCapacity">How many removed ids to remember.</param>
        /// <param name="firstAllocatedId">
        /// The first id <see cref="Allocate"/> hands out; one unless a test starts near the end of the id space.
        /// </param>
        public NetworkEntityTable(int tombstoneCapacity = DefaultTombstoneCapacity, uint firstAllocatedId = 1)
        {
            if (tombstoneCapacity < 1) throw new ArgumentOutOfRangeException(nameof(tombstoneCapacity));
            if (firstAllocatedId == 0) throw new ArgumentOutOfRangeException(nameof(firstAllocatedId));
            this.tombstoneCapacity = tombstoneCapacity;
            nextId = firstAllocatedId;
        }

        public int Count => live.Count;

        public int TombstoneCount => tombstones.Count;

        /// <summary>
        /// Whether a kind is a lowercase token: a letter or digit, then up to 63 letters, digits, dots, dashes or
        /// underscores.
        /// </summary>
        public static bool IsValidKind(string? kind)
        {
            if (string.IsNullOrEmpty(kind) || kind!.Length > MaximumKindLength) return false;
            for (var index = 0; index < kind.Length; index++)
            {
                var character = kind[index];
                var alphanumeric = (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9');
                if (alphanumeric) continue;
                if (index == 0 || (character != '.' && character != '-' && character != '_')) return false;
            }

            return true;
        }

        /// <summary>Host: allocates the next id for a canonical entity.</summary>
        /// <exception cref="InvalidOperationException">The origin is already live, or ids are exhausted.</exception>
        public NetworkEntityId Allocate(string kind, NetOwner owner, NetSpawnOrigin origin = default)
        {
            RequireKind(kind);
            if (origin.IsSpecified && origins.ContainsKey(origin))
            {
                throw new InvalidOperationException("Spawn origin " + origin + " is already live.");
            }

            while (true)
            {
                if (exhausted) throw new InvalidOperationException("Network entity ids are exhausted.");
                var candidate = nextId;
                if (nextId == uint.MaxValue) exhausted = true;
                else nextId++;
                if (live.ContainsKey(candidate) || tombstones.Contains(candidate)) continue;
                var id = new NetworkEntityId(candidate);
                Insert(new NetworkEntityEntry(id, kind, owner, origin));
                return id;
            }
        }

        /// <summary>Client: mirrors an entity the host announced. Bad input is refused with a result.</summary>
        public NetworkEntityAddResult TryAdd(NetworkEntityId id, string? kind, NetOwner owner, NetSpawnOrigin origin = default)
        {
            if (!id.IsValid) return NetworkEntityAddResult.InvalidId;
            if (!IsValidKind(kind)) return NetworkEntityAddResult.InvalidKind;
            if (tombstones.Contains(id.Value)) return NetworkEntityAddResult.Tombstoned;
            if (live.ContainsKey(id.Value)) return NetworkEntityAddResult.AlreadyLive;
            if (origin.IsSpecified && origins.ContainsKey(origin)) return NetworkEntityAddResult.DuplicateOrigin;
            Insert(new NetworkEntityEntry(id, kind!, owner, origin));
            return NetworkEntityAddResult.Added;
        }

        public bool Contains(NetworkEntityId id) => live.ContainsKey(id.Value);

        public bool IsTombstoned(NetworkEntityId id) => tombstones.Contains(id.Value);

        public bool TryGet(NetworkEntityId id, out NetworkEntityEntry entry) => live.TryGetValue(id.Value, out entry);

        public bool TryFindByOrigin(NetSpawnOrigin origin, out NetworkEntityId id)
        {
            id = default;
            if (!origin.IsSpecified || !origins.TryGetValue(origin, out var value)) return false;
            id = new NetworkEntityId(value);
            return true;
        }

        public bool TrySetOwner(NetworkEntityId id, NetOwner owner)
        {
            if (!live.TryGetValue(id.Value, out var entry)) return false;
            live[id.Value] = entry.WithOwner(owner);
            return true;
        }

        /// <summary>Removes a live entity and tombstones its id. The oldest tombstone is forgotten past capacity.</summary>
        public bool Remove(NetworkEntityId id)
        {
            if (!live.TryGetValue(id.Value, out var entry)) return false;
            live.Remove(id.Value);
            if (entry.Origin.IsSpecified) origins.Remove(entry.Origin);
            if (tombstones.Add(id.Value))
            {
                tombstoneOrder.Enqueue(id.Value);
                if (tombstoneOrder.Count > tombstoneCapacity) tombstones.Remove(tombstoneOrder.Dequeue());
            }

            return true;
        }

        public int CountOwnedBy(NetOwner owner)
        {
            var count = 0;
            foreach (var entry in live.Values)
            {
                if (entry.Owner.Equals(owner)) count++;
            }

            return count;
        }

        /// <summary>
        /// Appends, in ascending order, the ids whose lifetime the owner holds. When a participant leaves, remove or
        /// reassign these before its slot is reused.
        /// </summary>
        public void CollectOwnedBy(NetOwner owner, List<NetworkEntityId> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            var start = into.Count;
            foreach (var entry in live.Values)
            {
                if (entry.Owner.Equals(owner)) into.Add(entry.Id);
            }

            into.Sort(start, into.Count - start, null);
        }

        /// <summary>Appends every live id in ascending order.</summary>
        public void CollectIds(List<NetworkEntityId> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            var start = into.Count;
            foreach (var key in live.Keys) into.Add(new NetworkEntityId(key));
            into.Sort(start, into.Count - start, null);
        }

        /// <summary>
        /// Ends the session's entities and tombstones. This process's own allocation keeps counting, so the ids it
        /// hands out never repeat; ids mirrored from a host never affected it.
        /// </summary>
        public void Clear()
        {
            live.Clear();
            origins.Clear();
            tombstones.Clear();
            tombstoneOrder.Clear();
        }

        private void Insert(NetworkEntityEntry entry)
        {
            live.Add(entry.Id.Value, entry);
            if (entry.Origin.IsSpecified) origins.Add(entry.Origin, entry.Id.Value);
        }

        private static void RequireKind(string kind)
        {
            if (!IsValidKind(kind)) throw new ArgumentException("The entity kind must be a lowercase token.", nameof(kind));
        }
    }
}
