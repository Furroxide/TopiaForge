using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Mods
{
    /// <summary>Bounded declarative UI, with callback identities instead of executable content.</summary>
    internal static partial class UiWireCodec
    {
        internal static bool Write(IUiWireWriter w, UiNode? node, string clockEpoch = "", UiWireEnvelope? envelope = null)
        {
            var hasCountdown = WriteComposition(w, node, clockEpoch, retained: false, envelope);
            UiClockBinding.Commit(node, clockEpoch);
            return hasCountdown;
        }

        // An admitted composition keeps its authored clock identity. Only its wire presentation expires.
        internal static bool WriteRetained(IUiWireWriter w, UiNode? node, string clockEpoch, UiWireEnvelope? envelope = null) =>
            WriteComposition(w, node, clockEpoch, retained: true, envelope);

        internal static bool Prepare(IUiWireWriter writer, UiNode? node, string clockEpoch, bool retained, UiWireEnvelope? envelope = null) =>
            WriteComposition(writer, node, clockEpoch, retained, envelope);

        private static bool WriteComposition(IUiWireWriter w, UiNode? node, string clockEpoch, bool retained, UiWireEnvelope? envelope)
        {
            UiWireValidation.Validate(node, requireImageWireMetadata: true);
            if (!retained) UiClockBinding.Check(node, clockEpoch);
            if (envelope != null) WriteEnvelope(w, envelope);
            else if (HasEnhancedNode(node)) throw new ArgumentException("The composition requires a version 2 UI envelope.");

            var hasCountdown = false;
            WriteNode(w, node, clockEpoch, retained, ref hasCountdown);
            return hasCountdown;
        }

        private static void WriteNode(IUiWireWriter w, UiNode? node, string clockEpoch, bool retained, ref bool hasCountdown)
        {
            if (node != null && IsEnhancedLayout(node))
            {
                WriteLayoutNode(w, node, clockEpoch, retained, ref hasCountdown);
                return;
            }
            switch (node)
            {
                case null: w.WriteByte(0); break;
                case UiText n: WriteText(w, n.Text, n.Style, n.Tone, n.Id); break;
                case UiColumn n: w.WriteByte(2); Children(w, n, clockEpoch, retained, ref hasCountdown); break;
                case UiRow n: w.WriteByte(3); Children(w, n, clockEpoch, retained, ref hasCountdown); break;
                case UiScroll n:
                    w.WriteByte(4); w.WriteSingle(n.Height);
                    WriteNode(w, n.Content, clockEpoch, retained, ref hasCountdown); break;
                case UiSplitPane n:
                    w.WriteByte(5); w.WriteByte((byte)n.Orientation); w.WriteSingle(n.PrimaryFraction);
                    WriteNode(w, n.Primary, clockEpoch, retained, ref hasCountdown);
                    WriteNode(w, n.Secondary, clockEpoch, retained, ref hasCountdown); break;
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
                case UiProgress n:
                    WriteProgress(w, n); break;
                case UiIcon n: WriteIcon(w, n); break;
                case UiImage n: WriteImage(w, n); break;
                case UiBadge n: WriteBadge(w, n); break;
                case UiPips n: WritePips(w, n); break;
                case UiCountdown n when retained && n.ClockEpoch != clockEpoch:
                    // Match the zero display without carrying a stale epoch or growing the admitted wire payload.
                    WriteText(w, n.Label.Length == 0 ? "0" : n.Label + " 0", UiTextStyle.Body, n.Tone,
                        EnhancedTone(n.Tone) ? n.Id : null); break;
                case UiCountdown n:
                    WriteCountdown(w, n, clockEpoch); hasCountdown = true; break;
                default: throw new ArgumentException(node.GetType().Name + " cannot be sent to a player. Use supported declarative UI nodes.");
            }
        }
        private static void Children(IUiWireWriter w, UiNode node, string clockEpoch, bool retained, ref bool hasCountdown)
        {
            var children = UiNodeChildren.Of(node);
            w.WriteVarUInt((ulong)children.Count);
            for (var i = 0; i < children.Count; i++) WriteNode(w, children[i], clockEpoch, retained, ref hasCountdown);
        }

        private static void WriteText(IUiWireWriter w, string text, UiTextStyle style, UiTone tone, string? id)
        {
            var enhanced = EnhancedText(style, tone, id);
            w.WriteByte(enhanced ? EnhancedTextTag : (byte)1);
            if (enhanced) Text(w, id ?? string.Empty);
            Text(w, text);
            w.WriteByte((byte)style);
            w.WriteByte((byte)tone);
        }

        private static void WriteProgress(IUiWireWriter w, UiProgress progress)
        {
            var enhanced = EnhancedTone(progress.Tone);
            w.WriteByte(enhanced ? EnhancedProgressTag : (byte)12);
            if (!enhanced) w.WriteByte(1);
            Text(w, progress.Id ?? string.Empty);
            w.WriteSingle(progress.Fraction);
            Text(w, progress.Label);
            w.WriteByte((byte)progress.Tone);
        }

        private static void WriteCountdown(IUiWireWriter w, UiCountdown countdown, string clockEpoch)
        {
            var enhanced = EnhancedTone(countdown.Tone);
            w.WriteByte(enhanced ? EnhancedCountdownTag : (byte)13);
            if (!enhanced) w.WriteByte(1);
            Text(w, countdown.Id ?? string.Empty);
            w.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(countdown.EndsAt));
            if (!enhanced) Text(w, clockEpoch);
            Text(w, countdown.Label);
            w.WriteByte((byte)countdown.Tone);
            if (enhanced) Text(w, clockEpoch);
        }

        internal static void Text(IUiWireWriter w, string text)
        {
            if (text.Length > 4096) throw new ArgumentException("Remote UI text is limited to 4096 characters.");
            w.WriteString(text);
        }

        private static void Flag(IUiWireWriter w, bool flag) => w.WriteByte(flag ? (byte)1 : (byte)0);

        internal static bool Invoke(UiNode? node, string id, string value)
        {
            if (!UiLayoutTree.TryFind(node, id, out var control, out var disabled) || disabled) return false;
            switch (control)
            {
                case UiButton b when value.Length == 0: b.Activated(); return true;
                case UiToggle t when value == "0" || value == "1": t.Changed(value == "1"); return true;
                case UiSlider s when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    Finite(number) && number >= s.Minimum && number <= s.Maximum:
                    s.Changed(number); return true;
                case UiTextInput t when value.Length <= t.MaximumLength: t.Changed(value); return true;
                case UiDropdown d:
                    for (var i = 0; i < d.Choices.Count; i++) if (d.Choices[i].Value == value) { d.Changed(value); return true; }
                    return false;
                case UiVirtualList l:
                    for (var i = 0; i < l.Items.Count; i++) if (l.Items[i].Id == value) { l.Selected(value); return true; }
                    return false;
                default: return false;
            }
        }
    }
}
