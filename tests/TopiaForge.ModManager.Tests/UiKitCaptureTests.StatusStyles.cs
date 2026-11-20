using System;
using System.Linq;
using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private const string RequestedStatusStyles = """
            {"schema":1,"id":"status","styles":[
              {"id":"warning.outline","target":"badge","radius":"small","fill":"warning"},
              {"id":"power.track","target":"pips","radius":"small","fill":"accent"}
            ]}
            """;

        private static void BadgeAndPipCapturesRetainAuthoredStyle()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            Check(ui.AddThemeFixture(RequestedStatusStyles).Succeeded, "Declare matching explicit Badge and Pips fills.");
            var content = new UiColumn(
                new UiBadge("Water low", UiPaintRole.Warning, UiBadgeStyle.Outlined,
                    style: new UiStyleRef("warning.outline"), id: "water"),
                new UiPips(5, 3, "Power", role: UiPaintRole.Accent, style: new UiStyleRef("power.track"), id: "power"),
                new UiBadge("Ready", UiPaintRole.Success, id: "unstyled"),
                new UiText("Station available", id: "ordinary"));
            using var surface = Make(ui, new UiSurfaceRequest("status-capture", "Status capture", "", content: content)
                .WithTheme(new UiThemeRef("status")));

            var fallback = surface.Capture(Viewport);
            CheckStatusCapture(fallback, resolved: false);
            ui.CompletePendingThemeRegistrations();
            var resolved = surface.Capture(Viewport);
            CheckStatusCapture(resolved, resolved: true);
            Check(fallback.Nodes.Where(IsStatus).All(node => node.StyleId == null),
                "Settling the pack does not rewrite an older immutable status capture.");

            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast: true, uiScale: 2f)).Succeeded,
                "Use high contrast at double scale.");
            CheckStatusCapture(surface.Capture(Viewport), resolved: true);
            for (var index = 0; index < 64; index++) surface.SetContent(content);
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 4096; index++)
                Check(surface.SetContent(content).Succeeded, "The retained status content is valid.");
            Check(GC.GetAllocatedBytesForCurrentThread() == start,
                "Status capture metadata adds no allocation to settled same-tree updates.");

            static bool IsStatus(UiCapturedNode row) => row.Kind is nameof(UiBadge) or nameof(UiPips);

            static void CheckStatusCapture(UiTreeCapture capture, bool resolved)
            {
                Check(Row(capture, "water") is
                { RequestedStyleId: "warning.outline", RequestedPaintRole: UiPaintRole.Warning, EffectivePaintRole: UiPaintRole.Warning } badge &&
                    badge.StyleId == (resolved ? "warning.outline" : null) && badge.UsedFallback != resolved,
                    "The badge retains the requested style and role, with an effective style only when resolved.");
                var pips = Row(capture, "power");
                Check(pips is
                { RequestedStyleId: "power.track", RequestedPaintRole: UiPaintRole.Accent, EffectivePaintRole: UiPaintRole.Accent } &&
                    pips.StyleId == (resolved ? "power.track" : null) && pips.UsedFallback != resolved,
                    "The pips retain the requested style and role, with an effective style only when resolved.");
                Check(Row(capture, "unstyled") is
                { RequestedStyleId: null, StyleId: null, RequestedPaintRole: UiPaintRole.Success, EffectivePaintRole: UiPaintRole.Success },
                    "An unstyled badge still reports its role in both role fields.");
                Check(capture.Nodes.Where(node => !IsStatus(node)).All(node =>
                    node.RequestedStyleId == null && node.RequestedPaintRole == null && node.EffectivePaintRole == null),
                    "Other groups, including status paint children, omit status request facts.");

                var serialized = JsonSerializer.SerializeToElement(GoldenNode(pips), CaptureJson);
                Check(serialized.GetProperty("requestedStyleId").GetString() == "power.track" &&
                    serialized.GetProperty("requestedPaintRole").GetString() == "accent" &&
                    serialized.GetProperty("effectivePaintRole").GetString() == "accent",
                    "The headless manifest hashes canonical lowercase status roles.");
                var text = JsonSerializer.SerializeToElement(GoldenNode(Row(capture, "ordinary")), CaptureJson);
                Check(!text.TryGetProperty("requestedPaintRole", out _) && !text.TryGetProperty("effectivePaintRole", out _) &&
                    !text.TryGetProperty("requestedStyleId", out _), "The headless hash format keeps ordinary rows unchanged.");
            }
        }
    }
}
