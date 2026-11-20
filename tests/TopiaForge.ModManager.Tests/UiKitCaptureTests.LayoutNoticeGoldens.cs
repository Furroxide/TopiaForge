using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void AddLayoutNoticeCases(IDictionary<string, GoldenCase> cases, (int Width, int Height)[] dimensions,
            string declaration, Func<string, string> readFixture)
        {
            foreach (var sample in new[] { false, true })
            foreach (var guest in new[] { false, true })
            {
                using var lifetime = new FakeModLifetime();
                using var players = guest ? new FakePlayerDirectory() : null;
                var recipient = players?.Add(new ParticipantId("gallery-guest"), "Sam");
                var ui = recipient == null ? new FakeUiService(lifetime) : players!.State(recipient.Id).Ui;
                if (sample)
                    Check(ui.AddThemeCatalogue(declaration, readFixture).Succeeded, "Load the Gallery's real notice sample scheme.");
                var request = FoundationLayoutPages.LayoutNotice(sample ? FoundationPages.SurvivalTheme : null);
                using var surface = recipient == null ? Make(ui, request) : Make(recipient.Ui.CreateSurface(request));
                ui.CompletePendingThemeRegistrations();

                foreach (var size in dimensions)
                foreach (var scale in new[] { 1, 2 })
                foreach (var high in new[] { false, true })
                foreach (var reduced in new[] { false, true })
                {
                    var viewport = new UiCaptureViewport(size.Width, size.Height);
                    var route = guest ? "guest-targeted" : "host-local";
                    var theme = sample ? "survival" : "kit";
                    var contrast = high ? "high" : "standard";
                    var motion = reduced ? "reduced" : "full";
                    var id = FormattableString.Invariant($"LayoutNotice/{theme}/hud/{contrast}/{size.Width}x{size.Height}/{scale}/{route}/{motion}/Rest");
                    Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(high, scale, reduced)).Succeeded,
                        "Apply the layout notice fixture's viewer preferences.");
                    if (scale == 1)
                    {
                        var screenScale = UiHudViewport.ScreenScale(size.Width, size.Height);
                        cases.Add(id, CaptureCase(id, surface, viewport, "Rest", null, screenScale));
                        continue;
                    }
                    var capture = surface.Capture(viewport);
                    var diagnostic = capture.Nodes.Single(node => node.Kind == "LayoutDiagnostic");
                    var message = capture.Nodes.Single(node => node.Kind == "LayoutFallback");
                    var issues = capture.CheckAccessibility();
                    Check(issues.Count == 1 && issues[0].Path == diagnostic.Path && issues[0].Kind == UiAccessibilityIssueKind.InvalidLayout &&
                        message.IsRendered && !message.Clipped && message.Text == UiHudLayoutNotice.Text &&
                        message.ContrastRatio >= (high ? 7f : 4.5f) && ReferenceEquals(surface.Content, request.Content),
                        id + " must show the readable notice and the one expected failure in the retained tree.");
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(capture.Nodes.Select(GoldenNode), CaptureJson);
                    cases.Add(id, new GoldenCase(1, capture.Nodes.Count(node => node.IsRendered), Convert.ToHexString(SHA256.HashData(bytes))));
                }
            }
        }
    }
}
