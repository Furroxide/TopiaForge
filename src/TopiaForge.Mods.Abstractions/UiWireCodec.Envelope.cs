using System;
using System.Buffers.Binary;
using System.Text;

namespace TopiaForge.Mods
{
    /// <summary>The originating package and presentation generation of one wire root.</summary>
    internal sealed class UiWireEnvelope
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal UiWireEnvelope(
            string ownerPackageId, ReadOnlySpan<byte> packageDigest, ulong generation, string? themeId,
            ReadOnlySpan<byte> descriptorDigest)
        {
            ValidateIdentity(ownerPackageId, packageDigest, themeId, descriptorDigest);
            if (generation == 0) throw new ArgumentException("The remote UI envelope requires a nonzero generation.", nameof(generation));

            OwnerPackageId = ownerPackageId;
            PackageDigest = packageDigest.ToArray();
            Generation = generation;
            ThemeId = themeId;
            DescriptorDigest = descriptorDigest.ToArray();
        }

        internal static void ValidateIdentity(string ownerPackageId, ReadOnlySpan<byte> packageDigest, string? themeId,
            ReadOnlySpan<byte> descriptorDigest)
        {
            if (string.IsNullOrWhiteSpace(ownerPackageId) || Utf8.GetByteCount(ownerPackageId) > 128)
                throw new ArgumentException("The owning package id must fit in 128 UTF-8 bytes.", nameof(ownerPackageId));
            if (packageDigest.Length != 32 || descriptorDigest.Length != 32)
                throw new ArgumentException("The remote UI envelope requires two SHA-256 digests.");
            if (themeId != null && !UiThemeIds.IsAuthorId(themeId))
                throw new ArgumentException("The remote UI theme id is invalid.", nameof(themeId));

            var hasDescriptor = false;
            foreach (var value in descriptorDigest)
                hasDescriptor |= value != 0;
            if (hasDescriptor != (themeId != null))
                throw new ArgumentException("A descriptor digest is required exactly when the envelope names a theme.");
        }

        internal string OwnerPackageId { get; }
        internal ReadOnlyMemory<byte> PackageDigest { get; }
        internal ulong Generation { get; }
        internal string? ThemeId { get; }
        internal ReadOnlyMemory<byte> DescriptorDigest { get; }
    }

    internal static partial class UiWireCodec
    {
        private const byte EnvelopeTag = 14;
        private const byte EnhancedTextTag = 15;
        private const byte EnhancedProgressTag = 33;
        private const byte EnhancedCountdownTag = 34;

        internal const int LegacyContractVersion = 1;
        internal const int FoundationContractVersion = 2;
        internal const int ControlsContractVersion = 3;

        internal static bool NeedsEnvelope(UiNode? node) => RequiredContractVersion(node) > LegacyContractVersion;

        internal static int RequiredContractVersion(UiNode? node)
        {
            UiComposition.Validate(node);
            return ContractVersionOf(node);
        }

        private static int ContractVersionOf(UiNode? node)
        {
            if (node == null) return LegacyContractVersion;
            if (ControlTag(node) != 0 || ActionTag(node) != 0 || StatusTag(node) != 0) return ControlsContractVersion;

            var foundation = node switch
            {
                UiText text => EnhancedText(text.Style, text.Tone, text.Id),
                UiProgress progress => EnhancedTone(progress.Tone),
                UiCountdown countdown => EnhancedTone(countdown.Tone),
                UiIcon or UiImage or UiBadge or UiPips => true,
                _ => IsEnhancedLayout(node),
            };

            var required = foundation ? FoundationContractVersion : LegacyContractVersion;
            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++)
            {
                required = Math.Max(required, ContractVersionOf(children[index]));
                if (required == ControlsContractVersion) break;
            }

            return required;
        }

        private static bool EnhancedText(UiTextStyle style, UiTone tone, string? id) =>
            id != null || style > UiTextStyle.Caption || EnhancedTone(tone);

        private static bool EnhancedTone(UiTone tone) => tone > UiTone.Danger;

        private static void WriteEnvelope(IUiWireWriter writer, UiWireEnvelope envelope)
        {
            writer.WriteByte(EnvelopeTag);
            writer.WriteByte(1);
            writer.WriteString(envelope.OwnerPackageId);
            WriteDigest(writer, envelope.PackageDigest.Span);
            writer.WriteVarUInt(envelope.Generation);
            writer.WriteString(envelope.ThemeId ?? string.Empty);
            WriteDigest(writer, envelope.DescriptorDigest.Span);
        }

        private static UiWireEnvelope ReadEnvelope(IUiWireReader reader)
        {
            if (Byte(reader) != 1 || !reader.TryReadString(128, out var ownerPackageId)) throw Bad();
            Span<byte> packageDigest = stackalloc byte[32];
            ReadDigest(reader, packageDigest);
            var generation = Id(reader);
            if (!reader.TryReadString(64, out var themeId)) throw Bad();
            Span<byte> descriptorDigest = stackalloc byte[32];
            ReadDigest(reader, descriptorDigest);
            return new UiWireEnvelope(ownerPackageId, packageDigest, generation,
                themeId.Length == 0 ? null : themeId, descriptorDigest);
        }

        private static void WriteDigest(IUiWireWriter writer, ReadOnlySpan<byte> digest)
        {
            for (var offset = 0; offset < 32; offset += 8)
                writer.WriteUInt64(BinaryPrimitives.ReadUInt64LittleEndian(digest.Slice(offset, 8)));
        }

        private static void ReadDigest(IUiWireReader reader, Span<byte> digest)
        {
            for (var offset = 0; offset < 32; offset += 8)
            {
                if (!reader.TryReadUInt64(out var bits)) throw Bad();
                BinaryPrimitives.WriteUInt64LittleEndian(digest.Slice(offset, 8), bits);
            }
        }
    }
}
