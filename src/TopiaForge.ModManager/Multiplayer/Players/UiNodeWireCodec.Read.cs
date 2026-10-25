using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal static partial class UiNodeWireCodec
    {
        internal static UiNode? Read(WorldWireReader reader, Action<string, string> reply)
        {
            var decoder = new Decoder(reader, reply);
            var root = decoder.Node(0);
            UiComposition.Validate(root);
            return root;
        }
        internal static string Text(WorldWireReader r, int limit = 4096)
        {
            if (!r.TryReadString(limit * 4, out var text) || text.Length > limit) throw Bad();
            return text;
        }
        internal static byte Byte(WorldWireReader r, byte max = byte.MaxValue)
        {
            if (!r.TryReadByte(out var value) || value > max) throw Bad();
            return value;
        }
        internal static float Number(WorldWireReader r)
        {
            if (!r.TryReadSingle(out var value) || !MultiplayerPlayers.Finite(value)) throw Bad();
            return value;
        }
        internal static ulong Id(WorldWireReader r)
        {
            if (!r.TryReadVarUInt(ulong.MaxValue, out var value)) throw Bad();
            return value;
        }
        internal static void End(WorldWireReader r) { if (!r.AtEnd) throw Bad(); }
        private static ArgumentException Bad() => new ArgumentException("The remote UI message is invalid.");

        private sealed class Decoder
        {
            private readonly WorldWireReader r;
            private readonly Action<string, string> reply;
            private int count;
            internal Decoder(WorldWireReader reader, Action<string, string> reply) { r = reader; this.reply = reply; }
            private int Count(int max)
            {
                if (!r.TryReadInt32(max, out var value)) throw Bad();
                return value;
            }
            internal UiNode? Node(int depth)
            {
                if (depth > 32 || ++count > 4096) throw Bad();
                var type = Byte(r, 11);
                if (type == 0) return null;
                if (type == 1) return new UiText(Text(r), (UiTextStyle)Byte(r, 2), (UiTone)Byte(r, 3));
                if (type == 2 || type == 3)
                {
                    var children = new UiNode[Count(256)];
                    for (var i = 0; i < children.Length; i++) children[i] = Node(depth + 1) ?? throw Bad();
                    return type == 2 ? (UiNode)new UiColumn(children) : new UiRow(children);
                }
                if (type == 4) { var height = Number(r); return new UiScroll(Node(depth + 1) ?? throw Bad(), height); }
                if (type == 5)
                {
                    var orientation = (UiSplitOrientation)Byte(r, 1); var fraction = Number(r);
                    return new UiSplitPane(Node(depth + 1) ?? throw Bad(), Node(depth + 1) ?? throw Bad(), orientation, fraction);
                }
                var id = Text(r, 128);
                if (string.IsNullOrWhiteSpace(id)) throw Bad();
                if (type == 11)
                {
                    var selected = Text(r); var rows = Byte(r, 20); var enabled = Byte(r, 1) != 0;
                    var items = new UiListItem[Count(4096)];
                    for (var i = 0; i < items.Length; i++) items[i] = new UiListItem(Text(r), Text(r), Text(r), Text(r));
                    return new UiVirtualList(id, items, value => reply(id, value), selected.Length == 0 ? null : selected, rows, enabled);
                }
                var label = Text(r);
                if (type == 6) return new UiButton(id, label, () => reply(id, ""), (UiButtonStyle)Byte(r, 3), Byte(r, 1) != 0);
                if (type == 7) return new UiToggle(id, label, Byte(r, 1) != 0, value => reply(id, value ? "1" : "0"), Byte(r, 1) != 0);
                if (type == 8) return new UiSlider(id, label, Number(r), Number(r), Number(r), value => reply(id, value.ToString("R", CultureInfo.InvariantCulture)), Byte(r, 1) != 0);
                if (type == 9)
                {
                    var value = Text(r); var placeholder = Text(r); var max = Count(4096); var enabled = Byte(r, 1) != 0;
                    return new UiTextInput(id, label, value, text => reply(id, text), placeholder, max, enabled);
                }
                var selectedValue = Text(r); var active = Byte(r, 1) != 0;
                var choices = new UiChoice[Count(256)];
                for (var i = 0; i < choices.Length; i++) choices[i] = new UiChoice(Text(r), Text(r));
                return new UiDropdown(id, label, choices, selectedValue, value => reply(id, value), active);
            }
        }
    }
}
