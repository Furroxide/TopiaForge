using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The per-player scope on the host (WS3.7): what the host made for a player is that player's, kept through the 30
    /// seconds the session keeps a departed player's place, and ended once that grace is over, with nothing else touched:
    /// not another player's objects, not the mod's own spawns or records, not the mod's lifetime. A slot given again starts
    /// with fresh budgets, and hosting that stops ends every scope.
    /// </summary>
    internal static class ProxyParticipantTests
    {
        internal static void Run()
        {
            ALeavingPlayersObjectsEndAfterItsGrace();
            APlayerBackWithinItsGraceKeepsItsObjects();
            StoppingHostingEndsEveryScope();
            ASlotGivenAgainStartsAfresh();
        }

        private static void ALeavingPlayersObjectsEndAfterItsGrace()
        {
            var rig = AuthorityRig.Create(players: 2);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var p2 = rig.Players[1];
            var hosts = new AuthorityMod(host);
            var crate = hosts.Spawn(hosts.Load(), 0).Value!;
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            var handles = Enumerable.Range(1, 3).Select(index => mine.SpawnAsPlayer(prefab, index).Value!).ToList();
            var theirs = new AuthorityMod(p2);
            var other = theirs.SpawnAsPlayer(theirs.Load(), 5).Value!;
            That(rig.Run(() => p1.Client.OwnedObjects == 3 && p2.Client.OwnedObjects == 1, 60), "both players' spawns are adopted");
            That(host.World.Proxies.TryGetProxy(p1.Control.Id, out var proxy), "the host keeps the leaver's scope");
            var slot = proxy.Slot;
            var tracked = host.World.Proxies.Limiter.TrackedParticipants;
            That(host.Runtime.Spawns.Live().Count == 5, "five canonical records: the mod's crate and the players' four");

            p1.Control.Leave();
            rig.Frames(4);
            That(handles.All(handle => !handle.IsAlive), "the leaver keeps nothing of the host's world: its own copies end with it");
            That(proxy.IsDeparted && AuthorityRig.OwnedBy(host, slot).Count == 3, "on the host its objects stay through its grace");
            rig.Clock.Advance(SpawnAuthorityRules.DepartureGraceMicros - 1_000_000);
            rig.Frames(2);
            That(AuthorityRig.OwnedBy(host, slot).Count == 3 && host.World.Proxies.ProxyCount == 2, "for all thirty seconds");
            rig.Clock.Advance(1_000_000);
            rig.Frames(2);
            That(AuthorityRig.OwnedBy(host, slot).Count == 0 && !host.World.Proxies.TryGetProxy(p1.Control.Id, out _) && host.World.Proxies.ProxyCount == 1,
                "then its scope is cleaned up, and its objects end");
            That(rig.Run(() => AuthorityRig.Live(p2).Count == 2, 60), "for everyone: the other player sees the mod's crate and its own");

            // Only the leaver's spawns retire: the mod's own spawn and record, the other player's object and the mod stay.
            That(crate.IsAlive && host.Runtime.Spawns.Find(crate) != null && host.Runtime.Spawns.Live().Count == 2, "the mod's crate and its record stand");
            That(other.IsAlive && p2.Client.OwnedObjects == 1 && host.World.Proxies.TryGetProxy(p2.Control.Id, out var stays) && AuthorityRig.OwnedBy(host, stays.Slot).Count == 1,
                "the other player's object stands, still its own");
            That(!hosts.Lifetime.IsStopping && hosts.Spawn(hosts.Load(), 9).Succeeded, "and the mod runs on, its lifetime untouched");
            That(host.World.Proxies.Limiter.TrackedParticipants == tracked - 1 && host.Counters().PlayersCleanedUp == 1 && host.Counters().PlayerObjectsEnded == 3,
                "the leaver's budgets are forgotten with its slot");
        }

        private static void APlayerBackWithinItsGraceKeepsItsObjects()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var prefab = mine.Load();
            mine.SpawnAsPlayer(prefab, 1);
            mine.SpawnAsPlayer(prefab, 2);
            That(rig.Run(() => p1.Client.OwnedObjects == 2, 60), "the player's spawns are adopted");
            That(host.World.Proxies.TryGetProxy(p1.Control.Id, out var proxy), "the host keeps its scope");

            p1.Control.Leave();
            rig.Frames(4);
            rig.Clock.Advance(10_000_000);
            rig.JoinAndSynchronize(p1);
            That(!proxy.IsDeparted && host.World.Proxies.TryGetProxy(p1.Control.Id, out var back) && ReferenceEquals(back, proxy), "back within its grace, the player has its scope");
            rig.Clock.Advance(SpawnAuthorityRules.DepartureGraceMicros + 10_000_000);
            rig.Frames(4);
            That(AuthorityRig.OwnedBy(host, proxy.Slot).Count == 2 && AuthorityRig.Live(p1).Count == 2, "and its objects, long after the grace would have ended");

            // A stream that drops and comes back keeps everything, the player's own handles included.
            var handle = mine.SpawnAsPlayer(prefab, 3).Value!;
            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60), "a new spawn is adopted");
            rig.Network.Rejoin(p1.Control);
            rig.Clock.Advance(SpawnAuthorityRules.DepartureGraceMicros + 1_000_000);
            That(rig.Run(() => p1.World.Progress.Phase == NetworkWorldPhase.Synchronized, 120) && handle.IsAlive && p1.Client.OwnedObjects == 1 &&
                 AuthorityRig.OwnedBy(host, proxy.Slot).Count == 3, "a link that comes back keeps the player's handle bound, and the host its objects");

            // Gone for good, it is cleaned up.
            p1.Control.Leave();
            rig.Frames(4);
            rig.Clock.Advance(SpawnAuthorityRules.DepartureGraceMicros);
            rig.Frames(2);
            That(AuthorityRig.Live(host).Count == 0 && host.World.Proxies.ProxyCount == 0, "a player gone for good leaves nothing behind");
        }

        private static void StoppingHostingEndsEveryScope()
        {
            var rig = AuthorityRig.Create(players: 2);
            var host = rig.Host;
            var hosts = new AuthorityMod(host);
            var crate = hosts.Spawn(hosts.Load(), 0).Value!;
            foreach (var player in rig.Players)
            {
                var mod = new AuthorityMod(player);
                var prefab = mod.Load();
                mod.SpawnAsPlayer(prefab, 1);
                mod.SpawnAsPlayer(prefab, 2);
            }

            That(rig.Run(() => rig.Players.All(player => player.Client.OwnedObjects == 2), 60), "both players' spawns are adopted");
            host.Control.StopHosting();
            rig.Frames(4);
            That(host.Runtime.Realm == NetRealm.Offline && !host.World.Proxies.IsActive && host.World.Proxies.ProxyCount == 0, "hosting stopped: no scope is left");
            That(AuthorityRig.Live(host).Count == 1 && crate.IsAlive && host.Counters().PlayersCleanedUp == 2 && host.Counters().PlayerObjectsEnded == 4,
                "every player's object ended at once, and the mod's crate stands");
            That(host.World.Proxies.OwnLoads == 0 && host.World.Proxies.Limiter.TrackedParticipants == 0, "and the host holds no load or budget for anyone");
        }

        private static void ASlotGivenAgainStartsAfresh()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            for (uint provisional = 1; provisional <= 8; provisional++) SpawnAbuseTests.Inject(rig, p1, SpawnAbuseTests.Spawn(provisional));
            rig.Frames(3);
            That(host.World.Proxies.TryGetProxy(p1.Control.Id, out var first) && AuthorityRig.OwnedBy(host, first.Slot).Count == 8, "the first player spends its whole burst");
            var slot = first.Slot;
            p1.Control.Leave();
            rig.Frames(4);
            rig.Clock.Advance(SpawnAuthorityRules.DepartureGraceMicros);
            rig.Frames(2);
            That(host.World.Proxies.ProxyCount == 0, "and leaves; its scope is cleaned up");

            var p2 = rig.AddPlayer("p2");
            rig.JoinAndSynchronize(p2);
            var log = SpawnAbuseTests.Log(rig);
            for (uint provisional = 1; provisional <= 8; provisional++) SpawnAbuseTests.Inject(rig, p2, SpawnAbuseTests.Spawn(provisional));
            rig.Frames(3);
            That(host.World.Proxies.TryGetProxy(p2.Control.Id, out var second) && second.Slot == slot, "the next player is given the free slot");
            That(SpawnAbuseTests.Verdicts(log, p2).Count(verdict => verdict.Id.IsValid) == 8, "with a whole burst of its own, not what the leaver left of it");
        }
    }
}
