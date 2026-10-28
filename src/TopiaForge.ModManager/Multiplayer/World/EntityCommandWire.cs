using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal static class EntityCommandWire
    {
        internal const byte Request = 71;

        internal static void Write(WorldWireWriter writer, AuthorityTarget target, string mod, string kind, byte[] payload)
        {
            writer.Begin(Request);
            AuthorityWire.WriteTarget(writer, target);
            writer.WriteString(mod);
            writer.WriteString(kind);
            writer.WriteBytes(payload);
        }

        internal static bool Read(WorldWireReader reader, out AuthorityTarget target, out string mod,
            out string kind, out byte[] payload)
        {
            mod = kind = string.Empty;
            payload = System.Array.Empty<byte>();
            return AuthorityWire.TryReadTarget(reader, out target)
                && reader.TryReadString(WorldEntryCodec.MaximumTokenBytes, out mod) && ManifestValidator.IsValidId(mod)
                && reader.TryReadString(WorldEntryCodec.MaximumTokenBytes, out kind) && InternalReplicationLimits.IsKind(kind)
                && reader.TryReadBytes(InternalReplicationLimits.MaximumPayloadBytes, out payload) && reader.AtEnd;
        }
    }
}
