using System;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>What the host's game is in, as its players follow it (WS3.8).</summary>
    internal enum HostWorldKind
    {
        /// <summary>The host is at the main menu, or in no level: its players wait at theirs.</summary>
        Menu = 0,

        /// <summary>The host is in a level outside a world session: its players load the same level.</summary>
        Level = 1,

        /// <summary>The host runs a world session: its players follow it.</summary>
        World = 2
    }

    /// <summary>
    /// The host's world as one announcement on the <c>SceneFollow</c> system channel. Every text in it is the host's, so a
    /// player reads it strictly (<see cref="SessionFollowWire"/>) and resolves its ids against its own packages; it never
    /// shows any of it. Immutable.
    /// </summary>
    internal sealed class HostWorldState
    {
        private HostWorldState(HostWorldKind kind, string? sessionId, LaunchRequest? request, string? gamemodeId, string? worldId,
            string? worldFamilyId, string? transition, string? scene, TransformState? spawn)
        {
            Kind = kind;
            SessionId = sessionId;
            Request = request;
            GamemodeId = gamemodeId;
            WorldId = worldId;
            WorldFamilyId = worldFamilyId;
            Transition = transition;
            Scene = scene;
            Spawn = spawn;
        }

        internal static HostWorldState Menu { get; } = new HostWorldState(HostWorldKind.Menu, null, null, null, null, null, null, null, null);

        internal HostWorldKind Kind { get; }

        /// <summary>Gets the host's world session id, for a world.</summary>
        internal string? SessionId { get; }

        /// <summary>Gets the launch the host made, for a world: its target and the world and transition it chose.</summary>
        internal LaunchRequest? Request { get; }

        internal string? GamemodeId { get; }

        internal string? WorldId { get; }

        internal string? WorldFamilyId { get; }

        internal string? Transition { get; }

        /// <summary>
        /// Gets the level's scene name: the level the host is in, for a level, and the level an additive arena is built over,
        /// for a world with one; null otherwise.
        /// </summary>
        internal string? Scene { get; }

        /// <summary>Gets where the host's player stood as its additive arena was built, for a world with a level of its own.</summary>
        internal TransformState? Spawn { get; }

        internal static HostWorldState Level(string scene)
        {
            if (!SessionFollowWire.IsSceneName(scene)) throw new ArgumentException("A level is named by its gameplay scene's name.", nameof(scene));
            return new HostWorldState(HostWorldKind.Level, null, null, null, null, null, null, scene, null);
        }

        internal static HostWorldState World(string sessionId, LaunchRequest request, string gamemodeId, string worldId, string? worldFamilyId,
            string transition, string? baseScene, TransformState? spawn)
        {
            if (!SessionFollowWire.IsSessionId(sessionId)) throw new ArgumentException("A world session id is 32 lowercase hexadecimal characters.", nameof(sessionId));
            if (request == null) throw new ArgumentNullException(nameof(request));
            LaunchContractValues.Identifier(gamemodeId, nameof(gamemodeId));
            LaunchContractValues.Identifier(worldId, nameof(worldId));
            LaunchContractValues.OptionalIdentifier(worldFamilyId, nameof(worldFamilyId));
            LaunchContractValues.Choice(transition, nameof(transition), ModTransitions.ByPrecedence);
            var additive = transition == ModTransitions.AdditiveArena;
            if (baseScene != null && (!additive || !SessionFollowWire.IsSceneName(baseScene)))
                throw new ArgumentException("Only an additive arena names the gameplay level it is built over.", nameof(baseScene));
            if (spawn.HasValue && (baseScene == null || !SessionFollowWire.IsSpawn(spawn.Value)))
                throw new ArgumentException("Only an arena built over a level carries the spawn it was built at, within the world's bounds.", nameof(spawn));
            return new HostWorldState(HostWorldKind.World, sessionId, request, gamemodeId, worldId, worldFamilyId, transition, baseScene, spawn);
        }

        /// <summary>Gets whether <paramref name="other"/> announces the same world: the same session, or the same level, or the menu.</summary>
        internal bool SameAs(HostWorldState? other)
        {
            if (other == null || other.Kind != Kind) return false;
            switch (Kind)
            {
                case HostWorldKind.Level:
                    return string.Equals(Scene, other.Scene, StringComparison.Ordinal);
                case HostWorldKind.World:
                    return string.Equals(SessionId, other.SessionId, StringComparison.Ordinal);
                default:
                    return true;
            }
        }

        public override string ToString() => Kind.ToString();
    }

    /// <summary>
    /// The <c>SceneFollow</c> channel's messages (WS3.8), which only the host sends: <c>[version 1][type]</c>, then the
    /// type's body, with world replication's primitives (<see cref="WorldWireWriter"/>, <see cref="WorldWireReader"/>).
    /// <list type="bullet">
    /// <item>1 <c>World</c>: session id, target, world override and transition override (each a flag byte, then the id),
    /// gamemode, world, world family (flag, then the id), transition, and the base level (flag, then its scene name and, with
    /// a second flag, the spawn: position, rotation and scale as ten little-endian singles).</item>
    /// <item>2 <c>Level</c>: the scene name.</item>
    /// <item>3 <c>Menu</c>: nothing.</item>
    /// </list>
    /// A player reads every field as the runtime checks its own: ids by the declaration rule, the session id as exactly 32
    /// lowercase hexadecimal characters, the transition as a known one, scene names as gameplay scene names without a path,
    /// at most 128 characters, and a spawn within the world's bounds; a message must end exactly where its last field does.
    /// Anything else is refused whole. Unity-free.
    /// </summary>
    internal static class SessionFollowWire
    {
        internal const byte Version = 1;
        internal const byte WorldType = 1;
        internal const byte LevelType = 2;
        internal const byte MenuType = 3;

        /// <summary>The largest message: every field at its bound fits well within it.</summary>
        internal const int MaximumMessageBytes = 2048;

        /// <summary>The most UTF-8 bytes an id or a scene name may take.</summary>
        internal const int MaximumTextBytes = 512;

        /// <summary>The most characters a scene name may have.</summary>
        internal const int MaximumSceneNameLength = 128;

        /// <summary>The farthest a spawn may be from the world's origin on each axis, and the largest scale it may have.</summary>
        internal const float MaximumCoordinate = 1_000_000f;

        internal const float MaximumScale = 10_000f;

        internal static void Write(WorldWireWriter writer, HostWorldState state)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (state == null) throw new ArgumentNullException(nameof(state));
            writer.Reset();
            writer.WriteByte(Version);
            switch (state.Kind)
            {
                case HostWorldKind.Level:
                    writer.WriteByte(LevelType);
                    writer.WriteString(state.Scene!);
                    return;
                case HostWorldKind.World:
                    writer.WriteByte(WorldType);
                    writer.WriteString(state.SessionId!);
                    writer.WriteString(state.Request!.TargetId);
                    WriteOptional(writer, state.Request.WorldOverride);
                    WriteOptional(writer, state.Request.TransitionOverride);
                    writer.WriteString(state.GamemodeId!);
                    writer.WriteString(state.WorldId!);
                    WriteOptional(writer, state.WorldFamilyId);
                    writer.WriteString(state.Transition!);
                    WriteOptional(writer, state.Scene);
                    if (state.Scene == null) return;
                    writer.WriteByte(state.Spawn.HasValue ? (byte)1 : (byte)0);
                    if (!state.Spawn.HasValue) return;
                    var spawn = state.Spawn.Value;
                    writer.WriteSingle(spawn.Position.X);
                    writer.WriteSingle(spawn.Position.Y);
                    writer.WriteSingle(spawn.Position.Z);
                    writer.WriteSingle(spawn.Rotation.X);
                    writer.WriteSingle(spawn.Rotation.Y);
                    writer.WriteSingle(spawn.Rotation.Z);
                    writer.WriteSingle(spawn.Rotation.W);
                    writer.WriteSingle(spawn.Scale.X);
                    writer.WriteSingle(spawn.Scale.Y);
                    writer.WriteSingle(spawn.Scale.Z);
                    return;
                default:
                    writer.WriteByte(MenuType);
                    return;
            }
        }

        /// <summary>Reads one message; false for anything that is not exactly a valid announcement.</summary>
        internal static bool TryRead(WorldWireReader reader, byte[]? payload, out HostWorldState? state)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            state = null;
            if (payload == null || payload.Length < 2 || payload.Length > MaximumMessageBytes) return false;
            reader.Reset(payload);
            if (!reader.TryReadByte(out var version) || version != Version || !reader.TryReadByte(out var type)) return false;
            try
            {
                switch (type)
                {
                    case MenuType:
                        state = HostWorldState.Menu;
                        break;
                    case LevelType:
                        if (!reader.TryReadString(MaximumTextBytes, out var level) || !IsSceneName(level)) return false;
                        state = HostWorldState.Level(level);
                        break;
                    case WorldType:
                        state = ReadWorld(reader);
                        break;
                    default:
                        return false;
                }
            }
            catch (ArgumentException)
            {
                // A field the runtime's own rules refuse, such as an id or a transition, refuses the whole message.
                state = null;
                return false;
            }

            if (state != null && reader.AtEnd) return true;
            state = null;
            return false;
        }

        /// <summary>Gets whether <paramref name="value"/> is a world session id as an orchestrator makes one.</summary>
        internal static bool IsSessionId(string? value)
        {
            if (value == null || value.Length != 32) return false;
            foreach (var character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            }

            return true;
        }

        /// <summary>
        /// Gets whether <paramref name="value"/> names a gameplay scene: 1 to 128 characters, none a control character or a path
        /// separator, and not a menu, boot or loader scene.
        /// </summary>
        internal static bool IsSceneName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value!.Length > MaximumSceneNameLength || value.Trim().Length != value.Length) return false;
            foreach (var character in value)
            {
                if (char.IsControl(character) || character == '/' || character == '\\' || char.IsSurrogate(character)) return false;
            }

            return !GameScenes.IsNonGameplayScene(value);
        }

        /// <summary>Gets whether <paramref name="spawn"/> lies within the world's bounds, with a rotation and a scale the engine can apply.</summary>
        internal static bool IsSpawn(TransformState spawn)
        {
            var position = spawn.Position;
            var scale = spawn.Scale;
            return Within(position.X, MaximumCoordinate) && Within(position.Y, MaximumCoordinate) && Within(position.Z, MaximumCoordinate)
                && spawn.Rotation.IsFinite && spawn.Rotation.LengthSquared > 0.5f && spawn.Rotation.LengthSquared < 2f
                && Within(scale.X, MaximumScale) && Within(scale.Y, MaximumScale) && Within(scale.Z, MaximumScale)
                && scale.X != 0f && scale.Y != 0f && scale.Z != 0f;
        }

        private static HostWorldState? ReadWorld(WorldWireReader reader)
        {
            if (!reader.TryReadString(MaximumTextBytes, out var session) || !IsSessionId(session)) return null;
            if (!reader.TryReadString(MaximumTextBytes, out var target)) return null;
            if (!TryReadOptional(reader, out var worldOverride) || !TryReadOptional(reader, out var transitionOverride)) return null;
            if (!reader.TryReadString(MaximumTextBytes, out var gamemode) || !reader.TryReadString(MaximumTextBytes, out var world)) return null;
            if (!TryReadOptional(reader, out var family) || !reader.TryReadString(MaximumTextBytes, out var transition)) return null;
            if (!TryReadOptional(reader, out var scene)) return null;
            TransformState? spawn = null;
            if (scene != null)
            {
                if (!reader.TryReadByte(out var hasSpawn) || hasSpawn > 1) return null;
                if (hasSpawn == 1)
                {
                    if (!TryReadSpawn(reader, out var read)) return null;
                    spawn = read;
                }
            }

            return HostWorldState.World(session, new LaunchRequest(target, worldOverride, transitionOverride), gamemode, world, family,
                transition, scene, spawn);
        }

        private static bool TryReadSpawn(WorldWireReader reader, out TransformState spawn)
        {
            spawn = default;
            var values = new float[10];
            for (var index = 0; index < values.Length; index++)
            {
                if (!reader.TryReadSingle(out values[index]) || float.IsNaN(values[index]) || float.IsInfinity(values[index])) return false;
            }

            var rotation = new Quat(values[3], values[4], values[5], values[6]);
            if (rotation.LengthSquared <= 0.5f || rotation.LengthSquared >= 2f) return false;
            var scale = new Vec3(values[7], values[8], values[9]);
            if (scale.X == 0f || scale.Y == 0f || scale.Z == 0f) return false;
            spawn = new TransformState(new Vec3(values[0], values[1], values[2]), rotation, scale);
            return IsSpawn(spawn);
        }

        private static void WriteOptional(WorldWireWriter writer, string? value)
        {
            writer.WriteByte(value == null ? (byte)0 : (byte)1);
            if (value != null) writer.WriteString(value);
        }

        private static bool TryReadOptional(WorldWireReader reader, out string? value)
        {
            value = null;
            if (!reader.TryReadByte(out var present) || present > 1) return false;
            if (present == 0) return true;
            if (!reader.TryReadString(MaximumTextBytes, out var read)) return false;
            value = read;
            return true;
        }

        private static bool Within(float value, float bound) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= bound;
    }
}
