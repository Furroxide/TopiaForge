using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager
{
    internal static partial class UiGalleryCaptureFixture
    {
        private static UiSurfaceRequest? MatchActions(UiSurfaceRequest request, UiThemeRef? theme, bool remote, bool transport)
        {
            var clock = 0d;
            var page = ActionGalleryPage.KeyGlyphs;
            if (request.Title == "Action slots")
            {
                if (request.Content is not UiColumn { Children.Count: 2 } column ||
                    column.Children[1] is not UiPips { Refill: { } refill }) return null;
                clock = refill.EndsAt - 8d;
                if (!double.IsFinite(clock) || clock < 0d || Math.Floor(clock) != clock) return null;
                page = ActionGalleryPage.Slots;
            }
            else if (request.Title != "Key glyphs") return null;

            var expected = ActionGalleryPages.CreateRequest(page, request.Kind, clock, theme, transport);
            return SameStatusSurface(request, expected, remote) &&
                SameNode(request.Content, remote ? NormalizeTransportContent(expected.Content) : expected.Content)
                ? StatusProjection(expected, request.Content!) : null;
        }

        private static UiNode? NormalizeTransportArtwork(UiNode? node) => UiTreeProjection.Rewrite(node, candidate =>
            candidate switch
            {
                UiImage image => NormalizeTransportImage(image),
                UiActionSlot { Image: { } image } slot when image.Equals(FoundationArtPages.TransportMark) =>
                    slot.WithWireImageDigest(Transport.Value.Digest.Span),
                _ => candidate,
            });

        private static bool SameActionSlot(UiActionSlot left, UiActionSlot right) =>
            left.ActionName == right.ActionName && left.Label == right.Label && left.Glyph == right.Glyph &&
            Equals(left.Image, right.Image) && left.Detail == right.Detail && left.Emphasis == right.Emphasis &&
            left.UnavailableReason == right.UnavailableReason && left.Style?.Id == right.Style?.Id &&
            SameCooldown(left.Cooldown, right.Cooldown) && SameCharges(left.Charges, right.Charges) &&
            left.PackDescriptorDigest.Span.SequenceEqual(right.PackDescriptorDigest.Span) &&
            left.HasWireDefaults == right.HasWireDefaults && SameBindingHints(left.WireDefaults, right.WireDefaults);

        private static bool SameCharges(UiCharges? left, UiCharges? right) =>
            left == null ? right == null : right != null && left.Available == right.Available &&
                left.Maximum == right.Maximum && SameCooldown(left.Refill, right.Refill);

        private static bool SameCooldown(UiCooldown? left, UiCooldown? right) =>
            left == null ? right == null : right != null && left.EndsAt == right.EndsAt && left.DurationSeconds == right.DurationSeconds;

        private static bool SameBindingHints(IReadOnlyList<UiInputBindingHint> actual, IReadOnlyList<UiInputBindingHint> expected) =>
            SameEntries(actual, expected, (left, right) => left.Kind == right.Kind && left.Control == right.Control);
    }
}
