using System;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitOverlayWireTests
    {
        internal static void Run()
        {
            LegacyTargetedFramesKeepTheirBytes();
            OverlayFlagIsStrictAndPartOfTheExactFrame();
            Console.WriteLine("K2 overlay wire passed for legacy bytes, ownership envelope, strict flags and exact framing.");
        }

        private static void LegacyTargetedFramesKeepTheirBytes()
        {
            for (var kind = UiSurfaceKind.Hud; kind <= UiSurfaceKind.FullscreenTool; kind++)
            {
                var request = new UiSurfaceRequest("legacy", "Title", "Body", kind, 320, 160, new UiText("Read"));
                var written = Write(request, request.Content, null);
                var previous = Begin();
                previous.WriteString(request.Id); previous.WriteString(request.Title); previous.WriteString(request.Body);
                previous.WriteByte((byte)kind); previous.WriteSingle(320); previous.WriteSingle(160);
                previous.WriteByte((byte)request.HudAnchor); previous.WriteByte((byte)request.HudVisibility);
                previous.WriteByte(1);
                UiNodeWireCodec.Write(previous, request.Content);
                Assert(written.AsSpan().SequenceEqual(previous.Segment.AsSpan()) && written.Length == Measure(request, request.Content, null),
                    "K2 must preserve the complete targeted frame for each legacy surface kind.");
            }
        }

        private static void OverlayFlagIsStrictAndPartOfTheExactFrame()
        {
            var envelope = new UiWireEnvelope("io.example.overlay", new byte[32], 1UL << 63, null, new byte[32]);
            var button = new UiButton("close", "Close", () => { });
            var request = new UiSurfaceRequest("overlay", "Title", "Body", UiSurfaceKind.Overlay, 320, 160, button);
            var nonDismissible = request.WithDismissible(false);
            var open = Write(request, button, envelope);
            var closed = Write(nonDismissible, button, envelope);
            Assert(open.Length == closed.Length && open.AsSpan(0, open.Length - 1).SequenceEqual(closed.AsSpan(0, closed.Length - 1)) &&
                open[open.Length - 1] == 1 && closed[closed.Length - 1] == 0,
                "The immutable overlay request adds one dismissible byte after the tree.");
            Assert(!ReadDismissible(closed) && ReadDismissible(open), "The real wire reader consumes exactly the overlay flag.");
            Assert(Refuses<ArgumentException>(() => ReadDismissible(open.AsSpan(0, open.Length - 1).ToArray())),
                "The overlay flag is required after its tree.");
            open[open.Length - 1] = 2;
            Assert(Refuses<ArgumentException>(() => ReadDismissible(open)), "A non-canonical overlay flag is refused.");
            Assert(Refuses<ArgumentException>(() => Write(request, new UiText("Ordinary"), null)),
                "Even an old text tag needs an overlay ownership envelope.");

            var children = new UiNode[8];
            for (var index = 0; index < 7; index++) children[index] = new UiText(new string('x', 4096));
            children[7] = new UiText(new string('x', 256));
            var initial = Measure(request, new UiColumn(children), envelope);
            var tail = 256 + MultiplayerPlayers.MaximumMessageBytes - initial;
            children[7] = new UiText(new string('x', tail));
            var exact = new UiColumn(children);
            var window = new UiSurfaceRequest(request.Id, request.Title, request.Body, UiSurfaceKind.Window, 320, 160);
            Assert(Measure(request, exact, envelope) == MultiplayerPlayers.MaximumMessageBytes &&
                Write(request, exact, envelope).Length == MultiplayerPlayers.MaximumMessageBytes &&
                Measure(window, exact, envelope) == MultiplayerPlayers.MaximumMessageBytes - 1,
                "The overlay flag consumes one byte of the complete targeted budget.");
            children[7] = new UiText(new string('x', tail + 1));
            var overflow = new UiColumn(children);
            Assert(Refuses<InvalidOperationException>(() => Measure(request, overflow, envelope)) &&
                Refuses<InvalidOperationException>(() => Write(request, overflow, envelope)),
                "Counting and writing both reject an overlay one byte over its framed limit.");
        }

        private static int Measure(UiSurfaceRequest request, UiNode? content, UiWireEnvelope? envelope) =>
            UiWireCodec.MeasureTargetedSurface("io.example.overlay", "guest", 1, 1,
                request, request.Body, true, content, "", envelope: envelope);

        private static byte[] Write(UiSurfaceRequest request, UiNode? content, UiWireEnvelope? envelope)
        {
            var writer = Begin();
            UiWireCodec.WriteTargetedSurfaceBody(writer, request, request.Body, true, content, "", retained: false, envelope);
            return MultiplayerPresentation.Copy(writer);
        }

        private static WorldWireWriter Begin()
        {
            var writer = new WorldWireWriter(MultiplayerPlayers.MaximumMessageBytes);
            writer.Begin(10); writer.WriteString("io.example.overlay"); writer.WriteString("guest");
            writer.WriteVarUInt(1); writer.WriteVarUInt(1);
            return writer;
        }

        private static bool ReadDismissible(byte[] bytes)
        {
            var reader = new WorldWireReader();
            reader.Reset(bytes);
            Assert(reader.TryReadHeader(out var kind) && kind == 10, "Read the existing targeted surface record.");
            _ = UiNodeWireCodec.Text(reader); _ = UiNodeWireCodec.Text(reader);
            _ = UiNodeWireCodec.Id(reader); _ = UiNodeWireCodec.Id(reader);
            _ = UiNodeWireCodec.Text(reader); _ = UiNodeWireCodec.Text(reader); _ = UiNodeWireCodec.Text(reader);
            Assert(UiNodeWireCodec.Byte(reader) == 3, "Overlay uses the appended kind 3.");
            _ = UiNodeWireCodec.Number(reader); _ = UiNodeWireCodec.Number(reader);
            _ = UiNodeWireCodec.Byte(reader); _ = UiNodeWireCodec.Byte(reader); _ = UiNodeWireCodec.Byte(reader);
            Assert(UiNodeWireCodec.Read(reader, (_, _) => { }, "", out var envelope) is UiButton && envelope != null,
                "The overlay flag follows the ordinary tree beneath the ownership envelope.");
            var dismissible = UiWireCodec.ReadSurfaceDismissible(reader, UiSurfaceKind.Overlay);
            UiNodeWireCodec.End(reader);
            return dismissible;
        }

        private static bool Refuses<TException>(Action action) where TException : Exception
        {
            try { action(); return false; }
            catch (TException) { return true; }
        }
    }
}
