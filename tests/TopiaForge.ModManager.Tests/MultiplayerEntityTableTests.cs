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
            Equal(41u, client.Allocate("asset", NetOwner.Server).Value, "allocation stays past mirrored ids");
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
            Throws<ArgumentException>(() => table.Allocate("Bad Kind", NetOwner.Server), "allocate validates kinds");
            Throws<ArgumentException>(() => table.TryAdd(new NetworkEntityId(1), "", NetOwner.Server), "mirror validates kinds");
            Throws<ArgumentException>(() => table.TryAdd(default, "asset", NetOwner.Server), "id zero is reserved");
            Throws<ArgumentOutOfRangeException>(() => new NetworkEntityId(0), "id zero");
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

            var nearlyFull = new NetworkEntityTable();
            nearlyFull.TryAdd(new NetworkEntityId(uint.MaxValue - 1), "asset", NetOwner.Server);
            Equal(uint.MaxValue, nearlyFull.Allocate("asset", NetOwner.Server).Value, "the last id");
            Throws<InvalidOperationException>(() => nearlyFull.Allocate("asset", NetOwner.Server), "ids never wrap to zero");
        }
    }
}
