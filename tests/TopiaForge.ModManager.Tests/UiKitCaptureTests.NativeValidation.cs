namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private const string NativeValidationCases = """
            using System;
            using System.Linq;
            using TMPro;
            using TopiaForge.ModManager;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UI;

            public static partial class NativeClipCases
            {
                private static void CaptureThePaintedValidationGlyph(Canvas canvas, RectTransform window)
                {
                    const string message = "Choose a route.";
                    var root = At(window, 100, 100, 260, 120);
                    var field = At(root, 100, 100, 260, 40);
                    var ring = At(field, 100, 100, 260, 40).Attach(new TopiaForgeRoundedImage(canvas)
                    {
                        ResolvedBorderWidth = 3,
                    });
                    ring.transform.gameObject.name = "Ring";

                    OwnerUiService.RenderFixtureValidation(new TopiaForgeContainer(root), message);
                    var warning = root.GetComponentsInChildren<Image>(true).Single(image => image.name == "Validation glyph");
                    var label = root.GetComponentsInChildren<TMP_Text>(true).Single();
                    var capture = new UiGalleryNativeTree(canvas).ReadInputError(root, field, message);
                    Check(capture != null && warning.sprite == TopiaForgeSprites.Glyph(UiGlyph.Warning),
                        "The decorated error paints and records the exact Warning sprite.");
                    var nodes = capture!.Nodes;
                    Check(nodes.Single(node => node.Kind == "FieldErrorOutline").BorderWidthPixels == 3f &&
                        nodes.Single(node => node.Kind == "FieldErrorGlyph") is { X: 100, Y: 148, Width: 18, Height: 18, IsRendered: true } &&
                        nodes.Single(node => node.Kind == "FieldErrorMessage").Text == message &&
                        label.margin.x == 24f && !warning.raycastTarget && capture.CheckAccessibility().Count == 0,
                        "The Warning glyph and three-unit outline preserve the caption and its accessible geometry.");

                    warning.sprite = TopiaForgeSprites.Glyph(UiGlyph.Cross);
                    Check(new UiGalleryNativeTree(canvas).ReadInputError(root, field, message) == null,
                        "A different non-null validation sprite is refused by the native recorder.");
                }
            }

            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    internal static void RenderFixtureValidation(TopiaForgeContainer container, string message) =>
                        RenderValidationMessage(container, message, UiPaintRole.Surface);
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal enum TopiaForgeTextStyle { Caption }
                internal enum TopiaForgeTone { Danger }

                internal sealed class TopiaForgeContainer(RectTransform root)
                {
                    internal TopiaForgeLabel Label(string message, TopiaForgeTextStyle style)
                    {
                        var rect = new RectTransform(root, new UiCaptureRect(root.bounds.X, root.bounds.Y + 48f, root.bounds.Width, 30f));
                        rect.Attach(new TMP_Text { text = message });
                        return new TopiaForgeLabel(rect);
                    }
                }

                internal sealed class TopiaForgeLabel(RectTransform rect)
                {
                    internal GameObject Go => rect.gameObject;
                    internal RectTransform Rect => rect;
                    internal TopiaForgeResolvedTheme ResolvedTheme { get; } = new();
                    internal TopiaForgeLabel Tone(TopiaForgeTone tone) => this;
                    internal void SetBackdrop(UiPaintRole role) { }
                }

                internal sealed class TopiaForgeResolvedTheme
                {
                    internal UiThemeColor ForegroundOn(UiPaintRole role, UiTone tone) => new(128, 0, 0);
                }

                internal static class TopiaForgeAnchors
                {
                    internal static void Place(RectTransform rect, float left, float top, float width, float height)
                    {
                        var parent = (RectTransform)rect.parent!;
                        rect.bounds = new UiCaptureRect(parent.bounds.X + left, parent.bounds.Y + top, width, height);
                    }
                }

                internal static class TopiaForgeSprites
                {
                    private static readonly object Warning = new();
                    private static readonly object Cross = new();

                    internal static object Glyph(UiGlyph glyph) => glyph == UiGlyph.Warning ? Warning : Cross;
                }
            }
            """;
    }
}
