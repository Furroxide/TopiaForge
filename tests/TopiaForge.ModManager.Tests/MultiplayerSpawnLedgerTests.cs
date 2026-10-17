using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The spawns the runtime records at spawn time, even offline, so a later promotion can baseline them: order, liveness,
    /// what is recorded at all, descriptions, bounds, and an offline frame that costs nothing however much was recorded.
    /// </summary>
    internal static class MultiplayerSpawnLedgerTests
    {
        private static readonly ModNetProfile Shared = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process);

        internal static void Run()
        {
            RecordsKeepTheirOrderAndEndWithTheirEntities();
            OnlyWhatAHostWouldBaselineIsRecorded();
            RegisteringAnEntityAgainDescribesItInPlace();
            DescriptionsRefuseWhatIsNotADescription();
            SweepsKeepTheLedgerBounded();
            OfflineFramesCostNothingHoweverMuchWasRecorded();
        }

        private static void RecordsKeepTheirOrderAndEndWithTheirEntities()
        {
            var ledger = new MultiplayerSpawnLedger();
            var first = new SeamEntity();
            var second = new SeamEntity();
            var third = new SeamEntity();
            var records = new[] { first, second, third }.Select(entity => ledger.Record(entity, Description("io.example.blocks"))!).ToArray();
            That(records.Select(record => record.Sequence).SequenceEqual(new[] { 1L, 2L, 3L }), "records are numbered in the order they happen");
            That(ledger.Live().Select(record => record.Entity).SequenceEqual(new IEntity[] { first, second, third }), "and read back in that order");
            second.IsAlive = false;
            ledger.Remove(records[2]);
            That(ledger.Live().Single().Entity == first && ledger.Count == 1, "a record ends with its entity, or when it is forgotten");
            That(ledger.Find(first) == records[0] && ledger.Find(second) == null && ledger.Find(third) == null, "only live records are found");
            var fourth = ledger.Record(new SeamEntity(), Description("io.example.blocks"))!;
            That(fourth.Sequence == 4, "a sequence is never reused");
            ledger.Remove(records[2]);
            That(ledger.Live().Count == 2, "forgetting a record twice changes nothing");
            That(new MultiplayerSpawnLedger().Live().Count == 0, "an empty ledger reads back empty");
        }

        private static void OnlyWhatAHostWouldBaselineIsRecorded()
        {
            foreach (var networkClass in Classes)
            {
                foreach (var context in Contexts)
                {
                    var profile = new ModNetProfile(networkClass, context);
                    var expected = networkClass == ModNetworkClass.Shared || networkClass == ModNetworkClass.ServerOnly;
                    That(MultiplayerSpawnLedger.Records(profile) == expected, profile + " is " + (expected ? string.Empty : "not ") + "recorded");
                    var ledger = new MultiplayerSpawnLedger();
                    var record = ledger.Record(new SeamEntity(), new MultiplayerSpawnDescription("io.example.mod", profile, MultiplayerSpawnSource.SdkSpawn,
                        MultiplayerSpawnKinds.AssetPrefab, null, TransformState.Identity, null));
                    That((record != null) == expected && ledger.Count == (expected ? 1 : 0), profile + " is kept only when a host would baseline it");
                }
            }
        }

        private static void RegisteringAnEntityAgainDescribesItInPlace()
        {
            var ledger = new MultiplayerSpawnLedger();
            var crate = new SeamEntity();
            var lamp = new SeamEntity();
            var asset = ledger.Record(crate, Description("io.example.creator"))!;
            ledger.Record(lamp, Description("io.example.blocks"));
            var described = new MultiplayerSpawnDescription("io.example.creator", Shared, MultiplayerSpawnSource.SdkSpawn, "creator.content", null,
                TransformState.Identity, "catalog:crate");
            var again = ledger.Record(crate, described)!;
            That(ReferenceEquals(asset, again) && again.Sequence == 1 && again.Description == described && ledger.Count == 2,
                "a provider that registers what it spawned through the asset service describes it in place, keeping its place");
            That(ledger.Live().Select(record => record.Entity).SequenceEqual(new IEntity[] { crate, lamp }), "the order is unchanged");
            That(ledger.Record(crate, new MultiplayerSpawnDescription("io.example.hats", new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.Process),
                     MultiplayerSpawnSource.SdkSpawn, "hats.hat", null, TransformState.Identity, null)) == null && ledger.Find(crate)!.Description == described,
                "a client-local registration never takes over a networked record");
            crate.IsAlive = false;
            var reborn = ledger.Record(new SeamEntity(crate.Id), Description("io.example.creator"))!;
            That(reborn.Sequence == 3 && !ReferenceEquals(reborn, asset), "an entity recorded after the last one with its id ended is a new record");
            That(ledger.Live().Select(record => record.Sequence).SequenceEqual(new[] { 2L, 3L }), "and the dead one is swept");
        }

        private static void DescriptionsRefuseWhatIsNotADescription()
        {
            Throws<ArgumentException>(() => Description(""), "a description needs its mod");
            foreach (var kind in new[] { "", "Asset.Prefab", "-asset", "asset prefab", "asset/prefab", new string('a', 65) })
            {
                Throws<ArgumentException>(() => new MultiplayerSpawnDescription("io.example.mod", Shared, MultiplayerSpawnSource.SdkSpawn, kind, null,
                    TransformState.Identity, null), "'" + kind + "' is not a kind");
            }

            Throws<ArgumentException>(() => new MultiplayerSpawnDescription("io.example.mod", Shared, MultiplayerSpawnSource.WorldContent, "worlds.arena", null,
                TransformState.Identity, null), "world content needs its key");
            Throws<ArgumentException>(() => new MultiplayerSpawnDescription("io.example.mod", Shared, MultiplayerSpawnSource.SdkSpawn, "worlds.arena", "key",
                TransformState.Identity, null), "and only world content has one");
            Throws<ArgumentOutOfRangeException>(() => new MultiplayerSpawnDescription("io.example.mod", Shared, (MultiplayerSpawnSource)9, "worlds.arena", null,
                TransformState.Identity, null), "an undefined source");
            var ledger = new MultiplayerSpawnLedger();
            Throws<ArgumentNullException>(() => ledger.Record(null!, Description("io.example.mod")), "a record needs its entity");
            Throws<ArgumentNullException>(() => ledger.Record(new SeamEntity(), null!), "and its description");
            Throws<ArgumentException>(() => ledger.Record(new SeamEntity(""), Description("io.example.mod")), "an entity without an id cannot be found again");
            Throws<ArgumentNullException>(() => ledger.Remove(null!), "forgetting needs a record");
            Throws<ArgumentNullException>(() => ledger.Find(null!), "finding needs an entity");

            // The SPI checks kinds the way the entity table does, so a kind one accepts the other accepts too.
            var random = new Random(3101);
            var alphabet = "abcxyz0189._-A/ é";
            for (var index = 0; index < 20_000; index++)
            {
                var length = random.Next(0, 70);
                var chars = new char[length];
                for (var at = 0; at < length; at++) chars[at] = alphabet[random.Next(alphabet.Length)];
                var kind = new string(chars);
                That(InternalReplicationLimits.IsKind(kind) == NetworkEntityTable.IsValidKind(kind), "the SPI and the entity table agree on '" + kind + "'");
            }
        }

        private static void SweepsKeepTheLedgerBounded()
        {
            // One body respawned over and over, as a mod that keeps replacing it would: the ledger never grows past the sweep's bound.
            var ledger = new MultiplayerSpawnLedger();
            SeamEntity? previous = null;
            var largest = 0;
            for (var index = 0; index < 10_000; index++)
            {
                if (previous != null) previous.IsAlive = false;
                previous = new SeamEntity();
                ledger.Record(previous, Description("io.example.churn"));
                largest = Math.Max(largest, ledger.Count);
            }

            That(largest <= MultiplayerSpawnLedger.MinimumSweepThreshold + 1 && ledger.Live().Count == 1,
                "churn keeps the ledger to its minimum bound (" + largest + " at most)");

            // A hundred live bodies under the same churn: at most twice what is alive, plus one, between sweeps.
            var crowd = new MultiplayerSpawnLedger();
            for (var index = 0; index < 100; index++) crowd.Record(new SeamEntity(), Description("io.example.crowd"));
            largest = 0;
            previous = null;
            for (var index = 0; index < 10_000; index++)
            {
                if (previous != null) previous.IsAlive = false;
                previous = new SeamEntity();
                crowd.Record(previous, Description("io.example.churn"));
                largest = Math.Max(largest, crowd.Count);
            }

            That(largest <= 2 * 101 + 1 && crowd.Live().Count == 101, "the bound follows what is alive (" + largest + " at most)");
        }

        private static void OfflineFramesCostNothingHoweverMuchWasRecorded()
        {
            // No provider: each hook reads one field, whatever the ledger holds.
            var log = new RecordingRuntimeLog();
            var offline = new MultiplayerRuntime(() => null, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            offline.Connect();
            for (var index = 0; index < 1000; index++) offline.Spawns.Record(new SeamEntity(), Description("io.example.blocks"));
            var withoutProvider = Allocated(() => Frame(offline));
            That(withoutProvider == 0, "an offline frame with a thousand recorded spawns allocates nothing (" + withoutProvider + " bytes)");

            // A solo provider: the frame pumps it and nothing else.
            var (solo, control, _) = Runtime(() => SessionPhase.Running);
            solo.Connect();
            for (var index = 0; index < 1000; index++) solo.Spawns.Record(new SeamEntity(), Description("io.example.blocks"));
            var withProvider = Allocated(() => Frame(solo));
            That(withProvider == 0 && control.Pumps > 10_000, "a solo frame with a thousand recorded spawns allocates nothing (" + withProvider + " bytes)");

            // A mod that is never networked pays nothing at spawn time either: its spawns are not recorded at all.
            var replication = MultiplayerReplication.Offline();
            var cosmetic = replication.Bind("io.example.hats", new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.Process), new OwnerModLifetime());
            var hat = new SeamEntity();
            var request = new AssetSpawnRequest(new SeamPrefab(), TransformState.Identity);
            var spawning = Allocated(() => cosmetic.RecordAssetSpawn(hat, request));
            That(spawning == 0 && replication.Runtime.Spawns.Count == 0, "a client-local spawn records nothing and allocates nothing (" + spawning + " bytes)");
        }

        private static MultiplayerSpawnDescription Description(string mod) =>
            new MultiplayerSpawnDescription(mod, Shared, MultiplayerSpawnSource.SdkSpawn, MultiplayerSpawnKinds.AssetPrefab, null, TransformState.Identity, null);
    }

    /// <summary>A prefab handle as a mod holds one.</summary>
    internal sealed class SeamPrefab : IPrefabAsset
    {
        public string Name => "seam prefab";

        public bool IsAlive { get; private set; } = true;

        public void Dispose() => IsAlive = false;
    }
}
