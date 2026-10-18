using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The world replication wire: varints, strings and transform records read strictly, every entry round trips, every
    /// field a host could abuse is refused, and seeded hostile messages never get past a joined player's reader.
    /// </summary>
    internal static class NetworkWorldWireTests
    {
        internal static void Run()
        {
            VarintsAreMinimalAndBounded();
            StringsAndBytesAreBoundedAndValid();
            TransformRecordsAreExact();
            EveryEntryRoundTrips();
            EntriesRefuseWhatTheRuntimeWouldNot();
            AssetDescriptionsRoundTripAndRefuseGarbage();
            HostileMessagesNeverGetPastTheReader();
            AHostNamingAssetsWithoutEndLoadsABoundedFew();
            AHostNamingPrefabsWithoutEndLoadsABoundedFew();
            KeysOfScenesNeverLoadedAreBounded();
            ABaselineNamesItsWorld();
        }

        private static void VarintsAreMinimalAndBounded()
        {
            foreach (var value in new ulong[] { 0, 1, 127, 128, 16_383, 16_384, uint.MaxValue, long.MaxValue, ulong.MaxValue })
            {
                var writer = new WorldWireWriter(64);
                writer.WriteVarUInt(value);
                var reader = Read(writer);
                That(reader.TryReadVarUInt(ulong.MaxValue, out var read) && read == value && reader.AtEnd, "varint " + value + " round trips");
            }

            foreach (var bytes in new[] { new byte[] { 0x80, 0x00 }, new byte[] { 0xFF, 0x00 }, new byte[] { 0x80, 0x80, 0x00 }, Enumerable.Repeat((byte)0xFF, 10).Concat(new byte[] { 0x01 }).ToArray(), new byte[] { 0x80 } })
            {
                var reader = new WorldWireReader();
                reader.Reset(bytes);
                That(!reader.TryReadVarUInt(ulong.MaxValue, out _) && reader.Position == 0, "a non-minimal, overlong or truncated varint is refused and consumes nothing (" + BitConverter.ToString(bytes) + ")");
            }

            var bounded = new WorldWireReader();
            bounded.Reset(new byte[] { 0x81, 0x01 });
            That(!bounded.TryReadVarUInt(128, out _) && bounded.TryReadVarUInt(129, out var at) && at == 129, "a varint past its maximum is refused");
        }

        private static void StringsAndBytesAreBoundedAndValid()
        {
            var writer = new WorldWireWriter(256);
            writer.WriteString("Kränzchen ☃");
            writer.WriteBytes(new byte[] { 1, 2, 3 });
            var reader = Read(writer);
            That(reader.TryReadString(64, out var text) && text == "Kränzchen ☃" && reader.TryReadBytes(16, out var bytes) && bytes.SequenceEqual(new byte[] { 1, 2, 3 }) && reader.AtEnd,
                "strings and bytes round trip");
            var longer = Read(writer);
            That(!longer.TryReadString(5, out _) && longer.Position == 0, "a string past its bound is refused and consumes nothing");
            foreach (var invalid in new[] { new byte[] { 2, 0xC3, 0x28 }, new byte[] { 3, 0xED, 0xA0, 0x80 }, new byte[] { 1, 0xFF }, new byte[] { 4, 0x41 } })
            {
                var bad = new WorldWireReader();
                bad.Reset(invalid);
                That(!bad.TryReadString(64, out _) && bad.Position == 0, "malformed or truncated UTF-8 is refused (" + BitConverter.ToString(invalid) + ")");
            }

            var full = new WorldWireWriter(16);
            full.Begin(WorldMessages.Despawn);
            Throws<InvalidOperationException>(() => full.WriteBytes(new byte[32]), "a writer refuses what does not fit");
        }

        private static void TransformRecordsAreExact()
        {
            var poses = new[]
            {
                new NetPose(new NetVector3(1.5f, -2.25f, 4095.5f), NetQuaternion.Identity),
                new NetPose(new NetVector3(-4096f, 0f, 12.001953125f), new NetQuaternion(0f, 0.7071068f, 0f, 0.7071068f), new NetVector3(3.5f, -63.9375f, 0f))
            };

            foreach (var pose in poses)
            {
                var writer = new WorldWireWriter(64);
                writer.WriteTransform(pose);
                That(writer.Length == (pose.HasVelocity ? 18 : 14), "a record is a flags byte and 13 or 17 bytes (" + writer.Length + ")");
                var reader = Read(writer);
                That(reader.TryReadTransform(out var read) && reader.AtEnd && Near(read.Position, pose.Position, 1e-6) && read.HasVelocity == pose.HasVelocity,
                    "a pose on the grid round trips exactly");
                var again = new WorldWireWriter(64);
                again.WriteTransform(read);
                That(again.Segment.SequenceEqual(writer.Segment), "and writes the same bytes again");
            }

            var good = new WorldWireWriter(64);
            good.WriteTransform(poses[0]);
            var bytes = good.Segment.ToArray();
            foreach (var mutate in new Action<byte[]>[] { b => b[0] = 2, b => b[0] = 1, b => b[13] |= 0x80 })
            {
                var copy = (byte[])bytes.Clone();
                mutate(copy);
                var reader = new WorldWireReader();
                reader.Reset(copy);
                That(!reader.TryReadTransform(out _) && reader.Position == 0, "a bad flag, a velocity the flag denies, or set spare bits are refused");
            }

            var truncated = new WorldWireReader();
            truncated.Reset(bytes.Take(10).ToArray());
            That(!truncated.TryReadTransform(out _), "a truncated record is refused");
        }

        private static void EveryEntryRoundTrips()
        {
            var pose = new NetPose(new NetVector3(3f, 4f, 5f), NetQuaternion.Identity);
            var writer = new WorldWireWriter();
            WorldEntryCodec.WriteSceneObject(writer, new NetworkEntityId(7), new SceneObjectKey(0xDEADBEEF01234567UL), pose);
            WorldEntryCodec.WriteSpawn(writer, new NetworkEntityId(8), "creator.content", "io.example.creator", 42, 3, new NetVector3(2f, 2f, 2f), pose, new byte[] { 9, 8, 7 });
            WorldEntryCodec.WriteContent(writer, new NetworkEntityId(uint.MaxValue), "worlds.arena", "arena/platform#0 ☃", "io.example.worlds", 0, new NetVector3(1f, 1f, 1f), pose, ReadOnlySpan<byte>.Empty);
            WorldEntryCodec.WriteRemoved(writer, new SceneObjectKey(1));
            var reader = Read(writer);
            var entry = new WorldEntry();
            That(WorldEntryCodec.TryRead(reader, entry) && entry.Source == WorldEntrySource.SceneObject && entry.Id.Value == 7 && entry.SceneKey.Value == 0xDEADBEEF01234567UL &&
                 Near(entry.Pose.Position, pose.Position), "a scene object entry round trips");
            That(WorldEntryCodec.TryRead(reader, entry) && entry.Source == WorldEntrySource.SdkSpawn && entry.Id.Value == 8 && entry.Kind == "creator.content" &&
                 entry.OwnerModId == "io.example.creator" && entry.OriginSequence == 42 && entry.OwnerSlot == 3 && entry.Scale.Equals(new NetVector3(2f, 2f, 2f)) &&
                 entry.Payload.SequenceEqual(new byte[] { 9, 8, 7 }), "an SDK spawn entry round trips");
            That(WorldEntryCodec.TryRead(reader, entry) && entry.Source == WorldEntrySource.WorldContent && entry.Id.Value == uint.MaxValue &&
                 entry.ContentKey == "arena/platform#0 ☃" && entry.Payload.Length == 0, "a world content entry round trips");
            That(WorldEntryCodec.TryRead(reader, entry) && entry.Source == WorldEntrySource.RemovedSceneObject && entry.SceneKey.Value == 1 && reader.AtEnd,
                "a removed scene object entry round trips");
        }

        private static void EntriesRefuseWhatTheRuntimeWouldNot()
        {
            var pose = new NetPose(NetVector3.Zero, NetQuaternion.Identity);
            var cases = new List<(string Name, Action<WorldWireWriter> Write)>
            {
                ("id zero", w => WorldEntryCodec.WriteSceneObject(w, default, new SceneObjectKey(1), pose)),
                ("a kind that is not a token", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), "Asset.Prefab", "io.example.mod", 1, 0, new NetVector3(1, 1, 1), pose, default)),
                ("a kind past 64 characters", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), new string('a', 65), "io.example.mod", 1, 0, new NetVector3(1, 1, 1), pose, default)),
                ("a mod id that is not one", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), "x.y", ".mod", 1, 0, new NetVector3(1, 1, 1), pose, default)),
                ("sequence zero", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), "x.y", "io.example.mod", 0, 0, new NetVector3(1, 1, 1), pose, default)),
                ("an owner past the slots", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), "x.y", "io.example.mod", 1, 256, new NetVector3(1, 1, 1), pose, default)),
                ("a zero scale", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), "x.y", "io.example.mod", 1, 0, new NetVector3(0, 1, 1), pose, default)),
                ("a NaN scale", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), "x.y", "io.example.mod", 1, 0, new NetVector3(float.NaN, 1, 1), pose, default)),
                ("an endless scale", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), "x.y", "io.example.mod", 1, 0, new NetVector3(1, float.PositiveInfinity, 1), pose, default)),
                ("a huge scale", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), "x.y", "io.example.mod", 1, 0, new NetVector3(1, 1, 20_000), pose, default)),
                ("a content key with a control character", w => WorldEntryCodec.WriteContent(w, new NetworkEntityId(1), "x.y", "a\u0001b", "io.example.mod", 0, new NetVector3(1, 1, 1), pose, default)),
                ("a content key past 256 units", w => WorldEntryCodec.WriteContent(w, new NetworkEntityId(1), "x.y", new string('k', 257), "io.example.mod", 0, new NetVector3(1, 1, 1), pose, default)),
                ("a description past 16 KiB", w => WorldEntryCodec.WriteSpawn(w, new NetworkEntityId(1), "x.y", "io.example.mod", 1, 0, new NetVector3(1, 1, 1), pose, new byte[InternalReplicationLimits.MaximumPayloadBytes + 1])),
                ("an unknown source", w => w.WriteByte(9))
            };

            foreach (var (name, write) in cases)
            {
                var writer = new WorldWireWriter();
                write(writer);
                That(!WorldEntryCodec.TryRead(Read(writer), new WorldEntry()), "an entry with " + name + " is refused");
            }
        }

        private static void AssetDescriptionsRoundTripAndRefuseGarbage()
        {
            var descriptor = MultiplayerAssetDescriptor.Create("assets/world.bundle", "Crate ☃");
            var decoded = WorldEntryCodec.DecodeAsset(WorldEntryCodec.EncodeAsset(descriptor));
            That(decoded.IsPortable && decoded.BundlePath == "assets/world.bundle" && decoded.AssetName == "Crate ☃", "an asset description round trips");
            That(WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Unknown).Length == 0 && !WorldEntryCodec.DecodeAsset(Array.Empty<byte>()).IsPortable,
                "a description without provenance is empty, and builds nothing");
            var trailing = WorldEntryCodec.EncodeAsset(descriptor).Concat(new byte[] { 0 }).ToArray();
            That(!WorldEntryCodec.DecodeAsset(trailing).IsPortable, "trailing bytes are refused");
            var control = new WorldWireWriter(64);
            control.WriteString("a\nb");
            control.WriteString("Crate");
            That(!WorldEntryCodec.DecodeAsset(control.Segment.ToArray()).IsPortable, "a path with a control character is refused");
            var random = new Random(3603);
            for (var round = 0; round < 2000; round++)
            {
                var bytes = new byte[random.Next(0, 40)];
                random.NextBytes(bytes);
                var garbage = WorldEntryCodec.DecodeAsset(bytes);
                That(!garbage.IsPortable || (garbage.BundlePath!.Length <= MultiplayerAssetDescriptor.MaximumBundlePathLength), "garbage never decodes past its bounds");
            }
        }

        private static void HostileMessagesNeverGetPastTheReader()
        {
            // A joined player reads whatever its host sends: valid traffic, mutated, truncated and extended, from a seed.
            var rig = new WorldRig();
            rig.UseScene(scene =>
            {
                var yard = scene.AddScene("Yard");
                for (var item = 0; item < 20; item++) scene.Add(yard, "Box", null, rigidbody: true, position: new NetVector3(item, 0, 0));
            });
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("p1");
            rig.JoinAndSynchronize(player);
            var channel = player.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            var samples = ValidMessages();
            var random = new Random(3604);
            var accepted = 0;
            for (var round = 0; round < 20_000; round++)
            {
                var bytes = (byte[])samples[random.Next(samples.Count)].Clone();
                switch (random.Next(4))
                {
                    case 0:
                        if (bytes.Length > 0) bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                        break;
                    case 1:
                        Array.Resize(ref bytes, random.Next(bytes.Length + 1));
                        break;
                    case 2:
                        var tail = new byte[random.Next(1, 8)];
                        random.NextBytes(tail);
                        bytes = bytes.Concat(tail).ToArray();
                        break;
                    default:
                        if (bytes.Length > 2) bytes[2 + random.Next(bytes.Length - 2)] = (byte)random.Next(256);
                        break;
                }

                channel.Inject(rig.Network.Host.Id, bytes);
                var before = player.World.Channel.Handled;
                player.Client.Receive();
                if (player.World.Channel.Handled > before) accepted++;
                That(player.Client.EntityCount <= NetworkWorldClient.MaximumEntities, "a hostile host cannot grow the mirror past its bound");
            }

            That(accepted > 0 && player.World.Channel.Discarded > 0 && player.Counters().MalformedMessages > 0,
                "some mutations still read and the rest are discarded, none throws (" + accepted + " read, " + player.World.Channel.Discarded + " discarded)");
            player.Control.Leave();
            rig.Frames(2);
            That(!player.World.IsOnline && player.Client.Suppressor.SuppressedCount == 0, "and leaving still restores everything");
        }

        /// <summary>
        /// A hostile host describes one asset spawn again and again, each time from a bundle and a prefab it makes up. Every
        /// load asks the owning mod's package files and is kept for the session, so a joined player loads a bounded few and
        /// refuses the rest; the host's own crates, from a bundle it loaded before, are still built.
        /// </summary>
        private static void AHostNamingAssetsWithoutEndLoadsABoundedFew()
        {
            var rig = new WorldRig();
            rig.UseScene(scene => scene.AddScene("Yard"));
            rig.OpenToMultiplayer();
            var first = rig.Host.SpawnCrate(1);
            var player = rig.AddPlayer("p1");
            rig.JoinAndSynchronize(player);
            That(player.Assets.Spawned.Count == 1, "the host's crate is built");
            var channel = player.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            var writer = new WorldWireWriter();
            var pose = new NetPose(new NetVector3(1, 2, 3), NetQuaternion.Identity);
            for (var round = 0; round < 2_000; round++)
            {
                writer.Begin(WorldMessages.Spawn);
                writer.WriteVarUInt(1);
                WorldEntryCodec.WriteSpawn(writer, new NetworkEntityId(900_000), "asset.prefab", WorldPeer.ModId, 9_999, 0, new NetVector3(1, 1, 1), pose,
                    WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Create("made-up/" + round + ".bundle", "Thing " + round)));
                channel.Inject(rig.Network.Host.Id, writer.Segment.ToArray());
                if (round % 50 == 49) player.Client.Receive();
            }

            player.Client.Receive();
            That(player.Assets.BundleLoads <= NetworkWorldClient.MaximumBundleLoads && player.Assets.PrefabLoads <= NetworkWorldClient.MaximumBundleLoads &&
                 player.Counters().AssetLoadsRefused > 1_000,
                "the player loaded a bounded few of the made-up assets and refused the rest (" + player.Assets.BundleLoads + " bundles, " + player.Assets.PrefabLoads +
                " prefabs, " + player.Counters().AssetLoadsRefused + " refused; malformed " + player.Counters().MalformedMessages + ", refused entities " +
                player.Counters().EntitiesRefused + ", unbuildable " + player.Counters().ReplicasUnbuildable + ", entities " + player.Client.EntityCount + ", discarded " +
                player.World.Channel.Discarded + ")");
            var second = rig.Host.SpawnCrate(2);
            That(rig.Run(() => player.Assets.Spawned.Count(replica => replica.Object.IsAlive) == 2, 30), "and the host's next crate is still built, from what it loaded before");
            GC.KeepAlive(first);
            GC.KeepAlive(second);
        }

        /// <summary>
        /// The same host names the one bundle the player does load, each time with a prefab name it makes up: the bundle is
        /// loaded once, and the prefabs are bounded on their own, since a load of each is kept for the session as well.
        /// </summary>
        private static void AHostNamingPrefabsWithoutEndLoadsABoundedFew()
        {
            var rig = new WorldRig();
            rig.UseScene(scene => scene.AddScene("Yard"));
            rig.OpenToMultiplayer();
            var crate = rig.Host.SpawnCrate(1);
            var player = rig.AddPlayer("p1");
            rig.JoinAndSynchronize(player);
            var channel = player.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            var writer = new WorldWireWriter();
            var pose = new NetPose(new NetVector3(1, 2, 3), NetQuaternion.Identity);
            var refused = player.Counters().AssetLoadsRefused;
            for (var round = 0; round < NetworkWorldClient.MaximumPrefabLoads + 500; round++)
            {
                writer.Begin(WorldMessages.Spawn);
                writer.WriteVarUInt(1);
                WorldEntryCodec.WriteSpawn(writer, new NetworkEntityId(900_000), "asset.prefab", WorldPeer.ModId, 9_999, 0, new NetVector3(1, 1, 1), pose,
                    WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Create("assets/world.bundle", "Thing " + round)));
                channel.Inject(rig.Network.Host.Id, writer.Segment.ToArray());
                if (round % 50 == 49) player.Client.Receive();
            }

            player.Client.Receive();
            That(player.Assets.BundleLoads == 1 && player.Assets.PrefabLoads == NetworkWorldClient.MaximumPrefabLoads && player.Counters().AssetLoadsRefused - refused >= 500,
                "the bundle loaded once, and a bounded few of the made-up prefabs (" + player.Assets.BundleLoads + " bundles, " + player.Assets.PrefabLoads + " prefabs, " +
                (player.Counters().AssetLoadsRefused - refused) + " refused)");
            GC.KeepAlive(crate);
        }

        /// <summary>
        /// A hostile host names scene objects this player never loaded, without end: objects it says are gone, and poses for
        /// objects it says are unbound. The player keeps both for scenes it may load later, but at most
        /// <see cref="NetworkWorldClient.MaximumPendingKeys"/> of each, counts the rest, and goes on mirroring the world.
        /// </summary>
        private static void KeysOfScenesNeverLoadedAreBounded()
        {
            var rig = WorldBaselineSyncTests.Yard(3);
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("p1");
            rig.JoinAndSynchronize(player);
            var channel = player.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            var writer = new WorldWireWriter();
            var random = new Random(4_242);
            var bytes = new byte[8];
            ulong NextKey()
            {
                random.NextBytes(bytes);
                return BitConverter.ToUInt64(bytes, 0) | 1UL;
            }

            // Objects the host says are gone, about 3,000 keys a message, past the bound.
            const int KeysPerMessage = 3_000;
            var removedSent = 0;
            while (removedSent < NetworkWorldClient.MaximumPendingKeys + 10_000)
            {
                writer.Begin(WorldMessages.SceneRemoved);
                writer.WriteVarUInt(KeysPerMessage);
                for (var index = 0; index < KeysPerMessage; index++) writer.WriteUInt64(NextKey());
                channel.Inject(rig.Network.Host.Id, writer.Segment.ToArray());
                player.Client.Receive();
                removedSent += KeysPerMessage;
            }

            // Poses for objects the host says are unbound, about 1,000 entries a message, past the bound.
            const int PosesPerMessage = 1_000;
            var pose = new NetPose(new NetVector3(1, 2, 3), NetQuaternion.Identity);
            var posesSent = 0;
            while (posesSent < NetworkWorldClient.MaximumPendingKeys + 10_000)
            {
                writer.Begin(WorldMessages.Spawn);
                writer.WriteVarUInt(PosesPerMessage);
                for (var index = 0; index < PosesPerMessage; index++) WorldEntryCodec.WriteScenePose(writer, new SceneObjectKey(NextKey()), pose);
                channel.Inject(rig.Network.Host.Id, writer.Segment.ToArray());
                player.Client.Receive();
                posesSent += PosesPerMessage;
            }

            var dropped = player.Counters().SceneKeysDropped;
            That(player.Client.RemovedKeyCount == NetworkWorldClient.MaximumPendingKeys && player.Client.PendingPoseCount == NetworkWorldClient.MaximumPendingKeys &&
                 dropped >= removedSent + posesSent - 2 * NetworkWorldClient.MaximumPendingKeys && player.Counters().MalformedMessages == 0,
                "the player keeps at most " + NetworkWorldClient.MaximumPendingKeys + " of each and counts the rest (" + player.Client.RemovedKeyCount + " removed, " +
                player.Client.PendingPoseCount + " poses, " + dropped + " dropped of " + (removedSent + posesSent) + ")");

            // The world it mirrors goes on: a box the host moves binds and follows.
            var box = rig.Host.Scene.Scenes[0].Roots[1];
            box.Position = new NetVector3(9, 1, 1);
            var mine = player.Scene.Scenes[0].Roots[1];
            That(rig.Run(() => mine.Mark != 0 && Math.Abs(mine.Position.X - 9) < 0.05f, 120), "and a box the host moves still binds and follows (" + mine.Position.X + ")");
        }

        /// <summary>Every baseline names the host's world, drawn afresh at each promotion; one that names none is malformed.</summary>
        private static void ABaselineNamesItsWorld()
        {
            var rig = WorldBaselineSyncTests.Yard(2);
            rig.OpenToMultiplayer();
            var first = rig.Host.Host.WorldInstance;
            var player = rig.AddPlayer("p1");
            rig.JoinAndSynchronize(player);
            var channel = player.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
            var malformed = player.Counters().MalformedMessages;
            foreach (var instance in new ulong?[] { null, 0UL })
            {
                var writer = new WorldWireWriter(64);
                writer.Begin(WorldMessages.BaselineBegin);
                writer.WriteVarUInt(7);
                writer.WriteVarUInt(0);
                writer.WriteVarUInt(1);
                if (instance.HasValue) writer.WriteUInt64(instance.Value);
                channel.Inject(rig.Network.Host.Id, writer.Segment.ToArray());
            }

            player.Client.Receive();
            That(first != 0 && player.Counters().MalformedMessages == malformed + 2 && player.Counters().WorldsReplaced == 0 && player.World.Progress.Phase == NetworkWorldPhase.Synchronized,
                "a baseline that names no world is malformed and changes nothing");
            rig.Network.StopHosting();
            rig.Frames(2);
            rig.OpenToMultiplayer();
            That(rig.Host.Host.IsActive && rig.Host.Host.WorldInstance != 0 && rig.Host.Host.WorldInstance != first, "a second promotion serves another world");
        }

        /// <summary>One valid message of every type a host sends.</summary>
        private static List<byte[]> ValidMessages()
        {
            var messages = new List<byte[]>();
            var pose = new NetPose(new NetVector3(1, 2, 3), NetQuaternion.Identity, new NetVector3(1, 0, 0));
            var writer = new WorldWireWriter();
            writer.Begin(WorldMessages.BaselineBegin);
            writer.WriteVarUInt(9);
            writer.WriteVarUInt(3);
            writer.WriteVarUInt(5_000_000);
            writer.WriteUInt64(0x0123_4567_89AB_CDEFUL);
            messages.Add(writer.Segment.ToArray());
            writer.Begin(WorldMessages.BaselineEntries);
            writer.WriteVarUInt(9);
            writer.WriteVarUInt(2);
            WorldEntryCodec.WriteSpawn(writer, new NetworkEntityId(500), "asset.prefab", "io.example.world", 1, 0, new NetVector3(1, 1, 1), pose,
                WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Create("assets/world.bundle", "Crate")));
            WorldEntryCodec.WriteRemoved(writer, new SceneObjectKey(77));
            messages.Add(writer.Segment.ToArray());
            writer.Begin(WorldMessages.BaselineEnd);
            writer.WriteVarUInt(9);
            messages.Add(writer.Segment.ToArray());
            writer.Begin(WorldMessages.Spawn);
            writer.WriteVarUInt(1);
            WorldEntryCodec.WriteContent(writer, new NetworkEntityId(501), "worlds.arena", "arena/0", "io.example.world", 0, new NetVector3(1, 1, 1), pose, default);
            messages.Add(writer.Segment.ToArray());
            writer.Begin(WorldMessages.Despawn);
            writer.WriteVarUInt(2);
            writer.WriteVarUInt(500);
            writer.WriteVarUInt(501);
            messages.Add(writer.Segment.ToArray());
            writer.Begin(WorldMessages.Snapshot);
            writer.WriteVarUInt(6_000_000);
            writer.WriteVarUInt(1);
            writer.WriteVarUInt(500);
            writer.WriteTransform(pose);
            messages.Add(writer.Segment.ToArray());
            writer.Begin(WorldMessages.SceneRemoved);
            writer.WriteVarUInt(1);
            writer.WriteUInt64(77);
            messages.Add(writer.Segment.ToArray());
            messages.Add(new byte[] { 2, WorldMessages.Spawn, 0 });
            messages.Add(new byte[] { WorldMessages.Version, 99 });
            messages.Add(Encoding.ASCII.GetBytes("not a world message at all"));
            return messages;
        }

        private static WorldWireReader Read(WorldWireWriter writer)
        {
            var reader = new WorldWireReader();
            reader.Reset(writer.Segment.ToArray());
            return reader;
        }
    }
}
