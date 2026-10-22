using System;
using System.Linq;
using System.Threading;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Spawn adoption (WS3.7): a joined player's spawn on its intent is made at once, frozen, asked of the host, made there
    /// canonically as the player's, and bound in place under the same handle and <see cref="IEntity.Id"/>; a refusal kills
    /// the handle with one toast; a verdict lost with its stream is asked for again, and one that never comes ends the
    /// prediction. The host makes it from the mod's own handles first.
    /// </summary>
    internal static class SpawnAdoptionTests
    {
        internal static void Run()
        {
            APlayersSpawnIsAdoptedInPlace();
            TheHostMakesItFromTheModsOwnHandles();
            TheHostLoadsWhatTheModDoesNotHoldAndLetsItGo();
            ARefusalKillsTheHandleWithOneToast();
            AVerdictLostWithItsStreamIsAskedForAgain();
            AVerdictThatNeverComesEndsThePrediction();
            AProvidersSpawnIsMadeByItsHandlerForThePlayer();
            APlayerRecordsNothingAsCanonical();
            APlayerBuildsReplicasFromItsModsOwnHandles();
            APlayerPastItsQuotaIsToldAtOnce();
            ABundlePathTheHostWouldRefuseIsRefusedHereSayingWhy();
            ACommandsAnswerActsOnThePlayersOwnAction();
            AParticipantsObjectsAreThePlayersAndEndWithIt();
        }

        private static void APlayersSpawnIsAdoptedInPlace()
        {
            var rig = AuthorityRig.Create();
            var host = rig.Host;
            var p1 = rig.Players[0];
            var p2 = rig.Players[1];
            new AuthorityMod(host).Load();
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();

            var made = mine.SpawnAsPlayer(prefab, 3);
            That(made.Succeeded, "the player's spawn succeeds here at once (" + made + ")");
            var handle = made.Value!;
            var instance = AuthorityRig.Object(handle);
            var id = handle.Id;
            That(handle.IsAlive && instance.Kinematic && instance.Mark == 0 && p1.Client.PendingPredictions == 1,
                "it stands where the mod put it, frozen and unmarked, while it waits for the host");

            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60), "the host adopts it (" + rig.Describe(p1) + ")");
            That(host.World.Proxies.TryGetProxy(p1.Control.Id, out var proxy), "the host keeps a scope for the player");
            var owned = AuthorityRig.OwnedBy(host, proxy.Slot);
            That(owned.Count == 1 && Near(((FakeWorldObject)owned[0].Object!).Position, new NetVector3(3, 1, 0)), "the host made one object, the player's, where it asked");
            var network = owned[0].Id;
            That(handle.IsAlive && handle.Id == id && instance.Mark == network.Value && p1.Client.BindingOf(network) == ClientEntityBinding.Own,
                "the player's own instance stands for it, under the same handle and id");
            That(p1.Client.TryGetEntity(network, out var found) && found.Id == id, "a lookup by its network id finds the mod's own handle");
            That(AuthorityRig.Live(p1).Count == 1 && p1.Assets.Spawned.Count == 1 && p1.Client.PendingPredictions == 0, "and nothing else stands for it: no replica was built");
            That(rig.Run(() => p2.Client.BindingOf(network) == ClientEntityBinding.Asset, 60) && AuthorityRig.Live(p2).Count == 1,
                "the other player sees it as one of the host's");
            That(host.Counters().SpawnsAdopted == 1 && p1.Counters().PredictionsAdopted == 1 && mine.Ui.Toasts.Count == 0, "one adoption, and nothing to tell the player");
            That(p2.Counters().PredictionsMade == 0 && host.Runtime.Spawns.Live().Count(record => record.OwnerSlot == proxy.Slot) == 1,
                "the host records it as canonical, the player's own");
        }

        private static void TheHostMakesItFromTheModsOwnHandles()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var theirs = new AuthorityMod(host);
            theirs.Load();
            var loads = (host.Assets.BundleLoads, host.Assets.PrefabLoads);
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            for (var index = 0; index < 3; index++) mine.SpawnAsPlayer(prefab, index);
            That(rig.Run(() => p1.Client.OwnedObjects == 3, 60), "three spawns are adopted");
            That((host.Assets.BundleLoads, host.Assets.PrefabLoads) == loads && host.World.Proxies.OwnLoads == 0,
                "the host made them from the prefab its mod loaded: the game is never asked to load a bundle twice");

            // While the mod's own load of the bundle still runs, the host waits for it rather than load the bundle itself.
            theirs.Lifetime.Dispose();
            host.Assets.HoldLoads = true;
            var other = new AuthorityMod(host);
            var loading = other.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            var bundles = host.Assets.BundleLoads;
            mine.SpawnAsPlayer(prefab, 5);
            rig.Frames(20);
            That(!loading.IsCompleted && host.Assets.BundleLoads == bundles && p1.Client.PendingPredictions == 1, "the host waits for the mod's own bundle load");
            host.Assets.HoldLoads = false;
            host.Assets.CompleteLoads();
            That(rig.Run(() => p1.Client.OwnedObjects == 4, 60), "and makes the spawn from the bundle once it lands");
            That(host.Assets.BundleLoads == bundles && host.World.Proxies.OwnLoads == 1, "loading only the prefab, from the mod's bundle");
            rig.Clock.Advance(NetworkWorldProxies.OwnLoadIdleMicros);
            rig.Frames(2);
            That(host.World.Proxies.OwnLoads == 0 && AuthorityRig.Live(host).Count == 4, "the prefab is let go of once idle, and what was made of it stays");
        }

        private static void TheHostLoadsWhatTheModDoesNotHoldAndLetsItGo()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var loads = (host.Assets.BundleLoads, host.Assets.PrefabLoads);
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            mine.SpawnAsPlayer(prefab, 1);
            mine.SpawnAsPlayer(prefab, 2);
            That(rig.Run(() => p1.Client.OwnedObjects == 2, 60), "both spawns are adopted");
            That(host.Assets.BundleLoads == loads.BundleLoads + 1 && host.Assets.PrefabLoads == loads.PrefabLoads + 1 && host.World.Proxies.OwnLoads == 2,
                "the host loaded the bundle and the prefab once, through the mod's package, for both");
            rig.Clock.Advance(NetworkWorldProxies.OwnLoadIdleMicros - 100_000);
            rig.Frames(2);
            That(host.World.Proxies.OwnLoads == 2, "it keeps them while they may be used again");
            rig.Clock.Advance(200_000);
            rig.Frames(2);
            That(host.World.Proxies.OwnLoads == 0 && AuthorityRig.Live(host).Count == 2, "and lets go of both once idle, so the mod can load the bundle itself");

            // A bundle the host cannot load refuses the spawn as unbuildable.
            host.Assets.MissingBundle = AuthorityMod.Bundle;
            var handle = mine.SpawnAsPlayer(prefab, 3).Value!;
            That(rig.Run(() => !handle.IsAlive, 60) && mine.Ui.Toasts.Count == 1 &&
                 mine.Ui.Toasts[0].Message == SpawnAuthorityRules.RefusalText(SpawnRefusal.Unbuildable), "a bundle the host cannot load is refused, and the player told");
            That(host.World.Proxies.OwnLoads == 0 && AuthorityRig.Live(host).Count == 2, "and the failed load is let go of");
        }

        private static void ARefusalKillsTheHandleWithOneToast()
        {
            var rig = AuthorityRig.Create(players: 1, shared: false);
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            var first = mine.SpawnAsPlayer(prefab, 1).Value!;
            var second = mine.SpawnAsPlayer(prefab, 2).Value!;
            That(first.IsAlive && second.IsAlive, "both stand here while they wait");
            That(rig.Run(() => !first.IsAlive && !second.IsAlive, 60), "the host refuses both, and their handles die");
            That(mine.Ui.Toasts.Count == 1 && mine.Ui.Toasts[0].Tone == UiTone.Warning && mine.Ui.Toasts[0].Message == SpawnAuthorityRules.RefusalText(SpawnRefusal.NotShared),
                "one warning toast says why (" + string.Join(" | ", mine.Ui.Toasts.Select(toast => toast.Message)) + ")");
            That(p1.Counters().PredictionsRefused == 2 && p1.Counters().RefusalToasts == 1 && p1.Client.PendingPredictions == 0 && AuthorityRig.Live(p1).Count == 0 &&
                 AuthorityRig.Live(rig.Host).Count == 0, "nothing of either stands anywhere");
            rig.Clock.Advance(SpawnAuthorityRules.RefusalToastIntervalMicros);
            var third = mine.SpawnAsPlayer(prefab, 3).Value!;
            That(rig.Run(() => !third.IsAlive, 60) && mine.Ui.Toasts.Count == 2, "a refusal after the interval is shown again");

            // A handle the mod already disposed is let go of quietly: the player removed it, so nothing is said.
            rig.Clock.Advance(SpawnAuthorityRules.RefusalToastIntervalMicros);
            var fourth = mine.SpawnAsPlayer(prefab, 4).Value!;
            fourth.Dispose();
            rig.Frames(10);
            That(mine.Ui.Toasts.Count == 2 && p1.Client.PendingPredictions == 0, "a spawn its mod removed before the verdict is not shown as refused");
        }

        private static void AVerdictLostWithItsStreamIsAskedForAgain()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            var handle = mine.SpawnAsPlayer(prefab, 4).Value!;

            // The request reaches the host, which adopts it; the stream restarts before the verdict arrives.
            rig.Frame();
            host.Frame();
            That(host.Counters().SpawnsAdopted == 1, "the host adopted the spawn");
            rig.Network.Rejoin(p1.Control);
            That(rig.Run(() => p1.Client.OwnedObjects == 1 && p1.World.Progress.Phase == NetworkWorldPhase.Synchronized, 120),
                "the player asks again and is adopted into the same object (" + rig.Describe(p1) + ")");
            That(host.Counters().SpawnsAdopted == 1 && AuthorityRig.Live(host).Count == 1, "the host made it once: asked again, it answers with the verdict it has");
            That(handle.IsAlive && AuthorityRig.Live(p1).Count == 1 && p1.Client.PendingPredictions == 0, "the player's handle stands for it, and nothing else does");

            // The swap: when the entity's entry arrives before its verdict, the replica built for it gives way to the handle.
            var stray = new WorldWireWriter();
            AuthorityWire.WriteSpawnRequest(stray, 2, WorldPeer.ModId, MultiplayerSpawnKinds.AssetPrefab, IntentSource.UiCallback, false, new NetVector3(1, 1, 1),
                new NetPose(new NetVector3(9, 1, 0), NetQuaternion.Identity), WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Create(AuthorityMod.Bundle, "Crate")));
            host.Control.Channel(InternalSystemChannelKind.WorldReplication)!.Inject(p1.Control.Id, stray.Segment.ToArray());
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 2, 60) && p1.Counters().VerdictsIgnored >= 1, "a spawn the player never predicted arrives as a replica");
            var replicas = p1.Assets.Spawned.Count;
            var late = mine.SpawnAsPlayer(prefab, 9).Value!;
            That(rig.Run(() => p1.Client.OwnedObjects == 2, 60) && late.IsAlive && AuthorityRig.Live(p1).Count == 2,
                "the verdict that follows binds the handle in place of the replica (" + AuthorityRig.Live(p1).Count + " crates)");
            That(p1.Assets.Spawned.Count == replicas + 1 && host.Counters().SpawnsAdopted == 2, "with no third crate anywhere");
        }

        private static void AVerdictThatNeverComesEndsThePrediction()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            host.Paused = true;
            var handle = mine.SpawnAsPlayer(prefab, 1).Value!;
            rig.Frames(3);
            rig.Clock.Advance(SpawnAuthorityRules.PredictionTimeoutMicros - 1_000_000);
            rig.Frames(2);
            That(handle.IsAlive && p1.Client.PendingPredictions == 1, "a prediction waits a minute for its verdict");
            rig.Clock.Advance(1_000_000);
            rig.Frames(2);
            That(!handle.IsAlive && p1.Counters().PredictionsTimedOut == 1 && mine.Ui.Toasts.Count == 1 &&
                 mine.Ui.Toasts[0].Message == SpawnAuthorityRules.RefusalText(SpawnRefusal.TimedOut), "then it ends, and the player is told to try again");

            // The host comes back and makes it after all: the player's request to destroy it follows, so nothing is left.
            host.Paused = false;
            rig.Frames(20);
            That(AuthorityRig.Live(host).Count == 0 && AuthorityRig.Live(p1).Count == 0 && p1.Counters().VerdictsIgnored == 1,
                "what the host made late ends at the player's request, and the late verdict is ignored");
        }

        private static void AProvidersSpawnIsMadeByItsHandlerForThePlayer()
        {
            var rig = AuthorityRig.Create();
            var host = rig.Host;
            var p1 = rig.Players[0];
            var p2 = rig.Players[1];
            var hostSeam = host.Seam();
            var hostHandler = new RecordingSpawnHandler(host.Scene, () => hostSeam);
            That(hostSeam.RegisterSpawnHandler("tests.box", hostHandler).Succeeded, "the host's provider says how it makes its kind for players");
            var mine = new AuthorityMod(p1);
            var handler = new RecordingSpawnHandler(p1.Scene, () => mine.Seam);
            mine.Seam.RegisterSpawnHandler("tests.box", handler);
            p2.Seam().RegisterReplicaFactory("tests.box", p2.Factories);

            var payload = new byte[] { 4, 5, 6 };
            var at = AuthorityMod.At(2);
            var made = mine.AsPlayer(() => mine.Seam.RequestSpawn(new InternalSpawnDescriptor("tests.box", at, payload), () => handler.MakeHere(at)));
            That(made.Succeeded && made.Value!.IsAlive && p1.Client.PendingPredictions == 1, "the provider's spawn is made here at once, as a prediction");
            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60), "and adopted");
            var spawnedFor = hostHandler.SpawnedFor.Single();
            That(spawnedFor.Source == InternalIntent.OwnerCommand && spawnedFor.ParticipantId == "p1",
                "the host's handler made it inside the player's intent, so the provider knows whose it is (" + spawnedFor.Source + ", " + spawnedFor.ParticipantId + ")");
            That(host.World.Proxies.TryGetProxy(p1.Control.Id, out var proxy) && AuthorityRig.OwnedBy(host, proxy.Slot).Count == 1, "it is the player's on the host");
            That(rig.Run(() => p2.Factories.Built.Count == 1, 60) && p2.Factories.Built[0].Payload.ToArray().SequenceEqual(payload),
                "the other player builds it from the provider's description");

            // Without a handler on this player its provider cannot say how the host makes it: the spawn is refused here.
            var none = mine.AsPlayer(() => mine.Seam.RequestSpawn(new InternalSpawnDescriptor("tests.other", at, payload), () => handler.MakeHere(at)));
            That(!none.Succeeded && none.ErrorCode == ModErrorCode.NotAuthoritative && none.ErrorMessage == RuntimeReplication.OnlyTheHostMakes,
                "a kind with no spawn handler is refused on a joined player (" + none + ")");

            // The host's handler refusing makes the player's spawn unbuildable.
            hostHandler.Refuse = true;
            var refused = mine.AsPlayer(() => mine.Seam.RequestSpawn(new InternalSpawnDescriptor("tests.box", at, payload), () => handler.MakeHere(at))).Value!;
            That(rig.Run(() => !refused.IsAlive, 60) && handler.Despawned.Contains(refused), "a spawn the host's handler refuses ends here through the player's handler");
        }

        private static void APlayerBuildsReplicasFromItsModsOwnHandles()
        {
            var rig = AuthorityRig.Create(players: 2);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var p2 = rig.Players[1];
            var theirs = new AuthorityMod(p2);
            theirs.Load();
            var loads = (p2.Assets.BundleLoads, p2.Assets.PrefabLoads);
            var mine = new AuthorityMod(p1);
            var handle = mine.SpawnAsPlayer(mine.Load(), 3).Value!;
            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60), "the first player's crate is adopted");
            var id = new NetworkEntityId(AuthorityRig.Object(handle).Mark);
            That(rig.Run(() => p2.Client.BindingOf(id) == ClientEntityBinding.Asset, 60), "the other player builds its replica");
            That((p2.Assets.BundleLoads, p2.Assets.PrefabLoads) == loads, "from the prefab its own mod holds: the game is never asked to load the bundle twice");

            // A bundle the mod holds, with another prefab: only the prefab loads, from the mod's bundle.
            var hosts = new AuthorityMod(host);
            hosts.Spawn(hosts.Load("Barrel"), 5);
            That(rig.Run(() => AuthorityRig.Live(p2, "Barrel").Count == 1, 60), "the host's barrel is built");
            That(p2.Assets.BundleLoads == loads.BundleLoads && p2.Assets.PrefabLoads == loads.PrefabLoads + 1, "loading only its prefab, from the mod's bundle");

            // While the mod's own load of a bundle runs, the replica waits for it.
            p2.Assets.HoldLoads = true;
            var other = new AuthorityMod(p2);
            var loading = other.Assets.LoadBundleAsync("assets/other.bundle");
            hosts.Spawn(hosts.Load("Crate", "assets/other.bundle"), 7);
            rig.Frames(10);
            That(!loading.IsCompleted && p2.Client.PendingBuilds == 1 && p2.Assets.BundleLoads == loads.BundleLoads + 1, "the replica waits for the mod's own load");
            p2.Assets.HoldLoads = false;
            p2.Assets.CompleteLoads();
            That(rig.Run(() => p2.Client.PendingBuilds == 0, 60) && AuthorityRig.Live(p2).Count == 2 && p2.Assets.BundleLoads == loads.BundleLoads + 1,
                "and is built from the mod's bundle once it lands");
        }

        private static void APlayerPastItsQuotaIsToldAtOnce()
        {
            var rig = AuthorityRig.Create(players: 1);
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            for (var index = 0; index < SpawnAuthorityRules.MaximumPendingSpawns; index++) That(mine.SpawnAsPlayer(prefab, index).Succeeded, "spawn " + index + " is made");
            var made = p1.Assets.Spawned.Count;
            var past = mine.SpawnAsPlayer(prefab, 99);
            That(!past.Succeeded && past.ErrorCode == ModErrorCode.RateLimited && past.ErrorMessage == RuntimeReplication.TooManyObjects && p1.Assets.Spawned.Count == made,
                "past the host's quota the spawn is refused before anything is made, saying what to do (" + past + ")");
            That(mine.Ui.Toasts.Count == 0 && p1.Client.PendingPredictions == SpawnAuthorityRules.MaximumPendingSpawns, "the mod hears it; nothing is shown, and nothing more waits");
        }

        private static void ABundlePathTheHostWouldRefuseIsRefusedHereSayingWhy()
        {
            var rig = AuthorityRig.Create(players: 1);
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);

            // The asset service takes "./" in a package path, but the host makes players' objects only from plain paths.
            var prefab = mine.Load(bundlePath: "./" + AuthorityMod.Bundle);
            var made = p1.Assets.Spawned.Count;
            var sent = p1.Counters().AuthorityRequestsSent;
            var spawned = mine.SpawnAsPlayer(prefab, 1);
            That(!spawned.Succeeded && spawned.ErrorCode == ModErrorCode.Unavailable && spawned.ErrorMessage == RuntimeReplication.NotAPlainPath,
                "the player's spawn is refused before anything is made, saying how to name the bundle (" + spawned.ErrorMessage + ")");

            // So is one from a bundle named as Windows names a device, which the host would never open.
            foreach (var device in new[] { "assets/aux.bundle", "Assets/Com1.bundle", "nul/world.bundle" })
            {
                var named = mine.SpawnAsPlayer(mine.Load(bundlePath: device), 2);
                That(!named.Succeeded && named.ErrorCode == ModErrorCode.Unavailable && named.ErrorMessage == RuntimeReplication.NotAPlainPath,
                    "a spawn from '" + device + "', a device's name, is refused here too (" + named.ErrorMessage + ")");
            }

            rig.Frames(10);
            That(p1.Assets.Spawned.Count == made && p1.Counters().AuthorityRequestsSent == sent && mine.Ui.Toasts.Count == 0 && AuthorityRig.Live(rig.Host).Count == 0,
                "nothing was made or asked for, and no toast blames the host");
        }

        private static void ACommandsAnswerActsOnThePlayersOwnAction()
        {
            // A generated command's answer runs, frames later, in the execution context its submitting code had: the
            // session captures it when the answer is deferred and runs the answer inside it when the answer arrives.
            var rig = AuthorityRig.Create(players: 1);
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            var submitted = mine.AsPlayer(() => ExecutionContext.Capture())!;
            rig.Frames(3);
            OperationResult<ISpawnedEntity>? answered = null;
            ExecutionContext.Run(submitted, _ => answered = mine.Spawn(prefab, 4), null);
            That(answered != null && answered.Succeeded && p1.Client.PendingPredictions == 1 && p1.Counters().ShadowsMade == 0,
                "a spawn the answer makes is the player's own action: a prediction, never a shadow");
            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60) && answered!.Value!.IsAlive, "and the host adopts it under the same handle");

            // The same call outside the answer is the script's: a shadow of the host's own run.
            mine.Spawn(prefab, 5);
            That(p1.Client.WaitingShadows == 1 && p1.Client.PendingPredictions == 0, "while the same call outside the answer is a shadow");
        }

        private static void AParticipantsObjectsAreThePlayersAndEndWithIt()
        {
            var rig = AuthorityRig.Create(players: 2);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var p2 = rig.Players[1];

            // A participant controller acts for its player whatever caused its code to run: its spawn is a prediction.
            var participant = new AuthorityMod(p1, ModExecutionContext.Participant, assets: lifetime => new FakeWorldAssets(p1.Scene, lifetime));
            var handle = participant.Spawn(participant.Load(), 4).Value!;
            That(handle.IsAlive && p1.Client.PendingPredictions == 1, "a participant's spawn with no intent is made at once and asked of the host");
            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60), "the host adopts it");
            var id = new NetworkEntityId(AuthorityRig.Object(handle).Mark);
            That(host.World.Proxies.TryGetProxy(p1.Control.Id, out var proxy) && AuthorityRig.OwnedBy(host, proxy.Slot).Count == 1 &&
                 rig.Run(() => p2.Client.Knows(id), 60), "as the player's object, which the other player sees");
            That(host.Runtime.Spawns.Live().All(record => record.ShadowKey == 0), "and it takes no shadow number");

            // The participant's scope ends: its handles end with its lifetime, and the host ends what it made of them.
            participant.Lifetime.Dispose();
            That(!handle.IsAlive, "the scope's own handle ends with it");
            That(rig.Run(() => AuthorityRig.OwnedBy(host, proxy.Slot).Count == 0 && !p2.Client.Knows(id), 60),
                "and the host ends the player's object for everyone, as the scope's objects end with it offline");
        }

        private static void APlayerRecordsNothingAsCanonical()
        {
            var rig = AuthorityRig.Create(players: 1);
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            for (var index = 0; index < 3; index++) mine.SpawnAsPlayer(prefab, index);
            mine.Spawn(prefab, 7);
            That(rig.Run(() => p1.Client.OwnedObjects == 3, 60), "the predictions are adopted");
            That(p1.Runtime.Spawns.Live().Count == 0 && !p1.Host.IsActive, "a joined player records no prediction and no shadow as canonical");
            GC.KeepAlive(mine);
        }
    }
}
