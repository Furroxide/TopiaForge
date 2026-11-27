using System;

namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        private const byte KeyGlyphTag = 40;
        private const byte ActionSlotTag = 41;
        private const byte RefillingPipsTag = 42;
        private const int ActionEpochLimit = 256;

        private static byte ActionTag(UiNode? node) => node switch
        {
            UiKeyGlyph => KeyGlyphTag,
            UiActionSlot => ActionSlotTag,
            UiPips { Refill: not null } => RefillingPipsTag,
            _ => 0,
        };

        private static void WriteActionNode(IUiWireWriter writer, UiNode node, byte tag, string epoch,
            bool retained, ref bool timed)
        {
            if (node is UiPips { Refill: { } refill } stalePips && retained && Stale(refill, epoch))
            {
                WritePips(writer, stalePips, Math.Min(stalePips.Filled + 1, stalePips.Total), fraction: 0);
                return;
            }

            LayoutHeader(writer, tag, node);
            switch (node)
            {
                case UiKeyGlyph glyph:
                    Text(writer, glyph.ActionName);
                    WriteInputDefaults(writer, glyph.WireDefaults);
                    return;
                case UiActionSlot slot:
                    WriteActionSlot(writer, slot, epoch, retained);
                    // A tag 41 node always carries its epoch, even if both deadlines are absent.
                    timed = true;
                    return;
                case UiPips pips:
                    writer.WriteByte((byte)pips.Total);
                    writer.WriteByte((byte)pips.Filled);
                    Text(writer, pips.Label);
                    writer.WriteByte((byte)pips.Presentation);
                    writer.WriteByte((byte)pips.Role);
                    writer.WriteString(pips.Style?.Id ?? string.Empty);
                    WriteCooldown(writer, pips.Refill!);
                    WriteActionEpoch(writer, epoch);
                    timed = true;
                    return;
                default:
                    throw new ArgumentException("This UI action node cannot be sent to a player.");
            }
        }

        private static void WriteActionSlot(IUiWireWriter writer, UiActionSlot slot, string epoch, bool retained)
        {
            Text(writer, slot.ActionName);
            WriteInputDefaults(writer, slot.WireDefaults);
            Text(writer, slot.Label);
            Flag(writer, slot.Glyph.HasValue);
            if (slot.Glyph is UiGlyph glyph) writer.WriteByte((byte)glyph);
            Flag(writer, slot.Image != null);
            if (slot.Image is { } image)
            {
                writer.WriteString(image.PackId);
                WriteDigest(writer, slot.PackDescriptorDigest.Span);
                writer.WriteString(image.ImageId);
            }
            Text(writer, slot.Detail ?? string.Empty);
            Flag(writer, slot.Emphasis);
            WriteOptionalCooldown(writer, retained && Stale(slot.Cooldown, epoch) ? null : slot.Cooldown);
            Flag(writer, slot.Charges != null);
            if (slot.Charges is { } charges)
            {
                var arrived = retained && Stale(charges.Refill, epoch);
                var available = arrived ? Math.Min(charges.Available + 1, charges.Maximum) : charges.Available;
                writer.WriteByte((byte)available);
                writer.WriteByte((byte)charges.Maximum);
                WriteOptionalCooldown(writer, arrived ? null : charges.Refill);
            }
            Text(writer, slot.UnavailableReason ?? string.Empty);
            writer.WriteString(slot.Style?.Id ?? string.Empty);
            WriteActionEpoch(writer, epoch);
        }

        private static bool Stale(UiCooldown? cooldown, string epoch) =>
            cooldown?.ClockEpoch is { } admitted && !StringComparer.Ordinal.Equals(admitted, epoch);

        private static void WriteOptionalCooldown(IUiWireWriter writer, UiCooldown? cooldown)
        {
            Flag(writer, cooldown != null);
            if (cooldown != null) WriteCooldown(writer, cooldown);
        }

        private static void WriteCooldown(IUiWireWriter writer, UiCooldown cooldown)
        {
            WriteDouble(writer, cooldown.EndsAt);
            WriteDouble(writer, cooldown.DurationSeconds);
        }

        private static void WriteActionEpoch(IUiWireWriter writer, string epoch)
        {
            if (epoch.Length > ActionEpochLimit) throw new ArgumentException("The UI clock epoch exceeds 256 characters.");
            Text(writer, epoch);
        }

        private sealed partial class Decoder
        {
            private UiNode ActionNode(byte type)
            {
                var id = LayoutId();
                return type switch
                {
                    KeyGlyphTag => new UiKeyGlyph(Text(r, 64), id, ReadInputDefaults()),
                    ActionSlotTag => ReadActionSlot(id),
                    RefillingPipsTag => ReadRefillingPips(id),
                    _ => throw Bad(),
                };
            }

            private UiActionSlot ReadActionSlot(string? id)
            {
                var action = Text(r, 64);
                var defaults = ReadInputDefaults();
                var label = Text(r, 24);
                var glyph = Present() ? (UiGlyph?)Glyph() : null;
                UiImageRef? image = null;
                Span<byte> digest = stackalloc byte[32];
                if (Present())
                {
                    if (glyph.HasValue) throw Bad();
                    var pack = AssetId();
                    ReadDigest(r, digest);
                    image = new UiImageRef(pack, AssetId());
                }
                var detail = Text(r, 48);
                var emphasis = Present();
                var cooldown = OptionalCooldown();
                var charges = Present() ? new UiCharges(Byte(r, 32), Byte(r, 32), OptionalCooldown()) : null;
                var reason = Text(r, 48);
                var style = StyleId();
                ReadActionEpoch();
                var slot = new UiActionSlot(action, label, glyph, image, detail, emphasis, cooldown, charges, reason, style, id);
                return slot.WithWireMetadata(defaults, image == null ? ReadOnlySpan<byte>.Empty : digest);
            }

            private UiPips ReadRefillingPips(string? id)
            {
                var total = Byte(r, 32);
                var filled = Byte(r, total);
                var label = Text(r, 256);
                var presentation = (UiPipStyle)Byte(r, (byte)UiPipStyle.Segments);
                var role = PaintRole();
                var style = StyleId();
                var refill = Cooldown();
                ReadActionEpoch();
                return UiPips.WithRefill(total, filled, label, refill, presentation, role, style, id);
            }

            private UiCooldown? OptionalCooldown() => Present() ? Cooldown() : null;
            private UiCooldown Cooldown() => new UiCooldown(Double(), Double());

            private void ReadActionEpoch()
            {
                if (!StringComparer.Ordinal.Equals(Text(r, ActionEpochLimit), clockEpoch))
                    throw new ArgumentException("The UI action clock epoch is stale; request the current composition.");
            }
        }
    }
}
