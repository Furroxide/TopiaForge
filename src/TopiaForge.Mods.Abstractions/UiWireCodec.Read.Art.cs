using System;

namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        private sealed partial class Decoder
        {
            private UiNode ArtNode(byte type)
            {
                var id = LayoutId();
                return type switch
                {
                    IconTag => new UiIcon(Glyph(), Text(r, 256), Number(r), Tone(), Present(), id),
                    ImageTag => ReadImage(id),
                    BadgeTag => ReadBadge(id),
                    PipsTag => ReadPips(id),
                    _ => throw Bad(),
                };
            }

            private UiImage ReadImage(string? id)
            {
                var packId = AssetId();
                Span<byte> descriptorDigest = stackalloc byte[32];
                ReadDigest(r, descriptorDigest);
                var reference = new UiImageRef(packId, AssetId());
                var label = Text(r, 256);
                var width = Number(r);
                var height = Number(r);
                var fit = (UiImageFit)Byte(r, (byte)UiImageFit.Cover);
                var tint = Present() ? (UiTone?)Tone() : null;
                return new UiImage(reference, label, width, height, fit, tint, Present(), id, descriptorDigest);
            }

            private UiBadge ReadBadge(string? id)
            {
                var text = Text(r, 256);
                var role = PaintRole();
                var presentation = (UiBadgeStyle)Byte(r, (byte)UiBadgeStyle.Outlined);
                var glyph = Present() ? (UiGlyph?)Glyph() : null;
                return new UiBadge(text, role, presentation, glyph, StyleId(), id);
            }

            private UiPips ReadPips(string? id)
            {
                var total = Byte(r, 32);
                var filled = Byte(r, total);
                var label = Text(r, 256);
                var fraction = Number(r);
                var presentation = (UiPipStyle)Byte(r, (byte)UiPipStyle.Segments);
                return new UiPips(total, filled, label, fraction, presentation, PaintRole(), StyleId(), id);
            }

            private string AssetId()
            {
                if (!r.TryReadString(64, out var id)) throw Bad();
                return id;
            }

            private UiGlyph Glyph() => (UiGlyph)Byte(r, (byte)UiGlyph.Play);
            private UiTone Tone() => (UiTone)Byte(r, (byte)UiTone.Accent);
            private UiPaintRole PaintRole() => (UiPaintRole)Byte(r, (byte)UiPaintRole.Danger);
        }
    }
}
