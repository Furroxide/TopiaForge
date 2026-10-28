using System;
using System.IO;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    internal enum RobotCommand : byte
    {
        Brain, Movement, MoveTo, Chase, Stop, Tint, Emote, Name, Scale, Interaction,
        Damage, Kill, Ragdoll, Knockback, Despawn
    }

    // Versioned, bounded values only. Credentials, conversation history and callbacks have no wire representation.
    internal static class RobotReplicationWire
    {
        internal const string Kind = "robotkit.agent";
        internal const int MaximumBytes = 2048;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static byte[] Write(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write((byte)1);
            write(writer);
            if (stream.Length > MaximumBytes) throw new ArgumentException("Robot data exceeds its limit.");
            return stream.ToArray();
        }

        internal static T Read<T>(ReadOnlyMemory<byte> bytes, Func<BinaryReader, T> read)
        {
            if (bytes.Length > MaximumBytes) throw new ArgumentException("Robot data exceeds its limit.");
            using var stream = new MemoryStream(bytes.ToArray(), false);
            using var reader = new BinaryReader(stream, Utf8, true);
            if (reader.ReadByte() != 1) throw new ArgumentException("Unsupported robot data version.");
            var result = read(reader);
            if (stream.Position != stream.Length) throw new ArgumentException("Unexpected robot data.");
            return result;
        }

        internal static byte[] Spawn(RobotAgentSpawnRequest request, string emote = "") => Write(writer =>
        {
            Vector(writer, request.Position);
            writer.Write(request.Facing.HasValue);
            if (request.Facing.HasValue) Vector(writer, request.Facing.Value);
            writer.Write((byte)request.BrainMode);
            writer.Write((byte)request.Gait);
            writer.Write(request.MoveSpeed); writer.Write(request.TurnSpeed); writer.Write(request.StopDistance);
            writer.Write(request.Scale);
            writer.Write(request.Tint.HasValue);
            if (request.Tint.HasValue) Color(writer, request.Tint.Value);
            Text(writer, request.Name ?? "", 128); Text(writer, request.RobotTypeId ?? "", 128);
            writer.Write(request.Interaction.NativeTalkMode == RobotNativeTalkMode.Enabled);
            writer.Write(request.Interaction.NativeTalkDistance);
            Text(writer, emote, 64);
        });

        internal static (RobotAgentSpawnRequest Request, string Emote) Spawn(ReadOnlyMemory<byte> bytes) => Read(bytes, reader =>
        {
            var position = Vector(reader);
            Vec3? facing = Boolean(reader) ? Vector(reader) : null;
            var brain = Enum<RobotBrainMode>(reader, 1);
            var gait = Enum<RobotGait>(reader, 2);
            var speed = Number(reader, 0, 100); var turn = Number(reader, 0, 3600); var stop = Number(reader, 0, 1000);
            var scale = Number(reader, 0.05f, 20);
            RobotColor? tint = Boolean(reader) ? Color(reader) : null;
            var name = Text(reader, 128); var type = Text(reader, 128);
            var talk = Boolean(reader); var distance = Number(reader, 0, 100);
            var emote = Text(reader, 64);
            return (new RobotAgentSpawnRequest(position, facing, brain, gait, speed, turn, stop, tint,
                name.Length == 0 ? null : name, scale,
                talk ? RobotInteractionOptions.NativeTalkAtDistance(distance) : RobotInteractionOptions.DisableNativeTalk(),
                type.Length == 0 ? null : type), emote);
        });

        internal static void Vector(BinaryWriter writer, Vec3 value)
        { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        internal static Vec3 Vector(BinaryReader reader) => new Vec3(Number(reader, -100000, 100000), Number(reader, -100000, 100000), Number(reader, -100000, 100000));
        internal static void Color(BinaryWriter writer, RobotColor value)
        { writer.Write(value.R); writer.Write(value.G); writer.Write(value.B); writer.Write(value.A); }
        internal static RobotColor Color(BinaryReader reader) => new RobotColor(Number(reader, 0, 1), Number(reader, 0, 1), Number(reader, 0, 1), Number(reader, 0, 1));
        internal static float Number(BinaryReader reader, float min, float max)
        {
            var value = reader.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max) throw new ArgumentException("Invalid robot value.");
            return value;
        }
        internal static bool Boolean(BinaryReader reader)
        {
            var value = reader.ReadByte();
            if (value > 1) throw new ArgumentException("Invalid robot flag.");
            return value != 0;
        }
        internal static T Enum<T>(BinaryReader reader, byte max) where T : struct
        {
            var value = reader.ReadByte();
            if (value > max) throw new ArgumentException("Invalid robot option.");
            return (T)System.Enum.ToObject(typeof(T), value);
        }
        internal static void Text(BinaryWriter writer, string value, int max)
        {
            if (value.Length > max || (value.Length != 0 && (!InternalReplicationLimits.IsText(value, max) || value.IndexOfAny(new[] { '<', '>' }) >= 0)))
                throw new ArgumentException("Robot text must be bounded plain text.");
            var bytes = Utf8.GetBytes(value);
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        internal static string Text(BinaryReader reader, int max)
        {
            var count = reader.ReadUInt16();
            if (count > max * 4) throw new ArgumentException("Robot text exceeds its limit.");
            var bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            var value = Utf8.GetString(bytes);
            if (value.Length > max || (value.Length != 0 && (!InternalReplicationLimits.IsText(value, max) || value.IndexOfAny(new[] { '<', '>' }) >= 0)))
                throw new ArgumentException("Robot text must be bounded plain text.");
            return value;
        }
        internal static void Reference(BinaryWriter writer, InternalNetRef value)
        { writer.Write((byte)value.Kind); Text(writer, value.Value ?? "", 128); }
        internal static InternalNetRef Reference(BinaryReader reader) =>
            new InternalNetRef(Enum<InternalNetRefKind>(reader, 2), Text(reader, 128));
    }
}
