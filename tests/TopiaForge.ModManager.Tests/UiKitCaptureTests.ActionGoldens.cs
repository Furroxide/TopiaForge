using System;
using System.Collections.Generic;
using System.IO;
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
        private const string ActionGoldenPath = "tests/fixtures/ui-kit/k2-action-captures.json";

        internal static void WriteActionGoldenManifest(string path) => File.WriteAllText(path, CreateActionGoldenManifest());

        private static void VerifyActionGoldenManifest()
        {
            var expected = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), ActionGoldenPath));
            var actual = CreateActionGoldenManifest();
            if (expected == actual) return;
            using var baseline = JsonDocument.Parse(expected);
            using var candidate = JsonDocument.Parse(actual);
            var prior = baseline.RootElement.GetProperty("cases");
            foreach (var entry in candidate.RootElement.GetProperty("cases").EnumerateObject())
                Check(prior.TryGetProperty(entry.Name, out var old) && JsonElement.DeepEquals(old, entry.Value),
                    "Review the changed action capture " + entry.Name + ".");
            Check(false, "Action capture fixture metadata or matrix coverage changed.");
        }

        private static string CreateActionGoldenManifest()
        {
            var assets = new GalleryArtFixtures();
            var cases = new SortedDictionary<string, ActionGoldenCase>(StringComparer.Ordinal);
            foreach (var page in Enum.GetValues<ActionGalleryPage>())
                foreach (var themed in new[] { false, true })
                    foreach (var kind in new[] { UiSurfaceKind.Window, UiSurfaceKind.Hud })
                    {
                        using var host = ActionContext();
                        using var guest = ActionContext();
                        var clock = new StatusCaptureClock();
                        clock.Time.AdvanceFrame(120f);
                        clock.Connect(host.Ui);
                        clock.Connect(guest.Ui);
                        assets.Install(host.Ui, inheritDefaultTheme: themed);
                        assets.Install(guest.Ui, inheritDefaultTheme: themed);
                        InstallActionDefinitions(host);
                        InstallActionDefinitions(guest);
                        Check(guest.Input.GetAction(ActionGalleryPages.Confirm).Rebind(new[] {
                    InputBinding.Key("Q"), InputBinding.GamepadButton(InputGamepadButton.West) }).Succeeded,
                            "The receiver opts into a different binding from the host's definition.");
                        var request = ActionGalleryPages.CreateRequest(page, kind, host.Ui.ClockTime,
                            themed ? FoundationPages.SurvivalTheme : null);
                        using var local = Make(host.Ui, request);
                        host.Ui.CompletePendingThemeRegistrations();
                        using var received = OpenActionReceiver(guest.Ui, host, request, clock.Epoch);
                        guest.Ui.CompletePendingThemeRegistrations();
                        CaptureMatrix(0);
                        if (page == ActionGalleryPage.Slots)
                        {
                            clock.Time.AdvanceFrame(6f);
                            CaptureMatrix(6);
                            clock.Time.AdvanceFrame(2f);
                            CaptureMatrix(8);
                        }

                        void CaptureMatrix(int elapsed)
                        {
                            foreach (var (ui, surface, route) in new[] { (host.Ui, local, "host-local"), (guest.Ui, received, "guest-received") })
                                foreach (var (width, height) in new[] { (1920, 1080), (2560, 1440) })
                                    foreach (var scale in new[] { 1f, 2f })
                                        foreach (var high in new[] { false, true })
                                            foreach (var device in Enum.GetValues<UiInputDevice>())
                                            {
                                                ui.LastInputDevice = device;
                                                string? normalMotion = null;
                                                foreach (var reduced in new[] { false, true })
                                                {
                                                    Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(high, scale, reduced)).Succeeded,
                                                        "The action capture uses viewer-local accessibility and device choices.");
                                                    var capture = surface.Capture(new UiCaptureViewport(width, height));
                                                    var name = FormattableString.Invariant(
                                                        $"{page}/{(themed ? "survival" : "kit")}/{kind}/{(high ? "high" : "standard")}/{width}x{height}/{scale}/{route}/{device}/{(reduced ? "reduced" : "full")}/{elapsed}s");
                                                    Check(capture.Nodes[0].ThemeId == (themed ? FoundationPages.SurvivalTheme.Id : UiThemePackParser.Default.Id) &&
                                                        !capture.Nodes[0].UsedFallback, name + " resolves the expected theme without a fallback.");
                                                    var issues = capture.CheckAccessibility();
                                                    Check(issues.Count == 0, name + " " + string.Join(", ", issues.Select(issue => issue.Kind + " at " + issue.Path)));
                                                    AssertActionBoard(capture, page, elapsed, route == "guest-received", device, name);
                                                    var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(capture.Nodes.Select(node =>
                                                        new { group = GoldenNode(node), node.State, node.KeyLabel, node.RemainingSeconds, node.Fraction }), CaptureJson)));
                                                    if (reduced) Check(hash == normalMotion, "Reduced motion preserves deadline-driven readings and state at " + name + ".");
                                                    else normalMotion = hash;
                                                    cases.Add(name, new ActionGoldenCase(hash, capture.Nodes.Count(node => node.IsRendered)));
                                                }
                                            }
                        }
                    }

            Check(cases.Count == 1024, "The action capture includes the full theme, size, device, accessibility, clock and route matrix.");
            return JsonSerializer.Serialize(new
            {
                schema = 1,
                source = "FakeUiService headless captured trees",
                fixture = "ActionGalleryPages",
                transport = "Guest trees use the production ownership envelope, encoder, decoder, and fake receiver admission.",
                clock = "120-second epoch with initial readings, cooldown expiry after 6 seconds and refill expiry after 8 seconds.",
                assets.ManifestSha256,
                assets.SampleSha256,
                assets.DefinitionSha256,
                assets.BundleSha256,
                assets.ReceiptSha256,
                cases,
            }, new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n";
        }

        private static FakeUiSurface OpenActionReceiver(FakeUiService receiver, FakeModContext owner, UiSurfaceRequest request,
            string epoch, bool retained = false)
        {
            var owned = UiTreeProjection.Rewrite(request.Content, node => node is UiActionSlot { Image: { } icon } slot
                ? slot.WithWireImageDigest(ThemeForWire(owner.Ui, new UiThemeRef(icon.PackId))!.Digest.Span) : node);
            var content = UiWireCodec.ProjectInputDefaults(owned, name =>
            {
                _ = owner.Input.GetAction(name);
                return ActionGalleryPages.Definitions.Single(definition => definition.Name == name).DefaultBindings;
            });
            var theme = request.Theme == null ? null : ThemeForWire(owner.Ui, request.Theme);
            var envelope = new UiWireEnvelope(StatusPublisher, new byte[32], 1, theme?.Id,
                theme?.Digest.ToArray() ?? new byte[32]);
            return OpenActionPresentation(receiver, request, content, envelope, epoch, retained);
        }

        private static FakeUiSurface OpenActionPresentation(FakeUiService receiver, UiSurfaceRequest request, UiNode? content,
            UiWireEnvelope envelope, string epoch, bool retained)
        {
            var writer = new WorldWireWriter(32768);
            UiWireCodec.Prepare(writer, content, epoch, retained, envelope);
            var reader = new WorldWireReader();
            reader.Reset(writer.Segment.AsSpan().ToArray());
            var copy = UiWireCodec.ReadWithEnvelope(reader, (_, _) => throw new InvalidOperationException("Action displays do not reply."),
                epoch, out var admitted);
            UiWireCodec.End(reader);
            var received = new UiSurfaceRequest(request.Id, request.Title, request.Body, request.Kind, request.Width, request.Height, copy);
            if (request.Kind == UiSurfaceKind.Hud)
                received = received.WithHudAnchor(request.HudAnchor).WithHudVisibility(request.HudVisibility);
            return Make(CreateReceivedSurface(receiver, received, admitted!));
        }

        private static void AssertActionBoard(UiTreeCapture capture, ActionGalleryPage page, int elapsed, bool guest, UiInputDevice device, string name)
        {
            var keyboard = device == UiInputDevice.KeyboardMouse;
            var confirm = guest ? keyboard ? "Q" : "X" : keyboard ? "E" : "A";
            if (page == ActionGalleryPage.KeyGlyphs)
            {
                Check(ActionNode(capture, "key-confirm").KeyLabel == confirm &&
                    ActionNode(capture, "key-scan").KeyLabel == (keyboard ? "Space" : "LB") &&
                    ActionNode(capture, "key-scan").SemanticLabel == (keyboard ? "key Space" : "key left bumper") &&
                    ActionNode(capture, "key-marker").KeyLabel == "Mouse 4" &&
                    ActionNode(capture, "key-shortcut").KeyLabel == "Backspace" &&
                    ActionNode(capture, "key-pan").KeyLabel == "RS ↔",
                    "Key captions are readable, device-local, and keep their first other-device fallback.");
                return;
            }

            Check(ActionNode(capture, "slot-ready") is { State: "ready", IsRendered: true, Clipped: false } ready && ready.KeyLabel == confirm &&
                ActionNode(capture, "slot-emphasis").State == "emphasis" &&
                ActionNode(capture, "slot-unavailable") is { State: "unavailable", Disabled: true } &&
                capture.Nodes.Any(node => node.Kind == "ActionGlyph" && node.IsRendered) &&
                capture.Nodes.Any(node => node.Kind == "ActionImage" && node.IsRendered && !node.UsedFallback && node.InkColor != null) &&
                capture.Nodes.Any(node => node.Kind == "ActionEmphasis" && node.IsRendered && node.BorderWidthPixels > 0) &&
                capture.Nodes.Any(node => node.Kind == "ActionStatusGlyph" && node.Path.EndsWith("/cue:Lock", StringComparison.Ordinal)),
                "All slot states retain the visible artwork, mark, lock and emphasis ring.");
            var timed = ActionNode(capture, "slot-cooldown");
            var charges = ActionNode(capture, "slot-charging");
            var pips = ActionNode(capture, "pips-refill");
            Check(timed.State == (elapsed == 0 ? "cooldown" : "ready") && timed.RemainingSeconds == (elapsed == 0 ? 6d : null),
                name + " cooldown reads " + timed.State + " " + timed.RemainingSeconds + ".");
            Check(charges.State == (elapsed < 8 ? "charging" : "ready") && charges.RemainingSeconds == 8 - elapsed &&
                pips.RemainingSeconds == 8 - elapsed, name + " charge/pip reads " + charges.RemainingSeconds + " and " + pips.RemainingSeconds + ".");
            foreach (var label in capture.Nodes.Where(node => node.Kind is "ActionLabel" or "ActionStatus" or "KeyCapLabel"))
                Check(label.IsRendered && !label.Clipped && label.TextHeightPixels > 0 && label.FillColor != null,
                    name + " loses the readable filled label at " + label.Path + " (" + label.Text + ").");
        }

        private sealed record ActionGoldenCase(string TreeSha256, int RenderedGroups);
    }
}
