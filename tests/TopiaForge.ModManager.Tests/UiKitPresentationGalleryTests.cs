using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitPresentationGalleryTests
    {
        private const string BaselinePath = "tests/fixtures/ui-kit/k2-presentation-captures.json";
        private const string GalleryPath = "mods/TopiaForge.UiGallery";
        private const string DefinitionPath = "ui/themes/survival.json";
        private static readonly UiThemeRef Survival = new UiThemeRef("survival");
        private static readonly JsonSerializerOptions ManifestJson = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        private static readonly JsonSerializerOptions FrameJson = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        private static readonly Func<FakeUiSurface, UiCaptureViewport, string, float, bool?, UiTreeCapture> CaptureControl =
            typeof(FakeUiSurface).GetMethod("CaptureForFixture", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<FakeUiSurface, UiCaptureViewport, string, float, bool?, UiTreeCapture>>();
        private static readonly Func<FakeUiService, UiCaptureViewport, UiTreeCapture> CaptureStinger = CaptureMethod("CaptureStinger");
        private static readonly Func<FakeUiService, UiCaptureViewport, UiTreeCapture> CaptureToasts = CaptureMethod("CaptureToasts");
        private static readonly Func<UiCapturedNode, float?> FillOpacity = typeof(UiCapturedNode)
            .GetProperty("FillOpacity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!.CreateDelegate<Func<UiCapturedNode, float?>>();

        internal static void Run()
        {
            var expected = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), BaselinePath));
            var actual = GoldenManifest(out var cases, out var snapshots);
            if (expected != actual)
            {
                using var baseline = JsonDocument.Parse(expected);
                using var candidate = JsonDocument.Parse(actual);
                var previous = baseline.RootElement.GetProperty("cases");
                foreach (var item in candidate.RootElement.GetProperty("cases").EnumerateObject())
                    Check(previous.TryGetProperty(item.Name, out var value) && JsonElement.DeepEquals(value, item.Value),
                        "K2 presentation fixture changed for " + item.Name + ". Review the captured geometry and paint before updating its baseline.");
                Check(false, "K2 presentation fixture metadata or case coverage changed.");
            }

            Console.WriteLine(FormattableString.Invariant($"K2 Gallery presentation passed {cases} cases and {snapshots} headless accessibility snapshots."));
        }

        internal static void WriteGoldenManifest(string outputPath) =>
            File.WriteAllText(outputPath, GoldenManifest(out _, out _), new UTF8Encoding(false));

        private static string GoldenManifest(out int caseCount, out int snapshots)
        {
            var package = Path.Combine(Program.FindRepoRoot(), GalleryPath);
            var definition = File.ReadAllText(Path.Combine(package, DefinitionPath));
            var descriptor = Require(UiThemePackParser.Parse(Encoding.UTF8.GetBytes(definition)));
            var manifest = File.ReadAllText(Path.Combine(package, "topiaforge.mod.json"));
            var cases = new SortedDictionary<string, GoldenCase>(StringComparer.Ordinal);

            foreach (var page in Enum.GetValues<PresentationPage>())
                foreach (var sample in new[] { false, true })
                    foreach (var hudControls in new[] { false, true })
                        foreach (var highContrast in new[] { false, true })
                            foreach (var dimensions in new[] { (Width: 1920, Height: 1080), (Width: 2560, Height: 1440) })
                                foreach (var scale in new[] { 1f, 2f })
                                    foreach (var guest in new[] { false, true })
                                        foreach (var reduced in new[] { false, true })
                                        {
                                            var viewport = new UiCaptureViewport(dimensions.Width, dimensions.Height);
                                            var preferences = new UiAccessibilityPreferences(highContrast, scale, reduced);
                                            var theme = sample ? Survival : null;
                                            var resolved = sample ? descriptor : UiThemePackParser.Default;
                                            var scheme = hudControls ? "hud" : "paper";
                                            var contrast = highContrast ? "high" : "standard";
                                            var route = guest ? "guest" : "host";
                                            var motion = reduced ? "reduced" : "full";
                                            var id = FormattableString.Invariant($"{page}/{(sample ? "survival" : "kit")}/{scheme}/{contrast}/{dimensions.Width}x{dimensions.Height}/{scale}/{route}/{motion}");
                                            using var frames = new CaptureFrames(id, viewport, hudControls, preferences, resolved);
                                            using var scene = new GalleryScenario(guest, preferences, theme, definition);
                                            switch (page)
                                            {
                                                case PresentationPage.Stingers: Stingers(scene, frames, theme); break;
                                                case PresentationPage.Overlays: Overlays(scene, frames, theme); break;
                                                case PresentationPage.Toasts: Toasts(scene, frames, theme); break;
                                                default: throw new ArgumentOutOfRangeException(nameof(page));
                                            }
                                            cases.Add(id, frames.Complete());
                                        }

            caseCount = cases.Count;
            snapshots = cases.Values.Sum(item => item.Snapshots);
            return JsonSerializer.Serialize(new
            {
                schema = 1,
                source = "FakeUiService headless captured trees",
                fixture = "PresentationPages",
                schemes = "Launcher palettes are Paper or HUD. Overlay panels and stingers use the selected HUD scheme. Toasts use the kit HUD scheme.",
                routes = "Guest previews use FakePlayerDirectory. Role stingers and individual toasts are targeted. The announcement queue uses the fake gamemode broadcast.",
                geometry = "Predicted text and layout at deterministic fake times. Native pixels, compositing and device navigation belong to the phase visual gate.",
                galleryManifestSha256 = HashText(manifest),
                sampleDefinitionSha256 = HashText(definition),
                kitDescriptorSha256 = Convert.ToHexString(UiThemePackParser.Default.Digest.Span),
                sampleDescriptorSha256 = Convert.ToHexString(descriptor.Digest.Span),
                caseCount,
                snapshots,
                cases,
            }, ManifestJson) + "\n";
        }

        private static string HashText(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Replace("\r\n", "\n", StringComparison.Ordinal))));

        private static Func<FakeUiService, UiCaptureViewport, UiTreeCapture> CaptureMethod(string name) =>
            typeof(FakeUiService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<FakeUiService, UiCaptureViewport, UiTreeCapture>>();

        private static T Require<T>(OperationResult<T> result) where T : notnull
        {
            if (!result.TryGetValue(out var value)) throw new InvalidOperationException(result.ErrorCode + ": " + result.ErrorMessage);
            return value;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static UiCapturedNode Part(UiTreeCapture tree, string kind) => tree.Nodes.Single(node => node.Kind == kind);

        private sealed record GoldenCase(int Snapshots, int VisibleGroups, string TreeSha256);

        private sealed class CaptureFrames : IDisposable
        {
            private readonly IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private readonly string id;
            private readonly bool hudControls;
            private int snapshots, visibleGroups;
            internal readonly UiCaptureViewport Viewport;
            internal readonly UiAccessibilityPreferences Preferences;
            internal readonly UiThemeDescriptor Theme;

            internal CaptureFrames(string id, UiCaptureViewport viewport, bool hudControls,
                UiAccessibilityPreferences preferences, UiThemeDescriptor theme)
            {
                this.id = id;
                this.hudControls = hudControls;
                Viewport = viewport;
                Preferences = preferences;
                Theme = theme;
            }

            internal void Verify(bool condition, string message) => Check(condition, id + ": " + message);

            internal void Controls(FakeUiSurface surface, params string[] buttonIds)
            {
                var tree = CaptureControl(surface, Viewport, "Rest", 0f, hudControls);
                foreach (var buttonId in buttonIds)
                    Verify(tree.Nodes.Any(node => node.Id == buttonId && node.Kind == nameof(UiButton) && node.IsRendered),
                        "The launcher shows " + buttonId + ".");
                Add("controls", tree);
            }

            internal void Add(string frame, UiTreeCapture tree)
            {
                var issues = tree.CheckAccessibility();
                Verify(issues.Count == 0, frame + " has accessibility issues: " +
                    string.Join(", ", issues.Select(issue => issue.Kind + " at " + issue.Path)));
                visibleGroups += tree.Nodes.Count(node => node.IsRendered);
                snapshots++;
                Trace(new { frame, nodes = tree.Nodes.Select(PresentationNode).ToArray() });
            }

            internal void Trace(object value) => digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(value, FrameJson));
            internal GoldenCase Complete() => new GoldenCase(snapshots, visibleGroups, Convert.ToHexString(digest.GetHashAndReset()));
            public void Dispose() => digest.Dispose();
        }

        private static object PresentationNode(UiCapturedNode node) => new
        {
            node.Path,
            node.Id,
            node.Kind,
            node.Text,
            node.SemanticLabel,
            node.State,
            node.Announcement,
            x = Math.Round(node.X, 3),
            y = Math.Round(node.Y, 3),
            width = Math.Round(node.Width, 3),
            height = Math.Round(node.Height, 3),
            textHeight = Math.Round(node.TextHeightPixels, 3),
            contrast = Math.Round(node.ContrastRatio, 5),
            node.ThemeId,
            node.StyleId,
            node.InkColor,
            node.FillColor,
            opacity = Round(FillOpacity(node)),
            radius = Round(node.CornerRadiusPixels),
            border = Round(node.BorderWidthPixels),
            shadowX = Round(node.ShadowOffsetXPixels),
            shadowY = Round(node.ShadowOffsetYPixels),
            node.Decorative,
            node.Disabled,
            node.Selected,
            node.UsedFallback,
            node.IsRendered,
            node.Clipped,
            node.Occluded,
        };

        private static double? Round(float? value) => value.HasValue ? Math.Round(value.Value, 3) : null;
    }
}
