using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// What authority routing (WS3.7) costs: offline, the decorators add nothing to a call; online at rest, a host keeping
    /// players' scopes and players holding adopted objects and shadows allocate nothing per frame; and with a player moving
    /// its own object every frame, what it allocates and sends is measured and reported.
    /// </summary>
    internal static class AuthorityCostTests
    {
        internal static void Run()
        {
            OfflineTheDecoratorsAddNothing();
            OnlineAtRestNothingIsAllocated();
            WhatAPlayerMovingItsObjectCosts();
        }

        private static void OfflineTheDecoratorsAddNothing()
        {
            var rig = new WorldRig();
            rig.UseScene(scene => scene.AddScene("Yard"));
            var host = rig.Host;
            var mod = new AuthorityMod(host);
            var prefab = mod.Load();
            var handle = mod.Spawn(prefab, 1).Value!;
            That(host.Runtime.Realm == NetRealm.Offline && host.Runtime.Spawns.Find(handle) != null, "offline, a spawn is canonical and recorded as before");
            var at = AuthorityMod.At(2);
            var inner = Allocated(() => mod.FakeEntities.SetTransform(handle, at));
            var decorated = Allocated(() => mod.Entities.SetTransform(handle, at));
            That(decorated == inner, "offline the entity decorator adds nothing to a move (" + decorated + " bytes against " + inner + ")");
            var frame = Allocated(() => rig.Frame(), 600);
            That(frame == 0, "and an offline frame allocates nothing (" + frame + " bytes over 600 frames)");
        }

        private static void OnlineAtRestNothingIsAllocated()
        {
            var rig = AuthorityRig.Create(players: 2);
            var host = rig.Host;
            var hosts = new AuthorityMod(host);
            var hostPrefab = hosts.Load();
            for (var index = 0; index < 3; index++) hosts.Spawn(hostPrefab, index);
            var mods = new List<AuthorityMod>();
            foreach (var player in rig.Players)
            {
                var mod = new AuthorityMod(player);
                mods.Add(mod);
                var prefab = mod.Load();
                for (var index = 0; index < 3; index++) mod.SpawnAsPlayer(prefab, 10 + index);
                for (var index = 0; index < 3; index++) mod.Spawn(prefab, index);
            }

            That(rig.Run(() => rig.Players.All(player => player.Client.OwnedObjects == 3 && player.Counters().ShadowsBound == 3), 120),
                "each player holds three adopted objects and three bound shadows");
            That(host.World.Proxies.ProxyCount == 2, "and the host keeps a scope for each");

            rig.Frames(240);
            var resting = Allocated(() => rig.Frame(), 600);
            That(resting == 0, "a host and two players with authority at rest allocate nothing per frame (" + resting + " bytes over 600 frames)");
            var watch = Stopwatch.StartNew();
            for (var index = 0; index < 600; index++) rig.Frame();
            watch.Stop();
            Console.WriteLine("Authority routing at rest: 0 bytes a frame, " + (watch.Elapsed.TotalMilliseconds * 1000 / 600).ToString("0.0", CultureInfo.InvariantCulture) +
                " microseconds a frame for a host and two players together (loopback, fakes).");
            GC.KeepAlive(mods);
        }

        private static void WhatAPlayerMovingItsObjectCosts()
        {
            var rig = AuthorityRig.Create(players: 1);
            var p1 = rig.Players[0];
            var mine = new AuthorityMod(p1);
            var handle = mine.SpawnAsPlayer(mine.Load(), 1).Value!;
            That(rig.Run(() => p1.Client.OwnedObjects == 1, 60), "the player's crate is adopted");
            rig.Frames(240);

            // The player's mod drags its crate every frame, on its intent, as a tool would.
            var x = 0f;
            Action drag = () =>
            {
                x += 0.01f;
                using (p1.Runtime.Intents.EnterPlayerCommand(WorldPeer.ModId)) mine.Entities.SetTransform(handle, new TransformState(new Vec3(x, 1, 0), Quat.Identity, new Vec3(1, 1, 1)));
                rig.Frame();
            };

            var perFrame = Allocated(drag, 600) / 600.0;
            var channel = p1.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            var log = new List<(ParticipantId To, byte[] Payload)>();
            channel.Log = log;
            for (var index = 0; index < 600; index++) drag();
            channel.Log = null;
            var bytes = log.Sum(item => (long)item.Payload.Length);
            var requests = log.Count;
            That(rig.Host.Counters().ChangesRefused == 0 && requests <= 600 / 2 + 31, "an honest player's moves keep to its pace (" + requests + " requests in 600 frames)");
            That(perFrame < 400, "moving its crate costs the player and the host little per frame (" + perFrame.ToString("0.0", CultureInfo.InvariantCulture) + " bytes)");
            Console.WriteLine("Authority routing with a player dragging its own object every frame: " + perFrame.ToString("0.0", CultureInfo.InvariantCulture) +
                " bytes allocated a frame across host and player, " + requests + " requests and " + bytes + " bytes sent over 600 frames (" +
                (bytes / 600.0).ToString("0.0", CultureInfo.InvariantCulture) + " bytes a frame).");
        }
    }
}
