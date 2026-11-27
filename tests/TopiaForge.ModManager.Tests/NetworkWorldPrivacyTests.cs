using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The network world never exposes an address: what a host writes into kinds, mod ids and content keys, which may hold
    /// anything shaped like one, reaches no log; players are never named in what it logs, counts or reports, even when
    /// their ids look like addresses; and the world's own texts are this game's words.
    /// </summary>
    internal static class NetworkWorldPrivacyTests
    {
        private static readonly string[] Secrets = { "203.0.113.7", "play.example.org", "2001:db8" };

        internal static void Run()
        {
            HostWrittenTextReachesNoLog();
            PlayersAreNeverNamed();
        }

        private static void HostWrittenTextReachesNoLog()
        {
            var rig = new WorldRig();
            rig.UseScene(scene => scene.AddScene("Yard"));
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("p1");
            rig.JoinAndSynchronize(player);

            // A hostile host names its entities with address-shaped kinds, mod ids and content keys, all valid tokens.
            var pose = new NetPose(new NetVector3(1, 2, 3), NetQuaternion.Identity);
            var writer = new WorldWireWriter();
            writer.Begin(WorldMessages.Spawn);
            writer.WriteVarUInt(4);
            WorldEntryCodec.WriteSpawn(writer, new NetworkEntityId(90), "play.example.org", "203.0.113.7", 1, 0, new NetVector3(1, 1, 1), pose, default);
            WorldEntryCodec.WriteSpawn(writer, new NetworkEntityId(91), "asset.prefab", "203.0.113.7", 2, 0, new NetVector3(1, 1, 1), pose,
                WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Create("http://play.example.org/203.0.113.7.bundle", "[2001:db8::7]:27250")));
            WorldEntryCodec.WriteContent(writer, new NetworkEntityId(92), "x.y", "join 203.0.113.7:27250 at play.example.org", "203.0.113.7", 0, new NetVector3(1, 1, 1), pose, default);
            WorldEntryCodec.WriteSceneObject(writer, new NetworkEntityId(93), new SceneObjectKey(7), pose);
            var channel = player.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            channel.Inject(rig.Network.Host.Id, writer.Segment.ToArray());
            rig.Frames(5);
            That(player.Client.EntityCount == 4 && player.Counters().ReplicasUnbuildable >= 2 && player.Counters().SceneKeysUnbound >= 1,
                "the player mirrors what it cannot build, and counts it");

            // The world logs only its own words: realm changes, counts and channel failures.
            player.Control.Leave();
            rig.Frames(2);
            foreach (var line in player.Log.Lines.Concat(rig.Host.Log.Lines))
            {
                foreach (var secret in Secrets) That(!line.Contains(secret, StringComparison.OrdinalIgnoreCase), "'" + line + "' holds nothing the host wrote");
            }

            foreach (var text in new[] { NetworkWorldClient.HoldReason })
            {
                That(MultiplayerDiagnosticRedactor.Redact(text) == text, "'" + text + "' holds nothing address-shaped");
            }
        }

        private static void PlayersAreNeverNamed()
        {
            // Participant ids are opaque, so an engine may give ones that look like addresses: the world must never repeat them.
            var ids = new[] { "198.51.100.4:27250", "[2001:db8::4]:27250" };
            var rig = WorldBaselineSyncTests.Yard(4);
            var host = rig.Host;
            rig.Network.HostWindowMessages = 24;
            for (var index = 0; index < 30; index++) WorldBaselineSyncTests.SpawnRobot(host, index, 2048);
            rig.OpenToMultiplayer();
            var first = WorldBaselineSyncTests.AddPlayer(rig, ids[0]);
            var second = WorldBaselineSyncTests.AddPlayer(rig, ids[1]);
            rig.JoinAndSynchronize(first);
            rig.JoinAndSynchronize(second);

            // A stall that skips broadcasts, a rejoin, a malformed message from each player, and both leaving.
            first.Paused = true;
            var box = host.Scene.Scenes[0].Roots[0];
            for (var frame = 0; frame < 200; frame++)
            {
                box.Position = new NetVector3(frame * 0.05f, 0, 0);
                rig.Frame();
            }

            first.Paused = false;
            rig.Network.Rejoin(second.Control);
            var channel = host.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            channel.Inject(first.Control.Id, new byte[] { WorldMessages.Version, 99 });
            channel.Inject(second.Control.Id, new byte[] { WorldMessages.Version, WorldMessages.BaselineRequest, 7 });
            rig.Frames(60);
            That(host.Counters().Rebaselines > 0 && host.World.Channel.Discarded >= 2, "the players were skipped, and their malformed messages discarded");
            first.Control.Leave();
            second.Control.Leave();
            rig.Frames(3);

            var told = new List<string>();
            foreach (var peer in new[] { host, first, second })
            {
                told.AddRange(peer.Log.Lines);
                told.Add(peer.World.Progress.ToString());
            }

            foreach (var line in told)
            {
                foreach (var secret in ids.Concat(new[] { "198.51.100.4", "2001:db8" }))
                {
                    That(!line.Contains(secret, StringComparison.OrdinalIgnoreCase), "'" + line + "' names no player");
                }
            }

            var counters = typeof(NetworkWorldCounters).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            That(counters.Length > 20 && counters.All(property => property.PropertyType == typeof(long)), "the world's counters are counts and nothing else");
            var progress = typeof(NetworkWorldProgress).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            That(progress.All(property => property.PropertyType == typeof(int) || property.PropertyType == typeof(double) || property.PropertyType == typeof(NetworkWorldPhase)),
                "and its progress is a phase and numbers");
        }
    }
}
