using System;
using System.Collections.Generic;
using System.Text;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Scene object keys and the census that excludes duplicates (WS3.5).</summary>
    internal static class MultiplayerSceneKeyTests
    {
        public static void Run()
        {
            HashesMatchPublishedFnvVectors();
            PreimagesAreCanonicalAndUnambiguous();
            CensusExcludesDuplicatesInsteadOfGuessing();
            RejectsMalformedDescriptors();
            Console.WriteLine("MultiplayerSceneKeyTests passed.");
        }

        private static void HashesMatchPublishedFnvVectors()
        {
            Equal(0xCBF29CE484222325UL, NetHash.Fnv1a64(string.Empty), "empty input is the offset basis");
            Equal(0xAF63DC4C8601EC8CUL, NetHash.Fnv1a64("a"), "FNV-1a 64 of 'a'");
            Equal(0x85944171F73967E8UL, NetHash.Fnv1a64("foobar"), "FNV-1a 64 of 'foobar'");
            var bytes = Encoding.ASCII.GetBytes("xfoobarx");
            Equal(0x85944171F73967E8UL, NetHash.Fnv1a64(bytes, 1, 6), "a slice");
            Throws<ArgumentOutOfRangeException>(() => NetHash.Fnv1a64(bytes, 5, 4), "a slice past the end");
        }

        private static void PreimagesAreCanonicalAndUnambiguous()
        {
            var crate = Describe("Sandbox", "UnityEngine.Rigidbody", ("World", 0), ("Props", 3), ("Crate", 12));
            Equal("Sandbox\0World#0/Props#3/Crate#12\0UnityEngine.Rigidbody", SceneObjectKey.FormatPreimage(crate), "preimage text");
            Equal(NetHash.Fnv1a64("Sandbox\0World#0/Props#3/Crate#12\0UnityEngine.Rigidbody"), SceneObjectKey.Compute(crate).Value, "key is FNV-1a of the preimage");
            Equal(SceneObjectKey.Compute(crate), SceneObjectKey.Compute(Describe("Sandbox", "UnityEngine.Rigidbody", ("World", 0), ("Props", 3), ("Crate", 12))), "stable");

            var escaped = Describe("A/B", "T#1", ("x\\y/z#w\0v", 2));
            Equal("A\\/B\0x\\\\y\\/z\\#w\\0v#2\0T\\#1", SceneObjectKey.FormatPreimage(escaped), "every field escapes the same four characters");

            Distinct(Describe("S", "C", ("a#0/b", 0)), Describe("S", "C", ("a", 0), ("b", 0)), "a slash inside a name");
            Distinct(Describe("S\0a#0", "C", ("b", 0)), Describe("S", "C", ("a", 0), ("b", 0)), "a NUL inside the scene");
            Distinct(Describe("S", "C", ("Crate", 1)), Describe("S", "C", ("Crate", 2)), "the sibling index");
            Distinct(Describe("S", "UnityEngine.Rigidbody", ("Crate", 1)), Describe("S", "UnityEngine.Collider", ("Crate", 1)), "the component");
            Distinct(Describe("Level1", "C", ("Crate", 1)), Describe("Level2", "C", ("Crate", 1)), "the scene");
            Distinct(Describe("S", "C", ("", 0)), Describe("S", "C", ("", 1)), "unnamed objects still differ by index");

            Equal(SceneObjectKey.Compute(Describe("S", "C", ("�", 0))), SceneObjectKey.Compute(Describe("S", "C", ("\ud800", 0))), "a lone surrogate hashes as U+FFFD");
            Equal("00000000000000ff", new SceneObjectKey(255).ToString(), "keys print as 16 hex digits");
        }

        private static void CensusExcludesDuplicatesInsteadOfGuessing()
        {
            var objects = new List<SceneObjectDescriptor>
            {
                Describe("S", "C", ("Lamp", 0)),
                Describe("S", "C", ("Door", 1)),
                Describe("S", "C", ("Door", 1)),
                Describe("S", "C", ("Crate", 2)),
                Describe("S", "C", ("Door", 1))
            };
            var census = SceneObjectKeyCensus.Build(objects);
            Equal(5, census.ObjectCount, "objects");
            Equal(2, census.UniqueCount, "unique keys");
            Equal(3, census.ExcludedObjectCount, "every copy of a duplicate is excluded");
            Equal(1, census.DuplicateKeys.Count, "one duplicated key");
            var door = SceneObjectKey.Compute(objects[1]);
            Equal(door, census.DuplicateKeys[0], "the duplicated key");
            True(census.IsExcluded(door), "excluded");
            True(!census.TryGetObjectIndex(door, out _), "an excluded key binds nothing");
            True(census.TryGetObjectIndex(SceneObjectKey.Compute(objects[3]), out var crate) && crate == 3, "a unique key binds its object");
            True(census.TryGetObjectIndex(SceneObjectKey.Compute(objects[0]), out var lamp) && lamp == 0, "the first object");
            True(!census.TryGetObjectIndex(new SceneObjectKey(12345), out _), "an unknown key");
            Equal(census.KeyAt(2), census.KeyAt(4), "KeyAt exposes every key");

            var many = new List<SceneObjectDescriptor>();
            for (var index = 0; index < 1000; index++) many.Add(Describe("S", "C", ("Prop", index)));
            many.Add(Describe("S", "C", ("Prop", 999)));
            var large = SceneObjectKeyCensus.Build(many);
            Equal(999, large.UniqueCount, "only the duplicated pair drops out");
            Sequence(new[] { SceneObjectKey.Compute(many[999]) }, large.DuplicateKeys, "duplicates are reported ascending");
            Equal(0, SceneObjectKeyCensus.Build(new List<SceneObjectDescriptor>()).ObjectCount, "an empty scene");
        }

        private static void RejectsMalformedDescriptors()
        {
            Throws<ArgumentException>(() => new SceneObjectDescriptor("S", Array.Empty<SceneObjectPathSegment>(), "C"), "an empty path");
            Throws<ArgumentOutOfRangeException>(() => new SceneObjectPathSegment("x", -1), "a negative sibling index");
            Throws<ArgumentNullException>(() => new SceneObjectPathSegment(null!, 0), "a null name");
            Throws<ArgumentNullException>(() => new SceneObjectDescriptor(null!, new[] { new SceneObjectPathSegment("x", 0) }, "C"), "a null scene");
            Throws<ArgumentNullException>(() => new SceneObjectDescriptor("S", new[] { new SceneObjectPathSegment("x", 0) }, null!), "a null component");
            Throws<ArgumentException>(() => SceneObjectKeyCensus.Build(new SceneObjectDescriptor[] { null! }), "a null census entry");
        }

        private static void Distinct(SceneObjectDescriptor left, SceneObjectDescriptor right, string message)
        {
            True(!SceneObjectKey.Compute(left).Equals(SceneObjectKey.Compute(right)), "keys must differ: " + message);
        }

        internal static SceneObjectDescriptor Describe(string scene, string component, params (string Name, int Sibling)[] path)
        {
            var segments = new SceneObjectPathSegment[path.Length];
            for (var index = 0; index < path.Length; index++) segments[index] = new SceneObjectPathSegment(path[index].Name, path[index].Sibling);
            return new SceneObjectDescriptor(scene, segments, component);
        }
    }
}
