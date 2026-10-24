using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Validated <c>SetTransform</c> and <c>Destroy</c> (WS3.7): a joined player's change to an object of its own is a
    /// request the host carries out, whatever code makes it, and the result reaches everyone; a change before the verdict
    /// applies here and there; a change on the player's intent to anything else is refused here, and a scripted one is
    /// left to the host's own run of the script. An honest player's pace never meets the host's limits.
    /// </summary>
    internal static class SpawnChangeTests
    {
        internal static void Run()
        {
            APlayerMovesItsOwnObjectThroughTheHost();
            APlayerDestroysItsOwnObjectThroughTheHost();
            ScriptedCodeChangesThePlayersOwnObjectsThroughTheHostToo();
            DisposingAHandleAsksTheHostToDestroyIt();
            AChangeBeforeTheVerdictAppliesHereAndThere();
            ADestroyBeforeTheVerdictEndsItHereAtOnce();
            WhatIsNotThePlayersIsRefusedHere();
            AnHonestPlayerIsNeverRefused();
        }

        /// <summary>A rig with p1's crate adopted, and its handle.</summary>
        private static (WorldRig Rig, AuthorityMod Mine, ISpawnedEntity Handle, NetworkEntityId Id) Adopted(int players = 2)
        {
            var rig = AuthorityRig.Create(players);
            var mine = new AuthorityMod(rig.Players[0]);
            var handle = mine.SpawnAsPlayer(mine.Load(), 1).Value!;
            That(rig.Run(() => rig.Players[0].Client.OwnedObjects == 1, 60), "the player's crate is adopted");
            var id = new NetworkEntityId(AuthorityRig.Object(handle).Mark);
            That(id.IsValid, "and marked with its id");
            if (players > 1) That(rig.Run(() => rig.Players[1].Client.TryGetEntity(id, out _), 60), "the other player has it");
            return (rig, mine, handle, id);
        }

        private static FakeWorldObject HostObject(WorldRig rig, NetworkEntityId id) =>
            rig.Host.Host.TryGetHosted(id, out var hosted) ? (FakeWorldObject)hosted.Object! : throw new InvalidOperationException("The host has no " + id + ".");

        private static FakeWorldObject PlayerObject(WorldPeer player, NetworkEntityId id) =>
            player.Client.TryGetEntity(id, out var entity) ? AuthorityRig.Object(entity) : throw new InvalidOperationException(player.Name + " has no " + id + ".");

        private static void APlayerMovesItsOwnObjectThroughTheHost()
        {
            var (rig, mine, handle, id) = Adopted();
            var p2 = rig.Players[1];
            var moved = mine.AsPlayer(() => mine.Entities.SetTransform(handle, AuthorityMod.At(6, 2)));
            That(moved.Succeeded && mine.FakeEntities.Moves == 0, "the move is asked of the host, not made here (" + moved + ")");
            That(rig.Run(() => Near(HostObject(rig, id).Position, new NetVector3(6, 2, 0)), 30), "the host moves it");
            That(rig.Run(() => Near(AuthorityRig.Object(handle).Position, new NetVector3(6, 2, 0), 0.05) && Near(PlayerObject(p2, id).Position, new NetVector3(6, 2, 0), 0.05), 120),
                "and both players see it there, the mover's own handle included");
            That(rig.Host.Counters().TransformsApplied == 1 && rig.Host.Counters().ChangesRefused == 0, "one change, applied");
        }

        private static void APlayerDestroysItsOwnObjectThroughTheHost()
        {
            var (rig, mine, handle, id) = Adopted();
            var p2 = rig.Players[1];
            var destroyed = mine.AsPlayer(() => mine.Entities.Destroy(handle));
            That(destroyed.Succeeded && handle.IsAlive, "the destroy is asked of the host; the object stands until the host ends it");
            That(rig.Run(() => !handle.IsAlive && !p2.Client.Knows(id), 60), "the host ends it for everyone, and the handle dies with it");
            That(!rig.Host.Host.Knows(id) && AuthorityRig.Live(rig.Host).Count == 0 && rig.Host.Counters().DestroysApplied == 1 &&
                 rig.Host.Runtime.Spawns.Live().Count == 0, "nothing of it stands on the host, nor in its records");
            That(rig.Players[0].Client.OwnedObjects == 0 && mine.Ui.Toasts.Count == 0, "the player owns nothing now, and is told nothing");
        }

        private static void ScriptedCodeChangesThePlayersOwnObjectsThroughTheHostToo()
        {
            var (rig, mine, handle, id) = Adopted(players: 1);
            That(mine.Entities.SetTransform(handle, AuthorityMod.At(4)).Succeeded, "a scripted move of the player's own object is asked of the host");
            That(rig.Run(() => Near(HostObject(rig, id).Position, new NetVector3(4, 1, 0)), 30), "which carries it out: the host's run of the script never held it");
            That(mine.Entities.Destroy(handle).Succeeded && rig.Run(() => !handle.IsAlive, 60) && !rig.Host.Host.Knows(id), "a scripted destroy too");
        }

        private static void DisposingAHandleAsksTheHostToDestroyIt()
        {
            var (rig, _, handle, id) = Adopted();
            var p1 = rig.Players[0];
            handle.Dispose();
            That(rig.Run(() => !rig.Host.Host.Knows(id) && !rig.Players[1].Client.Knows(id), 60), "a mod disposing its own object ends it on the host and for everyone");
            That(p1.Counters().DisposalsForwarded == 1 && p1.Client.OwnedObjects == 0 && !p1.Client.Knows(id), "the player asked once, and holds nothing of it");
        }

        private static void AChangeBeforeTheVerdictAppliesHereAndThere()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();

            // The host is still loading the bundle when the move arrives: it makes the object where the player moved it.
            host.Assets.HoldLoads = true;
            var handle = mine.SpawnAsPlayer(prefab, 1).Value!;
            var moved = mine.AsPlayer(() => mine.Entities.SetTransform(handle, AuthorityMod.At(8)));
            That(moved.Succeeded && Near(AuthorityRig.Object(handle).Position, new NetVector3(8, 1, 0)), "a move before the verdict applies here at once");
            rig.Frames(6);
            host.Assets.HoldLoads = false;
            host.Assets.CompleteLoads();
            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60), "the spawn is adopted");
            var id = new NetworkEntityId(AuthorityRig.Object(handle).Mark);
            That(Near(HostObject(rig, id).Position, new NetVector3(8, 1, 0)) && Near(AuthorityRig.Object(handle).Position, new NetVector3(8, 1, 0), 0.05),
                "the host made it where the player moved it");

            // Made already, it moves there.
            var second = mine.SpawnAsPlayer(prefab, 2).Value!;
            mine.AsPlayer(() => mine.Entities.SetTransform(second, AuthorityMod.At(9)));
            That(rig.Run(() => p1.Client.OwnedObjects == 2, 60), "the second is adopted");
            var other = new NetworkEntityId(AuthorityRig.Object(second).Mark);
            That(rig.Run(() => Near(HostObject(rig, other).Position, new NetVector3(9, 1, 0)), 30) &&
                 rig.Run(() => Near(AuthorityRig.Object(second).Position, new NetVector3(9, 1, 0), 0.05), 120), "and one made before the move arrives moves there");
        }

        private static void ADestroyBeforeTheVerdictEndsItHereAtOnce()
        {
            var rig = AuthorityRig.Create(players: 2);
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            var handle = mine.SpawnAsPlayer(prefab, 1).Value!;
            That(mine.AsPlayer(() => mine.Entities.Destroy(handle)).Succeeded && !handle.IsAlive, "a destroy before the verdict ends it here at once");
            rig.Frames(20);
            That(AuthorityRig.Live(rig.Host).Count == 0 && AuthorityRig.Live(rig.Players[1]).Count == 0 && AuthorityRig.Live(p1).Count == 0,
                "and the host ends what it made of it, so nobody sees it");
            That(p1.Client.PendingPredictions == 0 && p1.Client.OwnedObjects == 0 && mine.Ui.Toasts.Count == 0, "nothing waits, and nothing is said");

            // One still queued behind the player's pace was never sent: nothing at all is asked of the host for it.
            rig.Clock.Advance(2_000_000);
            var sent = p1.Counters().AuthorityRequestsSent;
            var batch = Enumerable.Range(0, 5).Select(index => mine.SpawnAsPlayer(prefab, 10 + index).Value!).ToList();
            That(p1.Counters().AuthorityRequestsSent == sent + 4 && p1.Client.PendingPredictions == 5, "four requests leave at once, and the fifth waits its turn");
            That(mine.AsPlayer(() => mine.Entities.Destroy(batch[4])).Succeeded && !batch[4].IsAlive, "the fifth ends here at once");
            That(rig.Run(() => p1.Client.OwnedObjects == 4, 120) && p1.Counters().AuthorityRequestsSent == sent + 4 && AuthorityRig.Live(rig.Host).Count == 4,
                "and was never asked for: the host made the four");
        }

        private static void WhatIsNotThePlayersIsRefusedHere()
        {
            var rig = AuthorityRig.Create(players: 2, boxes: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var p2 = rig.Players[1];
            var theirs = new AuthorityMod(p2);
            var hosts = new AuthorityMod(host);
            var other = theirs.SpawnAsPlayer(theirs.Load(), 5).Value!;
            var crate = hosts.Spawn(hosts.Load(), 7).Value!;

            // The yard's box moves on the host, so it is one of the host's entities too.
            var hostBox = host.Scene.Scenes[0].Roots[0];
            hostBox.Position = new NetVector3(0, 2, 0);
            That(rig.Run(() => p2.Client.OwnedObjects == 1 && p1.Client.EntityCount == 3, 60), "p1 sees the yard's box, p2's crate and the host's (" + rig.Describe(p1) + ")");
            var otherId = new NetworkEntityId(AuthorityRig.Object(other).Mark);
            var crateId = host.Scene.TryGetNetworkId(crate, out var hostId) ? hostId : NetworkEntityId.None;
            var othersPresent = p1.Client.TryGetEntity(otherId, out var othersObject);
            var hostsPresent = p1.Client.TryGetEntity(crateId, out var hostsObject);
            That(othersPresent && hostsPresent, "both crates are present on p1");
            var box = p1.Scene.Scenes[0].Roots[0];
            That(box.Mark != 0, "and the yard's box is bound to the host's");

            var mine = new AuthorityMod(p1);
            var sent = p1.Counters().AuthorityRequestsSent;
            foreach (var target in new[] { othersObject, hostsObject, box.Entity })
            {
                var move = mine.AsPlayer(() => mine.Entities.SetTransform(target, AuthorityMod.At(30)));
                var destroy = mine.AsPlayer(() => mine.Entities.Destroy(target));
                That(!move.Succeeded && move.ErrorCode == ModErrorCode.NotAuthoritative && move.ErrorMessage == NetworkWorldClient.NotYoursToMove,
                    "the player may not move " + target.Name + " (" + move + ")");
                That(!destroy.Succeeded && destroy.ErrorCode == ModErrorCode.NotAuthoritative && destroy.ErrorMessage == NetworkWorldClient.NotYoursToDestroy,
                    "nor destroy it (" + destroy + ")");

                // Scripted, the call is the host's script's: its own run of it changes the object there.
                That(mine.Entities.SetTransform(target, AuthorityMod.At(30)).Succeeded && mine.Entities.Destroy(target).Succeeded && target.IsAlive,
                    "a scripted change of " + target.Name + " leaves it to the host's run of the script");
            }

            That(p1.Counters().AuthorityRequestsSent == sent && p1.Counters().RequestsRefusedHere == 6 && mine.FakeEntities.Moves == 0 && mine.FakeEntities.Destroys == 0,
                "nothing was sent, and nothing changed here");
            rig.Frames(10);
            That(other.IsAlive && crate.IsAlive && Near(HostObject(rig, otherId).Position, new NetVector3(5, 1, 0)) && Near(HostObject(rig, crateId).Position, new NetVector3(7, 1, 0)) &&
                 hostBox.IsAlive && Near(hostBox.Position, new NetVector3(0, 2, 0)), "nor on the host");

            // An entity the network world does not hold is the game's, as offline.
            var loose = p1.Scene.Add(p1.Scene.AddScene("Loose"), "Loose").Entity;
            That(mine.AsPlayer(() => mine.Entities.SetTransform(loose, AuthorityMod.At(3))).Succeeded && mine.FakeEntities.Moves == 1, "something of the player's own game moves as offline");
        }

        private static void AnHonestPlayerIsNeverRefused()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            var handles = Enumerable.Range(0, 20).Select(index => mine.SpawnAsPlayer(prefab, index).Value!).ToList();
            That(rig.Run(() => p1.Client.OwnedObjects == 20, 600), "twenty spawns in one go are all adopted (" + p1.Client.OwnedObjects + ")");
            That(host.Counters().SpawnsRefused == 0 && p1.Counters().PredictionsRefused == 0 && mine.Ui.Toasts.Count == 0, "none was refused: the player kept to the host's pace");

            // A hundred moves of each in a burst coalesce, and none meets the host's limit.
            for (var step = 0; step < 100; step++)
            {
                foreach (var handle in handles) mine.AsPlayer(() => mine.Entities.SetTransform(handle, AuthorityMod.At(step, 3)));
            }

            var last = new NetworkEntityId(AuthorityRig.Object(handles[19]).Mark);
            That(rig.Run(() => Near(HostObject(rig, last).Position, new NetVector3(99, 3, 0)), 600), "each object ends where it was last moved");
            That(host.Counters().ChangesRefused == 0 && host.Counters().TransformsApplied <= 60, "no change was refused, and each object's moves were coalesced (" +
                host.Counters().TransformsApplied + " applied)");
            foreach (var handle in handles) mine.AsPlayer(() => mine.Entities.Destroy(handle));
            That(rig.Run(() => AuthorityRig.Live(host).Count == 0, 600) && host.Counters().ChangesRefused == 0, "and twenty destroys in one go all pass");
        }
    }
}
