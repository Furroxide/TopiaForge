using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void VirtualListRowsFitBeforeScrolling()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var ready = new UiListItem("ready", "Ready", "North", "Online");
            var original = new UiVirtualList("hosts", new[] { ready }, _ => { }, selectedItemId: ready.Id, visibleRows: 1);
            using var surface = Make(ui, new UiSurfaceRequest("hosts", "Hosts", "", width: 320f, height: 220f, content: original));
            var huge = new string('W', 200);
            var malformed = new[]
            {
                new UiListItem("unseen", huge, badge: "Offline"),
                new UiListItem("unseen", "Base", subtitle: huge, badge: "Offline"),
                new UiListItem("unseen", "Base", badge: huge),
                new UiListItem("unseen", "North\nWest", badge: "Offline"),
            };
            foreach (var candidate in malformed)
            {
                // Index 11 is well beyond the first row and any initial native pool overscan.
                var items = Enumerable.Range(0, 11).Select(index => new UiListItem("row-" + index, "Ready"))
                    .Append(candidate).ToArray();
                var list = new UiVirtualList("hosts", items, _ => { }, visibleRows: 1);
                var result = surface.SetContent(list);
                Check(result.ErrorCode == ModErrorCode.InvalidArgument && result.ErrorMessage.Contains("index 11", StringComparison.Ordinal),
                    "An unfit title, subtitle or badge on a hidden row rejects the complete virtual list.");
                Check(ReferenceEquals(surface.Content, original) &&
                    surface.Capture(Viewport).Nodes.Single(row => row.Kind == "ListItem").Id == ready.Id,
                    "Failed list admission keeps the existing content and selection.");
            }
        }

        private static void VirtualListBadgesCaptureTheirResolvedPaint()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            const string definition = """
                {"schema":1,"id":"list-paint","typography":{"label":{"case":"upper","trackingEm":0.07}},
                 "paper":{"colors":{"surface":{"fill":"#FFFFFF","on":"#000000"},"raised":{"fill":"#224455","on":"#FFFFFF"},
                    "sunken":{"fill":"#000000","on":"#FFFFFF"},"selected":{"fill":"#3E2525","on":"#FFFFFF"}}}}
                """;
            Check(ui.AddThemeFixture(definition).Succeeded && ui.Themes.UseTheme(new UiThemeRef("list-paint")).Succeeded,
                "Load the list's distinct Raised and Selected paint and authored Label typography.");
            ui.CompletePendingThemeRegistrations();
            var list = new UiVirtualList("hosts", new[]
            {
                new UiListItem("north", "North", "Sector 7", "Offline"),
                new UiListItem("south", "South")
            }, _ => { }, selectedItemId: "north", visibleRows: 1);
            using var surface = Make(ui, new UiSurfaceRequest("hosts", "Hosts", "", width: 420f, height: 220f, content: list));
            var pressed = CaptureForFixture(surface, Viewport, "Pressed", 0f, null);
            var title = pressed.Nodes.Single(row => row.Kind == "ListItem");
            var secondary = pressed.Nodes.Single(row => row.Kind == "ListItemSubtitle");
            var badge = pressed.Nodes.SingleOrDefault(row => row.Kind == "ListItemBadge");
            Check(badge is { Text: "OFFLINE", SemanticLabel: "Offline", FillColor: "#224455",
                    InkColor: "#FFFFFF", TextHeightPixels: 18f, IsRendered: true, Clipped: false },
                "The actual badge capture records its own Raised fill and readable authored Label paint.");
            var edge = pressed.Nodes.Single(row => row.Kind == "ListItemBadgeBoundary");
            var target = pressed.Nodes.Single(row => row.Kind == "HitTarget");
            Check(title.X > target.X && title.X + title.Width < secondary.X && secondary.X + secondary.Width < edge.X &&
                badge!.X > edge.X && badge.X + badge.Width < edge.X + edge.Width && edge.X + edge.Width < target.X + target.Width,
                "Title, trailing subtitle and padded badge reserve disjoint horizontal slots within the unchanged target.");
            Check(edge.FillColor == title.FillColor && edge.ContrastRatio >= 3f && edge.BorderWidthPixels == 2f &&
                pressed.CheckAccessibility().Count == 0, "The badge outline reads against the actual pressed row.");

            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast: true, uiScale: 2f)).Succeeded,
                "Apply double-scale high contrast while retaining the virtual list.");
            var focused = CaptureForFixture(surface, Viewport, "Focused", 0f, null);
            Check(focused.Nodes.Single(row => row.Kind == "ListItemBadge") is { TextHeightPixels: 36f, ContrastRatio: >= 7f } &&
                focused.Nodes.Single(row => row.Kind == "ListItemBadgeBoundary").BorderWidthPixels == 6f &&
                focused.CheckAccessibility().Count == 0, "The badge label and boundary retain their checked scaled paint.");
            Check(surface.SelectListItem("hosts", "south").Succeeded &&
                !surface.Capture(Viewport).Nodes.Any(row => row.Kind.StartsWith("ListItemBadge", StringComparison.Ordinal)),
                "Scrolling the pooled slot onto an unbadged row leaves no stale badge paint.");
        }
    }
}
