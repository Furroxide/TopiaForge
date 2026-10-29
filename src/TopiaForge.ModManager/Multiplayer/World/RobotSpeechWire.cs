using System;
using System.Text;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal static class RobotSpeechWire
    {
        internal const byte Ready = 72, Line = 73, Cancel = 74, Withdraw = 75;
        internal const int MaximumBytes = 2560, MaximumText = 512;
        internal const float Earshot = 20;
        internal static bool Text(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumText || !InternalReplicationLimits.IsText(text, MaximumText)
                || text.IndexOf('<') >= 0 || text.IndexOf('>') >= 0) return false;
            try { return new UTF8Encoding(false, true).GetByteCount(text) <= 2048; }
            catch (ArgumentException) { return false; }
        }
        internal static bool Hex(string value, int length)
        {
            if (value.Length != length) return false;
            foreach (var c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        internal static void Header(WorldWireWriter writer, byte type, ulong world, string challenge, ulong nonce, uint entity)
        {
            writer.Begin(type); writer.WriteByte(1); writer.WriteUInt64(world); writer.WriteString(challenge);
            writer.WriteUInt64(nonce); writer.WriteVarUInt(entity);
        }
    }

    internal sealed partial class NetworkWorldHost
    {
        internal bool SpeechTarget(Mods.IEntity entity, string mod, string kind, out uint id)
        {
            id = 0;
            if (!IsActive) return false;
            foreach (var value in ordered)
                if (value.IsAlive && value.Record != null && ReferenceEquals(value.Record.Entity, entity)
                    && value.OwnerSlot == 0 && value.OwnerModId == mod && value.Kind == kind)
                {
                    // A spawn followed immediately by Say must announce its descriptor before the private event.
                    if (value.AnnouncePending) SendAnnouncements();
                    id = value.Id.Value; return true;
                }
            return false;
        }
    }

    internal sealed partial class NetworkWorldClient
    {
        internal ulong SpeechWorld => IsActive && synchronized && pendingBuilds == 0 ? worldInstance : 0;
        internal bool SpeechTarget(uint id, string mod, string kind, out Mods.IEntity? entity)
        {
            entity = null;
            if (SpeechWorld == 0 || !entities.TryGetValue(id, out var entry) || entry.Stale || entry.OwnerSlot != 0
                || entry.OwnerModId != mod || entry.Kind != kind || entry.Object?.IsAlive != true) return false;
            // The factory body is the provider's identity; the engine adapter can expose a different IEntity wrapper.
            entity = entry.FactoryReplica ?? entry.Local?.Entity ?? entry.Object.Entity;
            return entity.IsAlive;
        }
    }
}
