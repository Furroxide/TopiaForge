using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager
{
    internal static class UiGalleryCaptureFixture
    {
        internal const string Owner = "io.github.furroxide.topiaforge.uigallery";
        internal const string RemoteFixtureOwner = "dev.topiaforge.multiplayer-acceptance";
        private static readonly FoundationPage[] Pages = (FoundationPage[])Enum.GetValues(typeof(FoundationPage));

        internal static bool MatchesOwner(string owner, bool remote, string? authenticatedOwner) =>
            remote ? authenticatedOwner == RemoteFixtureOwner : owner == Owner || owner == RemoteFixtureOwner;

        internal static string? SafeDisplayText(string? nativeText, string authored, bool uppercase)
        {
            if (nativeText == authored) return authored;
            if (!uppercase) return null;
            var upper = authored.ToUpperInvariant();
            return nativeText == upper ? upper : null;
        }

        internal static UiSurfaceRequest? Match(string owner, bool remote, string? authenticatedOwner, UiSurfaceRequest request,
            string? authenticatedThemeId = null)
        {
            if (!MatchesOwner(owner, remote, authenticatedOwner)) return null;
            if (request.Kind != UiSurfaceKind.Window && request.Kind != UiSurfaceKind.Hud) return null;
            var requestedThemeId = remote ? authenticatedThemeId : request.Theme?.Id;
            var allowedTheme = remote || owner == RemoteFixtureOwner ? FoundationPages.TransportTheme : FoundationPages.SurvivalTheme;
            if (requestedThemeId != null && requestedThemeId != allowedTheme.Id) return null;
            var theme = requestedThemeId == null ? null : allowedTheme;
            if (remote && !IsTransportId(request.Id, "remote-") &&
                (request.Kind != UiSurfaceKind.Hud || !IsTransportId(request.Id, "shared-"))) return null;

            foreach (var page in Pages)
            {
                if (page == FoundationPage.InteractionStates && (request.Kind == UiSurfaceKind.Hud || remote ||
                    owner == RemoteFixtureOwner)) continue;
                var expected = FoundationPages.CreateRequest(page, request.Kind, theme);
                if ((!remote && expected.Id != request.Id) || request.Title != expected.Title || request.Body != expected.Body ||
                    request.Width != expected.Width || request.Height != expected.Height || request.HudAnchor != expected.HudAnchor ||
                    request.HudVisibility != expected.HudVisibility || !SameNode(request.Content, expected.Content)) continue;
                return expected;
            }
            return null;
        }

        private static bool IsTransportId(string id, string prefix) => id.StartsWith(prefix, StringComparison.Ordinal) &&
            ulong.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var token) && token != 0;

        private static bool SameNode(UiNode? actual, UiNode? expected)
        {
            if (actual == null || expected == null || actual.GetType() != expected.GetType() || actual.Id != expected.Id) return false;
            return (actual, expected) switch
            {
                (UiText left, UiText right) => left.Text == right.Text && left.Style == right.Style && left.Tone == right.Tone,
                (UiLayoutNode left, UiLayoutNode right) => SameEntries(left.Children, right.Children, SameNode),
                (UiScroll left, UiScroll right) => left.Height == right.Height && SameNode(left.Content, right.Content),
                (UiSplitPane left, UiSplitPane right) => left.Orientation == right.Orientation &&
                    left.PrimaryFraction == right.PrimaryFraction && SameNode(left.Primary, right.Primary) &&
                    SameNode(left.Secondary, right.Secondary),
                (UiButton left, UiButton right) => left.Label == right.Label && left.Style == right.Style &&
                    left.Enabled == right.Enabled,
                (UiToggle left, UiToggle right) => left.Label == right.Label && left.Value == right.Value &&
                    left.Enabled == right.Enabled,
                (UiSlider left, UiSlider right) => left.Label == right.Label && left.Minimum == right.Minimum &&
                    left.Maximum == right.Maximum && left.Value == right.Value && left.Enabled == right.Enabled,
                (UiTextInput left, UiTextInput right) => left.Label == right.Label && left.Value == right.Value &&
                    left.Placeholder == right.Placeholder && left.MaximumLength == right.MaximumLength &&
                    left.Enabled == right.Enabled,
                (UiDropdown left, UiDropdown right) => left.Label == right.Label && left.SelectedValue == right.SelectedValue &&
                    left.Enabled == right.Enabled && SameEntries(left.Choices, right.Choices,
                        (first, second) => first.Value == second.Value && first.Label == second.Label),
                (UiVirtualList left, UiVirtualList right) => left.VisibleRows == right.VisibleRows &&
                    left.SelectedItemId == right.SelectedItemId && left.Enabled == right.Enabled &&
                    SameEntries(left.Items, right.Items, (first, second) => first.Id == second.Id && first.Title == second.Title &&
                        first.Subtitle == second.Subtitle && first.Badge == second.Badge),
                (UiGraphCanvas left, UiGraphCanvas right) => left.Edges.Count == 0 && right.Edges.Count == 0 &&
                    left.Viewport.Equals(right.Viewport) && left.Height == right.Height && left.Enabled == right.Enabled &&
                    left.SelectedNodeId == right.SelectedNodeId && SameEntries(left.Nodes, right.Nodes, SameGraphNode),
                _ => false,
            };
        }

        private static bool SameGraphNode(UiGraphNode first, UiGraphNode second) => first.Id == second.Id && first.Type == second.Type &&
            first.Title == second.Title && first.Subtitle == second.Subtitle && first.Ports.Count == 0 && second.Ports.Count == 0 &&
            first.Position.Equals(second.Position) && first.Tone == second.Tone && first.Enabled == second.Enabled;

        private static bool SameEntries<T>(IReadOnlyList<T> first, IReadOnlyList<T> second, Func<T, T, bool> equal)
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
