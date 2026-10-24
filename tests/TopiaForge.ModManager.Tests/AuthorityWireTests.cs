using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Authority routing's messages (WS3.7): each round trips exactly, sits in the range section 13 keeps for it, and a
    /// field a hostile peer bends, a truncation anywhere, or a byte after the last field is refused whole.
    /// </summary>
    internal static class AuthorityWireTests
    {
        private static readonly NetPose Pose = new NetPose(new NetVector3(1.5f, 2f, -3f), new NetQuaternion(0f, 0.7071068f, 0f, 0.7071068f));

        internal static void Run()
        {
            TheMessagesSitInTheirRange();
            SpawnRequestsRoundTrip();
            BentSpawnRequestsAreRefused();
            ChangesRoundTrip();
            VerdictsRoundTrip();
            TargetsAreNeverNone();
        }

        private static void TheMessagesSitInTheirRange()
        {
            var types = new[]
            {
                AuthorityMessages.SpawnRequest, AuthorityMessages.TransformRequest, AuthorityMessages.DestroyRequest, AuthorityMessages.SpawnAdopted,
                AuthorityMessages.SpawnRefused
            };
            That(types.All(type => type >= WorldMessages.FirstRoutingType && type <= WorldMessages.LastRoutingType) && types.Distinct().Count() == types.Length,
                "authority routing's types are distinct and inside 64 to 127");
            That(types.Take(3).All(type => type < 96) && types.Skip(3).All(type => type >= 96), "player requests sit below 96, host verdicts from 96");
        }

        private static void SpawnRequestsRoundTrip()
        {
            var payload = WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Create("assets/world.bundle", "Crate"));
            var writer = new WorldWireWriter();
            AuthorityWire.WriteSpawnRequest(writer, 7, "io.example.world", "asset.prefab", IntentSource.UiCallback, false, new NetVector3(1f, 2f, 3f), Pose, payload);
            var request = new AuthoritySpawnRequest();
            That(Read(writer, AuthorityMessages.SpawnRequest, reader => AuthorityWire.TryReadSpawnRequest(reader, request)) &&
                 request.Provisional == 7 && request.ModId == "io.example.world" && request.Kind == "asset.prefab" && request.Source == IntentSource.UiCallback &&
                 !request.FromInputFrame && request.Scale.Equals(new NetVector3(1f, 2f, 3f)) && Near(request.Pose.Position, Pose.Position) &&
                 request.Payload.SequenceEqual(payload), "a spawn request round trips");

            AuthorityWire.WriteSpawnRequest(writer, uint.MaxValue, "io.example.world", "tests.box", IntentSource.InputActiveFrame, false, new NetVector3(-1f, 1f, 1f), Pose,
                ReadOnlySpan<byte>.Empty);
            That(Read(writer, AuthorityMessages.SpawnRequest, reader => AuthorityWire.TryReadSpawnRequest(reader, request)) && request.Provisional == uint.MaxValue &&
                 request.FromInputFrame && request.Payload.Length == 0, "an input frame's spawn reads as coarse whatever its flag says, and the largest id reads");

            AuthorityWire.WriteSpawnRequest(writer, 9, "io.example.world", "tests.box", IntentSource.None, true, new NetVector3(1f, 1f, 1f), Pose, new byte[] { 1 });
            That(Read(writer, AuthorityMessages.SpawnRequest, reader => AuthorityWire.TryReadSpawnRequest(reader, request)) && request.Source == IntentSource.None &&
                 request.FromInputFrame, "a participant context's own spawn carries no intent, and its frame flag reads");
            Throws<ArgumentOutOfRangeException>(
                () => AuthorityWire.WriteSpawnRequest(writer, 0, "io.example.world", "tests.box", IntentSource.UiCallback, false, new NetVector3(1f, 1f, 1f), Pose, default),
                "provisional id zero is never written");
        }

        private static void BentSpawnRequestsAreRefused()
        {
            var request = new AuthoritySpawnRequest();
            var good = Spawn(7, "io.example.world", "asset.prefab", 2, 0, 1f, new byte[] { 1, 2, 3 });
            That(Accepts(good, request), "the unbent request reads");
            var cases = new (string What, byte[] Bytes)[]
            {
                ("provisional id zero", Spawn(0, "io.example.world", "asset.prefab", 2, 0, 1f, new byte[0])),
                ("a mod id the manifest rule refuses", Spawn(7, "Not A Mod", "asset.prefab", 2, 0, 1f, new byte[0])),
                ("a mod id shaped like a path", Spawn(7, "../io.example", "asset.prefab", 2, 0, 1f, new byte[0])),
                ("a kind that is no token", Spawn(7, "io.example.world", "asset prefab!", 2, 0, 1f, new byte[0])),
                ("an intent the runtime does not define", Spawn(7, "io.example.world", "asset.prefab", 6, 0, 1f, new byte[0])),
                ("a frame flag that is neither", Spawn(7, "io.example.world", "asset.prefab", 2, 2, 1f, new byte[0])),
                ("a scale of zero", Spawn(7, "io.example.world", "asset.prefab", 2, 0, 0f, new byte[0])),
                ("a scale that is no number", Spawn(7, "io.example.world", "asset.prefab", 2, 0, float.NaN, new byte[0])),
                ("an infinite scale", Spawn(7, "io.example.world", "asset.prefab", 2, 0, float.PositiveInfinity, new byte[0])),
                ("a scale past the world's bound", Spawn(7, "io.example.world", "asset.prefab", 2, 0, 10_001f, new byte[0])),
                ("a description past 16 KiB", Spawn(7, "io.example.world", "asset.prefab", 2, 0, 1f, new byte[InternalReplicationLimits.MaximumPayloadBytes + 1])),
                ("a byte after the last field", good.Concat(new byte[] { 0 }).ToArray())
            };

            foreach (var (what, bytes) in cases) That(!Accepts(bytes, request), what + " is refused");
            for (var length = 2; length < good.Length; length++) That(!Accepts(good.Take(length).ToArray(), request), "a request cut at " + length + " bytes is refused");
            That(Accepts(Spawn(7, "io.example.world", "asset.prefab", 2, 0, 1f, new byte[InternalReplicationLimits.MaximumPayloadBytes]), request),
                "a description of exactly 16 KiB reads");
        }

        private static void ChangesRoundTrip()
        {
            var writer = new WorldWireWriter();
            foreach (var target in new[] { AuthorityTarget.Entity(new NetworkEntityId(12)), AuthorityTarget.Provisional(3), AuthorityTarget.Provisional(uint.MaxValue) })
            {
                AuthorityWire.WriteTransformRequest(writer, target, new NetVector3(2f, 2f, 2f), Pose);
                AuthorityTarget read = default;
                var scale = default(NetVector3);
                var pose = default(NetPose);
                That(Read(writer, AuthorityMessages.TransformRequest, reader => AuthorityWire.TryReadTransformRequest(reader, out read, out scale, out pose)) &&
                     read.Equals(target) && scale.Equals(new NetVector3(2f, 2f, 2f)) && Near(pose.Position, Pose.Position), "a transform request for " + target + " round trips");
                AuthorityWire.WriteDestroyRequest(writer, target);
                That(Read(writer, AuthorityMessages.DestroyRequest, reader => AuthorityWire.TryReadDestroyRequest(reader, out read)) && read.Equals(target),
                    "a destroy request for " + target + " round trips");
            }

            foreach (var (what, bytes) in new (string, byte[])[]
                     {
                         ("a target of a kind that is none", new byte[] { WorldMessages.Version, AuthorityMessages.DestroyRequest, 2, 5 }),
                         ("a target of id zero", new byte[] { WorldMessages.Version, AuthorityMessages.DestroyRequest, AuthorityMessages.TargetEntity, 0 }),
                         ("a provisional target of zero", new byte[] { WorldMessages.Version, AuthorityMessages.DestroyRequest, AuthorityMessages.TargetProvisional, 0 }),
                         ("a byte after the target", new byte[] { WorldMessages.Version, AuthorityMessages.DestroyRequest, AuthorityMessages.TargetEntity, 5, 0 }),
                         ("no target", new byte[] { WorldMessages.Version, AuthorityMessages.DestroyRequest })
                     })
            {
                That(!Read(bytes, reader => AuthorityWire.TryReadDestroyRequest(reader, out _)), what + " is refused");
            }

            AuthorityWire.WriteTransformRequest(writer, AuthorityTarget.Entity(new NetworkEntityId(12)), new NetVector3(2f, 2f, 2f), Pose);
            var moved = writer.Segment.ToArray();
            That(!Read(moved.Concat(new byte[] { 1 }).ToArray(), reader => AuthorityWire.TryReadTransformRequest(reader, out _, out _, out _)), "a byte after a move is refused");
            for (var length = 2; length < moved.Length; length++)
            {
                That(!Read(moved.Take(length).ToArray(), reader => AuthorityWire.TryReadTransformRequest(reader, out _, out _, out _)), "a move cut at " + length + " is refused");
            }

            var flat = (byte[])moved.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(0f), 0, flat, 4, 4);
            That(!Read(flat, reader => AuthorityWire.TryReadTransformRequest(reader, out _, out _, out _)), "a move to a scale of zero is refused");
        }

        private static void VerdictsRoundTrip()
        {
            var writer = new WorldWireWriter();
            AuthorityWire.WriteAdopted(writer, 41, new NetworkEntityId(uint.MaxValue));
            uint provisional = 0;
            var id = NetworkEntityId.None;
            That(Read(writer, AuthorityMessages.SpawnAdopted, reader => AuthorityWire.TryReadAdopted(reader, out provisional, out id)) && provisional == 41 &&
                 id.Value == uint.MaxValue, "an adoption round trips");
            var reason = SpawnRefusal.None;
            foreach (SpawnRefusal refusal in Enum.GetValues(typeof(SpawnRefusal)))
            {
                if (refusal == SpawnRefusal.None) continue;
                AuthorityWire.WriteRefused(writer, 41, refusal);
                That(Read(writer, AuthorityMessages.SpawnRefused, reader => AuthorityWire.TryReadRefused(reader, out provisional, out reason)) && provisional == 41 &&
                     reason == (refusal == SpawnRefusal.TimedOut ? SpawnRefusal.Unbuildable : refusal), "a refusal for " + refusal + " round trips as the host may send it");
            }

            foreach (var (what, bytes) in new (string, byte[])[]
                     {
                         ("an adoption of provisional id zero", new byte[] { WorldMessages.Version, AuthorityMessages.SpawnAdopted, 0, 5 }),
                         ("an adoption as id zero", new byte[] { WorldMessages.Version, AuthorityMessages.SpawnAdopted, 5, 0 }),
                         ("an adoption with a byte after it", new byte[] { WorldMessages.Version, AuthorityMessages.SpawnAdopted, 5, 5, 5 }),
                         ("an adoption cut short", new byte[] { WorldMessages.Version, AuthorityMessages.SpawnAdopted, 5 })
                     })
            {
                That(!Read(bytes, reader => AuthorityWire.TryReadAdopted(reader, out _, out _)), what + " is refused");
            }

            That(!Read(new byte[] { WorldMessages.Version, AuthorityMessages.SpawnRefused, 5, 1, 0 }, reader => AuthorityWire.TryReadRefused(reader, out _, out _)) &&
                 !Read(new byte[] { WorldMessages.Version, AuthorityMessages.SpawnRefused, 5 }, reader => AuthorityWire.TryReadRefused(reader, out _, out _)) &&
                 Read(new byte[] { WorldMessages.Version, AuthorityMessages.SpawnRefused, 5, 200 }, reader => AuthorityWire.TryReadRefused(reader, out _, out reason)) &&
                 reason == SpawnRefusal.Unbuildable, "a refusal is exact, and one whose reason this build does not know reads as unbuildable");
        }

        private static void TargetsAreNeverNone()
        {
            Throws<ArgumentException>(() => AuthorityTarget.Entity(NetworkEntityId.None), "a target names a valid id");
            Throws<ArgumentOutOfRangeException>(() => AuthorityTarget.Provisional(0), "or a provisional id from one");
            var entity = AuthorityTarget.Entity(new NetworkEntityId(5));
            var provisional = AuthorityTarget.Provisional(5);
            That(!entity.Equals(provisional) && entity.GetHashCode() != provisional.GetHashCode() && entity.Equals(AuthorityTarget.Entity(new NetworkEntityId(5))),
                "an entity and a provisional spawn of the same number are different targets");
        }

        private static byte[] Spawn(uint provisional, string mod, string kind, byte source, byte inputFrame, float scale, byte[] payload)
        {
            var writer = new WorldWireWriter();
            writer.Begin(AuthorityMessages.SpawnRequest);
            writer.WriteVarUInt(provisional);
            writer.WriteString(mod);
            writer.WriteString(kind);
            writer.WriteByte(source);
            writer.WriteByte(inputFrame);
            writer.WriteSingle(scale);
            writer.WriteSingle(1f);
            writer.WriteSingle(1f);
            writer.WriteTransform(Pose);
            writer.WriteBytes(payload);
            return writer.Segment.ToArray();
        }

        private static bool Accepts(byte[] bytes, AuthoritySpawnRequest request) => Read(bytes, reader => AuthorityWire.TryReadSpawnRequest(reader, request));

        private static bool Read(WorldWireWriter writer, byte type, Func<WorldWireReader, bool> read)
        {
            var bytes = writer.Segment.ToArray();
            return bytes.Length > 1 && bytes[1] == type && Read(bytes, read);
        }

        private static bool Read(byte[] bytes, Func<WorldWireReader, bool> read)
        {
            var reader = new WorldWireReader();
            reader.Reset(bytes);
            return reader.TryReadHeader(out _) && read(reader);
        }
    }
}
