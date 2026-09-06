using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldDiscoveryIdentityTests
    {
        public static void Run()
        {
            StableKeysIgnoreDisplayAndEnumerationOrder();
            LongFamiliesAndUnicodeKeysStayInsideTheContract();
            AmbiguousSourcesNeverReceiveInventedIdentities();
            InvalidAndUnboundedDiscoveryFails();
            Console.WriteLine("All world discovery identity tests passed.");
        }
        private static void StableKeysIgnoreDisplayAndEnumerationOrder()
        {
            var first = Map("example.worlds.level", Entry("Assets/A/City.unity", "City"), Entry("Assets/B/City.unity", "City"));
            var second = Map("example.worlds.level", Entry("Assets/B/City.unity", "Renamed"), Entry("Assets/A/City.unity", "Another title"));
            Assert(first.Select(x => x.Descriptor.Id).SequenceEqual(second.Select(x => x.Descriptor.Id)), "enumeration order and display changes cannot change identities");
            Assert(first[0].Descriptor.Id != first[1].Descriptor.Id, "same scene filename under distinct native paths must remain distinct");
            Assert(first.Select(x => x.SourceKey).SequenceEqual(second.Select(x => x.SourceKey)), "identity-to-source mapping must remain stable");
            var curated = WorldDiscoveryIdentity.Map("example.worlds.level", NativeWorldSource.CuratedLevels,
                new[] { new NativeWorldEntry(NativeWorldSource.CuratedLevels, "Assets/A/City.unity", "City", "", "City") }, 1);
            Assert(curated.Succeeded && curated.Value![0].Descriptor.Id != first.Single(x => x.SourceKey == "Assets/A/City.unity").Descriptor.Id, "source kind participates in the identity");
        }
        private static void LongFamiliesAndUnicodeKeysStayInsideTheContract()
        {
            foreach (var length in new[] { 63, 64, 65, 93, 94 })
            {
                var family = "example." + new string('a', length - 8);
                var result = Map(family, Entry("Assets/世界/🚀.unity", "Unicode 🌍"));
                var descriptor = result[0].Descriptor;
                Assert(descriptor.Id.Length <= 96 && descriptor.Id.Length > family.Length + 1, "instance must fit its declaration budget");
                Assert(descriptor.Id.All(c => c < 128), "native Unicode must never become a Unicode identifier");
                Assert(descriptor.FamilyId == family && descriptor.Name == "Unicode 🌍", "family and display metadata remain intact");
            }
            Assert(Map("example." + new string('a', 86), Entry("one", "One"))[0].Descriptor.Id.Length == 96, "a 94-character family has exactly one suffix character");
        }
        private static void AmbiguousSourcesNeverReceiveInventedIdentities()
        {
            var duplicate = WorldDiscoveryIdentity.Map("example.worlds", NativeWorldSource.BuildScenes, new[] { Entry("same", "One"), Entry("same", "Two") }, 2);
            Assert(!duplicate.Succeeded && duplicate.ErrorCode == ModErrorCode.Conflict, "duplicate stable keys must fail rather than choose a display entry");
            var collisions = WorldDiscoveryIdentity.Map("example." + new string('a', 86), NativeWorldSource.BuildScenes,
                Enumerable.Range(0, 17).Select(x => Entry("source-" + x, "World")).ToArray(), 1);
            Assert(!collisions.Succeeded && collisions.ErrorCode == ModErrorCode.Conflict, "one hexadecimal suffix has only sixteen identities: collisions outside the requested page must fail too");
        }
        private static void InvalidAndUnboundedDiscoveryFails()
        {
            Assert(!WorldDiscoveryIdentity.Map("example." + new string('a', 87), NativeWorldSource.BuildScenes, new[] { Entry("one", "One") }, 1).Succeeded, "95-character families cannot produce legal instances");
            Assert(!WorldDiscoveryIdentity.Map("example.worlds", NativeWorldSource.BuildScenes, new[] { Entry("one", new string('x', 129)) }, 1).Succeeded, "display limits cannot be bypassed");
            Assert(!WorldDiscoveryIdentity.Map("example.worlds", NativeWorldSource.BuildScenes, Array.Empty<NativeWorldEntry>(), 0).Succeeded, "zero result budgets must fail");
            Assert(!WorldDiscoveryIdentity.Map("example.worlds", NativeWorldSource.BuildScenes, Enumerable.Range(0, 4097).Select(x => Entry("key" + x, "World")).ToArray(), 1).Succeeded, "unbounded native catalogs must fail before mapping");
            var wrongSource = new NativeWorldEntry(NativeWorldSource.CuratedLevels, "key", "World", "", "World");
            Assert(!WorldDiscoveryIdentity.Map("example.worlds", NativeWorldSource.BuildScenes, new[] { wrongSource }, 1).Succeeded, "one source cannot publish another source's entries");
        }
        private static NativeWorldEntry Entry(string key, string name) => new NativeWorldEntry(NativeWorldSource.BuildScenes, key, name, "", "City");
        private static IReadOnlyList<MappedNativeWorld> Map(string family, params NativeWorldEntry[] entries)
        {
            var result = WorldDiscoveryIdentity.Map(family, NativeWorldSource.BuildScenes, entries, 4096);
            Assert(result.Succeeded, result.ErrorMessage);
            return result.Value!;
        }
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
