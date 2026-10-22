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
    /// refused. And a replica built through a mod context's own handles, which ends with that context, is built again.
    /// </summary>
    internal static class AuthorityAssetLoadTests
    {
        internal static void Run()
        {
            TheHostsOwnLoadGivesWayToItsModsLoad();
            APlayersReplicaLoadGivesWayToItsModsLoad();
            AReplicaOutlivesTheModContextWhoseHandlesBuiltIt();
            ALoadOffTheGameThreadTouchesNothingOfTheWorlds();
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
