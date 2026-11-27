using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private const string ControlGoldenPath = "tests/fixtures/ui-kit/k2-control-captures.json";
        private const string ExpeditionKeyboard = "gallery-bind-keyboard";

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetBindingListening")]
        private static extern void SetBindingListening(FakeUiSurface surface, string? id);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetDropdownOpen")]
        private static extern void SetDropdownOpen(FakeUiSurface surface, string? id);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "HoverTooltip")]
        private static extern void SetGalleryTooltipHover(FakeUiSurface surface, string? id);

        internal static void WriteControlGoldenManifest(string outputPath) =>
            File.WriteAllText(Path.GetFullPath(outputPath), CreateControlGoldenManifest());

        internal static void VerifyControlGoldenManifest()
        {
            var path = Path.Combine(Program.FindRepoRoot(), ControlGoldenPath);
            Check(File.Exists(path), "Record the K2 controls capture baseline.");
            var expected = File.ReadAllText(path);
            var actual = CreateControlGoldenManifest();
            if (actual == expected) return;
            using var baseline = JsonDocument.Parse(expected);
            using var candidate = JsonDocument.Parse(actual);
            var previous = baseline.RootElement.GetProperty("cases");
            foreach (var current in candidate.RootElement.GetProperty("cases").EnumerateObject())
                Check(previous.TryGetProperty(current.Name, out var value) && JsonElement.DeepEquals(value, current.Value),
                    "K2 Gallery controls changed at " + current.Name + ".");
            Check(false, "The K2 controls capture metadata or case coverage changed.");
        }

        private static string CreateControlGoldenManifest()
        {
            var package = Path.Combine(Program.FindRepoRoot(), GalleryPath);
            string Read(string path) => File.ReadAllText(Path.Combine(package, path));
            var declaration = Read("topiaforge.mod.json");
            var dimensions = new[] { (Width: 1920, Height: 1080), (Width: 2560, Height: 1440) };
            var cases = new SortedDictionary<string, GoldenCase>(StringComparer.Ordinal);
            foreach (var page in Enum.GetValues<ControlGalleryPage>())
                foreach (var scenario in ControlScenarios(page))
                    foreach (var sample in new[] { false, true })
                        foreach (var guest in new[] { false, true })
                        {
                            if (!guest && scenario == "Unavailable") continue;
                            var owner = guest ? UiGalleryCaptureFixture.RemoteFixtureOwner : UiGalleryCaptureFixture.Owner;
                            using var context = new FakeModContext(new ModIdentity(owner, "Gallery", SemanticVersion.Parse("0.2.0")));
                            if (scenario != "Unavailable")
                            {
                                Check(context.Input.RegisterAction(ControlGalleryPages.Expedition()).Succeeded, "Register the Gallery shortcut.");
                                Check(context.Input.RegisterAction(ControlGalleryPages.Ledger()).Succeeded, "Register the Gallery conflict action.");
                            }
                            if (sample) Check(context.Ui.AddThemeCatalogue(declaration, Read).Succeeded, "Load the real Gallery theme catalogue.");
                            var request = ControlGalleryPages.Create(page, sample ? FoundationPages.SurvivalTheme : null);
                            using var surface = OpenControlGallery(context.Ui, request, owner, page, scenario, guest);
                            context.Ui.CompletePendingThemeRegistrations();
                            ConfigureControlGallery(context, surface, scenario);

                            foreach (var hud in new[] { false, true })
                                foreach (var size in dimensions)
                                    foreach (var scale in new[] { 1, 2 })
                                        foreach (var highContrast in new[] { false, true })
                                            foreach (var reducedMotion in new[] { false, true })
                                            {
                                                var route = guest ? "guest-received" : "host-local";
                                                var theme = sample ? "survival" : "kit";
                                                var scheme = hud ? "hud" : "paper";
                                                var contrast = highContrast ? "high" : "standard";
                                                var motion = reducedMotion ? "reduced" : "full";
                                                var id = FormattableString.Invariant(
                                                    $"{page}/{theme}/{scheme}/{contrast}/{size.Width}x{size.Height}/{scale}/{route}/{motion}/{scenario}");
                                                Check(context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast, scale, reducedMotion)).Succeeded,
                                                    "Apply the Gallery viewer's accessibility preferences.");
                                                var paint = scenario is "Hovered" or "Focused" or "Pressed" ? scenario : "Rest";
                                                var viewport = new UiCaptureViewport(size.Width, size.Height);
                                                var screenScale = (float)Math.Sqrt(size.Width / 1920d * size.Height / 1080d);
                                                cases.Add(id, CaptureCase(id, surface, viewport, paint, hud, screenScale * scale,
                                                    node => new { paint = GoldenNode(node), node.State, node.KeyLabel }));
                                            }
                        }
            return JsonSerializer.Serialize(new
            {
                schema = 1,
                source = "FakeUiService K2 control captures",
                fixture = "ControlGalleryPages",
                transport = "Guest views are decoded from the K2 codec and admitted with the test publisher envelope.",
                palettePreview = "Both source schemes are exercised on interactive windows.",
                unavailable = "A missing named registration is observable on the receiving guest.",
                cases,
            }, new JsonSerializerOptions(CaptureJson) { WriteIndented = true, NewLine = "\n" }) + "\n";
        }

        private static FakeUiSurface OpenControlGallery(FakeUiService viewer, UiSurfaceRequest request, string owner,
            ControlGalleryPage page, string scenario, bool guest)
        {
            try { return guest ? ReceiveControlGallery(viewer, request, owner) : Make(viewer, request); }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException($"Could not open K2 Gallery {page}/{scenario} for {owner}. {error.Message}", error);
            }
        }

        private static FakeUiSurface ReceiveControlGallery(FakeUiService viewer, UiSurfaceRequest request, string owner)
        {
            var theme = request.Theme == null ? null : ThemeForWire(viewer, request.Theme);
            var envelope = new UiWireEnvelope(owner, new byte[32], 1, theme?.Id, theme == null ? new byte[32] : theme.Digest.Span);
            var content = UiWireCodec.ProjectInputDefaults(request.Content, ControlGalleryPages.DefaultBindings);
            var writer = new WorldWireWriter();
            UiWireCodec.Write(writer, content, "gallery-epoch", envelope);
            var reader = new WorldWireReader();
            reader.Reset(writer.Segment.ToArray());
            var tree = UiWireCodec.ReadWithEnvelope(reader, (_, _) => { }, "gallery-epoch", out var received);
            UiWireCodec.End(reader);
            var remote = new UiSurfaceRequest("remote-1", request.Title, request.Body, request.Kind, request.Width, request.Height, tree);
            Check(tree != null && received?.OwnerPackageId == owner &&
                received.ThemeId == theme?.Id, "The test transport retains the fixture's authored tree and authenticated theme.");
            return Make(CreateReceivedSurface(viewer, remote, received!));
        }

        private static string[] ControlScenarios(ControlGalleryPage page) => page switch
        {
            ControlGalleryPage.Buttons => new[] { "Rest", "Hovered", "Focused", "Pressed" },
            ControlGalleryPage.Fields => new[] { "Rest", "Focused", "DropdownOpen" },
            ControlGalleryPage.Selections => new[] { "Rest", "Focused", "OtherTabs" },
            ControlGalleryPage.Bindings => new[] { "Rest", "Listening", "Reserved", "Conflict", "Unavailable" },
            ControlGalleryPage.HelpAndTiles => new[] { "Rest", "Focused", "HoverElapsed" },
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        private static void ConfigureControlGallery(FakeModContext context, FakeUiSurface surface, string scenario)
        {
            switch (scenario)
            {
                case "DropdownOpen":
                    SetDropdownOpen(surface, "gallery-sector");
                    break;
                case "OtherTabs":
                    Check(surface.SelectTab("gallery-tab-bar", "maps").Succeeded &&
                        surface.SelectTab("gallery-tab-rail", "arrivals").Succeeded, "Switch the two live tab layouts.");
                    break;
                case "Listening":
                    SetBindingListening(surface, ExpeditionKeyboard);
                    break;
                case "Reserved":
                    Check(surface.CaptureBinding(ExpeditionKeyboard, InputBinding.Key("F8")).ErrorCode == ModErrorCode.InvalidArgument,
                        "Reserved capture leaves the default binding in place.");
                    break;
                case "Conflict":
                    Check(surface.CaptureBinding(ExpeditionKeyboard, InputBinding.Key("K")).Succeeded &&
                        surface.CaptureBinding("gallery-bind-gamepad", InputBinding.GamepadButton(InputGamepadButton.LeftShoulder)).Succeeded,
                        "Both receiving device fields can show a named action conflict.");
                    break;
                case "HoverElapsed":
                    SetGalleryTooltipHover(surface, "gallery-tooltip-description");
                    context.Time.AdvanceFrame(0.45f);
                    context.Time.AdvanceFrame(0.001f);
                    break;
            }
        }
    }
}
