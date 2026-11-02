using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        internal static UiNode? Read(IUiWireReader reader, Action<string, string> reply, string clockEpoch = "")
        {
            var decoder = new Decoder(reader, reply, clockEpoch);
            var root = decoder.Node(0);
            UiWireValidation.Validate(root);
            UiClockBinding.Bind(root, clockEpoch);
            return root;
        }
        internal static string Text(IUiWireReader r, int limit = 4096)
        {
            if (!r.TryReadString(limit * 4, out var text) || text.Length > limit) throw Bad();
            return text;
        }
        internal static byte Byte(IUiWireReader r, byte max = byte.MaxValue)
        {
            if (!r.TryReadByte(out var value) || value > max) throw Bad();
            return value;
        }
        internal static float Number(IUiWireReader r)
        {
            if (!r.TryReadSingle(out var value) || !Finite(value)) throw Bad();
            return value;
        }
        internal static ulong Id(IUiWireReader r)
        {
            if (!r.TryReadVarUInt(ulong.MaxValue, out var value)) throw Bad();
            return value;
        }
        internal static void End(IUiWireReader r) { if (!r.AtEnd) throw Bad(); }
        private static ArgumentException Bad() => new ArgumentException("The remote UI message is invalid.");

        private sealed class Decoder
        {
            private readonly IUiWireReader r;
            private readonly Action<string, string> reply;
            private int count;
            private readonly string clockEpoch;
            internal Decoder(IUiWireReader reader, Action<string, string> reply, string clockEpoch) { r = reader; this.reply = reply; this.clockEpoch = clockEpoch; }
            private int Count(int max)
            {
                if (!r.TryReadInt32(max, out var value)) throw Bad();
                return value;
            }
            internal UiNode? Node(int depth)
            {
                if (depth > 32 || ++count > 4096) throw Bad();
                var type = Byte(r, 13);
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
                if (type == 12 || type == 13)
                {
                    if (Byte(r) != 1) throw Bad();
                    var widgetId = Text(r, 128);
                    if (type == 12) return new UiProgress(Number(r), Text(r, 256), (UiTone)Byte(r, 3), widgetId.Length == 0 ? null : widgetId);
                    if (!r.TryReadUInt64(out var bits)) throw Bad();
                    var deadline = BitConverter.Int64BitsToDouble((long)bits);
                    var epoch = Text(r, 256);
                    if (epoch != clockEpoch) throw new ArgumentException("UiCountdown clock epoch is stale; request the current composition.");
                    return new UiCountdown(deadline, Text(r, 256), (UiTone)Byte(r, 3), widgetId.Length == 0 ? null : widgetId);
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
