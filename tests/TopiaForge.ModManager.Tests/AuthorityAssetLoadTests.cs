using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The mod's own asset loads beside the network world's (WS3.7). The game loads a bundle once per process, so a load the
    /// network world made itself, on a listen host for a player's spawn or on a joined player for a replica, gives way when
    /// the mod starts its own load of the bundle, however a player spelled its path: a mod that loads lazily is never
    /// refused. One file is one load there, and a spelling the game's platform reads as another name of the file is
    /// refused before it loads. A replica built through a mod context's own handles, which ends with that context, is built
    /// again, as is one whose build failed through them as it ended; and the host's next build for a player outlives them.
    /// </summary>
    internal static class AuthorityAssetLoadTests
    {
        internal static void Run()
        {
            TheHostsOwnLoadGivesWayToItsModsLoad();
            APlayersReplicaLoadGivesWayToItsModsLoad();
            AReplicaOutlivesTheModContextWhoseHandlesBuiltIt();
            ALoadOffTheGameThreadTouchesNothingOfTheWorlds();
            ASpellingThePlatformReadsAsTheModsFileLoadsNothing();
            OneFileIsOneLoadHoweverPlayersSpellIt();
            AReplicaWhoseBuildEndedWithItsContextIsBuiltAgain();
            TheHostsNextBuildOutlivesTheContextThatHeldItsBundle();
        }

        private static void TheHostsOwnLoadGivesWayToItsModsLoad()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];

            // The host's package loads as the game's does: a bundle file that is loaded already is refused a second load.
            var native = new NativeWorldAssets(host.Scene, host.Lifetime);
            host.ModAssets = native;
            var hosts = new AuthorityMod(host, assets: _ => native);
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            mine.SpawnAsPlayer(prefab, 1);
            That(rig.Run(() => Settled(native, () => p1.Client.OwnedObjects == 1), 120) && host.World.Proxies.OwnLoads == 2 && native.LoadedBundles == 1,
                "the host's mod holds nothing, so the host loads the player's crate's bundle itself");

            // The player keeps dropping crates, each well within the host's idle time, so the host keeps its own load.
            for (var drop = 2; drop <= 4; drop++)
            {
                rig.Clock.Advance(1_000_000);
                mine.SpawnAsPlayer(prefab, drop);
                That(rig.Run(() => Settled(native, () => p1.Client.OwnedObjects == drop), 120) && host.World.Proxies.OwnLoads == 2, "the player's crate " + drop + " is made");
            }

            // The host's own player drops a crate, and its mod loads the bundle then, lazily: the host's own load gives way.
            var load = hosts.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            native.CompleteLoads();
            That(load.IsCompleted && load.Result.Succeeded && native.RefusedLoads == 0,
                "the mod's own load is never refused, however much the players spawn from the bundle (" + Describe(load) + ")");
            That(host.World.Proxies.OwnLoads == 0 && host.Counters().AssetLoadsYielded == 1 && AuthorityRig.Live(host).Count == 4,
                "the host's own load gave way, and every crate made from it stands");

            // The players' next crates are made from the mod's own handles.
            hosts.Assets.LoadPrefabAsync(load.Result.Value!, "Crate");
            native.CompleteLoads();
            var bundles = native.BundleLoads;
            mine.SpawnAsPlayer(prefab, 9);
            That(rig.Run(() => Settled(native, () => p1.Client.OwnedObjects == 5), 120) && native.BundleLoads == bundles && host.World.Proxies.OwnLoads == 0,
                "and the player's next crate is made from the mod's handles");

            // A player that spells the bundle's file another way is no different: the mod's load of the file is never refused.
            var other = AuthorityRig.Create(players: 1);
            var otherNative = new NativeWorldAssets(other.Host.Scene, other.Host.Lifetime);
            other.Host.ModAssets = otherNative;
            var log = SpawnAbuseTests.Log(other);
            SpawnAbuseTests.Inject(other, other.Players[0], SpawnAbuseTests.Spawn(1, bundle: "Assets\\World.bundle"));
            That(other.Run(() => Settled(otherNative, () => SpawnAbuseTests.Verdicts(log, other.Players[0]).Count == 1), 120) &&
                 SpawnAbuseTests.Verdicts(log, other.Players[0]).Single().Id.IsValid && other.Host.World.Proxies.OwnLoads == 2,
                "the host loads the bundle as a player named it");
            var theirs = new AuthorityMod(other.Host, assets: _ => otherNative);
            var spelled = theirs.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            otherNative.CompleteLoads();
            That(spelled.IsCompleted && spelled.Result.Succeeded && otherNative.RefusedLoads == 0 && other.Host.World.Proxies.OwnLoads == 0,
                "and gives that load up to the mod's own load of the same file");
        }

        private static void APlayersReplicaLoadGivesWayToItsModsLoad()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var native = new NativeWorldAssets(p1.Scene, p1.Lifetime);
            p1.ModAssets = native;

            // The host's player drops a crate before the player's mod has loaded its bundle: the player's world loads it itself.
            var hosts = new AuthorityMod(host);
            hosts.SpawnAsPlayer(hosts.Load(), 3);
            That(rig.Run(() => Settled(native, () => native.Spawned.Count(item => item.IsAlive) == 1), 120) && native.LoadedBundles == 1,
                "the player builds the host's crate from a load of the world's own");

            // The player's mod then loads the bundle itself, lazily, as on its player's first drop.
            var mine = new AuthorityMod(p1, assets: _ => native);
            var bundle = mine.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            native.CompleteLoads();
            That(bundle.IsCompleted && bundle.Result.Succeeded && native.RefusedLoads == 0,
                "the mod's own load is never refused (" + Describe(bundle) + ")");
            That(native.Spawned.Count(item => item.IsAlive) == 1 && p1.Counters().AssetLoadsYielded == 1, "the world's own load gave way, and the replica stands");

            // So the player's own drop, the reason its mod loaded the bundle, is made and adopted.
            var prefab = mine.Assets.LoadPrefabAsync(bundle.Result.Value!, "Crate");
            native.CompleteLoads();
            var dropped = mine.SpawnAsPlayer(prefab.Result.Value!, 5);
            That(dropped.Succeeded && rig.Run(() => p1.Client.OwnedObjects == 1, 60) && mine.Ui.Toasts.Count == 0, "and the player's own crate is adopted (" + dropped.ErrorMessage + ")");

            // A hostile player that names the bundle's file another way, so the others' worlds load it under that spelling,
            // keeps no one's mod from loading it.
            var other = AuthorityRig.Create(players: 2);
            var victim = other.Players[0];
            var victimNative = new NativeWorldAssets(victim.Scene, victim.Lifetime);
            victim.ModAssets = victimNative;
            SpawnAbuseTests.Inject(other, other.Players[1], SpawnAbuseTests.Spawn(1, bundle: "Assets\\World.bundle"));
            That(other.Run(() => Settled(victimNative, () => victimNative.Spawned.Count(item => item.IsAlive) == 1), 120) && victimNative.LoadedBundles == 1,
                "the other player builds the hostile player's crate from the spelling it was given");
            var victims = new AuthorityMod(victim, assets: _ => victimNative);
            var spelled = victims.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            victimNative.CompleteLoads();
            That(spelled.IsCompleted && spelled.Result.Succeeded && victimNative.RefusedLoads == 0, "and its mod's own load of the same file is never refused (" + Describe(spelled) + ")");
        }

        private static void AReplicaOutlivesTheModContextWhoseHandlesBuiltIt()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];

            // The player's world-owner scope loads the crate's prefab, as its world's script does, before the host's crate arrives.
            var scope = new AuthorityMod(p1, ModExecutionContext.WorldOwner, assets: lifetime => new FakeWorldAssets(p1.Scene, lifetime));
            scope.Load();
            var hosts = new AuthorityMod(host);
            var crate = hosts.SpawnAsPlayer(hosts.Load(), 3).Value!;
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 1, 60), "the player builds the host's crate");
            var id = new NetworkEntityId(AuthorityRig.Object(crate).Mark);
            That(p1.Assets.BundleLoads == 0 && p1.Assets.Spawned.Count == 0, "through the scope's own handles: the package loaded nothing");

            // The scope ends, as a world session the player ran ends, and takes what was spawned through it; the player stays.
            scope.Lifetime.Dispose();
            That(AuthorityRig.Live(p1).Count == 0, "the replica ended with the scope");
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 1, 60) && crate.IsAlive && p1.Client.BindingOf(id) == ClientEntityBinding.Asset,
                "and is built again, so the player still sees the host's crate");
            That(p1.Counters().ReplicasRebuilt == 1 && p1.Assets.BundleLoads == 1 && p1.Assets.Spawned.Count(item => item.IsAlive) == 1,
                "from a load of the world's own, now that the scope has let go of the bundle");

            // A scope that holds the bundle alone: the player's world loads the crate's prefab off it, and builds it again too.
            var other = AuthorityRig.Create(players: 1);
            var p2 = other.Players[0];
            NativeWorldAssets? held = null;
            var bundleScope = new AuthorityMod(p2, ModExecutionContext.WorldOwner, assets: lifetime => held = new NativeWorldAssets(p2.Scene, lifetime));
            var bundle = bundleScope.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            held!.CompleteLoads();
            var theirs = new AuthorityMod(other.Host);
            theirs.Spawn(theirs.Load(), 3);
            That(bundle.Result.Succeeded && other.Run(() => Settled(held, () => AuthorityRig.Live(p2).Count == 1), 60) && held.PrefabLoads == 1 && p2.Assets.BundleLoads == 0,
                "a scope that holds only the bundle: the player builds the host's crate from a prefab it loads off it");
            bundleScope.Lifetime.Dispose();
            That(AuthorityRig.Live(p2).Count == 0, "the replica ended with the scope");
            That(other.Run(() => AuthorityRig.Live(p2).Count == 1, 60) && p2.Counters().ReplicasRebuilt == 1 && p2.Assets.BundleLoads == 1,
                "and is built again from a load of the world's own");
        }

        private static void ALoadOffTheGameThreadTouchesNothingOfTheWorlds()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            mine.SpawnAsPlayer(mine.Load(), 1);
            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60) && host.World.Proxies.OwnLoads == 2, "the host loads the player's crate's bundle itself");

            // A mod that starts a load from a worker thread breaks the SDK's rule, and the game's asset service refuses it there;
            // the network world, which belongs to the game thread, is not touched from that thread.
            var hosts = new AuthorityMod(host);
            System.Threading.Tasks.Task.Run(() => hosts.Assets.LoadBundleAsync(AuthorityMod.Bundle)).Wait();
            That(host.World.Proxies.OwnLoads == 2 && host.Counters().AssetLoadsYielded == 0, "a load started off the game thread yields nothing");
        }

        private static void ASpellingThePlatformReadsAsTheModsFileLoadsNothing()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var native = new NativeWorldAssets(host.Scene, host.Lifetime);
            host.ModAssets = native;
            var log = SpawnAbuseTests.Log(rig);

            // A hostile player names the host mod's bundle as the game's platform also reads it: a name ending in a period or a
            // space, or a short name. No comparison of names sees that file in it, so the host never loads it.
            var spellings = new[] { "assets/world.bundle.", "assets./world.bundle", "assets /world.bundle", "assets/WORLD~1.BUN" };
            for (var index = 0; index < spellings.Length; index++) SpawnAbuseTests.Inject(rig, p1, SpawnAbuseTests.Spawn((uint)index + 1, bundle: spellings[index]));
            That(rig.Run(() => Settled(native, () => SpawnAbuseTests.Verdicts(log, p1).Count == spellings.Length), 120) &&
                 SpawnAbuseTests.Verdicts(log, p1).All(verdict => verdict.Reason == SpawnRefusal.NotPermitted) && native.BundleLoads == 0 && host.World.Proxies.OwnLoads == 0,
                "each is refused before anything loads (" + string.Join(", ", SpawnAbuseTests.Verdicts(log, p1).Select(verdict => verdict.Reason)) + ")");

            // So it holds nothing against the host mod's own load of its bundle.
            var hosts = new AuthorityMod(host, assets: _ => native);
            var load = hosts.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            native.CompleteLoads();
            That(load.IsCompleted && load.Result.Succeeded && native.RefusedLoads == 0, "and the mod's own load is never refused (" + Describe(load) + ")");
        }

        private static void OneFileIsOneLoadHoweverPlayersSpellIt()
        {
            // A joined player builds crates whose paths players spelled three ways from one load of the file.
            var rig = AuthorityRig.Create(players: 2);
            var host = rig.Host;
            var victim = rig.Players[0];
            var hostile = rig.Players[1];
            var hostNative = new NativeWorldAssets(host.Scene, host.Lifetime);
            host.ModAssets = hostNative;
            var native = new NativeWorldAssets(victim.Scene, victim.Lifetime);
            victim.ModAssets = native;
            var spellings = new[] { AuthorityMod.Bundle, "Assets\\World.bundle", "ASSETS/WORLD.BUNDLE" };
            for (var index = 0; index < spellings.Length; index++)
            {
                var built = index + 1;
                SpawnAbuseTests.Inject(rig, hostile, SpawnAbuseTests.Spawn((uint)built, bundle: spellings[index], x: built * 2));
                That(rig.Run(() => Settled(hostNative, () => Settled(native, () => native.Spawned.Count(item => item.IsAlive) == built)), 120),
                    "the other player builds the crate spelled '" + spellings[index] + "' (" + native.BundleLoads + " loads, " + native.RefusedLoads + " refused)");
            }

            That(native.BundleLoads == 1 && native.RefusedLoads == 0 && hostNative.BundleLoads == 1,
                "from one load of the file there, and the host made them from one load of its own: a player cannot fill the loads a session allows with spellings");

            // While the host's mod loads its bundle itself, a player's spawn spelled another way waits for that load.
            var other = AuthorityRig.Create(players: 1);
            var p1 = other.Players[0];
            var otherNative = new NativeWorldAssets(other.Host.Scene, other.Host.Lifetime);
            other.Host.ModAssets = otherNative;
            var log = SpawnAbuseTests.Log(other);
            var hosts = new AuthorityMod(other.Host, assets: _ => otherNative);
            var load = hosts.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            SpawnAbuseTests.Inject(other, p1, SpawnAbuseTests.Spawn(1, bundle: "Assets\\World.bundle"));
            other.Frames(3);
            That(otherNative.BundleLoads == 1 && SpawnAbuseTests.Verdicts(log, p1).Count == 0, "the host starts no second load of a file its mod is loading, however it is spelled");
            That(other.Run(() => Settled(otherNative, () => SpawnAbuseTests.Verdicts(log, p1).Count == 1), 120) && SpawnAbuseTests.Verdicts(log, p1).Single().Id.IsValid &&
                 load.Result.Succeeded && otherNative.BundleLoads == 1 && otherNative.RefusedLoads == 0, "and makes the crate from the mod's own bundle once it lands");
        }

        private static void AReplicaWhoseBuildEndedWithItsContextIsBuiltAgain()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];

            // The player's world-owner scope holds the crate's bundle alone, so the player's world loads the prefab off it.
            NativeWorldAssets? held = null;
            var scope = new AuthorityMod(p1, ModExecutionContext.WorldOwner, assets: lifetime => held = new NativeWorldAssets(p1.Scene, lifetime));
            var bundle = scope.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            held!.CompleteLoads();
            var hosts = new AuthorityMod(host);
            var prefab = hosts.Load();
            var crate = hosts.Spawn(prefab, 3).Value!;
            var id = new NetworkEntityId(AuthorityRig.Object(crate).Mark);
            That(bundle.Result.Succeeded && rig.Run(() => p1.Client.Knows(id) && held.PrefabLoads == 1, 60) && p1.Client.BindingOf(id) == ClientEntityBinding.None,
                "the player's world loads the host's crate's prefab off the scope's bundle");

            // The scope ends while that load runs, as the runtime closes one: it stops, its native work lands, then it goes.
            scope.Lifetime.BeginStop();
            rig.Frames(2);
            held.CompleteLoads();
            scope.Lifetime.Dispose();
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 1, 60) && p1.Client.BindingOf(id) == ClientEntityBinding.Asset && p1.Counters().ReplicasRebuilt == 1,
                "once the scope has gone, the crate whose build failed with it is built again (" + p1.Client.BindingOf(id) + ", " + p1.Counters().ReplicasUnbuildable + " unbuildable)");

            // The load that failed with the scope is not kept: the next crate of that prefab is built too.
            hosts.Spawn(prefab, 6);
            That(rig.Run(() => AuthorityRig.Live(p1).Count == 2, 60), "and the next crate of that prefab is built as well");

            // A scope that holds the prefab itself and is stopping: a replica spawned through it fails, and is built again too.
            var other = AuthorityRig.Create(players: 1);
            var p2 = other.Players[0];
            NativeWorldAssets? stopping = null;
            var prefabScope = new AuthorityMod(p2, ModExecutionContext.WorldOwner, assets: lifetime => stopping = new NativeWorldAssets(p2.Scene, lifetime));
            var scopeBundle = prefabScope.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            stopping!.CompleteLoads();
            var scopePrefab = prefabScope.Assets.LoadPrefabAsync(scopeBundle.Result.Value!, "Crate");
            stopping.CompleteLoads();
            prefabScope.Lifetime.BeginStop();
            var theirs = new AuthorityMod(other.Host);
            var theirCrate = theirs.Spawn(theirs.Load(), 3).Value!;
            var theirId = new NetworkEntityId(AuthorityRig.Object(theirCrate).Mark);
            That(scopePrefab.Result.Succeeded && other.Run(() => p2.Client.Knows(theirId), 60) && other.Run(() => p2.Counters().ReplicasUnbuildable == 1, 10) &&
                 AuthorityRig.Live(p2).Count == 0, "a stopping scope's prefab makes no replica");
            prefabScope.Lifetime.Dispose();
            That(other.Run(() => AuthorityRig.Live(p2).Count == 1, 60) && p2.Client.BindingOf(theirId) == ClientEntityBinding.Asset && p2.Counters().ReplicasRebuilt == 1,
                "and once the scope has gone, the replica is built again");
        }

        private static void TheHostsNextBuildOutlivesTheContextThatHeldItsBundle()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];

            // The host mod's world-owner scope holds the bundle alone, so the host loads the player's crate's prefab off it.
            NativeWorldAssets? held = null;
            var scope = new AuthorityMod(host, ModExecutionContext.WorldOwner, assets: lifetime => held = new NativeWorldAssets(host.Scene, lifetime));
            var bundle = scope.Assets.LoadBundleAsync(AuthorityMod.Bundle);
            held!.CompleteLoads();
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            mine.SpawnAsPlayer(prefab, 1);
            That(bundle.Result.Succeeded && rig.Run(() => Settled(held, () => p1.Client.OwnedObjects == 1), 120) && held.PrefabLoads == 1,
                "the host makes the player's crate from a prefab it loads off its mod's scope's bundle");

            // The scope ends, as the host's world session does, and its bundle with it; the player drops another crate at once.
            scope.Lifetime.Dispose();
            rig.Frames(2);
            var next = mine.SpawnAsPlayer(prefab, 2);
            That(next.Succeeded && rig.Run(() => p1.Client.PendingPredictions == 0, 120) && next.Value!.IsAlive && mine.Ui.Toasts.Count == 0,
                "the host makes the next one from a load of its own, and refuses nothing (" + string.Join(" | ", mine.Ui.Toasts.Select(toast => toast.Message)) + ")");
        }

        private static string Describe(System.Threading.Tasks.Task<OperationResult<IAssetBundle>> load) =>
            !load.IsCompleted ? "pending" : load.Result.Succeeded ? "loaded" : load.Result.ErrorMessage ?? "failed";

        /// <summary>Lets the engine finish what it is loading, then reads <paramref name="condition"/>.</summary>
        private static bool Settled(NativeWorldAssets assets, System.Func<bool> condition)
        {
            assets.CompleteLoads();
            return condition();
        }
    }
}
