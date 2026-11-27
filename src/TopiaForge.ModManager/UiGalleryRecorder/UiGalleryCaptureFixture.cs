using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager
{
    internal static partial class UiGalleryCaptureFixture
    {
        internal const string Owner = "io.github.furroxide.topiaforge.uigallery";
        internal const string RemoteFixtureOwner = "dev.topiaforge.multiplayer-acceptance";
        private const string TransportBundleSha256 = "990599d76e9f7a00b08879dc413fafdeedc37f00fc7240b80d02c39bd67f32ae";
        private static readonly FoundationPage[] Pages = (FoundationPage[])Enum.GetValues(typeof(FoundationPage));
        private static readonly Lazy<UiThemeDescriptor> Transport = new Lazy<UiThemeDescriptor>(LoadTransport);
        private static readonly ControlGalleryPage[] ControlPages = (ControlGalleryPage[])Enum.GetValues(typeof(ControlGalleryPage));

        internal static bool MatchesOwner(string owner, bool remote, string? authenticatedOwner) =>
            remote ? owner == UiPresentationTheme.ReceiverOwner && authenticatedOwner == RemoteFixtureOwner : owner == Owner || owner == RemoteFixtureOwner;

        internal static string? SafeDisplayText(string? nativeText, string authored, bool uppercase)
        {
            if (nativeText == authored) return authored;
            if (!uppercase) return null;
            var upper = authored.ToUpperInvariant();
            return nativeText == upper ? upper : null;
        }

        internal static string? SafeAuthoredFieldValue(string? nativeValue, string? nativeLabel, string authored)
        {
            if (authored.Length == 0 || nativeValue != authored) return null;
            if (nativeLabel == authored) return authored;
            // TMP may append a zero-width caret sentinel to the rendered text component.
            return nativeLabel != null && nativeLabel.Length == authored.Length + 1 &&
                nativeLabel[nativeLabel.Length - 1] == '\u200B' && nativeLabel.StartsWith(authored, StringComparison.Ordinal)
                ? authored : null;
        }

        internal static UiSurfaceRequest? Match(string owner, bool remote, UiWireEnvelope? envelope, UiSurfaceRequest request,
            UiThemeDescriptor? declared = null)
        {
            if (remote != (envelope != null) || !MatchesOwner(owner, remote, envelope?.OwnerPackageId)) return null;
            if (request.Kind != UiSurfaceKind.Window && request.Kind != UiSurfaceKind.Hud) return null;
            if (remote && request.Theme != null) return null;
            var transport = remote || owner == RemoteFixtureOwner;
            var requestedThemeId = remote ? envelope!.ThemeId : request.Theme?.Id ?? declared?.Id;
            UiThemeRef? theme = null;
            if (requestedThemeId == FoundationPages.SurvivalTheme.Id)
            {
                if ((transport && !UiGallerySurvivalTheme.Matches(declared)) ||
                    (remote && !UiGallerySurvivalTheme.MatchesDigest(envelope!.DescriptorDigest.Span))) return null;
                theme = FoundationPages.SurvivalTheme;
            }
            else if (transport && requestedThemeId == FoundationPages.TransportTheme.Id)
            {
                var canonical = Transport.Value;
                if (remote ? !envelope!.DescriptorDigest.Span.SequenceEqual(canonical.Digest.Span)
                    : declared == null || !declared.Digest.Span.SequenceEqual(canonical.Digest.Span)) return null;
                theme = FoundationPages.TransportTheme;
            }
            else if (requestedThemeId != null) return null;
            if (remote && !IsTransportId(request.Id, "remote-") &&
                (request.Kind != UiSurfaceKind.Hud || !IsTransportId(request.Id, "shared-"))) return null;

            if (request.Kind == UiSurfaceKind.Hud)
            {
                var notice = FoundationLayoutPages.LayoutNotice(theme);
                if (Matches(notice)) return notice;
            }

            foreach (var page in Pages)
            {
                if (transport ? !FoundationPages.CanPublish(page) :
                    (page is FoundationPage.InteractionStates or FoundationPage.GraphConnections) && request.Kind == UiSurfaceKind.Hud)
                    continue;
                var expected = transport ? FoundationPages.CreateTransportRequest(page, request.Kind, theme)
                    : FoundationPages.CreateRequest(page, request.Kind, theme);
                if (Matches(expected)) return expected;
            }
            if (request.Kind == UiSurfaceKind.Window)
            {
                foreach (var page in ControlPages)
                {
                    var expected = ControlGalleryPages.Create(page, theme);
                    if (Matches(expected)) return expected;
                }
            }
            return MatchStatus(request, theme, remote) ?? MatchActions(request, theme, remote, transport);

            bool Matches(UiSurfaceRequest expected) => (remote || expected.Id == request.Id) &&
                request.Title == expected.Title && request.Body == expected.Body && request.Width == expected.Width &&
                request.Height == expected.Height && request.HudAnchor == expected.HudAnchor &&
                request.HudVisibility == expected.HudVisibility && SameNode(request.Content,
                    remote ? NormalizeTransportContent(expected.Content) : expected.Content);
        }

        private static UiNode? NormalizeTransportContent(UiNode? content) => UiWireCodec.ProjectInputDefaults(
            NormalizeTransportArtwork(content), ActionGalleryPages.DefaultBindings);

        private static bool IsTransportId(string id, string prefix) => id.StartsWith(prefix, StringComparison.Ordinal) &&
            id.Length > prefix.Length && id[prefix.Length] != '0' &&
            ulong.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);

        private static bool SameNode(UiNode? actual, UiNode? expected)
        {
            if (actual == null || expected == null || actual.GetType() != expected.GetType() || actual.Id != expected.Id) return false;
            return (actual, expected) switch
            {
                (UiText left, UiText right) => left.Text == right.Text && left.Style == right.Style && left.Tone == right.Tone,
                (UiProgress left, UiProgress right) => left.Label == right.Label && left.Fraction == right.Fraction &&
                    left.Tone == right.Tone && SameThresholds(left.Thresholds, right.Thresholds),
                (UiStatBar left, UiStatBar right) => left.Label == right.Label && left.Value == right.Value &&
                    left.Maximum == right.Maximum && left.Format == right.Format && left.Tone == right.Tone &&
                    SameThresholds(left.Thresholds, right.Thresholds),
                (UiCountdown left, UiCountdown right) => left.Label == right.Label && left.EndsAt == right.EndsAt &&
                    left.Format == right.Format && left.Tone == right.Tone && SameThresholds(left.Thresholds, right.Thresholds),
                (UiRow left, UiRow right) => SameFlex(left.Layout, right.Layout) && SameEntries(left.Children, right.Children, SameNode),
                (UiColumn left, UiColumn right) => SameFlex(left.Layout, right.Layout) && SameEntries(left.Children, right.Children, SameNode),
                (UiWrap left, UiWrap right) => left.Spacing == right.Spacing && left.RunSpacing == right.RunSpacing &&
                    left.Padding.Equals(right.Padding) && left.Alignment == right.Alignment &&
                    SameEntries(left.Children, right.Children, SameNode),
                (UiGrid left, UiGrid right) => left.MinimumCellWidth == right.MinimumCellWidth &&
                    left.MaximumColumns == right.MaximumColumns && left.Spacing == right.Spacing &&
                    left.RunSpacing == right.RunSpacing && left.Padding.Equals(right.Padding) &&
                    SameEntries(left.Children, right.Children, SameNode),
                (UiStack left, UiStack right) => left.Width == right.Width && left.Height == right.Height &&
                    SameEntries(left.Children, right.Children, SameNode),
                (UiPadding left, UiPadding right) => left.Padding.Equals(right.Padding) && SameNode(left.Child, right.Child),
                (UiSizedBox left, UiSizedBox right) => left.Width == right.Width && left.Height == right.Height &&
                    SameOptionalNode(left.Child, right.Child),
                (UiExpanded left, UiExpanded right) => left.Flex == right.Flex && SameNode(left.Child, right.Child),
                (UiSpacer left, UiSpacer right) => left.Flex == right.Flex,
                (UiPositioned left, UiPositioned right) => left.Left == right.Left && left.Top == right.Top &&
                    left.Width == right.Width && left.Height == right.Height && SameNode(left.Child, right.Child),
                (UiCard left, UiCard right) => left.Selected == right.Selected && left.Disabled == right.Disabled &&
                    left.Style?.Id == right.Style?.Id && Equals(left.Padding, right.Padding) &&
                    SameOptionalNode(left.Header, right.Header) && SameNode(left.Child, right.Child),
                (UiBox left, UiBox right) => left.Width == right.Width && left.Height == right.Height &&
                    left.Fill == right.Fill && left.Style?.Id == right.Style?.Id,
                (UiDivider left, UiDivider right) => left.LineStyle == right.LineStyle &&
                    left.Before == right.Before && left.After == right.After,
                (UiIcon left, UiIcon right) => left.Glyph == right.Glyph && left.Size == right.Size && left.Tone == right.Tone &&
                    left.Decorative == right.Decorative && left.SemanticLabel == right.SemanticLabel,
                (UiImage left, UiImage right) => SameImage(left, right),
                (UiBadge left, UiBadge right) => left.Text == right.Text && left.Role == right.Role &&
                    left.Presentation == right.Presentation && left.Icon == right.Icon && left.Style?.Id == right.Style?.Id,
                (UiPips left, UiPips right) => left.Total == right.Total && left.Filled == right.Filled &&
                    left.Fraction == right.Fraction && left.Label == right.Label && left.Presentation == right.Presentation &&
                    left.Role == right.Role && left.Style?.Id == right.Style?.Id && SameCooldown(left.Refill, right.Refill),
                (UiKeyGlyph left, UiKeyGlyph right) => left.ActionName == right.ActionName && left.HasWireDefaults == right.HasWireDefaults &&
                    SameBindingHints(left.WireDefaults, right.WireDefaults),
                (UiActionSlot left, UiActionSlot right) => SameActionSlot(left, right),
                (UiScroll left, UiScroll right) => left.Height == right.Height && SameNode(left.Content, right.Content),
                (UiSplitPane left, UiSplitPane right) => left.Orientation == right.Orientation &&
                    left.PrimaryFraction == right.PrimaryFraction && SameNode(left.Primary, right.Primary) &&
                    SameNode(left.Secondary, right.Secondary),
                (UiButton left, UiButton right) => left.Label == right.Label && left.Style == right.Style &&
                    left.Enabled == right.Enabled,
                (UiToggle left, UiToggle right) => left.Label == right.Label && left.Value == right.Value &&
                    left.Enabled == right.Enabled,
                (UiCheckbox left, UiCheckbox right) => left.Label == right.Label && left.Value == right.Value &&
                    left.Enabled == right.Enabled,
                (UiSlider left, UiSlider right) => left.Label == right.Label && left.Minimum == right.Minimum &&
                    left.Maximum == right.Maximum && left.Value == right.Value && left.Enabled == right.Enabled &&
                    left.Divisions == right.Divisions && left.Format.Kind == right.Format.Kind && left.Format.Decimals == right.Format.Decimals,
                (UiTextInput left, UiTextInput right) => left.Label == right.Label && left.Value == right.Value &&
                    left.Placeholder == right.Placeholder && left.MaximumLength == right.MaximumLength &&
                    left.Enabled == right.Enabled && left.Error == right.Error,
                (UiTabs left, UiTabs right) => left.Style == right.Style && left.Enabled == right.Enabled &&
                    left.SelectedTabId == right.SelectedTabId && SameEntries(left.Tabs, right.Tabs,
                        (first, second) => first.Id == second.Id && first.Label == second.Label && SameNode(first.Child, second.Child)),
                (UiTooltip left, UiTooltip right) => left.Message == right.Message && SameNode(left.Child, right.Child),
                (UiKeyBindingField left, UiKeyBindingField right) => SameBindingField(left, right),
                (UiDropdown left, UiDropdown right) => left.Label == right.Label && left.SelectedValue == right.SelectedValue &&
                    left.Enabled == right.Enabled && SameEntries(left.Choices, right.Choices,
                        (first, second) => first.Value == second.Value && first.Label == second.Label),
                (UiVirtualList left, UiVirtualList right) => left.VisibleRows == right.VisibleRows &&
                    left.SelectedItemId == right.SelectedItemId && left.Enabled == right.Enabled &&
                    SameEntries(left.Items, right.Items, (first, second) => first.Id == second.Id && first.Title == second.Title &&
                        first.Subtitle == second.Subtitle && first.Badge == second.Badge),
                (UiGraphCanvas left, UiGraphCanvas right) => SameEntries(left.Edges, right.Edges,
                    (first, second) => first.Id == second.Id && first.SourceNodeId == second.SourceNodeId &&
                        first.SourcePortId == second.SourcePortId && first.TargetNodeId == second.TargetNodeId &&
                        first.TargetPortId == second.TargetPortId && first.Tone == second.Tone) &&
                    left.Viewport.Equals(right.Viewport) && left.Height == right.Height && left.Enabled == right.Enabled &&
                    left.SelectedNodeId == right.SelectedNodeId && SameEntries(left.Nodes, right.Nodes, SameGraphNode),
                _ => false,
            };
        }

        private static bool SameOptionalNode(UiNode? actual, UiNode? expected) =>
            expected == null ? actual == null : SameNode(actual, expected);

        private static bool SameBindingField(UiKeyBindingField actual, UiKeyBindingField expected) =>
            actual.ActionName == expected.ActionName && actual.Label == expected.Label &&
            actual.Device == expected.Device && actual.Enabled == expected.Enabled &&
            actual.HasWireDefaults == expected.HasWireDefaults &&
            SameBindingHints(actual.WireDefaults, expected.WireDefaults);

        private static bool SameFlex(UiFlexLayout left, UiFlexLayout right) =>
            left.Spacing == right.Spacing && left.Padding.Equals(right.Padding) &&
            left.MainAxisAlignment == right.MainAxisAlignment && left.CrossAxisAlignment == right.CrossAxisAlignment;

        private static bool SameImage(UiImage actual, UiImage expected) => actual.Image.Equals(expected.Image) &&
            actual.SemanticLabel == expected.SemanticLabel && actual.Tint == expected.Tint && actual.Fit == expected.Fit &&
            actual.Decorative == expected.Decorative && actual.Width == expected.Width && actual.Height == expected.Height &&
            actual.PackDescriptorDigest.Span.SequenceEqual(expected.PackDescriptorDigest.Span);

        private static UiImage NormalizeTransportImage(UiImage image)
        {
            var descriptor = Transport.Value;
            var metadata = UiImageProjection.Find(descriptor, image.Image.ImageId)
                ?? throw new InvalidOperationException("The Gallery transport descriptor lacks its authored image.");
            var frame = UiImageProjection.ResolveFrame(image, metadata);
            return image.WithWireMetadata(frame.Width, frame.Height, descriptor.Digest.Span);
        }

        private static UiThemeDescriptor LoadTransport()
        {
            using var source = typeof(UiGalleryCaptureFixture).Assembly.GetManifestResourceStream("TopiaForge.UiGallery.TransportPreview.json")
                ?? throw new InvalidOperationException("The Gallery transport descriptor is missing from the capture assembly.");
            if (source.Length > UiThemeJson.MaximumBytes) throw new InvalidOperationException("The Gallery transport descriptor is too large.");
            using var bytes = new MemoryStream((int)source.Length);
            source.CopyTo(bytes);
            var hash = new byte[32];
            for (var index = 0; index < hash.Length; index++)
                hash[index] = (byte)((UiThemeData.HexDigit(TransportBundleSha256[index * 2]) << 4) |
                    UiThemeData.HexDigit(TransportBundleSha256[index * 2 + 1]));
            var parsed = UiThemePackParser.Parse(bytes.ToArray(), packaged: true, expectedId: FoundationPages.TransportTheme.Id,
                fileHash: path => path == FoundationArtPages.ArtBundle ? hash : null);
            return parsed.Value ?? throw new InvalidOperationException(parsed.ErrorMessage);
        }

        private static bool SameGraphNode(UiGraphNode first, UiGraphNode second) => first.Id == second.Id && first.Type == second.Type &&
            first.Title == second.Title && first.Subtitle == second.Subtitle &&
            first.Position.Equals(second.Position) && first.Tone == second.Tone && first.Enabled == second.Enabled &&
            SameEntries(first.Ports, second.Ports, (left, right) => left.Id == right.Id && left.Label == right.Label &&
                left.Direction == right.Direction && left.DataType == right.DataType && left.Required == right.Required);

        private static bool SameEntries<TFirst, TSecond>(IReadOnlyList<TFirst> first, IReadOnlyList<TSecond> second,
            Func<TFirst, TSecond, bool> equal)
        {
            if (first.Count != second.Count) return false;
            for (var index = 0; index < first.Count; index++)
            {
                if (!equal(first[index], second[index])) return false;
            }
            return true;
        }
    }
}
