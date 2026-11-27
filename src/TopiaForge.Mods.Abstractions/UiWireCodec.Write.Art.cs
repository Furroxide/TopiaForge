namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        private const byte IconTag = 27;
        private const byte ImageTag = 28;
        private const byte BadgeTag = 29;
        private const byte PipsTag = 30;

        private static void WriteIcon(IUiWireWriter writer, UiIcon icon)
        {
            LayoutHeader(writer, IconTag, icon);
            writer.WriteByte((byte)icon.Glyph);
            Text(writer, icon.SemanticLabel);
            writer.WriteSingle(icon.Size);
            writer.WriteByte((byte)icon.Tone);
            Flag(writer, icon.Decorative);
        }

        private static void WriteImage(IUiWireWriter writer, UiImage image)
        {
            LayoutHeader(writer, ImageTag, image);
            writer.WriteString(image.Image.PackId);
            WriteDigest(writer, image.PackDescriptorDigest.Span);
            writer.WriteString(image.Image.ImageId);
            Text(writer, image.SemanticLabel);
            writer.WriteSingle(image.Width!.Value);
            writer.WriteSingle(image.Height!.Value);
            writer.WriteByte((byte)image.Fit);
            Flag(writer, image.Tint.HasValue);
            if (image.Tint is UiTone tint) writer.WriteByte((byte)tint);
            Flag(writer, image.Decorative);
        }

        private static void WriteBadge(IUiWireWriter writer, UiBadge badge)
        {
            LayoutHeader(writer, BadgeTag, badge);
            Text(writer, badge.Text);
            writer.WriteByte((byte)badge.Role);
            writer.WriteByte((byte)badge.Presentation);
            Flag(writer, badge.Icon.HasValue);
            if (badge.Icon is UiGlyph icon) writer.WriteByte((byte)icon);
            writer.WriteString(badge.Style?.Id ?? string.Empty);
        }

        private static void WritePips(IUiWireWriter writer, UiPips pips) =>
            WritePips(writer, pips, pips.Filled, pips.Fraction);

        private static void WritePips(IUiWireWriter writer, UiPips pips, int filled, float fraction)
        {
            LayoutHeader(writer, PipsTag, pips);
            writer.WriteByte((byte)pips.Total);
            writer.WriteByte((byte)filled);
            Text(writer, pips.Label);
            writer.WriteSingle(fraction);
            writer.WriteByte((byte)pips.Presentation);
            writer.WriteByte((byte)pips.Role);
            writer.WriteString(pips.Style?.Id ?? string.Empty);
        }
    }
}
