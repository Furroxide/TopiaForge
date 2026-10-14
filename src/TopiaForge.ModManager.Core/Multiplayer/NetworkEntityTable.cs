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
        DuplicateOrigin = 3
    }

    /// <summary>
    /// Live network entities with their kind, lifetime owner and spawn origin, plus a bounded set of removed ids. The
    /// host allocates ids, increasing and never reused; clients mirror the ids the host sends. Iteration helpers return
    /// ids in ascending order, so callers see the same order on every peer.
    /// </summary>
    internal sealed class NetworkEntityTable
    {
        public const int DefaultTombstoneCapacity = 8192;
        public const int MaximumKindLength = 64;

        private readonly Dictionary<uint, NetworkEntityEntry> live = new Dictionary<uint, NetworkEntityEntry>();
        private readonly Dictionary<NetSpawnOrigin, uint> origins = new Dictionary<NetSpawnOrigin, uint>();
        private readonly HashSet<uint> tombstones = new HashSet<uint>();
        private readonly Queue<uint> tombstoneOrder = new Queue<uint>();
        private readonly int tombstoneCapacity;
        private uint nextId = 1;
        private bool exhausted;

        public NetworkEntityTable(int tombstoneCapacity = DefaultTombstoneCapacity)
        {
            if (tombstoneCapacity < 1) throw new ArgumentOutOfRangeException(nameof(tombstoneCapacity));
            this.tombstoneCapacity = tombstoneCapacity;
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

            if (exhausted) throw new InvalidOperationException("Network entity ids are exhausted.");
            var id = new NetworkEntityId(nextId);
            if (nextId == uint.MaxValue) exhausted = true;
            else nextId++;
            Insert(new NetworkEntityEntry(id, kind, owner, origin));
            return id;
        }

        /// <summary>Client: mirrors an entity the host announced.</summary>
        public NetworkEntityAddResult TryAdd(NetworkEntityId id, string kind, NetOwner owner, NetSpawnOrigin origin = default)
        {
            if (!id.IsValid) throw new ArgumentException("A valid id is required.", nameof(id));
            RequireKind(kind);
            if (tombstones.Contains(id.Value)) return NetworkEntityAddResult.Tombstoned;
            if (live.ContainsKey(id.Value)) return NetworkEntityAddResult.AlreadyLive;
            if (origin.IsSpecified && origins.ContainsKey(origin)) return NetworkEntityAddResult.DuplicateOrigin;
            Insert(new NetworkEntityEntry(id, kind, owner, origin));
            if (id.Value >= nextId && !exhausted)
            {
                if (id.Value == uint.MaxValue) exhausted = true;
                else nextId = id.Value + 1;
            }

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

        /// <summary>Appends, in ascending order, the ids whose lifetime the owner holds.</summary>
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

        /// <summary>Ends the session's entities and tombstones. Allocation keeps counting, so ids never repeat.</summary>
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
