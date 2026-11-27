using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Network ids, lifetime owners, spawn origins and tombstones (WS3.5).</summary>
    internal static class MultiplayerEntityTableTests
    {
        public static void Run()
        {
            AllocatesIncreasingIds();
            MirrorsHostIdsAndRefusesResurrection();
            MirroredIdsNeverMoveAllocation();
            RefusesBadPeerInputWithoutThrowing();
            TracksOwnersAndOrigins();
            BoundsTombstones();
            ValidatesKindsAndIdentities();
            KeepsCountingAcrossSessionsAndStopsAtExhaustion();
            Console.WriteLine("MultiplayerEntityTableTests passed.");
        }

        private static void AllocatesIncreasingIds()
        {
            var table = new NetworkEntityTable();
            var first = table.Allocate("asset", NetOwner.Server);
            var second = table.Allocate("creator.content", NetOwner.ForParticipant(2));
            Equal(1u, first.Value, "ids start at one");
            Equal(2u, second.Value, "ids increase");
            True(table.TryGet(second, out var entry), "allocated entry is live");
            Equal("creator.content", entry.Kind, "kind");
            Equal(NetOwner.ForParticipant(2), entry.Owner, "owner");
            True(!entry.Origin.IsSpecified, "no origin given");
            Equal(2, table.Count, "count");
            True(table.Remove(first), "remove");
            Equal(3u, table.Allocate("asset", NetOwner.Server).Value, "a removed id is never reused");
        }

        private static void MirrorsHostIdsAndRefusesResurrection()
        {
            var client = new NetworkEntityTable();
            var id = new NetworkEntityId(40);
            Equal(NetworkEntityAddResult.Added, client.TryAdd(id, "asset", NetOwner.Server), "mirror a host spawn");
            Equal(NetworkEntityAddResult.AlreadyLive, client.TryAdd(id, "asset", NetOwner.Server), "a duplicate spawn");
            True(client.Remove(id), "despawn");
            True(!client.Remove(id), "despawning twice");
            True(client.IsTombstoned(id) && !client.Contains(id), "tombstoned");
            Equal(NetworkEntityAddResult.Tombstoned, client.TryAdd(id, "asset", NetOwner.Server), "a stale spawn cannot resurrect it");
            Equal(NetworkEntityAddResult.Added, client.TryAdd(new NetworkEntityId(7), "asset", NetOwner.Server), "older ids arrive late");
            Equal(2, client.Count + client.TombstoneCount, "one live and one tombstoned");
        }

        private static void MirroredIdsNeverMoveAllocation()
        {
            // One announcement of the last id, then a session change, used to exhaust allocation for the rest of the
            // process, so this player could never host.
            var table = new NetworkEntityTable();
            Equal(NetworkEntityAddResult.Added, table.TryAdd(new NetworkEntityId(uint.MaxValue), "asset", NetOwner.Server), "a host announces the last id");
            Equal(1u, table.Allocate("asset", NetOwner.Server).Value, "allocation does not jump to the host's ids");
            table.Clear();
            Equal(2u, table.Allocate("asset", NetOwner.Server).Value, "and hosting after the session change still works");

            var steered = new NetworkEntityTable();
            steered.TryAdd(new NetworkEntityId(1_000_000), "asset", NetOwner.Server);
            steered.Clear();
            Equal(1u, steered.Allocate("asset", NetOwner.Server).Value, "a host cannot steer this process's ids either");

            // A table used both ways still never hands out an id it holds: live and tombstoned ids are skipped.
            var mixed = new NetworkEntityTable();
            mixed.TryAdd(new NetworkEntityId(1), "asset", NetOwner.Server);
            mixed.TryAdd(new NetworkEntityId(2), "asset", NetOwner.Server);
            mixed.Remove(new NetworkEntityId(2));
            mixed.TryAdd(new NetworkEntityId(4), "asset", NetOwner.Server);
            Equal(3u, mixed.Allocate("asset", NetOwner.Server).Value, "skips a live and a tombstoned id");
            Equal(5u, mixed.Allocate("asset", NetOwner.Server).Value, "and the next live one");
            True(mixed.Contains(new NetworkEntityId(1)) && mixed.Contains(new NetworkEntityId(4)), "the mirrored entities are untouched");
        }

        private static void RefusesBadPeerInputWithoutThrowing()
        {
            var table = new NetworkEntityTable();
            Equal(NetworkEntityAddResult.InvalidId, table.TryAdd(default, "asset", NetOwner.Server), "id zero from a host");
            Equal(NetworkEntityAddResult.InvalidKind, table.TryAdd(new NetworkEntityId(1), "", NetOwner.Server), "an empty kind");
            Equal(NetworkEntityAddResult.InvalidKind, table.TryAdd(new NetworkEntityId(1), null, NetOwner.Server), "a missing kind");
            Equal(NetworkEntityAddResult.InvalidKind, table.TryAdd(new NetworkEntityId(1), "Unknown Kind", NetOwner.Server), "a malformed kind");
            Equal(NetworkEntityAddResult.InvalidKind, table.TryAdd(new NetworkEntityId(1), new string('a', 65), NetOwner.Server), "a kind that is too long");
            Equal(NetworkEntityAddResult.InvalidId, table.TryAdd(default, null, NetOwner.Server), "the id is checked first");
            True(table.Count == 0 && table.TombstoneCount == 0, "nothing was added");
            Equal(NetworkEntityAddResult.Added, table.TryAdd(new NetworkEntityId(1), "asset", NetOwner.Server), "the same id with a good kind");

            True(!NetworkEntityId.TryCreate(0, out var none) && !none.IsValid, "reading id zero from the wire");
            True(NetworkEntityId.TryCreate(uint.MaxValue, out var last) && last.Value == uint.MaxValue, "reading the last id");

            // The other announced fields read without throwing too.
            True(NetOwner.TryFromSlot(0, out var server) && server.IsServer, "slot zero is the server");
            True(NetOwner.TryFromSlot(5, out var player) && player.Equals(NetOwner.ForParticipant(5)), "a participant slot");
            True(!NetOwner.TryFromSlot(-1, out var refused) && refused.IsServer, "a negative slot");
            True(NetSpawnOrigin.TryCreate("io.github.example.sandbox", 3, out var origin) && origin.Equals(new NetSpawnOrigin("io.github.example.sandbox", 3)), "an origin");
            True(!NetSpawnOrigin.TryCreate("", 3, out var unnamed) && !unnamed.IsSpecified, "an origin with no mod id");
            True(!NetSpawnOrigin.TryCreate(null, 3, out _), "an origin with a missing mod id");
            True(!NetSpawnOrigin.TryCreate("io.github.example.sandbox", 0, out _), "an origin with sequence zero");
        }

        private static void TracksOwnersAndOrigins()
        {
            var table = new NetworkEntityTable();
            var origin = new NetSpawnOrigin("io.github.example.sandbox", 3);
            var spawned = table.Allocate("asset", NetOwner.ForParticipant(4), origin);
            True(table.TryFindByOrigin(origin, out var found) && found.Equals(spawned), "a shadow finds the host's spawn");
            True(!table.TryFindByOrigin(new NetSpawnOrigin("io.github.example.sandbox", 4), out _), "another sequence");
            True(!table.TryFindByOrigin(default, out _), "an unspecified origin finds nothing");
            Throws<InvalidOperationException>(() => table.Allocate("asset", NetOwner.Server, origin), "one live entity per origin");
            Equal(NetworkEntityAddResult.DuplicateOrigin, table.TryAdd(new NetworkEntityId(99), "asset", NetOwner.Server, origin), "mirror");

            table.Allocate("asset", NetOwner.ForParticipant(4));
            table.Allocate("asset", NetOwner.ForParticipant(5));
            table.Allocate("asset", NetOwner.Server);
            Equal(2, table.CountOwnedBy(NetOwner.ForParticipant(4)), "count by owner");
            var owned = new List<NetworkEntityId> { new NetworkEntityId(500) };
            table.CollectOwnedBy(NetOwner.ForParticipant(4), owned);
            Sequence(new[] { new NetworkEntityId(500), spawned, new NetworkEntityId(2) }, owned, "appended in ascending order");
            True(table.TrySetOwner(spawned, NetOwner.Server), "transfer the lifetime to the server");
            Equal(1, table.CountOwnedBy(NetOwner.ForParticipant(4)), "after transfer");
            True(!table.TrySetOwner(new NetworkEntityId(777), NetOwner.Server), "unknown id");
            var ids = new List<NetworkEntityId>();
            table.CollectIds(ids);
            Sequence(new[] { new NetworkEntityId(1), new NetworkEntityId(2), new NetworkEntityId(3), new NetworkEntityId(4) }, ids, "all ids");

            True(table.Remove(spawned), "despawn frees the origin");
            True(!table.TryFindByOrigin(origin, out _), "the origin is gone");
        }

        private static void BoundsTombstones()
        {
            var table = new NetworkEntityTable(tombstoneCapacity: 3);
            for (var index = 0; index < 5; index++) table.Allocate("asset", NetOwner.Server);
            for (uint value = 1; value <= 4; value++) table.Remove(new NetworkEntityId(value));
            Equal(3, table.TombstoneCount, "bounded");
            True(!table.IsTombstoned(new NetworkEntityId(1)), "the oldest is forgotten");
            True(table.IsTombstoned(new NetworkEntityId(2)) && table.IsTombstoned(new NetworkEntityId(4)), "the newest stay");
            Throws<ArgumentOutOfRangeException>(() => new NetworkEntityTable(0), "capacity");
        }

        private static void ValidatesKindsAndIdentities()
        {
            True(NetworkEntityTable.IsValidKind("creator.content"), "dotted kind");
            True(NetworkEntityTable.IsValidKind("robotkit.robot-v2_a"), "dash and underscore");
            True(NetworkEntityTable.IsValidKind(new string('a', 64)), "64 characters");
            foreach (var kind in new[] { "", "Asset", ".asset", "-a", "a b", "asset/x", new string('a', 65), "café" })
            {
                True(!NetworkEntityTable.IsValidKind(kind), "invalid kind '" + kind + "'");
            }

            True(!NetworkEntityTable.IsValidKind(null), "null kind");
            var table = new NetworkEntityTable();
            Throws<ArgumentException>(() => table.Allocate("Bad Kind", NetOwner.Server), "the host's own spawns must name a valid kind");
            Throws<ArgumentOutOfRangeException>(() => new NetworkEntityId(0), "id zero");
            Throws<ArgumentOutOfRangeException>(() => new NetworkEntityTable(firstAllocatedId: 0), "allocation cannot start at zero");
            Throws<ArgumentOutOfRangeException>(() => NetOwner.ForParticipant(0), "participant zero is the server");
            Throws<ArgumentException>(() => new NetSpawnOrigin("", 1), "origin mod id");
            Throws<ArgumentOutOfRangeException>(() => new NetSpawnOrigin("mod", 0), "origin sequence");
            True(NetOwner.Server.IsServer && !NetOwner.ForParticipant(1).IsServer, "server owner");
        }

        private static void KeepsCountingAcrossSessionsAndStopsAtExhaustion()
        {
            var table = new NetworkEntityTable();
            var id = table.Allocate("asset", NetOwner.Server);
            table.Remove(id);
            table.Clear();
            True(!table.IsTombstoned(id) && table.Count == 0, "a new session forgets entities and tombstones");
            Equal(2u, table.Allocate("asset", NetOwner.Server).Value, "ids keep counting across sessions");

            var nearlyFull = new NetworkEntityTable(firstAllocatedId: uint.MaxValue - 1);
            Equal(uint.MaxValue - 1, nearlyFull.Allocate("asset", NetOwner.Server).Value, "the second-last id");
            Equal(uint.MaxValue, nearlyFull.Allocate("asset", NetOwner.Server).Value, "the last id");
            Throws<InvalidOperationException>(() => nearlyFull.Allocate("asset", NetOwner.Server), "ids never wrap to zero");
            nearlyFull.Clear();
            Throws<InvalidOperationException>(() => nearlyFull.Allocate("asset", NetOwner.Server), "this process's own ids stay used up");

            var blocked = new NetworkEntityTable(firstAllocatedId: uint.MaxValue);
            blocked.TryAdd(new NetworkEntityId(uint.MaxValue), "asset", NetOwner.Server);
            Throws<InvalidOperationException>(() => blocked.Allocate("asset", NetOwner.Server), "skipping the last id leaves none");
            True(blocked.Count == 1, "the failed allocation adds nothing");
        }
    }
}
