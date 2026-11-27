using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Authority routing's messages (WS3.7), types 64 to 127 on the network world's <c>WorldReplication</c> channel, beside
    /// world replication's own on the same ordered stream. 64 to 95 go from a joined player to its host, 96 to 127 from the
    /// host to one player. A request never carries an origin, an owner or a network address: the host takes the sender
    /// from the channel and assigns everything else.
    /// </summary>
    internal static class AuthorityMessages
    {
        /// <summary>
        /// Player: a spawn on the player's intent, made here as a prediction.
        /// <c>[provisional varuint][mod string][kind string][intent byte][input frame byte][scale 3 x f32][transform][description bytes]</c>.
        /// </summary>
        internal const byte SpawnRequest = 64;

        /// <summary>Player: move its own object. <c>[target][scale 3 x f32][transform]</c>.</summary>
        internal const byte TransformRequest = 65;

        /// <summary>Player: destroy its own object. <c>[target]</c>.</summary>
        internal const byte DestroyRequest = 66;

        /// <summary>Host: the spawn of <c>provisional</c> is the entity <c>id</c>. <c>[provisional varuint][id varuint]</c>.</summary>
        internal const byte SpawnAdopted = 96;

        /// <summary>Host: the spawn of <c>provisional</c> was refused. <c>[provisional varuint][reason byte]</c>.</summary>
        internal const byte SpawnRefused = 97;

        /// <summary>A target names an entity by its network id.</summary>
        internal const byte TargetEntity = 0;

        /// <summary>A target names the sender's own spawn by its provisional id, before or after the host adopted it.</summary>
        internal const byte TargetProvisional = 1;
    }

    /// <summary>What a request names: an entity by its network id, or the sender's spawn by its provisional id.</summary>
    internal readonly struct AuthorityTarget : IEquatable<AuthorityTarget>
    {
        private AuthorityTarget(bool provisional, uint value)
        {
            IsProvisional = provisional;
            Value = value;
        }

        internal bool IsProvisional { get; }

        internal uint Value { get; }

        internal static AuthorityTarget Entity(NetworkEntityId id)
        {
            if (!id.IsValid) throw new ArgumentException("A target needs a valid id.", nameof(id));
            return new AuthorityTarget(false, id.Value);
        }

        internal static AuthorityTarget Provisional(uint provisional)
        {
            if (provisional == 0) throw new ArgumentOutOfRangeException(nameof(provisional));
            return new AuthorityTarget(true, provisional);
        }

        public bool Equals(AuthorityTarget other) => IsProvisional == other.IsProvisional && Value == other.Value;

        public override bool Equals(object? obj) => obj is AuthorityTarget other && Equals(other);

        public override int GetHashCode() => (int)Value * 2 + (IsProvisional ? 1 : 0);

        public override string ToString() => (IsProvisional ? "p" : "e") + Value;
    }

    /// <summary>One spawn request as the host reads it. The reader fills one instance again and again.</summary>
    internal sealed class AuthoritySpawnRequest
    {
        internal uint Provisional { get; set; }

        internal string ModId { get; set; } = string.Empty;

        internal string Kind { get; set; } = string.Empty;

        /// <summary>
        /// Gets what caused the player's intent, or <see cref="IntentSource.None"/> when a participant context's own code made
        /// the spawn: that code acts for its player whatever caused it. The host takes whose it is from the channel either way.
        /// </summary>
        internal IntentSource Source { get; set; }

        /// <summary>Gets whether the player's process made the request from an input-active frame, the coarse source.</summary>
        internal bool FromInputFrame { get; set; }

        internal NetVector3 Scale { get; set; }

        internal NetPose Pose { get; set; }

        /// <summary>Gets the description: an asset's bundle path and name for <c>asset.prefab</c>, the provider's payload otherwise.</summary>
        internal byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Writes and reads authority routing's messages. Every field a peer sends is checked as the runtime checks its own: a
    /// provisional id from 1, a mod id by the manifest rule, a kind by the token rule, an intent that is one, scales as
    /// world entries' are, transform records exact, descriptions up to 16 KiB, and nothing after the last field. An intent
    /// source is one the runtime defines; none is a participant context's own code.
    /// </summary>
    internal static class AuthorityWire
    {
        internal static void WriteSpawnRequest(
            WorldWireWriter writer,
            uint provisional,
            string modId,
            string kind,
            IntentSource source,
            bool fromInputFrame,
            NetVector3 scale,
            in NetPose pose,
            ReadOnlySpan<byte> payload)
        {
            if (provisional == 0) throw new ArgumentOutOfRangeException(nameof(provisional));
            writer.Begin(AuthorityMessages.SpawnRequest);
            writer.WriteVarUInt(provisional);
            writer.WriteString(modId);
            writer.WriteString(kind);
            writer.WriteByte((byte)source);
            writer.WriteByte(fromInputFrame ? (byte)1 : (byte)0);
            WriteScale(writer, scale);
            writer.WriteTransform(pose);
            writer.WriteBytes(payload);
        }

        internal static void WriteTransformRequest(WorldWireWriter writer, AuthorityTarget target, NetVector3 scale, in NetPose pose)
        {
            writer.Begin(AuthorityMessages.TransformRequest);
            WriteTarget(writer, target);
            WriteScale(writer, scale);
            writer.WriteTransform(pose);
        }

        internal static void WriteDestroyRequest(WorldWireWriter writer, AuthorityTarget target)
        {
            writer.Begin(AuthorityMessages.DestroyRequest);
            WriteTarget(writer, target);
        }

        internal static void WriteAdopted(WorldWireWriter writer, uint provisional, NetworkEntityId id)
        {
            writer.Begin(AuthorityMessages.SpawnAdopted);
            writer.WriteVarUInt(provisional);
            writer.WriteVarUInt(id.Value);
        }

        internal static void WriteRefused(WorldWireWriter writer, uint provisional, SpawnRefusal reason)
        {
            writer.Begin(AuthorityMessages.SpawnRefused);
            writer.WriteVarUInt(provisional);
            writer.WriteByte((byte)reason);
        }

        /// <summary>Reads a spawn request into <paramref name="request"/>; false when any field is malformed.</summary>
        internal static bool TryReadSpawnRequest(WorldWireReader reader, AuthoritySpawnRequest request)
        {
            request.Payload = Array.Empty<byte>();
            if (!TryReadProvisional(reader, out var provisional)) return false;
            if (!reader.TryReadString(WorldEntryCodec.MaximumTokenBytes, out var mod) || !ManifestValidator.IsValidId(mod)) return false;
            if (!reader.TryReadString(WorldEntryCodec.MaximumTokenBytes, out var kind) || !InternalReplicationLimits.IsKind(kind)) return false;
            if (!reader.TryReadByte(out var source) || source > (byte)IntentSource.MotionLease) return false;
            if (!reader.TryReadByte(out var inputFrame) || inputFrame > 1) return false;
            if (!TryReadScale(reader, out var scale) || !reader.TryReadTransform(out var pose)) return false;
            if (!reader.TryReadBytes(InternalReplicationLimits.MaximumPayloadBytes, out var payload) || !reader.AtEnd) return false;
            request.Provisional = provisional;
            request.ModId = mod;
            request.Kind = kind;
            request.Source = (IntentSource)source;
            request.FromInputFrame = inputFrame == 1 || source == (byte)IntentSource.InputActiveFrame;
            request.Scale = scale;
            request.Pose = pose;
            request.Payload = payload;
            return true;
        }

        internal static bool TryReadTransformRequest(WorldWireReader reader, out AuthorityTarget target, out NetVector3 scale, out NetPose pose)
        {
            scale = default;
            pose = default;
            return TryReadTarget(reader, out target) && TryReadScale(reader, out scale) && reader.TryReadTransform(out pose) && reader.AtEnd;
        }

        internal static bool TryReadDestroyRequest(WorldWireReader reader, out AuthorityTarget target) => TryReadTarget(reader, out target) && reader.AtEnd;

        internal static bool TryReadAdopted(WorldWireReader reader, out uint provisional, out NetworkEntityId id)
        {
            id = NetworkEntityId.None;
            if (!TryReadProvisional(reader, out provisional)) return false;
            return reader.TryReadUInt32(uint.MaxValue, out var value) && NetworkEntityId.TryCreate(value, out id) && reader.AtEnd;
        }

        internal static bool TryReadRefused(WorldWireReader reader, out uint provisional, out SpawnRefusal reason)
        {
            reason = SpawnRefusal.None;
            if (!TryReadProvisional(reader, out provisional) || !reader.TryReadByte(out var value) || !reader.AtEnd) return false;
            reason = SpawnAuthorityRules.FromWire(value);
            return true;
        }

        private static bool TryReadProvisional(WorldWireReader reader, out uint provisional) =>
            reader.TryReadUInt32(SpawnAuthorityRules.MaximumProvisionalId, out provisional) && provisional != 0;

        internal static void WriteTarget(WorldWireWriter writer, AuthorityTarget target)
        {
            writer.WriteByte(target.IsProvisional ? AuthorityMessages.TargetProvisional : AuthorityMessages.TargetEntity);
            writer.WriteVarUInt(target.Value);
        }

        internal static bool TryReadTarget(WorldWireReader reader, out AuthorityTarget target)
        {
            target = default;
            if (!reader.TryReadByte(out var kind) || kind > AuthorityMessages.TargetProvisional) return false;
            if (!reader.TryReadUInt32(uint.MaxValue, out var value) || value == 0) return false;
            target = kind == AuthorityMessages.TargetProvisional ? AuthorityTarget.Provisional(value) : AuthorityTarget.Entity(new NetworkEntityId(value));
            return true;
        }

        private static void WriteScale(WorldWireWriter writer, NetVector3 scale)
        {
            writer.WriteSingle(scale.X);
            writer.WriteSingle(scale.Y);
            writer.WriteSingle(scale.Z);
        }

        private static bool TryReadScale(WorldWireReader reader, out NetVector3 scale)
        {
            scale = default;
            if (!reader.TryReadSingle(out var x) || !reader.TryReadSingle(out var y) || !reader.TryReadSingle(out var z)) return false;
            if (!IsScale(x) || !IsScale(y) || !IsScale(z)) return false;
            scale = new NetVector3(x, y, z);
            return true;
        }

        private static bool IsScale(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value != 0f && value <= WorldEntryCodec.MaximumScale && value >= -WorldEntryCodec.MaximumScale;
    }
}
