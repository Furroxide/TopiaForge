using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Bounded declarative UI, with callback identities instead of executable content.</summary>
    internal static partial class UiNodeWireCodec
    {
        internal static void Write(WorldWireWriter w, UiNode? node)
        {
            UiComposition.Validate(node);
            WriteNode(w, node);
        }
        private static void WriteNode(WorldWireWriter w, UiNode? node)
        {
            switch (node)
            {
                case null: w.WriteByte(0); break;
                case UiText n: w.WriteByte(1); Text(w, n.Text); w.WriteByte((byte)n.Style); w.WriteByte((byte)n.Tone); break;
                case UiColumn n: w.WriteByte(2); Children(w, n); break;
                case UiRow n: w.WriteByte(3); Children(w, n); break;
                case UiScroll n: w.WriteByte(4); w.WriteSingle(n.Height); WriteNode(w, n.Content); break;
                case UiSplitPane n:
                    w.WriteByte(5); w.WriteByte((byte)n.Orientation); w.WriteSingle(n.PrimaryFraction); WriteNode(w, n.Primary); WriteNode(w, n.Secondary); break;
                case UiButton n:
                    w.WriteByte(6); Text(w, n.Id!); Text(w, n.Label); w.WriteByte((byte)n.Style); Flag(w, n.Enabled); break;
                case UiToggle n:
                    w.WriteByte(7); Text(w, n.Id!); Text(w, n.Label); Flag(w, n.Value); Flag(w, n.Enabled); break;
                case UiSlider n:
                    w.WriteByte(8); Text(w, n.Id!); Text(w, n.Label); w.WriteSingle(n.Minimum); w.WriteSingle(n.Maximum); w.WriteSingle(n.Value); Flag(w, n.Enabled); break;
                case UiTextInput n:
                    w.WriteByte(9); Text(w, n.Id!); Text(w, n.Label); Text(w, n.Value); Text(w, n.Placeholder); w.WriteVarUInt((ulong)n.MaximumLength); Flag(w, n.Enabled); break;
                case UiDropdown n:
                    w.WriteByte(10); Text(w, n.Id!); Text(w, n.Label); Text(w, n.SelectedValue); Flag(w, n.Enabled); w.WriteVarUInt((ulong)n.Choices.Count);
                    for (var i = 0; i < n.Choices.Count; i++) { Text(w, n.Choices[i].Value); Text(w, n.Choices[i].Label); }
                    break;
                case UiVirtualList n:
                    w.WriteByte(11); Text(w, n.Id!); Text(w, n.SelectedItemId ?? ""); w.WriteByte((byte)n.VisibleRows); Flag(w, n.Enabled); w.WriteVarUInt((ulong)n.Items.Count);
                    for (var i = 0; i < n.Items.Count; i++) { var row = n.Items[i]; Text(w, row.Id); Text(w, row.Title); Text(w, row.Subtitle); Text(w, row.Badge); }
                    break;
                case UiGraphCanvas _: WriteNode(w, new UiText("Graph canvas is available on the host only.", UiTextStyle.Caption)); break;
                default: throw new ArgumentException("This UI node cannot be sent to a player.");
            }
        }
        private static void Children(WorldWireWriter w, UiLayoutNode node)
        {
            w.WriteVarUInt((ulong)node.Children.Count);
            for (var i = 0; i < node.Children.Count; i++) WriteNode(w, node.Children[i]);
        }
        internal static void Text(WorldWireWriter w, string text)
        {
            if (text.Length > 4096) throw new ArgumentException("Remote UI text is limited to 4096 characters.");
            w.WriteString(text);
        }
        private static void Flag(WorldWireWriter w, bool flag) => w.WriteByte(flag ? (byte)1 : (byte)0);

        internal static bool Invoke(UiNode? node, string id, string value)
        {
            if (node == null) return false;
            if (node.Id == id)
            {
                switch (node)
                {
                    case UiButton b when b.Enabled && value.Length == 0: b.Activated(); return true;
                    case UiToggle t when t.Enabled && (value == "0" || value == "1"): t.Changed(value == "1"); return true;
                    case UiSlider s when s.Enabled && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && MultiplayerPlayers.Finite(number) && number >= s.Minimum && number <= s.Maximum:
                        s.Changed(number); return true;
                    case UiTextInput t when t.Enabled && value.Length <= t.MaximumLength: t.Changed(value); return true;
                    case UiDropdown d when d.Enabled:
                        for (var i = 0; i < d.Choices.Count; i++) if (d.Choices[i].Value == value) { d.Changed(value); return true; }
                        return false;
                    case UiVirtualList l when l.Enabled:
                        for (var i = 0; i < l.Items.Count; i++) if (l.Items[i].Id == value) { l.Selected(value); return true; }
                        return false;
                }
                return false;
            }
            if (node is UiLayoutNode layout)
            {
                for (var i = 0; i < layout.Children.Count; i++) if (Invoke(layout.Children[i], id, value)) return true;
            }
            else if (node is UiScroll scroll) return Invoke(scroll.Content, id, value);
            else if (node is UiSplitPane split) return Invoke(split.Primary, id, value) || Invoke(split.Secondary, id, value);
            return false;
        }
    }
}
