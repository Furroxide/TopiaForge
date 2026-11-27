using System;
using System.IO;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    // A provider payload on the existing world protocol, never a player/network identity or a new channel.
    internal static class RobotBodyWire
    {
        internal const string Kind = "robotkit.body";
        internal const int MaximumBytes = 128;

        internal static InternalSpawnDescriptor Describe(RobotBodyRequest request)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)1); // Payload version.
            writer.Write((byte)1); // Powered-down pose; no other pose is public.
            Color(writer, request.Appearance.Plastic);
            Color(writer, request.Appearance.Metal);
            writer.Write((byte)request.Appearance.AccessorySlot);
            var accessory = request.Appearance.Accessory;
            writer.Write((byte)(accessory == null ? 255 : accessory.Length));
            if (accessory != null) foreach (var c in accessory) writer.Write((byte)c);
            return new InternalSpawnDescriptor(Kind,
                new TransformState(request.Position, request.Rotation, new Vec3(1, 1, 1)), stream.ToArray());
        }

        internal static RobotBodyRequest Read(InternalSpawnDescriptor descriptor)
        {
            if (descriptor.Kind != Kind || descriptor.Source != InternalSpawnSource.SdkSpawn
                || descriptor.Payload.Length > MaximumBytes || !descriptor.Transform.Scale.Equals(new Vec3(1, 1, 1)))
                throw new ArgumentException("Invalid persistent body description.");
            using var stream = new MemoryStream(descriptor.Payload.ToArray(), false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != 1 || reader.ReadByte() != 1) throw new ArgumentException("Invalid body pose or version.");
            var plastic = Color(reader); var metal = Color(reader);
            var slot = (AvatarAccessorySlot)reader.ReadByte();
            var count = reader.ReadByte();
            string? accessory = null;
            if (count != 255)
            {
                if (count > 64) throw new ArgumentException("Invalid body accessory.");
                var chars = new char[count];
                for (var i = 0; i < count; i++) chars[i] = (char)reader.ReadByte();
                accessory = new string(chars);
            }
            if (stream.Position != stream.Length) throw new ArgumentException("Unexpected body data.");
            return new RobotBodyRequest(new AvatarAppearance(plastic, metal, accessory, slot),
                descriptor.Transform.Position, descriptor.Transform.Rotation);
        }

        private static void Color(BinaryWriter writer, RgbaColor color)
        { writer.Write(color.Red); writer.Write(color.Green); writer.Write(color.Blue); }
        private static RgbaColor Color(BinaryReader reader) => new RgbaColor(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
