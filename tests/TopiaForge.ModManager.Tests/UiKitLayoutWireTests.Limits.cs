using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitLayoutWireTests
    {
        private static void MalformedLayoutFieldsAreRefused()
        {
            Refused(() => Read(Raw(writer =>
            {
                writer.WriteByte(16);
                writer.WriteString("");
                writer.WriteByte(6);
                ZeroInsets(writer);
                writer.WriteByte(0);
                writer.WriteByte(0);
                writer.WriteVarUInt(0);
            })), "Sparse spacing values must be declared tokens.");

            Refused(() => Read(Raw(writer =>
            {
                writer.WriteByte(17);
                writer.WriteString("");
                writer.WriteByte(8);
                ZeroInsets(writer);
                writer.WriteByte(5);
                writer.WriteByte(2);
                writer.WriteByte(0);
                writer.WriteVarUInt(0);
            })), "A presence flag must be exactly zero or one.");

            Refused(() => Read(Raw(writer =>
            {
                writer.WriteByte(17);
                writer.WriteString("");
                writer.WriteByte(8);
                ZeroInsets(writer);
                writer.WriteByte(0);
                writer.WriteByte(1);
                writer.WriteByte(4);
                writer.WriteVarUInt(0);
            })), "The cross enum rejects undeclared values.");

            foreach (var value in new[] { float.NaN, float.PositiveInfinity, -1f, 4097f })
            {
                Refused(() => Read(Raw(writer =>
                {
                    writer.WriteByte(31);
                    writer.WriteString("");
                    writer.WriteByte(1);
                    writer.WriteSingle(value);
                    writer.WriteByte(0);
                    writer.WriteByte(0);
                })), "Present dimensions must be finite and within the layout range.");
            }

            foreach (var style in new[] { "Capitalized", "topiaforge.reserved", new string('a', 65) })
                Refused(() => Read(Raw(writer => WriteRawBox(writer, style))), "Named styles use the exact safe identifier grammar and UTF-8 byte bound.");

            Refused(() => Read(Raw(writer =>
            {
                writer.WriteByte(26);
                writer.WriteString("");
                writer.WriteSingle(100);
                writer.WriteSingle(50);
                writer.WriteByte(0);
                writer.WriteVarUInt(2);
                writer.WriteByte(0xC3);
                writer.WriteByte(0x28);
            })), "Invalid UTF-8 inside a style id is refused before rendering.");

            Refused(() => Read(Raw(writer =>
            {
                writer.WriteByte(18);
                writer.WriteString("");
                ZeroInsets(writer);
                writer.WriteByte(14);
            })), "A layout wrapper cannot contain another root envelope.");
            Refused(() => Read(Raw(writer =>
            {
                writer.WriteByte(2);
                writer.WriteByte(0x80);
                writer.WriteByte(0);
            })), "Child counts use canonical varuints.");
            Refused(() => Read(Bytes(new UiBox(1, 1)).Concat(new byte[] { 0 }).ToArray()), "Trailing bytes fail the complete frame.");
            Refused(() => Read(Raw(writer =>
            {
                writer.WriteByte(32);
                writer.WriteString("");
                writer.WriteByte(1);
            })), "A decoded Spacer still requires an immediate row or column parent.");
        }

        private static void DecoderCountsWrappersAndBothCardChildren()
        {
            That(Read(Raw(writer => WriteDepth(writer, 32))) is UiPadding, "A 32-node chain is within the root-inclusive depth cap.");
            Refused(() => Read(Raw(writer => WriteDepth(writer, 33))), "The 33rd wrapper/leaf is refused.");
            That(Read(Raw(writer => WriteNodeCount(writer, 4096))) is UiColumn, "Exactly 4096 occurrences are admitted.");
            var excessCountOffset = 0;
            var excessBytes = Raw(writer => excessCountOffset = WriteNodeCount(writer, 4097));
            var excessReader = new WorldWireReader();
            excessReader.Reset(excessBytes);
            Refused(() => UiNodeWireCodec.Read(excessReader, Ignore, Epoch, out _), "The decoder refuses the next node count.");
            That(excessReader.Position == excessCountOffset, "The decoder refuses the excess child count before reading the branch.");
            Refused(() => Read(Raw(writer =>
            {
                writer.WriteByte(2);
                writer.WriteVarUInt(257);
            })), "A parent cannot claim a 257-child array.");

            var active = new UiCard(new UiText("Body", id: "y"), new UiText("Header", id: "x"));
            var duplicate = Bytes(active);
            var matches = Enumerable.Range(0, duplicate.Length - 2)
                .Where(index => duplicate[index] == 15 && duplicate[index + 1] == 1 && duplicate[index + 2] == (byte)'y');
            duplicate[matches.Single() + 2] = (byte)'x';
            Refused(() => Read(duplicate), "A card header and body cannot repeat the same ID.");

            var writer = new WorldWireWriter(32768);
            Refused(() => UiWireCodec.Write(writer, new UiPadding(new UnsupportedLayoutLeaf(), default), Epoch, Envelope),
                "A new wrapper cannot hide an unsupported node from validation.");
            That(writer.Length == 0, "Unsupported children are refused before serializing the envelope.");
        }

        private static void TargetedFramesCountLayoutBytesAndPreserveClockOnRefusal()
        {
            var timer = new UiCountdown(50, "Time", UiTone.Primary, "budget-timer");
            var children = Enumerable.Range(0, 7).Select(_ => (UiNode)new UiText(new string('a', 4096))).ToList();
            children.Add(new UiCard(new UiSizedBox(timer, width: 72, height: 24),
                new UiPadding(new UiText("Header"), Insets), new UiStyleRef("field-card")));
            var layout = new UiColumn(children);
            var request = new UiSurfaceRequest("layout", "Title", new string('b', 4096), content: layout);
            var treeSize = UiWireCodec.Measure(layout, Epoch, 32 * 1024, envelope: Envelope);
            That(treeSize < 32 * 1024, "The fixture only exceeds capacity when the enclosing surface fields are measured.");
            QuotaRefused(() => UiWireCodec.MeasureTargetedSurface(Package, "guest", 1, 3,
                request, request.Body, true, layout, Epoch, envelope: Envelope));
            That(timer.ClockEpoch == null, "A refused targeted frame cannot bind a proposed nested timer.");

            var writer = new WorldWireWriter(32768);
            writer.WriteByte(1);
            writer.WriteByte(10);
            writer.WriteString(Package);
            writer.WriteString("guest");
            writer.WriteVarUInt(1);
            writer.WriteVarUInt(3);
            UiWireCodec.WriteTargetedSurfaceBody(writer, request, "small", true, layout, Epoch, retained: false, envelope: Envelope);
            That(writer.Length == UiWireCodec.MeasureTargetedSurface(Package, "guest", 1, 3,
                request, "small", true, layout, Epoch, envelope: Envelope), "The complete target-frame measure equals production writing.");
            That(timer.ClockEpoch == null, "Staging framed bytes leaves the authored timer untouched until commit.");
        }

        private static byte[] Raw(Action<WorldWireWriter> body)
        {
            var writer = new WorldWireWriter(32768);
            writer.WriteByte(14);
            writer.WriteByte(1);
            writer.WriteString(Package);
            for (var i = 0; i < 4; i++) writer.WriteUInt64(0x2929292929292929);
            writer.WriteVarUInt(4);
            writer.WriteString("");
            for (var i = 0; i < 4; i++) writer.WriteUInt64(0);
            body(writer);
            return Copy(writer);
        }

        private static void WriteRawBox(WorldWireWriter writer, string style)
        {
            writer.WriteByte(26);
            writer.WriteString("");
            writer.WriteSingle(100);
            writer.WriteSingle(50);
            writer.WriteByte(0);
            writer.WriteString(style);
        }

        private static void ZeroInsets(WorldWireWriter writer)
        {
            for (var i = 0; i < 4; i++) writer.WriteByte(0);
        }

        private static void WriteDepth(WorldWireWriter writer, int total)
        {
            for (var i = 1; i < total; i++)
            {
                writer.WriteByte(18);
                writer.WriteString("");
                ZeroInsets(writer);
            }
            WriteRawLeaf(writer);
        }

        private static int WriteNodeCount(WorldWireWriter writer, int total)
        {
            var branches = new List<int>();
            for (var remaining = total - 1; remaining > 0;)
            {
                var children = Math.Min(256, remaining - 1);
                branches.Add(children);
                remaining -= children + 1;
            }

            writer.WriteByte(2);
            writer.WriteVarUInt((ulong)branches.Count);
            var lastCountOffset = writer.Length;
            foreach (var children in branches)
            {
                writer.WriteByte(2);
                lastCountOffset = writer.Length;
                writer.WriteVarUInt((ulong)children);
                for (var i = 0; i < children; i++) WriteRawLeaf(writer);
            }
            return lastCountOffset;
        }

        private static void WriteRawLeaf(WorldWireWriter writer)
        {
            writer.WriteByte(1);
            writer.WriteString("");
            writer.WriteByte(0);
            writer.WriteByte(0);
        }

        private sealed class UnsupportedLayoutLeaf : UiNode
        {
            internal UnsupportedLayoutLeaf() : base(null) { }
        }
    }
}
