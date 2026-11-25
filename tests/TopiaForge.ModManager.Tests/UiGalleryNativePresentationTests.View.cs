namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryNativePresentationTests
    {
        internal const string NativeCases = """
            using System;
            using System.IO;
            using System.Linq;
            using System.Text.Json;
            using TMPro;
            using TopiaForge.ModManager;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.UiGallery.Pages;
            using UnityEngine;
            using UnityEngine.UI;

            public static class NativePresentationCases
            {
                private static readonly UiThemeDescriptor Theme = UiThemePackParser.Default;
                private static readonly UiThemeColor Dark = new(10, 20, 34);
                private static readonly UiThemeColor Ink = new(245, 245, 245);

                public static void Run()
                {
                    Overlay();
                    Stinger();
                    Toast();
                    Console.WriteLine("Native presentation trees passed for scrim, panel, actual TMP paint, event attribution, and tone glyph.");
                }

                private static void Overlay()
                {
                    var canvas = Canvas(3100, pointer: false);
                    var root = Rect(canvas.transform, "Overlay", 0, 0, 1920, 1080);
                    var scrim = Rect(root, "Overlay scrim", 8, 16, 1896, 1050);
                    var scrimPaint = scrim.Attach(new Image { color = new Color(Dark, .72f) });
                    var scrimMotion = scrim.Attach(new CanvasGroup());
                    var panel = Rect(root, "Overlay panel", 620, 360, 680, 250);
                    Plate(panel, "Surface", Dark, radius: 22f);
                    Plate(panel, "Boundary", Ink, radius: 22f, border: 3f);
                    var content = Rect(panel, "Overlay content", 640, 380, 640, 210);
                    var fixture = PresentationPages.Overlay(PresentationOverlay.Passive, static () => { });
                    var title = Text(content, "Label", fixture.Title, 650, 388, 560, 55, 38);
                    var body = Text(content, "Body", fixture.Body, 650, 446, 560, 56, 32);
                    var composition = Rect(content, "Composition", 650, 508, 560, 56);
                    var node = Rect(composition, "UiText", 650, 508, 560, 56);
                    var widget = Rect(node, "Widget", 650, 508, 560, 56);
                    Text(widget, "Label", ((UiText)fixture.Content!).Text, 650, 508, 560, 56, 28);
                    var overlay = new TopiaForgeOverlay(root, content, (RectTransform)title.transform, fixture.Dismissible)
                    { Covered = true };

                    var recorder = Read(canvas);
                    Check(recorder.TryCaptureOverlay(fixture, overlay, composition, body, out var tree),
                        "A settled native overlay records its actual panel, scrim, title, body and authored composition.");
                    var scrimNode = tree!.Nodes.Single(node => node.Kind == "OverlayScrim");
                    var panelNode = tree.Nodes.Single(node => node.Kind == "Overlay");
                    Check(scrimNode is { X: 8, Y: 16, Width: 1896, FillOpacity: .72f, Decorative: true } &&
                        scrimNode.FillColor == Dark.ToString() && panelNode is { Width: 680, CornerRadiusPixels: 22, BorderWidthPixels: 3 } &&
                        recorder.PresentationOrder == 3100 && recorder.OverlayCovered == true &&
                        recorder.OverlayDismissible == true && recorder.PresentationTakesInput == false,
                        "Overlay evidence comes from sampled geometry, alpha, border and actual layer state.");
                    Check(tree.Nodes.Any(node => node.Kind == "SurfaceTitle" && node.Text == fixture.Title) &&
                        tree.Nodes.Any(node => node.Kind == "SurfaceBody" && node.Text == fixture.Body) &&
                        tree.Nodes.Any(node => node.Kind == nameof(UiText) && node.Text == ((UiText)fixture.Content!).Text),
                        "The overlay records both native role labels and reuses the declarative tree traversal.");

                    scrimMotion.alpha = .4f;
                    Check(!Read(canvas).TryCaptureOverlay(fixture, overlay, composition, body, out _),
                        "An entering overlay cannot produce a settled screenshot receipt.");
                    scrimMotion.alpha = 1;
                    panel.anchoredPosition = new Vector2(0, -10);
                    Check(!Read(canvas).TryCaptureOverlay(fixture, overlay, composition, body, out _),
                        "A moving overlay panel cannot produce a settled screenshot receipt.");
                    panel.anchoredPosition = default;
                    body.text = "Private native body value";
                    scrimPaint.color = new Color(Dark, .41f);
                    recorder = Read(canvas);
                    Check(recorder.TryCaptureOverlay(fixture, overlay, composition, body, out tree) &&
                        tree!.Nodes.Single(node => node.Kind == "OverlayScrim").FillOpacity == .41f &&
                        tree.Nodes.Single(node => node.Kind == "SurfaceBody").Text.Length == 0 &&
                        tree.CheckAccessibility().Count > 0,
                        "Incorrect actual scrim paint is preserved while unlisted native body text is redacted.");
                }

                private static void Stinger()
                {
                    var canvas = Canvas(3300, pointer: false);
                    var root = Rect(canvas.transform, "Stinger", 510, 240, 900, 155);
                    var fixture = PresentationPages.Stinger(UiPaintRole.Warning);
                    var id = PresentationPages.StingerButtonId(fixture.Role);
                    var label = fixture.Title + ". " + fixture.Subtitle;
                    var view = new TopiaForgeStinger(root, label);
                    Plate(root, "Surface", Dark, radius: 20);
                    Rect(root, "Glyph", 540, 276, 25, 26).Attach(new Image { sprite = UiGlyph.Warning, color = new Color(Ink) });
                    Text(root, "Title", fixture.Title, 585, 268, 700, 56, 50);
                    Text(root, "Subtitle", fixture.Subtitle, 585, 326, 700, 50, 38);
                    var recorder = Read(canvas);
                    root.gameObject.activeSelf = false;
                    Check(!recorder.TryCaptureStinger(id, fixture, view, 2.45d, out _),
                        "A hidden measurement or waiting stinger cannot be recorded.");
                    root.gameObject.activeSelf = true;
                    Check(recorder.TryCaptureStinger(id, fixture, view, 2.45d, out var result) &&
                        result!.Nodes.Single(node => node.Id == id) is { Announcement: var actual, RemainingSeconds: 2.45d } &&
                        actual == label && result.UsesDeclaredStingerFaces == false &&
                        result.Nodes.Any(node => node.Kind == "Glyph" && node.SemanticLabel == "Warning"),
                        "The visible stinger records the authored announcement, native glyph, and remaining time.");
                    var digest = Enumerable.Repeat((byte)0x71, 32).ToArray();
                    var requested = UiThemePackParser.Parse(System.Text.Encoding.UTF8.GetBytes("{\"schema\":1,\"id\":\"fixture-event-palette\"}")).Value!;
                    var envelope = new UiWireEventEnvelope("dev.topiaforge.multiplayer-acceptance", digest, requested.Id, requested.Digest.Span);
                    VerifyJson(recorder, result!, id, envelope, legacy: false, json =>
                        Check(json.GetProperty("delivery").GetString() == "authenticated-remote" &&
                            json.GetProperty("packageSha256").GetString() == Convert.ToHexString(digest) &&
                            json.GetProperty("requestedDescriptorSha256").GetString() == Convert.ToHexString(requested.Digest.Span) &&
                            json.GetProperty("stingerFontChain").GetString() == "kit",
                            "The stinger receipt carries the verified event envelope alongside effective native paint."));
                }

                private static void Toast()
                {
                    var canvas = Canvas(3500, pointer: false);
                    var root = Rect(canvas.transform, "Toast", 1420, 800, 420, 62);
                    var toast = new TopiaForgeContainer(root);
                    var message = PresentationPages.ToastMessage(UiTone.Success);
                    Plate(root, "Fill", Dark, radius: 14f);
                    Plate(root, "Ring", Ink, radius: 14f, border: 2f);
                    var label = Text(root, "Label", message, 1460, 810, 350, 50, 26);
                    var id = PresentationPages.ToastStackId;
                    Check(Read(canvas).TryCaptureToast(id, message, UiTone.Success, toast, out var result) &&
                        result!.CheckAccessibility().Any(issue => issue.Kind == UiAccessibilityIssueKind.ColorOnly),
                        "A stock toast that is missing its actual tone glyph reports an accessibility failure.");
                    var glyph = Rect(root, "Tone glyph", 1425, 818, 24, 24).Attach(new Image { sprite = UiGlyph.Cross, color = new Color(Ink) });
                    Check(Read(canvas).TryCaptureToast(id, message, UiTone.Success, toast, out result) &&
                        result!.CheckAccessibility().Any(issue => issue.Kind == UiAccessibilityIssueKind.ColorOnly),
                        "A stale glyph from a pooled toast does not satisfy the current tone's cue.");
                    glyph.sprite = UiGlyph.Check;
                    var recorder = Read(canvas, unitScale: 1.5f);
                    Check(recorder.TryCaptureToast(id, message, UiTone.Success, toast, out result) &&
                        result!.Nodes.Any(node => node.Kind == "Glyph" && node.SemanticLabel == "Check") &&
                        !result.CheckAccessibility().Any(issue => issue.Kind == UiAccessibilityIssueKind.ColorOnly),
                        "Only the actual matching cue clears the color-only failure.");
                    VerifyJson(recorder, result!, id, null, legacy: true, json =>
                        Check(json.GetProperty("delivery").GetString() == "legacy-targeted" &&
                            json.GetProperty("packageSha256").ValueKind == JsonValueKind.Null &&
                            json.GetProperty("requestedDescriptorSha256").ValueKind == JsonValueKind.Null &&
                            json.GetProperty("pixelsPerUnit").GetSingle() == 1.5f,
                            "The legacy toast receipt carries its actual view scale and no invented authenticated envelope."));
                    label.text = "Private pooled view";
                    Check(Read(canvas).TryCaptureToast(id, message, UiTone.Success, toast, out result) &&
                        result!.Nodes.Single(node => node.Kind == "ToastLabel").Text.Length == 0,
                        "A recycled native toast cannot disclose unrelated text.");
                }

                private static RectTransform Rect(Transform? parent, string name, float x, float y, float width, float height) =>
                    new RectTransform(parent, name, x, y, width, height);

                private static Canvas Canvas(int order, bool pointer)
                {
                    var root = Rect(null, "Canvas", 0, 0, 1920, 1080);
                    root.Attach(new GraphicRaycaster { enabled = pointer });
                    return root.Attach(new Canvas { sortingOrder = order });
                }

                private static UiGalleryNativeTree Read(Canvas canvas, float unitScale = 1f) =>
                    new UiGalleryNativeTree(canvas, new TopiaForgeResolvedTheme(Theme), new TopiaForgeEffectiveAccessibility(1f),
                        false, 2.1, "native-fixture", unitScale);

                private static TMP_Text Text(Transform parent, string name, string value, float x, float y, float width, float height, float size) =>
                    Rect(parent, name, x, y, width, height).Attach(new TMP_Text { text = value, font = new TMP_FontAsset("Fixture"),
                        color = new Color(Ink), fontSize = size });

                private static void Plate(RectTransform root, string name, UiThemeColor color, float radius, float border = 0f)
                {
                    var frame = root.Frame;
                    Rect(root, name, frame.X, frame.Y, frame.Width, frame.Height).Attach(new TopiaForgeRoundedImage
                    { color = new Color(color), ResolvedCornerRadius = radius, ResolvedBorderWidth = border });
                }

                private static void VerifyJson(UiGalleryNativeTree recorder, UiTreeCapture tree, string id,
                    UiWireEventEnvelope? envelope, bool legacy, Action<JsonElement> check)
                {
                    var folder = Directory.CreateTempSubdirectory("TopiaForgeNativeEvent-");
                    try
                    {
                        UiGalleryCaptureJson.SaveEvent(folder.FullName, id, "remote-guest", tree, recorder, envelope, legacy);
                        var path = Directory.EnumerateFiles(folder.FullName, "*.json", SearchOption.AllDirectories).Single();
                        using var json = JsonDocument.Parse(File.ReadAllText(path));
                        check(json.RootElement);
                    }
                    finally { folder.Delete(recursive: true); }
                }

                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException(message);
                }
            }
            """;
    }
}
