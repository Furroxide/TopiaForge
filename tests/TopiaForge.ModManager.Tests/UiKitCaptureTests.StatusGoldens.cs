using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private const string StatusGoldenPath = "tests/fixtures/ui-kit/k2-status-captures.json";
        private const string StatusPublisher = "io.github.furroxide.topiaforge.uigallery";
        private const double StatusStart = 120d;

        internal static void WriteStatusGoldenManifest(string path) => File.WriteAllText(path, CreateStatusGoldenManifest());

        private static void VerifyStatusGoldenManifest()
        {
            var expected = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), StatusGoldenPath));
            var actual = CreateStatusGoldenManifest();
            if (expected == actual) return;
            using var baseline = JsonDocument.Parse(expected);
            using var candidate = JsonDocument.Parse(actual);
            var prior = baseline.RootElement.GetProperty("cases");
            foreach (var current in candidate.RootElement.GetProperty("cases").EnumerateObject())
                Check(prior.TryGetProperty(current.Name, out var entry) && JsonElement.DeepEquals(entry, current.Value),
                    "Look at the changed K2 status paint, bounds, or numerics before recording " + current.Name + ".");
            Check(false, "K2 status capture metadata or case coverage changed.");
        }

        private static string CreateStatusGoldenManifest()
        {
            var root = Path.Combine(Program.FindRepoRoot(), GalleryPath);
            var manifestFile = Path.Combine(root, "topiaforge.mod.json");
            var manifest = File.ReadAllText(manifestFile);
            var sample = File.ReadAllBytes(Path.Combine(root, SurvivalPath));
            var cases = new SortedDictionary<string, StatusGoldenCase>(StringComparer.Ordinal);
            UiThemeDescriptor? sampleDescriptor = null;
            foreach (var page in Enum.GetValues<StatusGalleryPage>())
                foreach (var themed in new[] { false, true })
                    foreach (var kind in new[] { UiSurfaceKind.Window, UiSurfaceKind.Hud })
                    {
                        using var hostLife = new FakeModLifetime();
                        using var guestLife = new FakeModLifetime();
                        var host = new FakeUiService(hostLife);
                        var guest = new FakeUiService(guestLife);
                        var clock = new StatusCaptureClock();
                        clock.Time.AdvanceFrame((float)StatusStart);
                        clock.Connect(host);
                        clock.Connect(guest);
                        if (themed)
                        {
                            string ReadFile(string path) => File.ReadAllText(Path.Combine(root, path));
                            Check(host.AddThemeCatalogue(manifest, ReadFile).Succeeded && guest.AddThemeCatalogue(manifest, ReadFile).Succeeded,
                                "Read both viewers' admitted Gallery theme catalogue.");
                        }
                        var request = StatusGalleryPages.CreateRequest(page, kind, host.ClockTime,
                            themed ? FoundationPages.SurvivalTheme : null);
                        using var local = Make(host, request);
                        host.CompletePendingThemeRegistrations();
                        var descriptor = request.Theme == null ? null : ThemeForWire(host, request.Theme);
                        if (themed) sampleDescriptor = descriptor;
                        using var received = OpenStatusReceiver(guest, request, descriptor, clock.Epoch, retained: false);
                        guest.CompletePendingThemeRegistrations();
                        if (page == StatusGalleryPage.Status)
                        {
                            CaptureMatrix("readings", host, local, guest, received);
                            continue;
                        }

                        CaptureMatrix("initial", host, local, guest, received);
                        clock.Time.AdvanceFrame(2f);
                        CaptureMatrix("warning", host, local, guest, received);
                        clock.Time.AdvanceFrame(5f);
                        CaptureMatrix("critical", host, local, guest, received);
                        clock.Time.AdvanceFrame(5f);
                        CaptureMatrix("expiry", host, local, guest, received);
                        received.Dispose();
                        clock.Epoch = "status-next";
                        using var expired = OpenStatusReceiver(guest, request, descriptor, clock.Epoch, retained: true);
                        guest.CompletePendingThemeRegistrations();
                        CaptureMatrix("retained-epoch-zero", host, local, guest, expired);

                        void CaptureMatrix(string reading, FakeUiService source, FakeUiSurface localSurface,
                            FakeUiService viewer, FakeUiSurface remoteSurface)
                        {
                            foreach (var (ui, surface, route) in new[] { (source, localSurface, "host-local"), (viewer, remoteSurface, "guest-received") })
                                foreach (var (width, height) in new[] { (1920, 1080), (2560, 1440) })
                                    foreach (var scale in new[] { 1f, 2f })
                                        foreach (var high in new[] { false, true })
                                        {
                                            string? normalMotion = null;
                                            foreach (var reduced in new[] { false, true })
                                            {
                                                Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(high, scale, reduced)).Succeeded,
                                                    "The capture uses viewer-local preferences.");
                                                var capture = surface.Capture(new UiCaptureViewport(width, height));
                                                var key = FormattableString.Invariant($"{page}/{(themed ? "survival" : "kit")}/{kind}/{(high ? "high" : "standard")}/{width}x{height}/{scale}/{route}/{(reduced ? "reduced" : "full")}/{reading}");
                                                var issues = capture.CheckAccessibility();
                                                Check(issues.Count == 0, key + " " + string.Join(", ",
                                                    issues.Select(issue => issue.Kind + " at " + issue.Path + ". " + issue.Message)));
                                                if (page == StatusGalleryPage.Status) CheckStatusBoard(capture, descriptor ?? UiThemePackParser.Default, kind, high);
                                                else CheckCountdownBoard(capture, reading, route == "guest-received");
                                                var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(capture.Nodes.Select(node =>
                                                    new { tree = GoldenNode(node), node.State, node.RemainingSeconds, node.Fraction }), CaptureJson)));
                                                if (reduced) Check(hash == normalMotion, key + " has no motion-dependent paint.");
                                                else normalMotion = hash;
                                                cases.Add(key, new StatusGoldenCase(ui.ClockTime, hash, capture.Nodes.Count(node => node.IsRendered),
                                                    capture.Nodes.Where(node => node.Id != null && (node.RemainingSeconds.HasValue || node.Fraction.HasValue))
                                                        .Select(node => new StatusValue(node.Id!, node.State!, node.RemainingSeconds, node.Fraction)).ToArray()));
                                            }
                                        }
                        }
                    }

            Check(cases.Count == 768, "K2 status evidence includes the full viewer, scheme, size, scale, theme, and clock matrix.");
            Check(sampleDescriptor != null, "The status fixture resolves the admitted Survival descriptor.");
            return JsonSerializer.Serialize(new
            {
                schema = 1,
                source = "FakeUiService headless captured trees",
                fixture = "StatusGalleryPages",
                transport = "Guest trees use the production ownership envelope, encoder, decoder, and fake receiver admission.",
                clock = "Starts at 120 seconds and advances deterministically to warning, critical, and expiry before an epoch change.",
                packageManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestFile))),
                sampleDefinitionSha256 = Convert.ToHexString(SHA256.HashData(sample)),
                kitDescriptorSha256 = Convert.ToHexString(UiThemePackParser.Default.Digest.Span),
                sampleDescriptorSha256 = Convert.ToHexString(sampleDescriptor!.Digest.Span),
                cases,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                NewLine = "\n",
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n";
        }

        private static FakeUiSurface OpenStatusReceiver(FakeUiService guest, UiSurfaceRequest source,
            UiThemeDescriptor? palette, string epoch, bool retained)
        {
            var zero = new byte[32];
            var sent = new UiWireEnvelope(StatusPublisher, zero, retained ? 2UL : 1UL, palette?.Id,
                palette == null ? zero : palette.Digest.Span);
            var writer = new WorldWireWriter(32 * 1024);
            var timed = retained ? UiWireCodec.WriteRetained(writer, source.Content, epoch, sent) : UiWireCodec.Write(writer, source.Content, epoch, sent);
            if (retained) Check(!timed, "A retained old epoch writes no stale deadline.");
            var reader = new WorldWireReader();
            reader.Reset(writer.Segment.ToArray());
            var tree = UiWireCodec.ReadWithEnvelope(reader, (_, _) => { }, epoch, out var envelope);
            UiWireCodec.End(reader);
            Check(tree != null && envelope?.OwnerPackageId == StatusPublisher && envelope.ThemeId == sent.ThemeId,
                "Decode the same authenticated K2 composition the local fixture admitted.");
            var request = new UiSurfaceRequest(source.Id, source.Title, source.Body, source.Kind, source.Width, source.Height, tree);
            if (source.Kind == UiSurfaceKind.Hud)
                request = request.WithHudAnchor(source.HudAnchor).WithHudVisibility(source.HudVisibility);
            return Make(CreateReceivedSurface(guest, request, envelope!));
        }

        private static void CheckStatusBoard(UiTreeCapture capture, UiThemeDescriptor descriptor, UiSurfaceKind kind, bool high)
        {
            CheckProgress("pressure-normal", "Pressure 60%", 0.60f, "normal", null);
            CheckProgress("pressure-warning", "Pressure 85%", 0.85f, "warning", UiGlyph.Warning);
            CheckProgress("pressure-critical", "Pressure 95%", 0.95f, "critical", UiGlyph.Cross);
            CheckProgress("charge-normal", "Charge 60%", 0.60f, "normal", null);
            CheckProgress("charge-warning", "Charge 40%", 0.40f, "warning", UiGlyph.Warning);
            CheckProgress("charge-critical", "Charge 20%", 0.20f, "critical", UiGlyph.Cross);
            var scheme = descriptor.SelectScheme(kind == UiSurfaceKind.Hud, high);
            CheckStat("stat-value", "Integrity 54", 0.54f, "warning", UiTone.Warning, UiGlyph.Warning);
            CheckStat("stat-maximum", "Reserve 48 / 100", 0.48f, "critical", UiTone.Danger, UiGlyph.Cross);
            CheckStat("stat-percent", "Storage 51%", 0.51f, "normal", UiTone.Success, null);
            Check(Row(capture, "legacy-signal") is { IsRendered: true, Text: "Signal reserve", Fraction: 0.65f, State: "normal" },
                "The legacy progress fixture retains its plain caption and fill value.");

            void CheckProgress(string id, string text, float fraction, string state, UiGlyph? cue)
            {
                var row = Row(capture, id);
                var track = Child(capture, id, "Track");
                var fill = Child(capture, id, "TrackFill");
                Check(row is { IsRendered: true } && row.State == state && row.Fraction == fraction &&
                    Child(capture, id, "ProgressLabel").Text == text &&
                    Math.Abs(fill.Width / track.Width - fraction) < 0.00001f,
                    id + " exposes its percentage, threshold state and filled fraction.");
                AssertStatusCue(capture, id, cue);
            }

            void CheckStat(string id, string text, float fraction, string state, UiTone tone, UiGlyph? cue)
            {
                Check(Row(capture, id) is { IsRendered: true } row && row.Fraction == fraction && row.State == state,
                    id + " exposes its threshold state and exact value fraction.");
                AssertStatInk(capture, id, text, UiStatusMeterPaint.Resolve(scheme, tone, high), high);
                AssertStatusCue(capture, id, cue);
            }
        }

        private static void CheckCountdownBoard(UiTreeCapture capture, string reading, bool guest)
        {
            Check(Row(capture, "timer-elapsed").IsRendered, "Each countdown showcase is visible.");
            if (reading == "initial")
            {
                Check(Child(capture, "timer-seconds", "CountdownDigits").Text == "24" &&
                    Child(capture, "timer-minutes", "CountdownDigits").Text == "125:00" &&
                    Child(capture, "timer-hours", "CountdownDigits").Text == "1:02:03" &&
                    Child(capture, "timer-elapsed", "CountdownDigits").Text == "0:00",
                    "All formatted countdowns and initial expiry are visible in their own format.");
            }
            if (guest && reading == "retained-epoch-zero")
            {
                Check(Row(capture, "timer-seconds") is { Kind: "UiText", Text: "Seconds 0", RemainingSeconds: null } &&
                    Row(capture, "timer-minutes") is { Kind: "UiText", Text: "Minutes and seconds 0:00" } &&
                    Row(capture, "timer-hours") is { Kind: "UiText", Text: "Hours, minutes and seconds 0:00:00" },
                    "The retained receiver displays the format's plain zero surrogate without reviving a timer.");
                return;
            }
            var (seconds, state, cue) = reading switch
            {
                "initial" => (12, "normal", (UiGlyph?)null),
                "warning" => (10, "warning", UiGlyph.Warning),
                "critical" => (5, "critical", UiGlyph.Cross),
                _ => (0, "critical", UiGlyph.Cross),
            };
            var timer = Row(capture, "timer-transfer");
            Check(timer.IsRendered && timer.RemainingSeconds == seconds && timer.State == state &&
                Child(capture, "timer-transfer", "CountdownDigits").Text == "0:" + seconds.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                "Fake host and guest countdowns read the same frame clock and cross exact thresholds.");
            AssertStatusCue(capture, "timer-transfer", cue);
            if (reading == "retained-epoch-zero")
                Check(Row(capture, "timer-hours").RemainingSeconds == 0 && Child(capture, "timer-hours", "CountdownDigits").Text == "0:00:00",
                    "A previously active long countdown also expires on the local retained epoch.");
        }

        private sealed record StatusGoldenCase(double ClockTime, string TreeSha256, int RenderedGroups, StatusValue[] Values);
        private sealed record StatusValue(string Id, string State, double? RemainingSeconds, float? Fraction);
    }
}
