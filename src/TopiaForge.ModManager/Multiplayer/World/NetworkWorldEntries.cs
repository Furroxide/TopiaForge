using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>What a world entry describes.</summary>
    internal enum WorldEntrySource : byte
    {
        /// <summary>A scene object bound by its key: every peer has it already, so only the key and pose travel.</summary>
        SceneObject = 0,

        /// <summary>An SDK spawn: a joined player builds a replica from its kind and description.</summary>
        SdkSpawn = 1,

        /// <summary>World content every peer builds alike, bound by its content key.</summary>
        WorldContent = 2,

        /// <summary>A scene object the host no longer has; a baseline lists it so a late joiner hides its copy.</summary>
        RemovedSceneObject = 3,

        /// <summary>
        /// An unbound scene object's pose: a baseline lists the objects censused at a promotion, which the solo session may
        /// have moved before it, so a late joiner places its copy where the host has it.
        /// </summary>
        ScenePose = 4
    }

    /// <summary>One entry as a joined player reads it. The reader fills one instance again and again.</summary>
    internal sealed class WorldEntry
    {
        internal WorldEntrySource Source { get; set; }

        internal NetworkEntityId Id { get; set; }

        internal SceneObjectKey SceneKey { get; set; }

        internal string Kind { get; set; } = string.Empty;

        internal string OwnerModId { get; set; } = string.Empty;

        /// <summary>
        /// Gets the spawn's shadow key: its number in its mod's shadowable spawns, which names its origin with the mod and
        /// which a joined player's own run of the same script binds its shadow by (WS3.7). Zero when no shadow stands for it.
        /// </summary>
        internal long OriginSequence { get; set; }

        internal string? ContentKey { get; set; }

        /// <summary>Gets the lifetime owner: zero for the server, or the slot of the player whose spawn the host adopted (WS3.7).</summary>
        internal int OwnerSlot { get; set; }

        internal NetVector3 Scale { get; set; }

        internal NetPose Pose { get; set; }

        internal byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Writes and reads world entries. An entry starts with its source byte, then, for a scene object, its id, key and
    /// pose; for an SDK spawn, its id, kind, owning mod, shadow key (zero for none), owner slot, scale, pose and description; for world
    /// content, its id, kind, content key, owning mod, owner slot, scale, pose and description; for a removed scene object,
    /// its key; for an unbound scene object's pose, its key and pose. Every field a host sends is checked as the runtime
    /// checks its own.
    /// </summary>
    internal static class WorldEntryCodec
    {
        /// <summary>The longest kind or mod id, in UTF-8 bytes: both are ASCII tokens of at most 64 characters.</summary>
        internal const int MaximumTokenBytes = 64;

        /// <summary>The longest content key, in UTF-8 bytes: 256 UTF-16 units of at most 4 bytes each.</summary>
        internal const int MaximumContentKeyBytes = 4 * InternalReplicationLimits.MaximumContentKeyLength;

        /// <summary>The largest owner slot an entry may name.</summary>
        internal const int MaximumOwnerSlot = 255;

        /// <summary>The largest scale component an entry may carry.</summary>
        internal const float MaximumScale = 10_000f;

        internal static void WriteSceneObject(WorldWireWriter writer, NetworkEntityId id, SceneObjectKey key, in NetPose pose)
        {
            writer.WriteByte((byte)WorldEntrySource.SceneObject);
            writer.WriteVarUInt(id.Value);
            writer.WriteUInt64(key.Value);
            writer.WriteTransform(pose);
        }

        internal static void WriteSpawn(
            WorldWireWriter writer,
            NetworkEntityId id,
            string kind,
            string ownerModId,
            long originSequence,
            int ownerSlot,
            NetVector3 scale,
            in NetPose pose,
            ReadOnlySpan<byte> payload)
        {
            writer.WriteByte((byte)WorldEntrySource.SdkSpawn);
            writer.WriteVarUInt(id.Value);
            writer.WriteString(kind);
            writer.WriteString(ownerModId);
            writer.WriteVarUInt((ulong)originSequence);
            writer.WriteVarUInt((ulong)ownerSlot);
            WriteScale(writer, scale);
            writer.WriteTransform(pose);
            writer.WriteBytes(payload);
        }

        internal static void WriteContent(
            WorldWireWriter writer,
            NetworkEntityId id,
            string kind,
            string contentKey,
            string ownerModId,
            int ownerSlot,
            NetVector3 scale,
            in NetPose pose,
            ReadOnlySpan<byte> payload)
        {
            writer.WriteByte((byte)WorldEntrySource.WorldContent);
            writer.WriteVarUInt(id.Value);
            writer.WriteString(kind);
            writer.WriteString(contentKey);
            writer.WriteString(ownerModId);
            writer.WriteVarUInt((ulong)ownerSlot);
            WriteScale(writer, scale);
            writer.WriteTransform(pose);
            writer.WriteBytes(payload);
        }

        internal static void WriteRemoved(WorldWireWriter writer, SceneObjectKey key)
        {
            writer.WriteByte((byte)WorldEntrySource.RemovedSceneObject);
            writer.WriteUInt64(key.Value);
        }

        internal static void WriteScenePose(WorldWireWriter writer, SceneObjectKey key, in NetPose pose)
        {
            writer.WriteByte((byte)WorldEntrySource.ScenePose);
            writer.WriteUInt64(key.Value);
            writer.WriteTransform(pose);
        }

        /// <summary>Reads one entry into <paramref name="entry"/>; false, with the reader left part way, when it is malformed.</summary>
        internal static bool TryRead(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadByte(out var source)) return false;
            // Every field starts again, so nothing an earlier entry read leaks into this one: a joined player hashes them all.
            entry.Source = (WorldEntrySource)source;
            entry.ContentKey = null;
            entry.Kind = string.Empty;
            entry.OwnerModId = string.Empty;
            entry.OriginSequence = 0;
            entry.OwnerSlot = 0;
            entry.Payload = Array.Empty<byte>();
            entry.Id = NetworkEntityId.None;
            entry.SceneKey = default;
            entry.Scale = default;
            entry.Pose = default;
            switch (entry.Source)
            {
                case WorldEntrySource.SceneObject:
                    return TryReadId(reader, entry) && TryReadKey(reader, entry) && TryReadPose(reader, entry);
                case WorldEntrySource.SdkSpawn:
                    return TryReadId(reader, entry) && TryReadKind(reader, entry) && TryReadMod(reader, entry) &&
                           reader.TryReadVarUInt(long.MaxValue, out var sequence) && Set(entry, (long)sequence) &&
                           TryReadOwner(reader, entry) && TryReadScale(reader, entry) && TryReadPose(reader, entry) && TryReadPayload(reader, entry);
                case WorldEntrySource.WorldContent:
                    return TryReadId(reader, entry) && TryReadKind(reader, entry) && TryReadContentKey(reader, entry) && TryReadMod(reader, entry) &&
                           TryReadOwner(reader, entry) && TryReadScale(reader, entry) && TryReadPose(reader, entry) && TryReadPayload(reader, entry);
                case WorldEntrySource.RemovedSceneObject:
                    return TryReadKey(reader, entry);
                case WorldEntrySource.ScenePose:
                    return TryReadKey(reader, entry) && TryReadPose(reader, entry);
                default:
                    return false;
            }
        }

        /// <summary>Encodes the description a joined player builds an asset replica from: its bundle path and asset name.</summary>
        internal static byte[] EncodeAsset(MultiplayerAssetDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (!descriptor.IsPortable) return Array.Empty<byte>();
            var writer = new WorldWireWriter(16 * 1024);
            writer.WriteString(descriptor.BundlePath!);
            writer.WriteString(descriptor.AssetName!);
            var bytes = new byte[writer.Length];
            Buffer.BlockCopy(writer.Segment.Array!, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        /// <summary>Reads an asset description; one that is empty, malformed or past its bounds is <see cref="MultiplayerAssetDescriptor.Unknown"/>.</summary>
        internal static MultiplayerAssetDescriptor DecodeAsset(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return MultiplayerAssetDescriptor.Unknown;
            var reader = new WorldWireReader();
            reader.Reset(payload);
            if (!reader.TryReadString(4 * MultiplayerAssetDescriptor.MaximumBundlePathLength, out var bundle) ||
                !reader.TryReadString(4 * MultiplayerAssetDescriptor.MaximumAssetNameLength, out var asset) || !reader.AtEnd)
            {
                return MultiplayerAssetDescriptor.Unknown;
            }

            if (!InternalReplicationLimits.IsText(bundle, MultiplayerAssetDescriptor.MaximumBundlePathLength) ||
                !InternalReplicationLimits.IsText(asset, MultiplayerAssetDescriptor.MaximumAssetNameLength))
            {
                return MultiplayerAssetDescriptor.Unknown;
            }

            return MultiplayerAssetDescriptor.Create(bundle, asset);
        }

        private static bool Set(WorldEntry entry, long sequence)
        {
            entry.OriginSequence = sequence;
            return true;
        }

        private static void WriteScale(WorldWireWriter writer, NetVector3 scale)
        {
            writer.WriteSingle(scale.X);
            writer.WriteSingle(scale.Y);
            writer.WriteSingle(scale.Z);
        }

        private static bool TryReadId(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadUInt32(uint.MaxValue, out var value) || !NetworkEntityId.TryCreate(value, out var id)) return false;
            entry.Id = id;
            return true;
        }

        private static bool TryReadKey(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadUInt64(out var key)) return false;
            entry.SceneKey = new SceneObjectKey(key);
            return true;
        }

        private static bool TryReadPose(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadTransform(out var pose)) return false;
            entry.Pose = pose;
            return true;
        }

        private static bool TryReadKind(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadString(MaximumTokenBytes, out var kind) || !InternalReplicationLimits.IsKind(kind)) return false;
            entry.Kind = kind;
            return true;
        }

        private static bool TryReadMod(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadString(MaximumTokenBytes, out var mod) || !ManifestValidator.IsValidId(mod)) return false;
            entry.OwnerModId = mod;
            return true;
        }

        private static bool TryReadContentKey(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadString(MaximumContentKeyBytes, out var key) ||
                !InternalReplicationLimits.IsText(key, InternalReplicationLimits.MaximumContentKeyLength))
            {
                return false;
            }

            entry.ContentKey = key;
            return true;
        }

        private static bool TryReadOwner(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadInt32(MaximumOwnerSlot, out var slot)) return false;
            entry.OwnerSlot = slot;
            return true;
        }

        private static bool TryReadScale(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadSingle(out var x) || !reader.TryReadSingle(out var y) || !reader.TryReadSingle(out var z)) return false;
            if (!IsScale(x) || !IsScale(y) || !IsScale(z)) return false;
            entry.Scale = new NetVector3(x, y, z);
            return true;
        }

        private static bool TryReadPayload(WorldWireReader reader, WorldEntry entry)
        {
            if (!reader.TryReadBytes(InternalReplicationLimits.MaximumPayloadBytes, out var payload)) return false;
            if (entry.Kind == BlockerDescriptor.Kind &&
                (entry.Source != WorldEntrySource.SdkSpawn || entry.OwnerSlot != 0 ||
                 !BlockerDescriptor.TryRead(payload, out _, out _))) return false;
            entry.Payload = payload;
            return true;
        }

        private static bool IsScale(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value != 0f && value <= MaximumScale && value >= -MaximumScale;
    }
}
