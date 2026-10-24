using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A hostile joined player (WS3.7), writing raw requests past its own runtime: a flood of spawns is throttled and
    /// bounded; an asset or kind the host does not let players have is refused before a file is touched; a forged
    /// provisional id or shadow number reaches nothing; and nothing the player does not own moves or ends.
    /// </summary>
    internal static class SpawnAbuseTests
    {
        internal static void Run()
        {
            AFloodOfSpawnsIsThrottled();
            WhatAFloodLeavesIsBounded();
            TheHostMakesOnlyWhatPlayersMayHave();
            ADevicesNameOpensNothing();
            ForgedIdentitiesReachNothing();
            NoPlayerChangesWhatIsNotItsOwn();
            PlayersPastTheSlotsAreToldTheHostIsBusy();
        }

        private static void AFloodOfSpawnsIsThrottled()
        {
            var rig = AuthorityRig.Create(players: 2);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var log = Log(rig);
            for (uint provisional = 1; provisional <= 40; provisional++) Inject(rig, p1, Spawn(provisional, x: provisional));
            rig.Frames(3);
            var verdicts = Verdicts(log, p1);
            That(verdicts.Count == 40 && verdicts.Take(8).All(verdict => verdict.Id.IsValid) &&
                 verdicts.Skip(8).All(verdict => verdict.Reason == SpawnRefusal.RateLimited), "forty spawns at once: the burst of eight is made, and the rest throttled");
            That(host.World.Proxies.TryGetProxy(p1.Control.Id, out var proxy) && AuthorityRig.OwnedBy(host, proxy.Slot).Count == 8, "eight objects stand, the player's");
            That(p1.Counters().VerdictsIgnored == 40, "its own runtime ignores verdicts for spawns it never predicted");

            // Sustained, the host's rate holds: four a second.
            rig.Clock.Advance(1_000_000);
            for (uint provisional = 41; provisional <= 50; provisional++) Inject(rig, p1, Spawn(provisional, x: provisional));
            rig.Frames(3);
            verdicts = Verdicts(log, p1).Skip(40).ToList();
            That(verdicts.Count(verdict => verdict.Id.IsValid) == 4 && verdicts.Count(verdict => verdict.Reason == SpawnRefusal.RateLimited) == 6,
                "a second later, four more pass and six are throttled");

            // However slowly it asks, the player never owns more than the quota.
            for (uint provisional = 51; AuthorityRig.OwnedBy(host, proxy.Slot).Count < PermissionPolicy.DefaultMaximumLiveSpawnsPerPlayer && provisional < 200; provisional++)
            {
                rig.Clock.Advance(300_000);
                Inject(rig, p1, Spawn(provisional, x: provisional));
                rig.Frames(2);
            }

            rig.Clock.Advance(2_000_000);
            Inject(rig, p1, Spawn(500));
            rig.Frames(3);
            That(AuthorityRig.OwnedBy(host, proxy.Slot).Count == 64 && Verdicts(log, p1).Last().Reason == SpawnRefusal.QuotaExceeded,
                "past sixty-four live objects the player is refused for its quota");

            // Its flood takes nothing from anyone else.
            var theirs = new AuthorityMod(rig.Players[1]);
            var honest = theirs.SpawnAsPlayer(theirs.Load(), 1).Value!;
            That(rig.Run(() => rig.Players[1].Client.OwnedObjects == 1, 60) && honest.IsAlive && theirs.Ui.Toasts.Count == 0, "the other player's spawn is adopted as ever");
        }

        private static void WhatAFloodLeavesIsBounded()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];

            // The player stops reading, so its window fills: what the host owes it queues, up to a bound, and the rest is dropped.
            rig.Network.HostWindowMessages = 8;
            p1.Paused = true;
            for (uint provisional = 1; provisional <= 400; provisional++) Inject(rig, p1, Spawn(provisional));
            rig.Frames(3);
            That(host.World.Proxies.TryGetProxy(p1.Control.Id, out var proxy), "the host keeps the player's scope");
            That(proxy.Verdicts.Count <= ProxyParticipant.MaximumVerdicts && host.Counters().VerdictsDropped > 0 && proxy.Spawns.Count <= ProxyParticipant.MaximumSpawns,
                "what the host keeps for it is bounded (" + proxy.Verdicts.Count + " verdicts owed, " + host.Counters().VerdictsDropped + " dropped, " + proxy.Spawns.Count + " tracked)");
            That(host.Counters().SpawnsAdopted == 8 && host.Counters().SpawnsRefused == 392, "eight made, the rest refused");

            // The player reads again: the verdicts owed go out in order as its window allows.
            p1.Paused = false;
            rig.Network.HostWindowMessages = InternalSystemLimits.HostWindowMessages;
            That(rig.Run(() => proxy.Verdicts.Count == 0, 120), "and what was owed is delivered");
        }

        private static void TheHostMakesOnlyWhatPlayersMayHave()
        {
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var other = host.Replication.Bind("io.example.other", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), host.Lifetime);
            other.RegisterSpawnHandler("tests.other", new RecordingSpawnHandler(host.Scene, () => other));
            var log = Log(rig);
            var bundles = host.Assets.BundleLoads;
            var cases = new (string What, byte[] Request, SpawnRefusal Reason)[]
            {
                ("an asset of a mod the host does not share, as a client-local mod's", Spawn(1, mod: "io.example.other"), SpawnRefusal.NotShared),
                ("a bundle outside the mod's package", Spawn(2, bundle: "../outside.bundle"), SpawnRefusal.NotPermitted),
                ("a bundle named by a rooted path", Spawn(3, bundle: "/assets/world.bundle"), SpawnRefusal.NotPermitted),
                ("a bundle on a drive", Spawn(4, bundle: "C:/world.bundle"), SpawnRefusal.NotPermitted),
                ("an asset that does not say where it comes from", Kind(5, MultiplayerSpawnKinds.AssetPrefab, new byte[] { 1, 2, 3 }), SpawnRefusal.NotPermitted),
                ("a kind the runtime keeps for itself", Kind(6, MultiplayerSpawnKinds.SceneObject, Array.Empty<byte>()), SpawnRefusal.NotPermitted),
                ("a kind nobody makes for players", Kind(7, "tests.none", Array.Empty<byte>()), SpawnRefusal.NotShared),
                ("another mod's kind, named as this mod's", Kind(8, "tests.other", Array.Empty<byte>()), SpawnRefusal.NotShared)
            };

            foreach (var (_, request, _) in cases) Inject(rig, p1, request);
            rig.Frames(3);
            var verdicts = Verdicts(log, p1);
            for (var index = 0; index < cases.Length; index++)
            {
                var verdict = verdicts.FirstOrDefault(item => item.Provisional == index + 1);
                That(verdict.Provisional == index + 1 && verdict.Reason == cases[index].Reason, cases[index].What + " is refused as " + cases[index].Reason + " (" + verdict.Reason + ")");
            }

            That(AuthorityRig.Live(host).Count == 0 && host.Assets.BundleLoads == bundles && host.Scene.AllObjects().Count(item => item.Name.StartsWith("tests.", StringComparison.Ordinal)) == 0,
                "nothing was made, no file was touched, and no mod code ran");

            // The same request, of this mod's crate, is made.
            rig.Clock.Advance(2_000_000);
            Inject(rig, p1, Spawn(9));
            rig.Frames(3);
            That(Verdicts(log, p1).Last().Provisional == 9 && Verdicts(log, p1).Last().Id.IsValid && AuthorityRig.Live(host).Count == 1, "while this mod's crate is made");
        }

        private static void ADevicesNameOpensNothing()
        {
            // Windows opens a device, not a file, for a name such as CON or COM1, in any case and with any extension. A hostile
            // player's spawn that names one is refused before the host's loader opens anything.
            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var log = Log(rig);
            var bundles = host.Assets.BundleLoads;
            var devices = new[] { "assets/CON", "assets/con.bundle", "Nul", "aux.bundle", "assets/CoM1.bundle", "LPT9", "PRN/world.bundle", "assets/lpt0.tar.gz",
                "assets/com\u00B9.bundle", "assets/Con .bundle" };
            for (var index = 0; index < devices.Length; index++)
            {
                // Past the host's burst of eight the player waits for the host to take them in, so each is refused by the rule,
                // never by the pace.
                if (index == 8)
                {
                    rig.Frames(3);
                    rig.Clock.Advance(2_000_000);
                }

                Inject(rig, p1, Spawn((uint)index + 1, bundle: devices[index]));
            }

            rig.Frames(3);
            var verdicts = Verdicts(log, p1);
            for (var index = 0; index < devices.Length; index++)
            {
                var verdict = verdicts.FirstOrDefault(item => item.Provisional == index + 1);
                That(verdict.Provisional == index + 1 && verdict.Reason == SpawnRefusal.NotPermitted,
                    "a spawn from '" + devices[index] + "', a device's name, is refused as NotPermitted (" + verdict.Reason + ")");
            }

            That(host.Assets.BundleLoads == bundles && AuthorityRig.Live(host).Count == 0, "and the host opened nothing and made nothing");

            // Names that only start like a device's are ordinary names, and the host loads and makes them.
            rig.Clock.Advance(2_000_000);
            var ordinary = new[] { "assets/console.bundle", "assets/com10.bundle", "nullable/x.bundle" };
            for (var index = 0; index < ordinary.Length; index++) Inject(rig, p1, Spawn((uint)(devices.Length + index + 1), bundle: ordinary[index], x: index + 2));
            rig.Frames(3);
            var made = Verdicts(log, p1).Where(verdict => verdict.Provisional > devices.Length).ToList();
            That(made.Count == ordinary.Length && made.All(verdict => verdict.Id.IsValid) && AuthorityRig.Live(host).Count == ordinary.Length &&
                 host.Assets.BundleLoads == bundles + ordinary.Length,
                "while '" + string.Join("', '", ordinary) + "' are loaded and made (" + string.Join(", ", made.Select(verdict => verdict.Reason)) + ")");
        }

        private static void ForgedIdentitiesReachNothing()
        {
            var rig = AuthorityRig.Create(players: 2);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var p2 = rig.Players[1];
            var log = Log(rig);

            // A provisional id asked for again, with another asset and place, is the same spawn: the host answers as before.
            Inject(rig, p1, Spawn(5, x: 1));
            rig.Frames(3);
            var first = Verdicts(log, p1).Single();
            Inject(rig, p1, Spawn(5, asset: "Barrel", x: 9));
            rig.Frames(3);
            var again = Verdicts(log, p1).Last();
            That(first.Id.IsValid && again.Id.Equals(first.Id) && AuthorityRig.Live(host).Count == 1 && AuthorityRig.Live(host, "Barrel").Count == 0,
                "a provisional id reused makes nothing new: the host repeats its verdict");

            // Provisional ids are each player's own: another player's id names only its own spawn.
            Inject(rig, p2, Spawn(5, x: 3));
            rig.Frames(3);
            var theirs = Verdicts(log, p2).Single();
            That(theirs.Id.IsValid && !theirs.Id.Equals(first.Id) && AuthorityRig.Live(host).Count == 2, "the other player's provisional id five is its own spawn");
            Inject(rig, p2, Destroy(AuthorityTarget.Provisional(5)));
            That(rig.Run(() => !host.Host.Knows(theirs.Id), 30) && host.Host.Knows(first.Id) && host.Counters().DestroysApplied == 1,
                "and destroying its id five ends its own, never the first player's");
            Inject(rig, p1, Destroy(AuthorityTarget.Provisional(6)));
            rig.Frames(3);
            That(host.Counters().ChangesRefused == 1 && host.Host.Knows(first.Id), "an id the player never asked for names nothing");

            // A player's spawn carries no shadow number, so no shadow binds to it, and it takes none of the host's numbering.
            That(rig.Run(() => p2.Client.Knows(first.Id), 60), "the other player mirrors the first player's crate");
            var shadowing = new AuthorityMod(p2);
            var shadow = shadowing.Spawn(shadowing.Load(), 1).Value!;
            That(shadow.IsAlive && p2.Client.WaitingShadows == 1 && p2.Counters().ShadowsBound == 0, "a scripted spawn there does not bind to a player's crate");
            var hosts = new AuthorityMod(host);
            var scripted = hosts.Spawn(hosts.Load(), 1).Value!;
            That(host.Runtime.Spawns.Find(scripted)!.ShadowKey == 1, "the host's script numbers from one, whatever players made");
            That(rig.Run(() => p2.Counters().ShadowsBound == 1, 60) && host.Scene.TryGetNetworkId(scripted, out var scriptedId) &&
                 AuthorityRig.Object(shadow).Mark == scriptedId.Value, "and the shadow binds to the host's scripted crate");

            // Nothing in a request names an origin: one with a number appended is malformed and discarded whole.
            var discarded = host.World.Channel.Discarded;
            var forged = Spawn(10).Concat(new byte[] { 1 }).ToArray();
            HostChannel(rig).Inject(p1.Control.Id, forged);
            rig.Frames(2);
            That(host.Counters().MalformedRequests == 1 && host.World.Channel.Discarded == discarded + 1, "a request with an origin appended is discarded");

            // Neither are a player's messages in the host's voice heard: an announcement naming an origin, or a verdict.
            var entities = host.Host.EntityCount;
            var spoof = new WorldWireWriter();
            spoof.Begin(WorldMessages.Spawn);
            spoof.WriteVarUInt(1);
            WorldEntryCodec.WriteSpawn(spoof, new NetworkEntityId(900), MultiplayerSpawnKinds.AssetPrefab, WorldPeer.ModId, 1, 0, new NetVector3(1, 1, 1),
                new NetPose(new NetVector3(0, 0, 0), NetQuaternion.Identity), WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Create(AuthorityMod.Bundle, "Crate")));
            HostChannel(rig).Inject(p1.Control.Id, spoof.Segment.ToArray());
            var verdict = new WorldWireWriter();
            AuthorityWire.WriteAdopted(verdict, 1, first.Id);
            HostChannel(rig).Inject(p1.Control.Id, verdict.Segment.ToArray());
            AuthorityWire.WriteRefused(verdict, 1, SpawnRefusal.NotPermitted);
            HostChannel(rig).Inject(p1.Control.Id, verdict.Segment.ToArray());
            rig.Frames(2);
            That(host.World.Channel.Discarded == discarded + 4 && host.Host.EntityCount == entities, "a player's announcement or verdict is discarded on the host");
        }

        private static void NoPlayerChangesWhatIsNotItsOwn()
        {
            var rig = AuthorityRig.Create(players: 3, boxes: 1);
            var host = rig.Host;
            var p1 = rig.Players[0];
            var p2 = rig.Players[1];
            var p3 = rig.Players[2];
            var mine = new AuthorityMod(p1);
            var theirs = new AuthorityMod(p2);
            var hosts = new AuthorityMod(host);
            var own = mine.SpawnAsPlayer(mine.Load(), 1).Value!;
            var other = theirs.SpawnAsPlayer(theirs.Load(), 2).Value!;
            var crate = hosts.Spawn(hosts.Load(), 3).Value!;
            var gone = hosts.Spawn(hosts.Load(), 4).Value!;
            That(rig.Run(() => p1.Client.OwnedObjects == 1 && p2.Client.OwnedObjects == 1, 60), "both players' crates are adopted");
            var ownId = new NetworkEntityId(AuthorityRig.Object(own).Mark);
            var otherId = new NetworkEntityId(AuthorityRig.Object(other).Mark);
            var crateId = new NetworkEntityId(AuthorityRig.Object(crate).Mark);
            var goneId = new NetworkEntityId(AuthorityRig.Object(gone).Mark);
            var box = host.Scene.Scenes[0].Roots[0];
            box.Position = new NetVector3(0, 2, 0);
            gone.Dispose();
            That(rig.Run(() => box.Mark != 0 && !host.Host.Knows(goneId), 30), "the yard's box moved, so it is one of the host's entities, and one crate is gone");
            var boxId = new NetworkEntityId(box.Mark);

            var refused = host.Counters().ChangesRefused;
            var targets = new[] { otherId, crateId, boxId, goneId, new NetworkEntityId(9_999) };
            foreach (var target in targets)
            {
                Inject(rig, p1, Move(AuthorityTarget.Entity(target), 40));
                Inject(rig, p1, Destroy(AuthorityTarget.Entity(target)));
            }

            rig.Frames(4);
            That(host.Counters().ChangesRefused == refused + 10, "another player's object, the host's, a scene object, one destroyed and one never made: every change is refused (" +
                (host.Counters().ChangesRefused - refused) + ")");
            That(other.IsAlive && crate.IsAlive && box.IsAlive && host.Host.Knows(otherId) && host.Host.Knows(crateId) &&
                 !Near(((FakeWorldObject)FakeEntities.ObjectOf(other)!).Position, new NetVector3(40, 1, 0)) && Near(box.Position, new NetVector3(0, 2, 0)),
                "nothing moved and nothing ended");

            // A player with nothing of its own on the host has no scope, so nothing to change, and gets none by asking.
            var scopes = host.World.Proxies.ProxyCount;
            Inject(rig, p3, Destroy(AuthorityTarget.Entity(crateId)));
            Inject(rig, p3, Move(AuthorityTarget.Entity(ownId), 40));
            rig.Frames(3);
            That(host.World.Proxies.ProxyCount == scopes && host.Host.Knows(crateId) && host.Counters().ChangesRefused == refused + 12, "a player that made nothing changes nothing");

            // Its own object, the player may move.
            Inject(rig, p1, Move(AuthorityTarget.Entity(ownId), 40));
            rig.Frames(3);
            That(host.Host.TryGetHosted(ownId, out var hosted) && Near(((FakeWorldObject)hosted.Object!).Position, new NetVector3(40, 1, 0)), "while its own moves");
        }

        private static void PlayersPastTheSlotsAreToldTheHostIsBusy()
        {
            var rig = AuthorityRig.Create(players: 0);
            var host = rig.Host;
            var log = Log(rig);
            var controls = new List<LoopbackControl>();
            for (var index = 0; index <= WorldEntryCodec.MaximumOwnerSlot; index++)
            {
                var control = rig.Network.AddPlayer("x" + index);
                rig.Network.Join(control);
                controls.Add(control);
            }

            rig.Frames(2);
            foreach (var control in controls) Inject(rig, control.Id, Spawn(1));
            rig.Frames(3);
            var adopted = controls.Take(WorldEntryCodec.MaximumOwnerSlot).Count(control => Verdicts(log, control.Id).SingleOrDefault().Id.IsValid);
            That(host.World.Proxies.ProxyCount == WorldEntryCodec.MaximumOwnerSlot && adopted == WorldEntryCodec.MaximumOwnerSlot,
                "slots one to 255 each hold a player's scope and its crate (" + host.World.Proxies.ProxyCount + " scopes, " + adopted + " adopted)");
            var waiting = controls[WorldEntryCodec.MaximumOwnerSlot];
            That(Verdicts(log, waiting.Id).SingleOrDefault().Reason == SpawnRefusal.HostBusy && !host.World.Proxies.TryGetProxy(waiting.Id, out _),
                "a player past the slots is told the host is busy, and gets no scope");

            // A player leaves; once its grace is over its slot is free, and the waiting player is given it.
            That(host.World.Proxies.TryGetProxy(controls[0].Id, out var leaver), "the first player holds a slot");
            var slot = leaver.Slot;
            rig.Network.Leave(controls[0]);
            rig.Frames(2);
            rig.Clock.Advance(SpawnAuthorityRules.DepartureGraceMicros);
            rig.Frames(2);
            Inject(rig, waiting.Id, Spawn(2));
            rig.Frames(3);
            That(host.World.Proxies.TryGetProxy(waiting.Id, out var given) && given.Slot == slot && Verdicts(log, waiting.Id).Last().Id.IsValid,
                "once the leaver's grace is over, the waiting player is given its slot, and its crate is made");
        }

        internal static LoopbackChannel HostChannel(WorldRig rig) => rig.Host.Control.Channel(InternalSystemChannelKind.WorldReplication)!;

        internal static void Inject(WorldRig rig, WorldPeer from, byte[] message) => HostChannel(rig).Inject(from.Control.Id, message);

        internal static void Inject(WorldRig rig, ParticipantId from, byte[] message) => HostChannel(rig).Inject(from, message);

        /// <summary>Starts copying every message the host sends, to read its verdicts.</summary>
        internal static List<(ParticipantId To, byte[] Payload)> Log(WorldRig rig)
        {
            var log = new List<(ParticipantId To, byte[] Payload)>();
            HostChannel(rig).Log = log;
            return log;
        }

        /// <summary>The verdicts the host sent <paramref name="player"/>, in order: an adoption's id, or a refusal's reason.</summary>
        internal static List<(uint Provisional, NetworkEntityId Id, SpawnRefusal Reason)> Verdicts(List<(ParticipantId To, byte[] Payload)> log, WorldPeer player) =>
            Verdicts(log, player.Control.Id);

        internal static List<(uint Provisional, NetworkEntityId Id, SpawnRefusal Reason)> Verdicts(List<(ParticipantId To, byte[] Payload)> log, ParticipantId player)
        {
            var verdicts = new List<(uint, NetworkEntityId, SpawnRefusal)>();
            var reader = new WorldWireReader();
            foreach (var (to, payload) in log)
            {
                if (!to.Equals(player)) continue;
                reader.Reset(payload);
                if (!reader.TryReadHeader(out var type)) continue;
                if (type == AuthorityMessages.SpawnAdopted && AuthorityWire.TryReadAdopted(reader, out var adopted, out var id)) verdicts.Add((adopted, id, SpawnRefusal.None));
                else if (type == AuthorityMessages.SpawnRefused && AuthorityWire.TryReadRefused(reader, out var refused, out var reason)) verdicts.Add((refused, NetworkEntityId.None, reason));
            }

            return verdicts;
        }

        internal static byte[] Spawn(uint provisional, string mod = WorldPeer.ModId, string bundle = AuthorityMod.Bundle, string asset = "Crate", float x = 1) =>
            Kind(provisional, MultiplayerSpawnKinds.AssetPrefab, WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Create(bundle, asset)), mod, x);

        internal static byte[] Kind(uint provisional, string kind, byte[] payload, string mod = WorldPeer.ModId, float x = 1)
        {
            var writer = new WorldWireWriter();
            AuthorityWire.WriteSpawnRequest(writer, provisional, mod, kind, IntentSource.UiCallback, false, new NetVector3(1, 1, 1),
                new NetPose(new NetVector3(x, 1, 0), NetQuaternion.Identity), payload);
            return writer.Segment.ToArray();
        }

        internal static byte[] Move(AuthorityTarget target, float x)
        {
            var writer = new WorldWireWriter();
            AuthorityWire.WriteTransformRequest(writer, target, new NetVector3(1, 1, 1), new NetPose(new NetVector3(x, 1, 0), NetQuaternion.Identity));
            return writer.Segment.ToArray();
        }

        internal static byte[] Destroy(AuthorityTarget target)
        {
            var writer = new WorldWireWriter();
            AuthorityWire.WriteDestroyRequest(writer, target);
            return writer.Segment.ToArray();
        }
    }
}
