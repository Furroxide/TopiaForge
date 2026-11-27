using System;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitStingerWireTests
    {
        internal static void Run()
        {
            FieldsRoundTripWithoutASurfaceGeneration(UiStingerFontFit.KitOnly);
            FieldsRoundTripWithoutASurfaceGeneration(UiStingerFontFit.BothMeasured);
            BadFieldsAndTrailingBytesAreRefused();
            RetirementClosesOnlyAnExactNonzeroToken();
            Console.WriteLine("K2 stinger frame passed for exact event fields, strict bounds and no surface generation.");
        }

        private static void FieldsRoundTripWithoutASurfaceGeneration(UiStingerFontFit fontFit)
        {
            var package = new byte[32];
            var theme = new byte[32];
            Array.Fill(package, (byte)0x11);
            Array.Fill(theme, (byte)0x22);
            var identity = new UiWireEventEnvelope("io.example.stinger", package, "survival", theme);
            var request = new UiStingerRequest("WAVE CLEAR", "Requisitions open", UiGlyph.Check,
                UiPaintRole.Success, new UiStyleRef("wave-clear"), 4.25d);
            var actual = new WorldWireWriter(2304);
            actual.Begin(UiStingerDelivery.EventKind); actual.WriteString(""); actual.WriteString("recipient");
            const ulong scopeToken = 257;
            UiWireCodec.WriteStinger(actual, identity, scopeToken, fontFit, request);

            var expected = new WorldWireWriter(2304);
            expected.Begin(34); expected.WriteString(""); expected.WriteString("recipient");
            expected.WriteString("io.example.stinger");
            for (var i = 0; i < 4; i++) expected.WriteUInt64(0x1111111111111111UL);
            expected.WriteString("survival");
            for (var i = 0; i < 4; i++) expected.WriteUInt64(0x2222222222222222UL);
            expected.WriteVarUInt(scopeToken);
            expected.WriteByte((byte)fontFit);
            expected.WriteString("WAVE CLEAR"); expected.WriteString("Requisitions open");
            expected.WriteByte(1); expected.WriteByte((byte)UiGlyph.Check); expected.WriteByte(6);
            expected.WriteString("wave-clear");
            expected.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(4.25d));
            Assert(actual.Segment.AsSpan().SequenceEqual(expected.Segment.AsSpan()),
                "Event 34 encodes its exact owner, descriptor and scope token with no retained surface generation.");

            var read = new WorldWireReader();
            read.Reset(MultiplayerPresentation.Copy(actual));
            Assert(read.TryReadHeader(out var kind) && kind == 34 && UiNodeWireCodec.Text(read) == "" &&
                UiNodeWireCodec.Text(read) == "recipient", "The targeted event carries one exact recipient.");
            var decoded = UiWireCodec.ReadStinger(read, out var origin, out var decodedScope, out var decodedFontFit);
            Assert(decoded.Title == request.Title && decoded.Subtitle == request.Subtitle && decoded.Glyph == request.Glyph &&
                decoded.Role == request.Role && decoded.Style?.Id == "wave-clear" && decoded.DurationSeconds == 4.25d &&
                decodedScope == scopeToken && decodedFontFit == fontFit && origin.OwnerPackageId == identity.OwnerPackageId && origin.ThemeId == "survival" &&
                origin.PackageDigest.Span.SequenceEqual(package) && origin.DescriptorDigest.Span.SequenceEqual(theme),
                "The complete event round-trips through the strict reader.");
        }

        private static void BadFieldsAndTrailingBytesAreRefused()
        {
            Assert(Rejects(Broken(glyph: 2)) && Rejects(Broken(role: 9)) && Rejects(Broken(duration: double.NaN)) &&
                Rejects(Broken(duration: 6.01)) && Rejects(Broken(trailing: true)) && Rejects(Broken(style: "topiaforge.invalid")) &&
                Rejects(Broken(title: new string('x', 41))) && Rejects(Broken(theme: "missing")) && Rejects(Broken(scopeToken: 0)),
                "Invalid presence flags, roles, durations, strings, identity combinations and trailing bytes are rejected.");
            Assert(Rejects(Broken(fontFit: 2)) && Rejects(Broken(fontFit: 255)), "Unknown stinger font-fit values are refused.");
            var regular = Broken();
            Assert(Rejects(regular.AsSpan(0, regular.Length - 1).ToArray()), "The event requires its complete finite duration.");
        }

        private static void RetirementClosesOnlyAnExactNonzeroToken()
        {
            var digest = new byte[32];
            Array.Fill(digest, (byte)0x11);
            const string owner = "io.example.stinger";
            const ulong token = 257;
            var actual = new WorldWireWriter(2304);
            actual.Begin(UiStingerDelivery.RetireKind); actual.WriteString(""); actual.WriteString("recipient");
            UiWireCodec.WriteStingerRetirement(actual, owner, digest, token);
            var expected = new WorldWireWriter(2304);
            expected.Begin(35); expected.WriteString(""); expected.WriteString("recipient");
            expected.WriteString(owner);
            for (var index = 0; index < 4; index++) expected.WriteUInt64(0x1111111111111111UL);
            expected.WriteVarUInt(token);
            Assert(actual.Segment.AsSpan().SequenceEqual(expected.Segment.AsSpan()),
                "Event 35 contains only the exact audience, package and scope identity.");

            var reader = new WorldWireReader();
            reader.Reset(MultiplayerPresentation.Copy(actual));
            Assert(reader.TryReadHeader(out var kind) && kind == 35 && UiNodeWireCodec.Text(reader) == "" &&
                UiNodeWireCodec.Text(reader) == "recipient", "A closure carries one exact destination.");
            Span<byte> received = stackalloc byte[32];
            UiWireCodec.ReadStingerRetirement(reader, received, out var package, out var retired);
            Assert(package == owner && received.SequenceEqual(digest) && retired == token, "The closure identifies the exact scope.");
            Assert(RetirementRejects(new byte[] { 0 }) && RetirementRejects(new byte[] { 0x81, 0 }) &&
                RetirementRejects(new byte[] { 0x80 }) && RetirementRejects(new byte[] { 1, 0 }),
                "Zero, noncanonical, incomplete and trailing retirement tokens are refused.");

            bool RetirementRejects(byte[] suffix)
            {
                var writer = new WorldWireWriter(2304);
                writer.WriteString(owner);
                for (var index = 0; index < 4; index++) writer.WriteUInt64(0x1111111111111111UL);
                foreach (var item in suffix) writer.WriteByte(item);
                reader.Reset(MultiplayerPresentation.Copy(writer));
                Span<byte> checkedDigest = stackalloc byte[32];
                try { UiWireCodec.ReadStingerRetirement(reader, checkedDigest, out _, out _); return false; }
                catch (ArgumentException) { return true; }
            }
        }

        private static byte[] Broken(byte glyph = 0, byte role = 4, double duration = 2.5, bool trailing = false,
            string style = "", string title = "Ready", string theme = "", ulong scopeToken = 1, byte fontFit = 0)
        {
            var writer = new WorldWireWriter(2304);
            writer.WriteString("io.example.stinger");
            for (var index = 0; index < 4; index++) writer.WriteUInt64(1);
            writer.WriteString(theme);
            for (var index = 0; index < 4; index++) writer.WriteUInt64(0);
            writer.WriteVarUInt(scopeToken);
            writer.WriteByte(fontFit);
            writer.WriteString(title); writer.WriteString("");
            writer.WriteByte(glyph); writer.WriteByte(role); writer.WriteString(style);
            writer.WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(duration)));
            if (trailing) writer.WriteByte(0);
            return MultiplayerPresentation.Copy(writer);
        }

        private static bool Rejects(byte[] data)
        {
            var reader = new WorldWireReader();
            reader.Reset(data);
            try { _ = UiWireCodec.ReadStinger(reader, out _, out _, out _); return false; }
            catch (ArgumentException) { return true; }
        }
    }
}
