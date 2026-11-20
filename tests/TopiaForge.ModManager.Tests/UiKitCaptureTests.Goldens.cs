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
        private const string GoldenPath = "tests/fixtures/ui-kit/k1-foundation-captures.json";
        private const string GalleryPath = "mods/TopiaForge.UiGallery";
        private const string SurvivalPath = "ui/themes/survival.json";
        private static readonly JsonSerializerOptions CaptureJson = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        internal static void WriteGoldenManifest(string outputPath)
        {
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, CreateGoldenManifest());
        }

        private static void VerifyGoldenManifest()
        {
            var path = Path.Combine(Program.FindRepoRoot(), GoldenPath);
            Check(File.Exists(path), "The K1 Gallery headless capture baseline has not been recorded.");
            var expected = File.ReadAllText(path);
            var actual = CreateGoldenManifest();
            if (expected == actual) return;
            using var baseline = JsonDocument.Parse(expected);
            using var candidate = JsonDocument.Parse(actual);
            var expectedCases = baseline.RootElement.GetProperty("cases");
            var actualCases = candidate.RootElement.GetProperty("cases");
            foreach (var current in actualCases.EnumerateObject())
            {
                Check(expectedCases.TryGetProperty(current.Name, out var previous) && JsonElement.DeepEquals(current.Value, previous),
                    "K1 Gallery captured state changed for " + current.Name + ". Review the new bounds and paint before updating the baseline.");
            }
            Check(false, "K1 Gallery capture metadata or case coverage changed. Review the difference before updating the baseline.");
        }

        private static string CreateGoldenManifest()
        {
            var package = Path.Combine(Program.FindRepoRoot(), GalleryPath);
            var themeBytes = File.ReadAllBytes(Path.Combine(package, SurvivalPath));
            var manifestBytes = File.ReadAllBytes(Path.Combine(package, "topiaforge.mod.json"));
            var declaration = File.ReadAllText(Path.Combine(package, "topiaforge.mod.json"));
            string ReadFixture(string path) => File.ReadAllText(Path.Combine(package, path));

            var dimensions = new[] { (Width: 1920, Height: 1080), (Width: 2560, Height: 1440) };
            var cases = new SortedDictionary<string, GoldenCase>(StringComparer.Ordinal);
            UiThemeDescriptor? survival = null;
            foreach (var page in Enum.GetValues<FoundationPage>())
                foreach (var sample in new[] { false, true })
                    foreach (var useHud in new[] { false, true })
                        foreach (var guest in new[] { false, true })
                        {
                            var interactive = page == FoundationPage.InteractionStates;
                            var kind = useHud && !interactive ? UiSurfaceKind.Hud : UiSurfaceKind.Window;
                            using var lifetime = new FakeModLifetime();
                            using var players = guest ? new FakePlayerDirectory() : null;
                            var recipient = players?.Add(new ParticipantId("gallery-guest"), "Sam");
                            var ui = recipient == null ? new FakeUiService(lifetime) : players!.State(recipient.Id).Ui;
                            if (sample)
                                Check(ui.AddThemeCatalogue(declaration, ReadFixture).Succeeded, "The Gallery's real theme catalogue validates.");

                            var request = FoundationPages.CreateRequest(page, kind, sample ? FoundationPages.SurvivalTheme : null);
                            using var surface = recipient != null && !interactive ? Make(recipient.Ui.CreateSurface(request)) : Make(ui, request);
                            ui.CompletePendingThemeRegistrations();
                            if (sample) survival = ThemeForWire(ui, FoundationPages.SurvivalTheme);
                            var route = guest ? interactive ? "guest-local" : "guest-targeted" : "host-local";
                            var scheme = useHud ? "hud" : "paper";
                            var theme = sample ? "survival" : "kit";

                            foreach (var size in dimensions)
                                foreach (var scale in new[] { 1, 2 })
                                    foreach (var highContrast in new[] { false, true })
                                        foreach (var reducedMotion in new[] { false, true })
                                        {
                                            var viewport = new UiCaptureViewport(size.Width, size.Height);
                                            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast, scale, reducedMotion)).Succeeded,
                                                "Apply viewer-local capture preferences.");
                                            var states = interactive ? new[] { "Rest", "Hovered", "Focused", "Selected", "Pressed", "SelectedPressed" } : new[] { "Rest" };
                                            foreach (var state in states)
                                            {
                                                var contrast = highContrast ? "high" : "standard";
                                                var motion = reducedMotion ? "reduced" : "full";
                                                var id = FormattableString.Invariant($"{page}/{theme}/{scheme}/{contrast}/{size.Width}x{size.Height}/{scale}/{route}/{motion}/{state}");
                                                var hudOverride = interactive && useHud ? true : (bool?)null;
                                                var screenScale = (float)Math.Sqrt((size.Width / 1920d) * (size.Height / 1080d));
                                                cases.Add(id, CaptureCase(id, surface, viewport, state, hudOverride, screenScale * scale));
                                            }
                                        }
                        }

            Check(survival != null && survival.Id == "survival", "The sample theme must be resolved from the real Gallery fixture.");
            return JsonSerializer.Serialize(new
            {
                schema = 1,
                source = "FakeUiService headless captured trees",
                fixture = "FoundationPages",
                palettePreview = "Interaction states use window input. HUD coverage here evaluates HUD scheme paint in the fake.",
                transport = "Guest-targeted cases measure the production frame. Guest-local cases open the same fixture with local input.",
                geometry = "Estimated glyph advances and calibrated fixture text metrics. Native raster and input evidence belong to the visual gate.",
                assetIdentity = "Source package manifest and theme definition hashes. UI asset bundles are native visual evidence.",
                hudSurfaceAlpha = 1,
                hudSurfaceNote = "Opaque HUD panels keep the text background independent of gameplay.",
                packageManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)),
                sampleDefinitionSha256 = Convert.ToHexString(SHA256.HashData(themeBytes)),
                kit = DescribeTypography(UiThemePackParser.Default),
                survival = DescribeTypography(survival!),
                cases,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                NewLine = "\n",
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n";
        }

        private static object DescribeTypography(UiThemeDescriptor theme) => new
        {
            descriptorSha256 = Convert.ToHexString(theme.Digest.Span),
            ascender = 0.8f,
            descender = -0.2f,
            roles = Enum.GetValues<UiTextStyle>().Select(style => new
            {
                role = style.ToString(),
                face = theme.GetText(style).Font,
                referenceHeight = theme.GetText(style).ReferenceSize,
                uppercase = theme.GetText(style).IsUppercase,
                trackingEm = theme.GetText(style).TrackingEm,
            }).ToArray(),
        };

        private static GoldenCase CaptureCase(string caseId, FakeUiSurface surface, UiCaptureViewport viewport,
            string state, bool? hudScheme, float offsetScale)
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var first = CaptureForFixture(surface, viewport, state, 0f, hudScheme);
            var panel = first.Nodes[0];
            var children = first.Nodes.Single(node => node.Path == "/0");
            var bodyHeight = panel.Y + panel.Height - children.Y - 12f * offsetScale;
            var step = Math.Max(40, bodyHeight * 0.72f) / offsetScale;
            var maxOffset = Math.Max(0, children.Height - bodyHeight) / offsetScale;
            var snapshots = 0;
            var rendered = new HashSet<string>(StringComparer.Ordinal);
            for (var offset = 0f; ; offset = Math.Min(offset + step, maxOffset))
            {
                var capture = offset == 0f ? first : CaptureForFixture(surface, viewport, state, offset, hudScheme);
                var issues = capture.CheckAccessibility();
                Check(issues.Count == 0, caseId + ": " + string.Join(", ", issues.Select(issue => issue.Kind + " at " + issue.Path)));
                foreach (var node in capture.Nodes)
                {
                    if (node.IsRendered) rendered.Add(node.Path);
                }
                digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(capture.Nodes.Select(GoldenNode), CaptureJson));
                snapshots++;
                if (offset >= maxOffset) break;
            }

            var unread = first.Nodes.Where(node => node.Text.Length > 0 && !rendered.Contains(node.Path)).Select(node => node.Path).ToArray();
            Check(unread.Length == 0, caseId + " never displayed " + string.Join(", ", unread));
            return new GoldenCase(snapshots, rendered.Count, Convert.ToHexString(digest.GetHashAndReset()));
        }

        private static object GoldenNode(UiCapturedNode node) => new
        {
            node.Path,
            node.Id,
            node.Kind,
            node.Text,
            node.SemanticLabel,
            node.Decorative,
            node.Disabled,
            node.Selected,
            x = Math.Round(node.X, 3),
            y = Math.Round(node.Y, 3),
            width = Math.Round(node.Width, 3),
            height = Math.Round(node.Height, 3),
            size = Math.Round(node.TextHeightPixels, 3),
            contrast = Math.Round(node.ContrastRatio, 5),
            node.ThemeId,
            node.StyleId,
            node.InkColor,
            node.FillColor,
            radius = Round(node.CornerRadiusPixels),
            border = Round(node.BorderWidthPixels),
            shadowX = Round(node.ShadowOffsetXPixels),
            shadowY = Round(node.ShadowOffsetYPixels),
            node.UsedFallback,
            node.IsRendered,
            node.Clipped,
            node.Occluded,
        };

        private static double? Round(float? value) => value.HasValue ? Math.Round(value.Value, 3) : null;

        private sealed record GoldenCase(int Snapshots, int VisibleGroups, string TreeSha256);
    }
}
