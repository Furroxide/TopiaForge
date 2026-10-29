using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class PresencePresentationRecord
    {
        internal string Session = string.Empty;
        internal ParticipantId Id;
        internal string Key = string.Empty;
        internal Guid Lifetime;
        internal AvatarAppearance Appearance = null!;
        internal string? Name;
        internal bool Visible;
        internal bool PoweredDown;
    }

    internal static class PresencePresentationWire
    {
        internal const byte Message = 22;
        internal const int MaximumBytes = 2048;
        internal static void Write(WorldWireWriter writer, string session, PresencePeer peer)
        {
            var state = peer.Presentation!;
            PresenceWire.BeginMessage(writer, Message);
            writer.WriteString(session); writer.WriteString(peer.Id.Value); writer.WriteString(peer.PlayerKey.Value ?? string.Empty);
            writer.WriteString(peer.PresentationLifetime.ToString("N"));
            WriteColor(writer, state.Current.Plastic); WriteColor(writer, state.Current.Metal);
            writer.WriteByte((byte)state.Current.AccessorySlot);
            WriteOptional(writer, state.Current.Accessory);
            WriteOptional(writer, state.Name);
            writer.WriteByte(state.Visible ? (byte)1 : (byte)0);
            writer.WriteByte(state.PoweredDown ? (byte)1 : (byte)0);
        }
        internal static bool TryRead(WorldWireReader reader, out PresencePresentationRecord? record)
        {
            record = null;
            if (!reader.TryReadString(512, out var session) || !PresenceWire.IsIdentity(session)
                || !reader.TryReadString(512, out var id) || !PresenceWire.IsIdentity(id)
                || !reader.TryReadString(512, out var key) || (key.Length != 0 && !PresenceWire.IsIdentity(key))
                || !reader.TryReadString(32, out var lifetime) || !Guid.TryParseExact(lifetime, "N", out var incarnation)
                || !ReadColor(reader, out var plastic) || !ReadColor(reader, out var metal)
                || !reader.TryReadByte(out var slot) || slot > (byte)AvatarAccessorySlot.Necklace
                || !ReadOptional(reader, 64, out var accessory) || !AvatarAppearance.ValidAccessory(accessory)
                || !ReadOptional(reader, 128, out var name) || !AvatarAppearanceControls.ValidName(name)
                || !reader.TryReadByte(out var visible) || visible > 1
                || !reader.TryReadByte(out var powered) || powered > 1 || !reader.AtEnd) return false;
            record = new PresencePresentationRecord
            {
                Session = session,
                Id = new ParticipantId(id),
                Key = key,
                Lifetime = incarnation,
                Appearance = new AvatarAppearance(plastic, metal, accessory, (AvatarAccessorySlot)slot),
                Name = name == null ? null : Sessions.HostText.ForDisplay(name, 32),
                Visible = visible == 1,
                PoweredDown = powered == 1
            };
            return true;
        }
        private static void WriteColor(WorldWireWriter writer, RgbaColor color)
        { writer.WriteSingle(color.Red); writer.WriteSingle(color.Green); writer.WriteSingle(color.Blue); }
        private static bool ReadColor(WorldWireReader reader, out RgbaColor color)
        {
            color = default;
            if (!reader.TryReadSingle(out var r) || !reader.TryReadSingle(out var g) || !reader.TryReadSingle(out var b)) return false;
            color = new RgbaColor(r, g, b);
            return AvatarAppearance.ValidColor(color);
        }
        private static void WriteOptional(WorldWireWriter writer, string? text)
        { writer.WriteByte(text == null ? (byte)0 : (byte)1); if (text != null) writer.WriteString(text); }
        private static bool ReadOptional(WorldWireReader reader, int bound, out string? text)
        {
            text = null;
            if (!reader.TryReadByte(out var present) || present > 1) return false;
            if (present == 0) return true;
            if (!reader.TryReadString(bound, out var value)) return false;
            text = value;
            return true;
        }
    }
}
