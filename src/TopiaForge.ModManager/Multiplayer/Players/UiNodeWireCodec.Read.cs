using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal static partial class UiNodeWireCodec
    {
        internal static UiNode? Read(WorldWireReader reader, Action<string, string> reply, string clockEpoch = "") => UiWireCodec.Read(reader, reply, clockEpoch);
        internal static string Text(WorldWireReader reader, int limit = 4096) => UiWireCodec.Text(reader, limit);
        internal static byte Byte(WorldWireReader reader, byte max = byte.MaxValue) => UiWireCodec.Byte(reader, max);
        internal static float Number(WorldWireReader reader) => UiWireCodec.Number(reader);
        internal static ulong Id(WorldWireReader reader) => UiWireCodec.Id(reader);
        internal static void End(WorldWireReader reader) => UiWireCodec.End(reader);
    }
}
