using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>The runtime adapter for the shared bounded widget codec.</summary>
    internal static partial class UiNodeWireCodec
    {
        internal static bool Write(WorldWireWriter writer, UiNode? node, string clockEpoch = "") => UiWireCodec.Write(writer, node, clockEpoch);
        internal static bool WriteRetained(WorldWireWriter writer, UiNode? node, string clockEpoch) => UiWireCodec.WriteRetained(writer, node, clockEpoch);
        internal static void Text(WorldWireWriter writer, string text) => UiWireCodec.Text(writer, text);
        internal static bool Invoke(UiNode? node, string id, string value) => UiWireCodec.Invoke(node, id, value);
    }
}
