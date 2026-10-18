using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using static TopiaForge.ModManager.Tests.WorldAssert;
using static TopiaForge.ModManager.Tests.WorldBaselineSyncTests;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Scene keys are taken as each scene loads and kept for the scene's lifetime, so what the hierarchy goes through
    /// before a session needs them changes no key: a sibling destroyed or moved to another parent, or a renamed object.
    /// A joining player keys the scene the same way as it loads it fresh, so both name every object alike. Duplicates
    /// are still excluded, additive scenes are keyed as they load, an object made after its scene loaded is no scene
    /// object, and the keys cost the load alone.
    /// </summary>
    internal static class SceneKeyCaptureTests
    {
        internal static void Run()
        {
            TheHasherKeysExactlyAsTheModel();
            ASiblingDestroyedBeforeTheGameOpenedShiftsNoKey();
            AnObjectMovedToAnotherParentOrRenamedKeepsItsKey();
            DuplicatesAreStillExcluded();
            AnAdditiveSceneIsKeyedAsItLoads();
            ObjectsMadeAfterTheLoadAreNoSceneObjects();
            KeysAreTakenOncePerLoadAndForgottenWithTheScene();
            KeysCostTheLoadAlone();
            MarksAreCountedSoALoadSkipsTheirSearch();
        }

        private static void TheHasherKeysExactlyAsTheModel()
        {
            // Names with every escaped character, wide characters, surrogate pairs and lone surrogates at either end.
            var backslash = ((char)92).ToString();
            var nul = ((char)0).ToString();
            var high = ((char)0xD800).ToString();
            var low = ((char)0xDC00).ToString();
            var alphabet = new[]
            {
                "a", "Box", "/", "#", backslash, nul, " ", "(1)", ((char)0xE9).ToString(), ((char)0x65E5).ToString() + ((char)0x672C).ToString(),
                char.ConvertFromUtf32(0x1F600), high, low, "x" + high, low + "y", string.Empty
            };
            var random = new Random(3606);
            var hasher = new SceneKeyHasher();
            for (var round = 0; round < 3000; round++)
            {
                var scene = Name(random, alphabet);
                var depth = 1 + random.Next(6);
                var segments = new SceneObjectPathSegment[depth];
                var state = hasher.Scene(scene);
                for (var level = 0; level < depth; level++)
                {
                    var name = Name(random, alphabet);
                    var index = random.Next(4) == 0 ? random.Next(int.MaxValue) : random.Next(12);
                    segments[level] = new SceneObjectPathSegment(name, index);
                    state = hasher.Segment(state, name, index, level == 0);
                }

                var component = random.Next(4) == 0 ? Name(random, alphabet) : "UnityEngine.Rigidbody";
                var expected = SceneObjectKey.Compute(new SceneObjectDescriptor(scene, segments, component));
                That(hasher.Finish(state, component).Equals(expected),
                    "round " + round + ": the hasher keys a path exactly as the model does (" + SceneObjectKey.FormatPreimage(new SceneObjectDescriptor(scene, segments, component)).Length + " characters)");
            }

            // Two objects under one parent continue from the parent's state, and the largest sibling index is written whole.
            var parent = hasher.Segment(hasher.Scene("Yard"), "Pile", 3, true);
            var first = hasher.Finish(hasher.Segment(parent, "Box", 0, false), "UnityEngine.Rigidbody");
            var second = hasher.Finish(hasher.Segment(parent, "Box", int.MaxValue, false), "UnityEngine.Rigidbody");
            That(first.Equals(SceneObjectIndexTests.KeyOf("Yard", ("Pile", 3), ("Box", 0))) && second.Equals(SceneObjectIndexTests.KeyOf("Yard", ("Pile", 3), ("Box", int.MaxValue))),
                "a shared parent's state serves each child");
        }

        private static void ASiblingDestroyedBeforeTheGameOpenedShiftsNoKey()
        {
            var rig = Yard(6);
            var host = rig.Host;
            var boxes = host.Scene.Scenes[0].Roots.ToArray();

            // Single player: box 1 is destroyed, and the engine takes it out of the hierarchy, so boxes 2 to 5 each move up a
            // place. Keyed at promotion, box 3 would take box 2's key, and so on down the row.
            boxes[1].Remove();
            rig.OpenToMultiplayer();

            // Hosting: box 3 is pushed, and binds by its own key.
            boxes[3].Position = new NetVector3(30, 1, 1);
            rig.Frames(10);
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            var mine = player.Scene.Scenes[0].Roots;
            That(!mine[1].Active, "the joining player hides its copy of the box the host destroyed");
            That(mine[3].Mark == boxes[3].Mark && mine[3].Mark != 0 && Near(mine[3].Position, boxes[3].Position),
                "the pushed box binds to the player's own box 3, not to box 2, which took its place in the host's hierarchy");
            That(mine[2].Mark == 0 && mine[2].Active && Near(mine[2].Position, new NetVector3(4, 0, 0)), "box 2 stands as the yard loaded it");
            That(new[] { 0, 4, 5 }.All(index => mine[index].Mark == 0 && mine[index].Active && Near(mine[index].Position, new NetVector3(index * 2, 0, 0))),
                "and so do the others");
            That(SameWorld(host, player) && player.World.Counters.SceneKeysUnbound == 0, "every key the host named is one the player has");
            That(host.Host.Index.GoneWhenIndexed == 1, "the host's census had the destroyed box, by the key it took as the yard loaded");
        }

        private static void AnObjectMovedToAnotherParentOrRenamedKeepsItsKey()
        {
            var rig = new WorldRig();
            rig.UseScene(scene =>
            {
                var yard = scene.AddScene("Yard");
                for (var index = 0; index < 4; index++) scene.Add(yard, "Box", null, rigidbody: true, position: new NetVector3(index * 2, 0, 0));
                scene.Add(yard, "Shelf", null, position: new NetVector3(0, 3, 0));
            });
            var host = rig.Host;
            var roots = host.Scene.Scenes[0].Roots.ToArray();

            // Single player: box 1 goes onto the shelf, which moves boxes 2 and 3 and the shelf up a place, and box 2 is renamed.
            roots[1].Reparent(roots[4]);
            roots[1].Position = new NetVector3(0, 3.5f, 0);
            roots[2].Rename("Crate");
            rig.OpenToMultiplayer();
            roots[1].Position = new NetVector3(1, 3.5f, 0);
            roots[2].Position = new NetVector3(5, 1, 0);
            rig.Frames(10);

            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            var mine = player.Scene.Scenes[0].Roots;
            That(mine[1].Mark == roots[1].Mark && mine[1].Mark != 0 && Near(mine[1].Position, roots[1].Position),
                "the box moved onto the shelf keeps its key: the player's box 1 follows it");
            That(mine[2].Mark == roots[2].Mark && mine[2].Mark != 0 && Near(mine[2].Position, roots[2].Position), "and so does the renamed box");
            That(mine[3].Mark == 0 && Near(mine[3].Position, new NetVector3(6, 0, 0)) && player.World.Counters.SceneKeysUnbound == 0,
                "box 3 is untouched, and every key the host named was found");
        }

        private static void DuplicatesAreStillExcluded()
        {
            // The same scene loaded twice, additively: every key comes twice, and no copy binds.
            var rig = new WorldRig();
            rig.UseScene(scene =>
            {
                for (var copy = 0; copy < 2; copy++)
                {
                    var yard = scene.AddScene("Yard");
                    for (var index = 0; index < 3; index++) scene.Add(yard, "Box", null, rigidbody: true, position: new NetVector3(index * 2, 0, copy * 10));
                }
            });
            var host = rig.Host;
            rig.OpenToMultiplayer();
            var first = host.Scene.Scenes[0].Roots;
            first[0].Position = new NetVector3(9, 9, 9);
            rig.Frames(10);
            That(host.Host.Index.ExcludedCount == 3 && host.Host.Index.UniqueCount == 0 && first[0].Mark == 0,
                "a key both copies produce is excluded, every copy of it, so a box of either never binds");
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            That(player.Client.Index.ExcludedCount == 3 && player.Scene.Scenes.All(scene => scene.Roots.All(box => box.Mark == 0)),
                "and the joining player excludes them alike");
        }

        private static void AnAdditiveSceneIsKeyedAsItLoads()
        {
            var rig = Yard(2);
            var host = rig.Host;

            // Single player: an annex loads additively, and one of its boxes is destroyed before the game opens.
            var annex = Boxes(host.Scene, "Annex", 4, 20);
            host.LoadScenes();
            annex[0].Remove();
            rig.OpenToMultiplayer();

            // Hosting: a hall loads too, one of its boxes is destroyed, and a box in each scene moves.
            var hall = Boxes(host.Scene, "Hall", 4, 40);
            host.LoadScenes();
            hall[0].Remove();
            annex[2].Position = new NetVector3(4, 2, 21);
            hall[2].Position = new NetVector3(4, 2, 41);
            rig.Frames(10);

            var yard = rig.BuildScene!;
            rig.BuildScene = scene =>
            {
                yard(scene);
                Boxes(scene, "Annex", 4, 20);
                Boxes(scene, "Hall", 4, 40);
            };
            var player = AddPlayer(rig, "p1");
            rig.JoinAndSynchronize(player);
            var theirAnnex = player.Scene.Scenes[1].Roots;
            var theirHall = player.Scene.Scenes[2].Roots;
            That(!theirAnnex[0].Active && !theirHall[0].Active, "the boxes the host destroyed, before the game opened and after, are hidden");
            That(theirAnnex[2].Mark == annex[2].Mark && Near(theirAnnex[2].Position, annex[2].Position) &&
                 theirHall[2].Mark == hall[2].Mark && Near(theirHall[2].Position, hall[2].Position),
                "and the moved boxes bind to their own copies in each scene");
            That(new[] { 1, 3 }.All(index => theirAnnex[index].Mark == 0 && theirHall[index].Mark == 0 && theirAnnex[index].Active && theirHall[index].Active),
                "while their siblings stand as they loaded");

            // A scene the host unloads takes its keys with it, on both sides.
            host.UnloadScene(host.Scene.Scenes[2]);
            player.UnloadScene(player.Scene.Scenes[2]);
            rig.Frames(5);
            That(!host.World.SceneKeys.TryGet(hall[0].Scene.Handle, out _) && SameWorld(host, player), "and both sides end the hall's entities");
        }

        private static void ObjectsMadeAfterTheLoadAreNoSceneObjects()
        {
            var rig = Yard(2);
            var host = rig.Host;
            var late = host.Scene.Add(host.Scene.Scenes[0], "Box", null, rigidbody: true, position: new NetVector3(30, 0, 0));
            rig.OpenToMultiplayer();
            late.Position = new NetVector3(31, 0, 0);
            rig.Frames(10);
            That(host.Host.Index.UniqueCount == 2 && late.Mark == 0, "an object made after its scene loaded has no key, and moving it binds nothing");
        }

        private static void KeysAreTakenOncePerLoadAndForgottenWithTheScene()
        {
            var rig = Yard(3);
            var host = rig.Host;
            var keys = host.World.SceneKeys;
            var yard = host.Scene.Scenes[0];
            That(keys.SceneCount == 1 && keys.Captures == 1 && keys.TryGet(yard.Handle, out var captured) && captured.Count == 3 && keys.LastObjects == 3,
                "the yard was keyed as it loaded");
            var censuses = host.Scene.Censuses;
            yard.Roots[0].Remove();
            host.World.SceneLoaded(yard.Handle);
            That(keys.Captures == 1 && host.Scene.Censuses == censuses && keys.TryGet(yard.Handle, out var again) && again.Count == 3,
                "a scene is keyed once per load: a load reported again keeps the keys it took");
            host.UnloadScene(yard);
            That(keys.SceneCount == 0 && !keys.TryGet(yard.Handle, out _), "unloading the scene forgets them");

            // A load the engine side cannot key keeps nothing, and a session keys the scene when it first needs it.
            var hall = Boxes(host.Scene, "Hall", 2, 0);
            host.Scene.FailSceneCensus = new InvalidOperationException("the engine side failed");
            Throws<InvalidOperationException>(() => host.LoadScenes(), "the failure goes on to the runtime, which reports it");
            That(keys.Failures == 1 && !keys.TryGet(hall[0].Scene.Handle, out _), "and nothing is kept");
            host.Scene.FailSceneCensus = null;
            rig.OpenToMultiplayer();
            That(host.World.Counters.ScenesKeyedLate == 1 && keys.TryGet(hall[0].Scene.Handle, out var lateKeys) && lateKeys.Count == 2 && host.Host.Index.UniqueCount == 2,
                "the promotion keys it late, from the hierarchy as it is then, and keeps those keys");
        }

        private static void KeysCostTheLoadAlone()
        {
            // An offline game with a large keyed scene: frames pay nothing for the keys.
            var rig = Yard(500);
            var host = rig.Host;
            That(host.World.SceneKeys.LastObjects == 500 && host.Runtime.Realm == NetRealm.Offline, "the yard's 500 boxes were keyed as it loaded, offline");
            var frames = Allocated(() => host.Frame(), 2_000);
            That(frames == 0, "an offline frame with a keyed scene allocates nothing (" + frames + " bytes)");
        }

        /// <summary>
        /// The engine's census looks for network marks only while one may carry an id, which offline none does: each mark
        /// counts itself in when it is given an id and out when the id goes, once whatever its id becomes, and never below
        /// none.
        /// </summary>
        private static void MarksAreCountedSoALoadSkipsTheirSearch()
        {
            var start = NetworkMarks.Live;
            That(start == 0 && !NetworkMarks.AnyLive, "no test engine marks anything, so the count starts empty");
            NetworkMarks.Changed(0, 0);
            That(NetworkMarks.Live == 0, "a mark without an id counts nothing");
            NetworkMarks.Changed(0, 5);
            NetworkMarks.Changed(5, 9);
            NetworkMarks.Changed(0, 3);
            That(NetworkMarks.Live == 2 && NetworkMarks.AnyLive, "a mark given an id counts once, however its id changes after");
            NetworkMarks.Changed(9, 0);
            NetworkMarks.Changed(3, 0);
            That(NetworkMarks.Live == 0 && !NetworkMarks.AnyLive, "and counts out when its id goes, so a load skips the search again");
            NetworkMarks.Changed(4, 0);
            That(NetworkMarks.Live == 0, "and the count never goes below none");
        }

        /// <summary>Adds a scene of <paramref name="count"/> boxes in a row at depth <paramref name="z"/>, and returns them.</summary>
        private static FakeWorldObject[] Boxes(FakeWorldScene world, string name, int count, float z)
        {
            var scene = world.AddScene(name);
            for (var index = 0; index < count; index++) world.Add(scene, "Box", null, rigidbody: true, position: new NetVector3(index * 2, 0, z));
            return scene.Roots.ToArray();
        }

        private static string Name(Random random, string[] alphabet)
        {
            var parts = random.Next(4);
            var name = string.Empty;
            for (var part = 0; part < parts; part++) name += alphabet[random.Next(alphabet.Length)];
            return name;
        }
    }
}
