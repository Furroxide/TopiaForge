using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The scene object index: keys from the replication model's <see cref="SceneObjectKey"/>, every copy of a duplicated
    /// key excluded, lazy binding on first motion, and what a scene unload takes with it.
    /// </summary>
    internal static class SceneObjectIndexTests
    {
        internal static void Run()
        {
            KeysAreTheModelsAndDuplicatesAreExcluded();
            TheIndexAgreesWithTheModelsCensus();
            ObjectsBindOnlyOnFirstMotion();
            ALaterSceneCannotStealAKey();
            ASceneKeyedTwiceIsKeyedOnce();
            UnloadingASceneForgetsItsObjects();
            BindableLookupsRefuseWhatCannotBind();
        }

        private static void KeysAreTheModelsAndDuplicatesAreExcluded()
        {
            var world = new FakeWorldScene();
            var arena = world.AddScene("Arena");
            var props = world.Add(arena, "Props");
            var crate = world.Add(arena, "Crate", props, rigidbody: true);
            world.Add(arena, "Lamp", props, rigidbody: true);
            var census = Census(world);
            That(census.Count == 2, "the census finds the two rigidbody roots (" + census.Count + ")");
            var expected = SceneObjectKey.Compute(new SceneObjectDescriptor("Arena", new[] { new SceneObjectPathSegment("Props", 0), new SceneObjectPathSegment("Crate", 0) }, "UnityEngine.Rigidbody"));
            var index = new SceneObjectIndex();
            index.Add(census);
            That(index.TryGetBindable(expected, out var slot) && ReferenceEquals(slot.Object, crate), "a key is the model's scene object key of the object's path and rigidbody");
            That(index.UniqueCount == 2 && index.ExcludedCount == 0, "two unique keys");

            // The same scene loaded twice produces every key twice: none of them binds.
            var copy = world.AddScene("Arena");
            var copiedProps = world.Add(copy, "Props");
            world.Add(copy, "Crate", copiedProps, rigidbody: true);
            var twice = new SceneObjectIndex();
            twice.Add(Census(world));
            That(twice.UniqueCount == 1 && twice.ExcludedCount == 1 && !twice.TryGetBindable(expected, out _) && twice.IsExcluded(expected),
                "a key two objects produce is excluded, every copy of it (" + twice.UniqueCount + " unique, " + twice.ExcludedCount + " excluded)");
        }

        private static void ASceneKeyedTwiceIsKeyedOnce()
        {
            // A player joins with its yard loaded, and the yard's own load is reported after: the census at joining and the
            // one at the load both meet the same objects, which are the same keys, not duplicates.
            var world = new FakeWorldScene();
            var yard = world.AddScene("Yard");
            for (var box = 0; box < 4; box++) world.Add(yard, "Box", null, rigidbody: true);
            var index = new SceneObjectIndex();
            index.Add(Census(world));
            var again = new List<NetworkSceneObject>();
            world.CollectSceneObjects(yard.Handle, again);
            index.Add(again);
            index.Add(Census(world));
            That(index.UniqueCount == 4 && index.ExcludedCount == 0 && index.AlreadyIndexed == 8 && index.UnboundCount == 4,
                "a scene keyed again keeps its keys (" + index.UniqueCount + " unique, " + index.ExcludedCount + " excluded)");

            // Once unloaded, a scene with the same handle is a new load and is keyed afresh.
            index.RemoveScene(yard.Handle, null);
            index.Add(again);
            That(index.UniqueCount == 4 && index.ExcludedCount == 0, "after an unload the scene is keyed afresh");
        }

        private static void TheIndexAgreesWithTheModelsCensus()
        {
            // Seeded hierarchies with many same-named siblings and repeated scenes: the index binds exactly the keys the
            // replication model's census calls unique, and excludes exactly those it calls duplicated.
            var random = new Random(3601);
            for (var round = 0; round < 200; round++)
            {
                var world = new FakeWorldScene();
                var scenes = new[] { world.AddScene("A"), world.AddScene(random.Next(3) == 0 ? "A" : "B") };
                foreach (var scene in scenes)
                {
                    for (var root = 0; root < 1 + random.Next(4); root++) Grow(world, scene, null, random, 3);
                }

                var census = Census(world);
                var descriptors = new List<SceneObjectDescriptor>();
                world.CollectDescriptors(null, descriptors);
                var model = SceneObjectKeyCensus.Build(descriptors);
                var index = new SceneObjectIndex();
                index.Add(census);
                That(index.UniqueCount == model.UniqueCount && index.ExcludedCount == model.DuplicateKeys.Count,
                    "round " + round + ": the index's unique and excluded keys are the model's (" + index.UniqueCount + "/" + model.UniqueCount + ", " +
                    index.ExcludedCount + "/" + model.DuplicateKeys.Count + ")");
                for (var item = 0; item < census.Count; item++)
                {
                    var key = model.KeyAt(item);
                    var unique = model.TryGetObjectIndex(key, out var at) && at == item;
                    That(index.TryGetBindable(key, out var slot) == unique && (!unique || ReferenceEquals(slot.Object, census[item].Object)),
                        "round " + round + ": object " + item + " binds exactly when the model calls its key unique");
                }
            }
        }

        private static void ObjectsBindOnlyOnFirstMotion()
        {
            var world = new FakeWorldScene();
            var scene = world.AddScene("Yard");
            var objects = Enumerable.Range(0, 1200).Select(item => world.Add(scene, "Box", null, rigidbody: true, position: new NetVector3(item, 0, 0))).ToList();
            var index = new SceneObjectIndex();
            index.Add(Census(world));
            var moved = new List<SceneObjectSlot>();
            var removed = new List<SceneObjectSlot>();
            index.ScanForMotion(int.MaxValue, moved, removed);
            That(moved.Count == 0 && removed.Count == 0, "nothing that kept its census pose binds");

            objects[10].Position = new NetVector3(10f + 0.0009f, 0, 0);
            index.ScanForMotion(int.MaxValue, moved, removed);
            That(moved.Count == 0, "a move inside the position grid's step is no motion");
            objects[10].Position = new NetVector3(10.25f, 0, 0);
            objects[20].Rotation = new NetQuaternion(0f, 0.7071068f, 0f, 0.7071068f);
            objects[30].Destroy();
            index.ScanForMotion(int.MaxValue, moved, removed);
            That(moved.Select(slot => slot.Object).SequenceEqual(new[] { objects[10], objects[20] }) && removed.Single().Object == objects[30],
                "a move or a turn is first motion, and a destroyed object is reported as gone");

            // Binding takes the slot out of the scan; the scan takes turns through the rest at the slice it is given.
            moved[0].Bind(new NetworkEntityId(7));
            moved[1].Bind(new NetworkEntityId(8));
            removed[0].MarkRemoved();
            That(index.UnboundCount == 1197, "bound and removed objects leave the scan (" + index.UnboundCount + ")");
            foreach (var box in objects.Where(box => box.IsAlive)) box.Position = new NetVector3(box.Position.X, 5, 0);
            var seen = new HashSet<SceneObjectSlot>();
            for (var slice = 0; slice < 3; slice++)
            {
                moved.Clear();
                index.ScanForMotion(512, moved, removed);
                That(moved.Count <= 512, "a slice checks at most what it is given");
                foreach (var slot in moved) seen.Add(slot);
            }

            That(seen.Count == 1197, "three slices of 512 see every one of 1,197 unbound objects (" + seen.Count + ")");
            Throws<ArgumentException>(() => moved[0].Bind(NetworkEntityId.None), "a binding needs a valid id");
        }

        private static void ALaterSceneCannotStealAKey()
        {
            var world = new FakeWorldScene();
            var first = world.AddScene("Hall");
            var bound = world.Add(first, "Door", null, rigidbody: true);
            var spare = world.Add(first, "Chair", null, rigidbody: true);
            var index = new SceneObjectIndex();
            index.Add(Census(world));
            var doorKey = KeyOf("Hall", ("Door", 0));
            var chairKey = KeyOf("Hall", ("Chair", 1));
            That(index.TryGetBindable(doorKey, out var door), "the door binds");
            door.Bind(new NetworkEntityId(3));

            // An additive load of the same scene produces both keys again.
            var second = world.AddScene("Hall");
            world.Add(second, "Door", null, rigidbody: true);
            world.Add(second, "Chair", null, rigidbody: true);
            index.Add(Census(world, second.Handle));
            That(index.TryGet(doorKey, out var kept) && ReferenceEquals(kept.Object, bound) && kept.IsBound && kept.Ambiguous && !index.TryGetBindable(doorKey, out _),
                "a bound key keeps its object and binding, and binds nothing new");
            That(!index.TryGetBindable(chairKey, out _) && index.IsExcluded(chairKey), "an unbound key the second scene repeats is excluded");
            var moved = new List<SceneObjectSlot>();
            var removed = new List<SceneObjectSlot>();
            spare.Position = new NetVector3(4, 0, 0);
            index.ScanForMotion(int.MaxValue, moved, removed);
            That(moved.Count == 0, "an excluded object never binds on first motion, however it moves");
        }

        private static void UnloadingASceneForgetsItsObjects()
        {
            var world = new FakeWorldScene();
            var keep = world.AddScene("Keep");
            var drop = world.AddScene("Drop");
            world.Add(keep, "Crate", null, rigidbody: true);
            world.Add(drop, "Crate", null, rigidbody: true);
            world.Add(drop, "Barrel", null, rigidbody: true);
            var index = new SceneObjectIndex();
            index.Add(Census(world));
            That(index.TryGetBindable(KeyOf("Drop", ("Barrel", 1)), out var barrel), "the barrel is keyed");
            barrel.Bind(new NetworkEntityId(9));
            var unbound = new List<SceneObjectSlot>();
            index.RemoveScene(drop.Handle, unbound);
            That(unbound.Single() == barrel && !index.TryGet(KeyOf("Drop", ("Barrel", 1)), out _) && index.TryGetBindable(KeyOf("Keep", ("Crate", 0)), out _),
                "an unloaded scene's objects go, and its bound ones are handed back to end");
            index.Clear();
            That(index.UniqueCount == 0 && index.ExcludedCount == 0 && index.UnboundCount == 0, "clearing forgets everything");
        }

        private static void BindableLookupsRefuseWhatCannotBind()
        {
            var world = new FakeWorldScene();
            var scene = world.AddScene("Room");
            world.Add(scene, "Vase", null, rigidbody: true);
            var index = new SceneObjectIndex();
            index.Add(Census(world));
            var vase = KeyOf("Room", ("Vase", 0));
            That(index.TryGetBindable(vase, out var slot), "a unique key is bindable");
            slot.MarkRemoved();
            That(!index.TryGetBindable(vase, out _) && index.TryGet(vase, out _), "a removed object is found but not bindable");
            That(!index.TryGetBindable(new SceneObjectKey(12345), out _) && !index.TryGet(new SceneObjectKey(12345), out _), "an unknown key is neither");
            var removedKeys = new List<SceneObjectKey>();
            index.CollectRemovedKeys(removedKeys);
            That(removedKeys.SequenceEqual(new[] { vase }), "removed keys are listed for a baseline");
            Throws<ArgumentNullException>(() => index.Add(null!), "a census is required");
        }

        internal static List<NetworkSceneObject> Census(FakeWorldScene world, int? scene = null)
        {
            var census = new List<NetworkSceneObject>();
            world.CollectSceneObjects(scene, census);
            return census;
        }

        internal static SceneObjectKey KeyOf(string scene, params (string Name, int Index)[] path) =>
            SceneObjectKey.Compute(new SceneObjectDescriptor(scene, path.Select(segment => new SceneObjectPathSegment(segment.Name, segment.Index)).ToArray(), "UnityEngine.Rigidbody"));

        private static void Grow(FakeWorldScene world, FakeSceneData scene, FakeWorldObject? parent, Random random, int depth)
        {
            var names = new[] { "Crate", "Lamp", "Box", "" };
            var created = world.Add(scene, names[random.Next(names.Length)], parent, rigidbody: random.Next(3) == 0);
            if (depth == 0) return;
            for (var child = 0; child < random.Next(4); child++) Grow(world, scene, created, random, depth - 1);
        }
    }
}
