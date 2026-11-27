using System;

namespace TopiaForge.Mods
{
    /// <summary>Authenticated package and descriptor identity carried by a one-shot UI event.</summary>
    internal sealed class UiWireEventEnvelope
    {
        internal UiWireEventEnvelope(string ownerPackageId, ReadOnlySpan<byte> packageDigest, string? themeId,
            ReadOnlySpan<byte> descriptorDigest)
        {
            UiWireEnvelope.ValidateIdentity(ownerPackageId, packageDigest, themeId, descriptorDigest);
            OwnerPackageId = ownerPackageId;
            PackageDigest = packageDigest.ToArray();
            ThemeId = themeId;
            DescriptorDigest = descriptorDigest.ToArray();
        }

        internal string OwnerPackageId { get; }
        internal ReadOnlyMemory<byte> PackageDigest { get; }
        internal string? ThemeId { get; }
        internal ReadOnlyMemory<byte> DescriptorDigest { get; }
    }

    internal enum UiStingerFontFit : byte
    {
        KitOnly = 0,
        BothMeasured = 1,
    }

    internal static partial class UiWireCodec
    {
        internal static void WriteStinger(IUiWireWriter writer, UiWireEventEnvelope envelope, ulong scopeToken,
            UiStingerFontFit fontFit, UiStingerRequest request)
        {
            if (fontFit != UiStingerFontFit.KitOnly && fontFit != UiStingerFontFit.BothMeasured) throw Bad();
            writer.WriteString(envelope.OwnerPackageId);
            WriteDigest(writer, envelope.PackageDigest.Span);
            writer.WriteString(envelope.ThemeId ?? string.Empty);
            WriteDigest(writer, envelope.DescriptorDigest.Span);
            WriteStingerScopeToken(writer, scopeToken);
            writer.WriteByte((byte)fontFit);
            writer.WriteString(request.Title);
            writer.WriteString(request.Subtitle);
            Flag(writer, request.Glyph.HasValue);
            if (request.Glyph is UiGlyph glyph) writer.WriteByte((byte)glyph);
            writer.WriteByte((byte)request.Role);
            writer.WriteString(request.Style?.Id ?? string.Empty);
            WriteDouble(writer, request.DurationSeconds);
        }

        internal static UiStingerRequest ReadStinger(IUiWireReader reader, out UiWireEventEnvelope envelope,
            out ulong scopeToken, out UiStingerFontFit fontFit)
        {
            if (!reader.TryReadString(128, out var owner)) throw Bad();
            Span<byte> packageDigest = stackalloc byte[32];
            ReadDigest(reader, packageDigest);
            if (!reader.TryReadString(64, out var theme)) throw Bad();
            Span<byte> descriptorDigest = stackalloc byte[32];
            ReadDigest(reader, descriptorDigest);
            envelope = new UiWireEventEnvelope(owner, packageDigest, theme.Length == 0 ? null : theme, descriptorDigest);

            scopeToken = ReadStingerScopeToken(reader);
            fontFit = (UiStingerFontFit)Byte(reader, (byte)UiStingerFontFit.BothMeasured);
            var title = Text(reader, 40);
            var subtitle = Text(reader, 80);
            var glyph = Byte(reader, 1) != 0 ? (UiGlyph?)Byte(reader, (byte)UiGlyph.Play) : null;
            var role = (UiPaintRole)Byte(reader, (byte)UiPaintRole.Danger);
            if (!reader.TryReadString(64, out var styleId) || !reader.TryReadUInt64(out var bits)) throw Bad();
            End(reader);
            var style = styleId.Length == 0 ? null : new UiStyleRef(styleId);
            return new UiStingerRequest(title, subtitle, glyph, role, style, BitConverter.Int64BitsToDouble(unchecked((long)bits)));
        }

        internal static void WriteStingerRetirement(IUiWireWriter writer, string owner, ReadOnlySpan<byte> digest, ulong scopeToken)
        {
            writer.WriteString(owner);
            WriteDigest(writer, digest);
            WriteStingerScopeToken(writer, scopeToken);
        }

        internal static void ReadStingerRetirement(IUiWireReader reader, Span<byte> digest, out string owner, out ulong scopeToken)
        {
            if (!reader.TryReadString(128, out var package) || string.IsNullOrWhiteSpace(package)) throw Bad();
            owner = package;
            ReadDigest(reader, digest);
            scopeToken = ReadStingerScopeToken(reader);
            End(reader);
        }

        private static void WriteStingerScopeToken(IUiWireWriter writer, ulong scopeToken)
        {
            if (scopeToken == 0) throw Bad();
            writer.WriteVarUInt(scopeToken);
        }

        private static ulong ReadStingerScopeToken(IUiWireReader reader)
        {
            var scopeToken = Id(reader);
            if (scopeToken == 0) throw Bad();
            return scopeToken;
        }
    }
}
