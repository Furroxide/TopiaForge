using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Shadows (WS3.7): a joined player's run of a scripted spawn is a local no-op bound to the host's spawn with the same
    /// <c>(mod, number)</c>, so nothing is made twice, whichever comes first. One whose host spawn comes for another asset
    /// ends and the host's is built; one whose host spawn never comes ends after its wait; neither is ever recorded. The
    /// numbering counts only spawns a joined player shadows, and starts over when the player joins.
    /// </summary>
    internal static class SpawnShadowTests
    {
        internal static void Run()
        {
            AShadowBindsToTheHostsSpawnAlreadyHere();
            AShadowWaitsForTheHostsSpawn();
            AShadowOfAnotherAssetEndsAndTheHostsIsBuilt();
            AShadowWhoseSpawnNeverComesEnds();
            OnlyShadowedSpawnsAreNumbered();
            WhatTheHostMakesForAPlayerTakesNoNumber();
            TheNumberingStartsOverWhenThePlayerJoins();
            AKeyGivenAgainReachesAPlayerThatMissedTheOldOnesEnd();
            AKeyALiveEntityHoldsIsGivenToNoOther();
            AProvidersScriptedSpawnsShadowToo();
            ScriptedChangesLeaveTheHostsObjectsToTheHost();
        }

        private static void AShadowBindsToTheHostsSpawnAlreadyHere()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var hosts = new AuthorityMod(host);
            var prefab = hosts.Load();
            hosts.Spawn(prefab, 1);
            hosts.Spawn(prefab, 2);
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 2, 60), "the player builds the host's two crates");
            var keys = host.Runtime.Spawns.Live().Select(record => record.ShadowKey).OrderBy(key => key).ToArray();
            That(keys.SequenceEqual(new long[] { 1, 2 }), "the host numbered its scripted spawns (" + string.Join(",", keys) + ")");

            // The player's own run of the same script makes the same two: each stands for the host's in place of its replica.
            var mine = new AuthorityMod(p1);
            var mineFirst = mine.Spawn(mine.Load(), 1).Value!;
            var mineSecond = mine.Spawn(mine.Load(), 2).Value!;
            That(mineFirst.IsAlive && mineSecond.IsAlive && p1.Counters().ShadowsBound == 2 && p1.Client.WaitingShadows == 0, "both shadows bind at once");
            That(AuthorityRig.Live(p1).Count == 2, "nothing is duplicated: the replicas gave way (" + AuthorityRig.Live(p1).Count + " crates)");
            var id = new NetworkEntityId(AuthorityRig.Object(mineFirst).Mark);
            That(id.IsValid && p1.Client.BindingOf(id) == ClientEntityBinding.Shadow && p1.Client.TryGetEntity(id, out var found) && found.Id == mineFirst.Id,
                "the mod's own handle stands for the host's crate, under its id");

            // The host's crate moves; the player's handle follows it.
            That(host.Host.TryGetHosted(id, out var hosted), "the host has the crate");
            ((FakeWorldObject)hosted.Object!).Position = new NetVector3(1, 5, 0);
            That(rig.Run(() => Near(AuthorityRig.Object(mineFirst).Position, new NetVector3(1, 5, 0), 0.05), 120), "and follows the host's crate");
            That(host.Counters().SpawnRequests == 0 && p1.Counters().AuthorityRequestsSent == 0, "a shadow is never sent");
        }

        private static void AShadowWaitsForTheHostsSpawn()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var shadow = mine.Spawn(mine.Load(), 3).Value!;
            That(shadow.IsAlive && AuthorityRig.Object(shadow).Kinematic && p1.Client.WaitingShadows == 1, "the player's run comes first: its shadow waits, frozen");
            rig.Frames(30);
            var hosts = new AuthorityMod(host);
            hosts.Spawn(hosts.Load(), 3);
            That(rig.Run(() => p1.Counters().ShadowsBound == 1, 60) && shadow.IsAlive && p1.Client.WaitingShadows == 0, "the host's spawn arrives and the shadow stands for it");
            That(p1.Assets.Spawned.Count == 1 && AuthorityRig.Live(p1).Count == 1, "no replica was built");
        }

        private static void AShadowOfAnotherAssetEndsAndTheHostsIsBuilt()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var hosts = new AuthorityMod(host);

            // Shadow first: the host's spawn with its number is a crate, the player's a barrel.
            var barrel = mine.Spawn(mine.Load("Barrel"), 1).Value!;
            hosts.Spawn(hosts.Load(), 1);
            That(rig.Run(() => !barrel.IsAlive, 60) && rig.Run(() => AuthorityRig.Live(p1).Count == 1, 60) && AuthorityRig.Live(p1, "Barrel").Count == 0,
                "the barrel ends, and the host's crate is built");
            That(p1.Counters().ShadowsMismatched == 1 && p1.Client.WaitingShadows == 0, "counted as a mismatch");

            // Host first: the player's barrel with the next number ends at once.
            hosts.Spawn(hosts.Load(), 2);
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 2, 60), "the host's second crate is built");
            var late = mine.Spawn(mine.Load("Barrel"), 2).Value!;
            That(!late.IsAlive && p1.Counters().ShadowsMismatched == 2 && AuthorityRig.Live(p1).Count == 2, "a barrel made after the host's crate ends at once");
        }

        private static void AShadowWhoseSpawnNeverComesEnds()
        {
            var rig = AuthorityRig.Create(players: 1);
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var shadow = mine.Spawn(mine.Load(), 1).Value!;
            rig.Clock.Advance(SpawnAuthorityRules.ShadowWaitMicros - 500_000);
            rig.Frames(2);
            That(shadow.IsAlive && p1.Client.WaitingShadows == 1, "a shadow waits for the host's spawn");
            rig.Clock.Advance(600_000);
            rig.Frames(2);
            That(!shadow.IsAlive && p1.Client.WaitingShadows == 0 && p1.Counters().ShadowsExpired == 1 && AuthorityRig.Live(p1).Count == 0,
                "the host never made it: the shadow ends, so the player sees the host's world");

            // The wait counts only while the player's world is in place: a shadow made while it rebaselines waits for it.
            var waiting = mine.Spawn(mine.Load(), 2).Value!;
            p1.Paused = true;
            rig.Network.Rejoin(p1.Control);
            rig.Clock.Advance(SpawnAuthorityRules.ShadowWaitMicros * 2);
            p1.Paused = false;
            p1.Frame();
            That(waiting.IsAlive, "while the world is out of step, the wait does not run (" + p1.World.Progress + ")");
            That(rig.Run(() => !waiting.IsAlive, 1_000), "it runs again once the world is in place, and ends");
        }

        private static void OnlyShadowedSpawnsAreNumbered()
        {
            var rig = AuthorityRig.Create(players: 2);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var p2 = rig.Players[1];
            var hosts = new AuthorityMod(host);
            var gamemode = new AuthorityMod(host, ModExecutionContext.GamemodeServer);
            var prefab = hosts.Load();
            var mine = new AuthorityMod(p1);

            // The host's own player's action, a gamemode's script, and a player's adopted spawn take no number.
            hosts.SpawnAsPlayer(prefab, 1);
            gamemode.Spawn(gamemode.Load(), 2);
            mine.SpawnAsPlayer(mine.Load(), 3);
            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60), "the player's spawn is adopted");
            var scripted = hosts.Spawn(prefab, 4).Value!;
            That(host.Runtime.Spawns.Live().Select(record => record.ShadowKey).Where(key => key != 0).SequenceEqual(new long[] { 1 }),
                "only the host's scripted spawn took a number, and it is the first");
            That(host.Runtime.Spawns.Find(scripted)!.ShadowKey == 1, "the scripted crate is number one");

            // So the other player's run of the same script binds to the scripted crate, never to another.
            That(rig.Run(() => p2.Client.EntityCount == 4, 60), "the other player mirrors all four");
            var theirs = new AuthorityMod(p2);
            var shadow = theirs.Spawn(theirs.Load(), 4).Value!;
            var bound = new NetworkEntityId(AuthorityRig.Object(shadow).Mark);
            That(shadow.IsAlive && host.Scene.TryGetNetworkId(scripted, out var scriptedId) && bound.Equals(scriptedId), "and its shadow stands for the scripted crate");
        }

        private static void WhatTheHostMakesForAPlayerTakesNoNumber()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var hostSeam = host.Seam();
            var handler = new RecordingSpawnHandler(host.Scene, () => hostSeam) { RequestsThroughSeam = true };
            hostSeam.RegisterSpawnHandler("tests.box", handler);

            // A participant context's own code forwards with no intent, and the host's handler records what it makes itself.
            var request = new WorldWireWriter();
            AuthorityWire.WriteSpawnRequest(request, 1, WorldPeer.ModId, "tests.box", IntentSource.None, false, new NetVector3(1, 1, 1),
                new NetPose(new NetVector3(2, 1, 0), NetQuaternion.Identity), new byte[] { 1 });
            SpawnAbuseTests.Inject(rig, p1, request.Segment.ToArray());
            rig.Frames(3);
            That(handler.SpawnedFor.Count == 1 && handler.SpawnedFor[0].Source == InternalIntent.None, "the host's handler ran, with no intent to forward");
            That(host.World.Proxies.TryGetProxy(p1.Control.Id, out var proxy), "the host keeps the player's scope");
            var record = host.Runtime.Spawns.Live().Single();
            That(record.ShadowKey == 0 && record.OwnerSlot == proxy.Slot, "what the host made for the player is the player's, with no number");
            var hosts = new AuthorityMod(host);
            var scripted = hosts.Spawn(hosts.Load(), 2).Value!;
            That(host.Runtime.Spawns.Find(scripted)!.ShadowKey == 1, "and the host's script still numbers from one");
        }

        private static void TheNumberingStartsOverWhenThePlayerJoins()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var starts = p1.Runtime.Spawns.ShadowKeys.Starts;
            var hosts = new AuthorityMod(host);
            hosts.Spawn(hosts.Load(), 1);
            var mine = new AuthorityMod(p1);
            mine.Spawn(mine.Load(), 1);
            That(rig.Run(() => p1.Counters().ShadowsBound == 1, 60), "the player's first shadow binds");

            p1.Control.Leave();
            rig.Frames(4);
            rig.JoinAndSynchronize(p1);
            That(p1.Runtime.Spawns.ShadowKeys.Starts == starts + 1, "joining again starts the player's numbering over");
            var again = new AuthorityMod(p1);
            var shadow = again.Spawn(again.Load(), 1).Value!;
            That(shadow.IsAlive && p1.Counters().ShadowsBound == 2, "so its run of the world's script binds to the host's first crate again");

            // A host's own world session start starts its numbering over, as the session orchestrator says when it prepares one.
            host.Runtime.Spawns.BeginWorldSession();
            var next = hosts.Spawn(hosts.Load(), 5).Value!;
            That(host.Runtime.Spawns.Find(next)!.ShadowKey == 1, "a world session numbers from one");
            That(rig.Run(() => host.Counters().ShadowKeysReused == 1, 10), "and a number the old session's crate still holds is announced with none, so no shadow binds to the wrong one");
        }

        private static void AKeyGivenAgainReachesAPlayerThatMissedTheOldOnesEnd()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var hosts = new AuthorityMod(host);
            var prefab = hosts.Load();
            var old = hosts.Spawn(prefab, 1).Value!;
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 1, 60), "the player mirrors the host's scripted crate");
            var oldId = new NetworkEntityId(AuthorityRig.Object(old).Mark);

            // The player's link stalls while the host's world session starts over: the crate ends, and the new session's first
            // scripted crate takes its key, number one, again.
            p1.Paused = true;
            old.Dispose();
            rig.Frames(10);
            host.Runtime.Spawns.BeginWorldSession();
            var renewed = hosts.Spawn(prefab, 2).Value!;
            rig.Frames(10);
            var renewedId = new NetworkEntityId(AuthorityRig.Object(renewed).Mark);
            That(host.Runtime.Spawns.Find(renewed)!.ShadowKey == 1 && !host.Host.Knows(oldId), "the host gives the new crate the old one's key");

            // The link comes back within its grace: the old crate's despawn went with the old stream, and the new baseline
            // lists the new crate while the player still holds the old one under that key.
            rig.Network.Rejoin(p1.Control);
            p1.Paused = false;
            That(rig.Run(() => p1.World.Progress.Phase == NetworkWorldPhase.Synchronized && p1.Client.Knows(renewedId), 300),
                "the player mirrors the new crate (" + p1.World.Progress + ", " + p1.Counters().EntitiesRefused + " refused)");
            That(!p1.Client.Knows(oldId) && AuthorityRig.Live(p1).Count == 1 && p1.Counters().EntitiesRefused == 0, "in place of the old one, which it no longer holds");

            // Its own run of the new session's script binds to the new crate.
            p1.Runtime.Spawns.BeginWorldSession();
            var mine = new AuthorityMod(p1);
            var shadow = mine.Spawn(mine.Load(), 2).Value!;
            That(shadow.IsAlive && new NetworkEntityId(AuthorityRig.Object(shadow).Mark).Equals(renewedId) && AuthorityRig.Live(p1).Count == 1,
                "and the player's own run of the new session's script stands for it");
        }

        private static void AKeyALiveEntityHoldsIsGivenToNoOther()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var hosts = new AuthorityMod(host);
            var crate = hosts.Spawn(hosts.Load(), 1).Value!;
            var key = host.Runtime.Spawns.Find(crate)!.ShadowKey;
            That(key > 0 && rig.Run(() => AuthorityRig.Live(p1).Count == 1, 60), "the player mirrors the host's scripted crate, which holds a shadow key");
            var id = new NetworkEntityId(AuthorityRig.Object(crate).Mark);

            // Outside any baseline, a host announces another entity under the key the live crate holds.
            var other = new NetworkEntityId(id.Value + 100);
            var writer = new WorldWireWriter();
            writer.Begin(WorldMessages.Spawn);
            writer.WriteVarUInt(1);
            WorldEntryCodec.WriteSpawn(writer, other, MultiplayerSpawnKinds.AssetPrefab, WorldPeer.ModId, key, 0, new NetVector3(1, 1, 1),
                new NetPose(new NetVector3(5, 1, 0), NetQuaternion.Identity), WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Create(AuthorityMod.Bundle, "Crate")));
            p1.Control.Channel(InternalSystemChannelKind.WorldReplication)!.Inject(rig.Network.Host.Id, writer.Segment.ToArray());
            rig.Frames(3);
            That(p1.Client.Knows(id) && !p1.Client.Knows(other) && p1.Counters().EntitiesRefused == 1 && AuthorityRig.Live(p1).Count == 1,
                "the live crate keeps its key and the other is refused: only a holder the baseline under way has not listed gives its key up");
        }

        private static void AProvidersScriptedSpawnsShadowToo()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            p1.Seam().RegisterReplicaFactory("tests.box", p1.Factories);
            var hostHandler = new RecordingSpawnHandler(host.Scene, () => null);
            var at = AuthorityMod.At(4);
            var hostSeam = host.Seam();

            // The host's provider spawns two scripted boxes: one through RequestSpawn, one it made and registered.
            hostSeam.RequestSpawn(new InternalSpawnDescriptor("tests.box", at, new byte[] { 1 }), () => hostHandler.MakeHere(at));
            var registered = hostHandler.MakeHere(at).Value!;
            hostSeam.RegisterCanonical(registered, new InternalSpawnDescriptor("tests.box", at, new byte[] { 2 }));
            That(rig.Run(() => p1.Factories.Built.Count == 2, 60), "the player builds both from the provider's factory");

            var mine = new AuthorityMod(p1);
            var handler = new RecordingSpawnHandler(p1.Scene, () => mine.Seam);
            var requested = mine.Seam.RequestSpawn(new InternalSpawnDescriptor("tests.box", at, new byte[] { 9 }), () => handler.MakeHere(at));
            var made = handler.MakeHere(at).Value!;
            var registration = mine.Seam.RegisterCanonical(made, new InternalSpawnDescriptor("tests.box", at, new byte[] { 8 }));
            That(requested.Succeeded && registration.Succeeded && p1.Counters().ShadowsBound == 2 && p1.Factories.Released.Count == 2,
                "the player's run of the provider binds both shadows in place of the replicas: a provider's description may differ between runs");
            That(mine.Seam.RegisterCanonical(made, new InternalSpawnDescriptor("tests.box", at, new byte[] { 7 })).Succeeded && p1.Counters().ShadowsMade == 2,
                "describing a shadow again takes no number");
            That(p1.Runtime.Spawns.Live().Count == 0, "and nothing is recorded");
        }

        private static void ScriptedChangesLeaveTheHostsObjectsToTheHost()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var hosts = new AuthorityMod(host);
            var crate = hosts.Spawn(hosts.Load(), 1).Value!;
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 1, 60), "the player has the host's crate");
            var shadow = mine.Spawn(prefab, 1).Value!;
            That(p1.Counters().ShadowsBound == 1, "and its shadow stands for it");

            // The script moves and destroys its crate: on the host it happens, and here the call is a no-op.
            That(mine.Entities.SetTransform(shadow, AuthorityMod.At(3, 3)).Succeeded && mine.FakeEntities.Moves == 0, "a scripted move here changes nothing");
            hosts.Entities.SetTransform(crate, AuthorityMod.At(3, 3));
            That(rig.Run(() => Near(AuthorityRig.Object(shadow).Position, new NetVector3(3, 3, 0), 0.05), 120), "the host's run moves it, and the shadow follows");
            That(mine.Entities.Destroy(shadow).Succeeded && shadow.IsAlive, "a scripted destroy here ends nothing");
            hosts.Entities.Destroy(crate);
            That(rig.Run(() => !shadow.IsAlive, 60), "the host's run ends it, and the shadow with it");

            // A shadow that still waits is the player's alone: a scripted move or destroy applies to it here.
            var waiting = mine.Spawn(prefab, 2).Value!;
            That(mine.Entities.SetTransform(waiting, AuthorityMod.At(6)).Succeeded && mine.FakeEntities.Moves == 1, "a waiting shadow moves here");
            That(mine.Entities.Destroy(waiting).Succeeded && !waiting.IsAlive && p1.Client.WaitingShadows == 0, "and ends here");

            // On the player's intent, the host's object is not the player's to change.
            var replica = hosts.Spawn(hosts.Load(), 3).Value!;
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 1, 60), "the host's third crate arrives");
            var target = p1.Client.TryGetEntity(new NetworkEntityId(AuthorityRig.Object(replica).Mark), out var found) ? found : null;
            That(target != null && !mine.AsPlayer(() => mine.Entities.SetTransform(target, AuthorityMod.At(1))).Succeeded, "a player's own action may not move it");
        }
    }
}
