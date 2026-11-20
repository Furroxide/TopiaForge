using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiArtWireTests
    {
        private static readonly byte[] AssetDigest = Enumerable.Range(96, 32).Select(value => (byte)value).ToArray();
        private static readonly UiWireEnvelope Envelope = new UiWireEnvelope(
            "example.ui", Enumerable.Repeat((byte)17, 32).ToArray(), 129,
            "selected-theme", Enumerable.Repeat((byte)41, 32).ToArray());
        private static readonly byte[] EnvelopeBytes = Encode(null, Envelope)[..^1];

        internal static void Run()
        {
            ExactArtPayloads();
            AuthoredAndWireImageAreDistinct();
            ArtRequiresTheRootEnvelope();
            MalformedArtIsRejected();
            MeasureUsesTheSameBoundedWriter();
            Console.WriteLine("UI art wire tags, strict fields, image metadata, envelope routing and frame budgets passed.");
        }

        private static void ExactArtPayloads()
        {
            var icon = new UiIcon(UiGlyph.MicrophoneOff, size: 32f, tone: UiTone.Muted, id: "mic");
            var heard = RoundTrip<UiIcon>(icon, IconPayload());
            Check(heard.Id == "mic" && heard.Glyph == UiGlyph.MicrophoneOff && heard.SemanticLabel == "microphone off" &&
                heard.Size == 32f && heard.Tone == UiTone.Muted && !heard.Decorative, "The resolved icon label round-trips.");
            var decorativeIcon = new UiIcon(UiGlyph.Check, decorative: true);
            Check(RoundTrip<UiIcon>(decorativeIcon, IconPayload(id: "", glyph: 0, label: "", size: 24f, tone: 0, decorative: 1))
                is { Id: null, SemanticLabel: "", Decorative: true }, "A decorative icon remains unlabelled.");
            var accented = new string('é', 256);
            Check(RoundTrip(new UiIcon(UiGlyph.MicrophoneOff, accented, size: 32f, tone: UiTone.Muted, id: "mic"),
                IconPayload(label: accented)).SemanticLabel == accented, "The label limit counts UTF-16 units.");

            var authored = new UiImage(new UiImageRef("asset-pack", "emblem"), "Station emblem", width: 120f,
                fit: UiImageFit.Cover, tint: UiTone.Primary, id: "art");
            var image = authored.WithWireMetadata(120f, 64f, AssetDigest);
            var loaded = RoundTrip<UiImage>(image, ImagePayload());
            Check(loaded.Id == "art" && loaded.Image.Equals(authored.Image) &&
                loaded.SemanticLabel == "Station emblem" && loaded.Width == 120f && loaded.Height == 64f &&
                loaded.Fit == UiImageFit.Cover && loaded.Tint == UiTone.Primary && !loaded.Decorative &&
                loaded.PackDescriptorDigest.Span.SequenceEqual(AssetDigest) &&
                !loaded.PackDescriptorDigest.Span.SequenceEqual(Envelope.DescriptorDigest.Span),
                "An image carries its own pack digest, concrete axes and visible fallback label.");
            var decorativeImage = new UiImage(authored.Image, "", width: 1f, height: 1f, decorative: true)
                .WithWireMetadata(1f, 1f, AssetDigest);
            Check(RoundTrip<UiImage>(decorativeImage,
                ImagePayload(id: "", label: "", width: 1f, height: 1f, fit: 0, tintPresent: 0, decorative: 1)) is
                { Id: null, SemanticLabel: "", Width: 1f, Height: 1f, Tint: null, Decorative: true },
                "The image's optional tint flag keeps a decorative one-unit box.");

            var badge = new UiBadge("Warning", UiPaintRole.Warning, UiBadgeStyle.Outlined, UiGlyph.Warning,
                new UiStyleRef("status.edge"), id: "badge");
            Check(RoundTrip<UiBadge>(badge, BadgePayload()) is
                { Id: "badge", Text: "Warning", Role: UiPaintRole.Warning, Presentation: UiBadgeStyle.Outlined,
                    Icon: UiGlyph.Warning, Style.Id: "status.edge" }, "Badge role, glyph and style are preserved.");
            Check(RoundTrip<UiBadge>(new UiBadge("Warning"), BadgePayload(id: "", role: 0, presentation: 0, iconPresent: 0, style: "")) is
                { Id: null, Icon: null, Style: null }, "Optional badge fields stay absent.");

            var pips = new UiPips(32, 9, "Cells", 0.5f, UiPipStyle.Segments, UiPaintRole.Accent,
                new UiStyleRef("charge.track"), id: "pips");
            Check(RoundTrip<UiPips>(pips, PipsPayload()) is
                { Id: "pips", Total: 32, Filled: 9, Label: "Cells", Fraction: 0.5f, Presentation: UiPipStyle.Segments,
                    Role: UiPaintRole.Accent, Style.Id: "charge.track", DisplayLabel: "Cells: 9 / 32 (50% refilling)" },
                "Pips keep their static fill and accessible count.");
            Check(RoundTrip<UiPips>(new UiPips(1, 1, "Cells"),
                PipsPayload(id: "", total: 1, filled: 1, fraction: 0f, presentation: 0, style: "")) is
                { Total: 1, Filled: 1, Fraction: 0f, Style: null }, "A full pip track uses zero fraction.");
        }

        private static void AuthoredAndWireImageAreDistinct()
        {
            var image = new UiImage(new UiImageRef("asset-pack", "emblem"), "Station emblem", width: 120f);
            var digest = AssetDigest.ToArray();
            var normalized = image.WithWireMetadata(120f, 64f, digest);
            digest[0] ^= 0xff;
            Check(image.Height == null && image.PackDescriptorDigest.IsEmpty &&
                normalized.Height == 64f && normalized.PackDescriptorDigest.Span.SequenceEqual(AssetDigest),
                "The retained authored image stays nullable and its wire copy owns the digest.");

            var countdown = new UiCountdown(25, "Clock");
            var writer = new WorldWireWriter();
            var tree = new UiColumn(countdown, image);
            Throws<ArgumentException>(() => UiWireCodec.Write(writer, tree, "initial", Envelope));
            Check(writer.Length == 0 && countdown.ClockEpoch == null,
                "An unresolved image is refused before bytes or clock bindings are committed.");
            var sizedWithoutDigest = new UiImage(image.Image, image.SemanticLabel, width: 120f, height: 64f);
            Throws<ArgumentException>(() => UiWireCodec.Write(writer, sizedWithoutDigest, envelope: Envelope));
            Throws<ArgumentException>(() => image.WithWireMetadata(120f, 64f, new byte[31]));
            Check(writer.Length == 0, "Explicit dimensions alone cannot supply the image's package identity.");
        }

        private static void ArtRequiresTheRootEnvelope()
        {
            var image = new UiImage(new UiImageRef("asset-pack", "emblem"), "Station emblem")
                .WithWireMetadata(120f, 64f, AssetDigest);
            var nodes = new UiNode[] { new UiIcon(UiGlyph.Check), image, new UiBadge("Warning"), new UiPips(3, 1, "Cells") };
            for (var index = 0; index < nodes.Length; index++)
            {
                var art = nodes[index];
                // A legacy row adds no requirement of its own. The noninteractive art leaf requires the envelope.
                var tree = new UiRow(new UiText("Current"), art);
                Check(UiWireCodec.NeedsEnvelope(tree), "Nested art requires an authenticated version 2 envelope.");
                var writer = new WorldWireWriter();
                Throws<ArgumentException>(() => UiWireCodec.Write(writer, tree));
                Check(writer.Length == 0, "Legacy routing refuses art before writing.");
                var framed = Encode(art, Envelope);
                Throws<ArgumentException>(() => UiWireCodec.Read(Reader(framed), Ignore));
                Throws<ArgumentException>(() => Decode(framed[EnvelopeBytes.Length..]));
            }

            Check(!UiWireCodec.NeedsEnvelope(new UiRow(new UiText("x"))) &&
                Encode(new UiRow(new UiText("x")), null).SequenceEqual(new byte[] { 3, 1, 1, 1, (byte)'x', 0, 0 }),
                "Legacy text in a row remains byte-identical.");
            BadPayload(new byte[] { 2, 1 }.Concat(Encode(nodes[0], Envelope)).ToArray());
        }

        private static void MalformedArtIsRejected()
        {
            BadPayload(IconPayload(glyph: 31));
            BadPayload(IconPayload(tone: 7));
            BadPayload(IconPayload(decorative: 2));
            BadPayload(IconPayload(label: ""));
            BadPayload(IconPayload(label: new string('é', 257)));
            BadPayload(IconPayload(id: new string('x', 129)));
            BadPayload(IconPayload(size: float.NaN));
            BadPayload(IconPayload(size: 129f));
            BadPayload(IconPayload(decorative: 1));
            BadPayload(new byte[] { 27, 0, 0, 1, 0xff });
            BadPayload(new byte[] { 27, 0x81, 0x00, (byte)'x' }.Concat(IconPayload()[5..]).ToArray());

            BadPayload(ImagePayload(pack: "topiaforge.reserved"));
            BadPayload(ImagePayload(pack: new string('p', 65)));
            BadPayload(ImagePayload(image: "Invalid"));
            BadPayload(ImagePayload(width: 0f));
            BadPayload(ImagePayload(height: 1025f));
            BadPayload(ImagePayload(height: float.PositiveInfinity));
            BadPayload(ImagePayload(fit: 2));
            BadPayload(ImagePayload(tintPresent: 2));
            BadPayload(ImagePayload(tint: 7));
            BadPayload(ImagePayload(decorative: 2));
            BadPayload(ImagePayload(label: "", decorative: 0));
            BadPayload(ImagePayload()[..30]);
            BadPayload(ImagePayload().Concat(new byte[] { 0 }).ToArray());

            BadPayload(BadgePayload(text: ""));
            BadPayload(BadgePayload(role: 9));
            BadPayload(BadgePayload(presentation: 2));
            BadPayload(BadgePayload(iconPresent: 2));
            BadPayload(BadgePayload(glyph: 31));
            BadPayload(BadgePayload(style: "Invalid"));

            BadPayload(PipsPayload(total: 0, filled: 0));
            BadPayload(PipsPayload(total: 33));
            BadPayload(PipsPayload(total: 2, filled: 3));
            BadPayload(PipsPayload(total: 2, filled: 2));
            BadPayload(PipsPayload(fraction: -0.01f));
            BadPayload(PipsPayload(fraction: 1f));
            BadPayload(PipsPayload(presentation: 2));
            BadPayload(PipsPayload(role: 9));
            BadPayload(PipsPayload(style: "Invalid"));
            Throws<ArgumentException>(() => UiWireCodec.Measure(new UiIcon(UiGlyph.Check, "\ud800"), "", 32768, envelope: Envelope));
        }

        private static void MeasureUsesTheSameBoundedWriter()
        {
            var image = new UiImage(new UiImageRef("asset-pack", "emblem"), "Station emblem")
                .WithWireMetadata(120f, 64f, AssetDigest);
            var tree = new UiColumn(image, new UiPips(3, 1, "Charge", 0.75f));
            var request = new UiSurfaceRequest("station", "Station", "Current", content: tree);
            var actual = Encode(tree, Envelope);
            Check(UiWireCodec.Measure(tree, "", actual.Length, envelope: Envelope) == actual.Length,
                "An art composition exactly at its byte budget fits.");
            Throws<InvalidOperationException>(() => UiWireCodec.Measure(tree, "", actual.Length - 1, envelope: Envelope));

            var writer = new WorldWireWriter(32 * 1024);
            writer.Begin(10);
            writer.WriteString("example.ui"); writer.WriteString("guest");
            writer.WriteVarUInt(128); writer.WriteVarUInt(16384);
            UiWireCodec.WriteTargetedSurfaceBody(writer, request, request.Body, true, tree, "", retained: true, envelope: Envelope);
            Check(UiWireCodec.MeasureTargetedSurface("example.ui", "guest", 128, 16384, request, request.Body,
                true, tree, "", retained: true, envelope: Envelope) == writer.Length, "Targeted measurement counts the whole art frame.");
        }

        private static byte[] IconPayload(string id = "mic", byte glyph = 9, string label = "microphone off",
            float size = 32f, byte tone = 4, byte decorative = 0) => Payload(writer =>
        {
            writer.WriteByte(27); writer.WriteString(id); writer.WriteByte(glyph); writer.WriteString(label);
            writer.WriteSingle(size); writer.WriteByte(tone); writer.WriteByte(decorative);
        });

        private static byte[] ImagePayload(string id = "art", string pack = "asset-pack", string image = "emblem",
            string label = "Station emblem", float width = 120f, float height = 64f, byte fit = 1,
            byte tintPresent = 1, byte tint = 5, byte decorative = 0) => Payload(writer =>
        {
            writer.WriteByte(28); writer.WriteString(id); writer.WriteString(pack);
            foreach (var value in AssetDigest) writer.WriteByte(value);
            writer.WriteString(image); writer.WriteString(label); writer.WriteSingle(width); writer.WriteSingle(height);
            writer.WriteByte(fit); writer.WriteByte(tintPresent);
            if (tintPresent != 0) writer.WriteByte(tint);
            writer.WriteByte(decorative);
        });

        private static byte[] BadgePayload(string id = "badge", string text = "Warning", byte role = 7, byte presentation = 1,
            byte iconPresent = 1, byte glyph = 5, string style = "status.edge") => Payload(writer =>
        {
            writer.WriteByte(29); writer.WriteString(id); writer.WriteString(text); writer.WriteByte(role);
            writer.WriteByte(presentation); writer.WriteByte(iconPresent);
            if (iconPresent != 0) writer.WriteByte(glyph);
            writer.WriteString(style);
        });

        private static byte[] PipsPayload(string id = "pips", byte total = 32, byte filled = 9, string label = "Cells",
            float fraction = 0.5f, byte presentation = 1, byte role = 5, string style = "charge.track") => Payload(writer =>
        {
            writer.WriteByte(30); writer.WriteString(id); writer.WriteByte(total); writer.WriteByte(filled);
            writer.WriteString(label); writer.WriteSingle(fraction); writer.WriteByte(presentation); writer.WriteByte(role);
            writer.WriteString(style);
        });

        private static byte[] Payload(Action<WorldWireWriter> write)
        {
            var writer = new WorldWireWriter();
            write(writer);
            return writer.Segment.ToArray();
        }

        private static T RoundTrip<T>(T node, byte[] payload) where T : UiNode
        {
            var expected = EnvelopeBytes.Concat(payload).ToArray();
            Check(Encode(node, Envelope).SequenceEqual(expected), $"The {typeof(T).Name} writer uses the published byte order.");
            return Decode(expected) as T ?? throw new InvalidOperationException("The art node did not round-trip.");
        }

        private static void BadPayload(byte[] payload) =>
            Throws<ArgumentException>(() => Decode(EnvelopeBytes.Concat(payload).ToArray()));

        private static byte[] Encode(UiNode? node, UiWireEnvelope? envelope) =>
            Payload(writer => UiWireCodec.Write(writer, node, envelope: envelope));

        private static UiNode? Decode(byte[] bytes)
        {
            var reader = Reader(bytes);
            var result = UiWireCodec.ReadWithEnvelope(reader, Ignore, "", out _);
            UiWireCodec.End(reader);
            return result;
        }

        private static WorldWireReader Reader(byte[] bytes)
        {
            var reader = new WorldWireReader();
            reader.Reset(bytes);
            return reader;
        }

        private static void Ignore(string _, string __) { }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
    }
}
