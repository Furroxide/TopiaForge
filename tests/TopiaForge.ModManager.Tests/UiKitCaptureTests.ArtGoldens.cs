using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private const string ArtGoldenPath = "tests/fixtures/ui-kit/k1-art-captures.json";

        internal static void WriteArtGoldenManifest(string outputPath)
        {
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, CreateArtGoldenManifest());
        }

        private static void VerifyArtGoldenManifest()
        {
            var path = Path.Combine(Program.FindRepoRoot(), ArtGoldenPath);
            Check(File.Exists(path), "The K1 art headless capture baseline has not been recorded.");
            var expected = File.ReadAllText(path);
            var actual = CreateArtGoldenManifest();
            if (expected == actual) return;

            using var baseline = JsonDocument.Parse(expected);
            using var candidate = JsonDocument.Parse(actual);
            foreach (var section in new[] { "cases", "diagnostics" })
            {
                var expectedCases = baseline.RootElement.GetProperty(section);
                foreach (var current in candidate.RootElement.GetProperty(section).EnumerateObject())
                {
                    Check(expectedCases.TryGetProperty(current.Name, out var previous) && JsonElement.DeepEquals(current.Value, previous),
                        "K1 Gallery art changed for " + current.Name + ". Review its bounds, fit, paint and semantics before updating the baseline.");
                }
            }
            Check(false, "K1 Gallery art package metadata or case coverage changed. Review the baseline difference.");
        }

        private static string CreateArtGoldenManifest()
        {
            var fixture = new GalleryArtFixtures();
            var sizes = new[] { (Width: 1920, Height: 1080), (Width: 2560, Height: 1440) };
            var cases = new SortedDictionary<string, GoldenCase>(StringComparer.Ordinal);
            foreach (var page in Enum.GetValues<FoundationPage>().Where(FoundationArtPages.IsArt))
            foreach (var sample in new[] { false, true })
            foreach (var kind in new[] { UiSurfaceKind.Window, UiSurfaceKind.Hud })
            foreach (var guest in new[] { false, true })
            {
                using var authorLife = new FakeModLifetime();
                using var guestLife = guest ? new FakeModLifetime() : null;
                var owner = new FakeUiService(authorLife);
                fixture.Install(owner);
                var viewer = guestLife == null ? owner : new FakeUiService(guestLife);
                if (guest) fixture.Install(viewer);
                var request = FoundationPages.CreateRequest(page, kind, sample ? FoundationPages.SurvivalTheme : null);
                using var surface = guest ? OpenArtReceiver(viewer, owner, request) : Make(owner, request);
                viewer.CompletePendingThemeRegistrations();

                foreach (var size in sizes)
                foreach (var scale in new[] { 1, 2 })
                foreach (var highContrast in new[] { false, true })
                {
                    string? normalMotionHash = null;
                    foreach (var reduce in new[] { false, true })
                    {
                        var viewport = new UiCaptureViewport(size.Width, size.Height);
                        Check(viewer.ApplyAccessibility(new UiAccessibilityPreferences(highContrast, scale, reduce)).Succeeded,
                            "Use viewer-local art capture preferences.");
                        var theme = sample ? "survival" : "kit";
                        var scheme = kind == UiSurfaceKind.Window ? "paper" : "hud";
                        var contrast = highContrast ? "high" : "standard";
                        var route = guest ? "guest-received" : "host-local";
                        var motion = reduce ? "reduced" : "full";
                        var caseId = FormattableString.Invariant($"{page}/{theme}/{scheme}/{contrast}/{size.Width}x{size.Height}/{scale}/{route}/{motion}");
                        var screenScale = (float)Math.Sqrt(size.Width / 1920d * (size.Height / 1080d));
                        var captured = CaptureCase(caseId, surface, viewport, "Rest", null, screenScale * scale);
                        cases.Add(caseId, captured);
                        if (!reduce) normalMotionHash = captured.TreeSha256;
                        else Check(normalMotionHash == captured.TreeSha256, caseId + " static art changes with motion preferences.");
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                schema = 1,
                source = "FakeUiService headless captured trees",
                fixture = "FoundationPages",
                transport = "Guest trees are encoded and decoded through the UI wire codec before receiver admission.",
                geometry = "Shared reference layout, estimated public font metrics and verified package sprite dimensions. Native raster belongs to the visual gate.",
                hudSurfaceAlpha = 1,
                hudSurfaceNote = "Opaque HUD panels keep the text background independent of gameplay.",
                packageManifestSha256 = fixture.ManifestSha256,
                sampleDefinitionSha256 = fixture.SampleSha256,
                artDefinitionSha256 = fixture.DefinitionSha256,
                bundleSha256 = fixture.BundleSha256,
                receiptSha256 = fixture.ReceiptSha256,
                cases,
                diagnostics = CaptureArtDiagnostics(fixture),
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                NewLine = "\n",
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n";
        }

        private static SortedDictionary<string, ArtDiagnostic> CaptureArtDiagnostics(GalleryArtFixtures fixture)
        {
            var results = new SortedDictionary<string, ArtDiagnostic>(StringComparer.Ordinal);
            using var authorLife = new FakeModLifetime();
            var owner = new FakeUiService(authorLife);
            fixture.Install(owner);
            var page = FoundationPages.CreateRequest(FoundationPage.Images, UiSurfaceKind.Window);
            using var pending = Make(owner, page);
            Record("pending", pending, 5);
            owner.CompletePendingThemeRegistrations();
            var ready = pending.Capture(Viewport);
            Check(pending.SetContent(pending.Content!).Succeeded, "Reapply the same admitted Gallery art.");
            Record("ready-no-op", pending, 0);
            Check(Digest(ready) == results["ready-no-op"].TreeSha256, "Unchanged Gallery art keeps its golden captured state.");

            using var failureLife = new FakeModLifetime();
            var failed = new FakeUiService(failureLife);
            fixture.Install(failed, FakeUiAssetOutcome.LoadFailure);
            using var unreadable = Make(failed, new UiSurfaceRequest("tiny", "Supplies", "", content:
                new UiImage(FoundationArtPages.Card, "Full original semantic label", 1, 1, id: "tiny")));
            using var failedPage = Make(failed, page);
            failed.CompletePendingThemeRegistrations();
            Record("load-failure", failedPage, 5);
            Record("tiny-label", unreadable, 1);
            foreach (var mismatch in new[] { false, true })
            {
                using var guestLife = new FakeModLifetime();
                var guest = new FakeUiService(guestLife);
                if (mismatch) fixture.Install(guest, digestOverride: new string('e', 64));
                using var remote = OpenArtReceiver(guest, owner, page);
                guest.CompletePendingThemeRegistrations();
                Record(mismatch ? "guest-digest-mismatch" : "guest-missing-pack", remote, 5);
            }

            return results;

            void Record(string id, FakeUiSurface surface, int missing)
            {
                var capture = surface.Capture(Viewport);
                var issues = capture.CheckAccessibility();
                Check(issues.Count == missing && issues.All(issue => issue.Kind == UiAccessibilityIssueKind.MissingAsset),
                    id + ": " + string.Join(", ", issues.Select(issue => issue.Kind + " at " + issue.Path)));
                results.Add(id, new ArtDiagnostic(capture.Nodes.Count(node => node.IsRendered), missing,
                    capture.Nodes.Any(node => node.Kind == "ImageFallback" && node.IsRendered), Digest(capture)));
            }

            static string Digest(UiTreeCapture capture) =>
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(capture.Nodes.Select(GoldenNode), CaptureJson)));
        }

        private static FakeUiSurface OpenArtReceiver(FakeUiService guest, FakeUiService owner, UiSurfaceRequest source)
        {
            var art = ThemeForWire(owner, new UiThemeRef("preview-art"))!;
            var normalized = UiImageProjection.Rewrite(source.Content, image =>
            {
                var metadata = UiImageProjection.Find(art, image.Image.ImageId)!;
                var frame = UiImageProjection.ResolveFrame(image, metadata);
                return image.WithWireMetadata(frame.Width, frame.Height, art.Digest.Span);
            })!;
            var palette = source.Theme == null ? null : ThemeForWire(owner, source.Theme);
            var (tree, envelope) = ReceiveArt(normalized, palette);
            var request = new UiSurfaceRequest(source.Id, source.Title, source.Body, source.Kind, source.Width, source.Height, tree);
            if (source.Kind == UiSurfaceKind.Hud)
                request = request.WithHudAnchor(source.HudAnchor).WithHudVisibility(source.HudVisibility);
            return Make(CreateReceivedSurface(guest, request, envelope));
        }

        private sealed record ArtDiagnostic(int VisibleGroups, int MissingAssets, bool ImageUnavailable, string TreeSha256);

        private sealed class GalleryArtFixtures
        {
            private readonly string directory = Path.Combine(Program.FindRepoRoot(), GalleryPath);
            private readonly string manifest;
            private readonly UiThemeDescriptor art;
            private readonly UiBundleReceipt receipt;

            internal GalleryArtFixtures()
            {
                var manifestPath = Path.Combine(directory, "topiaforge.mod.json");
                manifest = File.ReadAllText(manifestPath);
                ManifestSha256 = Hash(File.ReadAllBytes(manifestPath));
                SampleSha256 = Hash(File.ReadAllBytes(Path.Combine(directory, SurvivalPath)));
                var definitionBytes = File.ReadAllBytes(Path.Combine(directory, FoundationArtPages.ArtThemeDefinition));
                DefinitionSha256 = Hash(definitionBytes);
                BundleSha256 = Hash(File.ReadAllBytes(Path.Combine(directory, FoundationArtPages.ArtBundle)));
                var receiptBytes = File.ReadAllBytes(Path.Combine(directory, UiBundleReceipt.PathFor(FoundationArtPages.ArtBundle)));
                ReceiptSha256 = Hash(receiptBytes);
                receipt = UiBundleReceipt.Parse(receiptBytes, BundleSha256);
                var parsed = UiThemePackParser.Parse(definitionBytes, expectedId: "preview-art");
                art = parsed.Value ?? throw new InvalidOperationException(parsed.ErrorMessage);
            }

            internal string ManifestSha256 { get; }
            internal string SampleSha256 { get; }
            internal string DefinitionSha256 { get; }
            internal string BundleSha256 { get; }
            internal string ReceiptSha256 { get; }

            internal void Install(FakeUiService ui, FakeUiAssetOutcome outcome = FakeUiAssetOutcome.Available,
                string? digestOverride = null)
            {
                Check(ui.AddThemeCatalogue(manifest, path => File.ReadAllText(Path.Combine(directory, path))).Succeeded,
                    "Read the Gallery's owned theme definitions.");
                foreach (var image in art.Images)
                {
                    Check(receipt.TryGetSprite(image.AssetName, out var sprite) &&
                        string.Equals(sprite.LicenseSha256, Hash(File.ReadAllBytes(Path.Combine(directory, image.LicensePath))),
                            StringComparison.OrdinalIgnoreCase), "The Gallery asset and CC0 notice match the verified receipt.");
                    var texture = receipt.Textures[sprite.Texture];
                    ui.AddAssetFixture(new FakeUiAssetMetadata(image.BundlePath, image.AssetName, digestOverride ?? BundleSha256,
                        sprite.Width, sprite.Height, texture.DecodedBytes, outcome: outcome, isMask: texture.IsMask));
                }
            }

            private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        }
    }
}
