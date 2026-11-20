using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void NativeCaptureDetectsFixedClippingAndMissingHits()
        {
            var root = Program.FindRepoRoot();
            const string native = "src/TopiaForge.ModManager/UiGalleryRecorder/";
            const string testing = "src/TopiaForge.Mods.Testing/";
            var recorder = Members(native + "UiGalleryNativeTree.cs", "Add", "Observe", "Clip", "PaddedMaskBounds", "Text",
                "Bounds", "ScreenPoint", "VerticalScale", "AtPoints", "ClipContains", "State", "Observation");
            var controls = Members(native + "UiGalleryNativeTree.Controls.cs", "HitAndFocus", "Hit", "CanReceivePointer",
                "HitGraphic", "HasRectangularRaycast", "Flags", "ObserveListSelection");
            var compilationSources = new[] { native + "UiGalleryCaptureClip.cs", testing + "UiCaptureViewport.cs",
                testing + "UiCapturedNode.cs", testing + "UiTreeCapture.cs" }.Select(path => File.ReadAllText(Path.Combine(root, path)))
                .Append(NativeCaptureBoundary.Replace("/* production recorder */", recorder + "\n" + controls))
                .Append(NativeClipCases).Append(NativeScrollCases);
            var options = new CSharpParseOptions(preprocessorSymbols: new[] { "TOPIAFORGE_NATIVE_CAPTURE" });
            var sources = compilationSources.Select(source => CSharpSyntaxTree.ParseText(source, options));
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = platform.Append(typeof(UiNode).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager.Tests", sources, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var output = new MemoryStream();
            var emit = compilation.Emit(output);
            Check(emit.Success, "The production native recorder compiles against the capture input boundary. " + string.Join("\n",
                emit.Diagnostics.Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            try { Assembly.Load(output.ToArray()).GetType("NativeClipCases")!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException failure) { throw failure.InnerException!; }

            string Members(string path, params string[] names)
            {
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path))).GetRoot();
                return string.Join("\n", syntax.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(member =>
                    member is MethodDeclarationSyntax method && names.Contains(method.Identifier.Text) ||
                    member is StructDeclarationSyntax type && names.Contains(type.Identifier.Text)));
            }
        }

        private const string NativeClipCases = """
            using System;
            using System.Linq;
            using TMPro;
            using TopiaForge.ModManager;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.UI;

            public static partial class NativeClipCases
            {
                public static void Run()
                {
                    var canvas = CanvasForFixture();
                    var window = At(canvas.transform, 50, 50, 400, 400);
                    var content = Scroll(window, 100, 100, 300, 300, 1700);
                    var stack = Mask(content, 120, 210, 250, 65);
                    var partial = At(stack, 140, 249, 100, 38).Attach(new TMP_Text());
                    var capture = Text(canvas, partial);
                    Check(capture.Nodes.Single().IsRendered && Clipped(capture), "Outer Window scroll cannot excuse partial fixed Stack clipping.");
                    var boundaryStack = Mask(content, 120, 355, 250, 75);
                    var mixedClip = At(boundaryStack, 140, 365, 100, 80).Attach(new TMP_Text());
                    Check(Clipped(Text(canvas, mixedClip)), "A vertical scroll and a fixed Stack can both clip the same protected node.");

                    var buriedStack = Mask(content, 120, 470, 250, 65);
                    var buriedText = At(buriedStack, 140, 541, 100, 38).Attach(new TMP_Text());
                    buriedText.canvasRenderer.cull = true;
                    buriedText.textInfo.characterCount = 0;
                    capture = Text(canvas, buriedText);
                    Check(!capture.Nodes.Single().IsRendered && capture.Nodes.Single().Text == "Authored" && Clipped(capture),
                        "An intended label entirely clipped by Stack stays diagnostic even when culled and outside Window scroll.");

                    var buriedControl = At(buriedStack, 140, 541, 100, 40);
                    var buriedButton = buriedControl.Attach(new Selectable());
                    var buriedGraphic = buriedControl.Attach(new Image(canvas));
                    buriedGraphic.canvasRenderer.cull = true;
                    capture = Hits(canvas, buriedButton);
                    Check(!capture.Nodes.Single().IsRendered && capture.Nodes.Single().Width == 100 && Clipped(capture),
                        "A wholly clipped hit retains Graphic bounds and fails the captured accessibility checks.");

                    var scrollingStack = Mask(content, 120, 1380, 220, 70);
                    var offscreen = At(scrollingStack, 130, 1390, 120, 35).Attach(new TMP_Text());
                    offscreen.canvasRenderer.cull = true;
                    offscreen.textInfo.characterCount = 0;
                    capture = Text(canvas, offscreen);
                    Check(!capture.Nodes.Single().IsRendered && capture.Nodes.Single().Clipped && !Clipped(capture),
                        "A valid Stack below the screen remains reachable through its Window scroll viewport.");
                    var outsideBody = At(content, 120, 1820, 140, 48).Attach(new TMP_Text());
                    Check(Clipped(Text(canvas, outsideBody)), "A ScrollRect cannot excuse content beyond its scrollable body.");
                    var side = At(content, 75, 220, 80, 50).Attach(new TMP_Text());
                    Check(Clipped(Text(canvas, side)), "A vertical viewport cannot excuse horizontal clipping.");

                    var stable = At(content, 180, 160, 100, 40);
                    var button = stable.Attach(new Selectable());
                    var hit = stable.Attach(new Image(canvas));
                    var movingBody = At(stable, 186, 168, 88, 37).Attach(new Image(canvas) { raycastTarget = false });
                    button.targetGraphic = movingBody;
                    capture = Hits(canvas, button);
                    Check(capture.Nodes.Single() is { IsRendered: true, X: 180, Y: 160, Width: 100, Height: 40 } &&
                        capture.CheckAccessibility().Count == 0 && hit.LastPoint == new Vector2(230, 900),
                        "Press paint cannot move the hit area and raycasts are checked at the actual Graphic.");

                    hit.isActiveAndEnabled = false;
                    capture = Hits(canvas, button);
                    Check(Invalid(capture) && capture.Nodes.Single() is { IsRendered: false, Width: 0 },
                        "A disabled Graphic or non-raycast press body cannot fabricate a hit target from the Selectable.");
                    hit.isActiveAndEnabled = true;
                    hit.allowsPointer = false;
                    Check(Invalid(Hits(canvas, button)), "Blocked pointer input is a diagnostic for an enabled control.");
                    button.interactable = false;
                    Check(Hits(canvas, button).Nodes.Single() is { IsRendered: true, Disabled: true, Width: 100 },
                        "An intentionally disabled row retains its affordance when its CanvasGroup blocks pointer input.");
                    button.interactable = true;
                    hit.raycastTarget = false;
                    movingBody.raycastTarget = true;
                    capture = Hits(canvas, button);
                    Check(capture.Nodes.Single() is { X: 186, Y: 168, Height: 37 } && Invalid(capture),
                        "A routed child Graphic supplies its real dimensions and fails below the minimum hit size.");

                    var fixedMask = Mask(content, 110, 215, 260, 145);
                    var innerScroll = Scroll(fixedMask, 120, 230, 205, 80, 600);
                    var revealLater = At(innerScroll, 140, 450, 100, 35).Attach(new TMP_Text());
                    Check(!Clipped(Text(canvas, revealLater)), "An inner vertical scroll retains a reachable aperture inside a fixed ancestor.");
                    var whichCannotReveal = Scroll(fixedMask, 125, 380, 205, 80, 600);
                    var invisibleAperture = At(whichCannotReveal, 135, 550, 100, 35).Attach(new TMP_Text());
                    Check(Clipped(Text(canvas, invisibleAperture)), "A fixed ancestor covering the entire inner scroll viewport cannot be excused.");

                    var ring = At(stable, 180, 160, 100, 40).Attach(new Image(canvas) { a = 1f });
                    var required = UiCaptureNodeFlags.RequiresNonColorCue;
                    var present = required | UiCaptureNodeFlags.HasNonColorCue;
                    var selected = UiCaptureNodeFlags.Selected;
                    Check(UiGalleryNativeTree.ListSelection(UiCaptureNodeFlags.None, ring) == (selected | present),
                        "A visible authored ring supplies its own selection cue.");
                    Check(UiGalleryNativeTree.ListSelection(required, ring) == (selected | required) &&
                        UiGalleryNativeTree.ListSelection(present, ring) == (selected | present),
                        "An authored selection ring cannot hide a missing collapsed press cue.");
                    ring.canvasRenderer.cull = true;
                    Check(UiGalleryNativeTree.ListSelection(UiCaptureNodeFlags.None, ring) == UiCaptureNodeFlags.None,
                        "A culled authored ring supplies no selection fact.");

                    ScrollAperturesMustRevealNativeGeometry(canvas, window);
                }

                private static Canvas CanvasForFixture()
                {
                    var root = new RectTransform(null, new UiCaptureRect(0, 0, 1920, 1080));
                    root.Attach(new GraphicRaycaster());
                    return root.Attach(new Canvas());
                }
                private static RectTransform At(Transform parent, float x, float y, float width, float height) =>
                    new RectTransform(parent, new UiCaptureRect(x, y, width, height));
                private static RectTransform Mask(Transform parent, float x, float y, float width, float height)
                {
                    var rect = At(parent, x, y, width, height);
                    rect.Attach(new RectMask2D());
                    return rect;
                }
                private static RectTransform Scroll(Transform parent, float x, float y, float width, float height, float contentHeight, float offset = 0)
                {
                    var root = At(parent, x, y, width, height);
                    var viewport = Mask(root, x, y, width, height);
                    var content = At(viewport, x, y - offset, width, contentHeight);
                    root.Attach(new ScrollRect { viewport = viewport, content = content, vertical = true });
                    return content;
                }
                private static UiTreeCapture Text(Canvas canvas, TMP_Text label) => new UiGalleryNativeTree(canvas).ReadText(label);
                private static UiTreeCapture Hits(Canvas canvas, Selectable button) => new UiGalleryNativeTree(canvas).ReadHit(button);
                private static bool Clipped(UiTreeCapture tree) => tree.CheckAccessibility().Any(issue => issue.Kind == UiAccessibilityIssueKind.ClippedContent);
                private static bool Invalid(UiTreeCapture tree) => tree.CheckAccessibility().Any(issue => issue.Kind == UiAccessibilityIssueKind.InvalidLayout);
                private static void Check(bool valid, string reason) { if (!valid) throw new InvalidOperationException(reason); }
            }
            """;

        private const string NativeCaptureBoundary = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using TMPro;
            using TopiaForge.ModManager;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed class UiGalleryNativeTree
                {
                    private const int Height = 1080;
                    private const float PixelsPerUnit = 1;
                    private readonly Canvas canvas;
                    private readonly Camera? camera = null;
                    private readonly UiCaptureRect screen = new(0, 0, 1920, 1080);
                    private readonly Vector3[] corners = new Vector3[4];
                    private readonly List<Observation> observations = new();
                    private readonly List<object> fonts = new();
                    private readonly UiThemeDescriptor Descriptor = UiThemePackParser.Default;
                    private readonly (float UiScale, bool Other) Accessibility = (1, false);

                    internal UiGalleryNativeTree(Canvas canvas) { this.canvas = canvas; }
                    internal UiTreeCapture ReadText(TMP_Text label, bool passive = false)
                    {
                        if (!Text("/text", null, passive ? nameof(UiText) : "Text", label, "Authored", UiTextStyle.Body, new UiThemeColor(255, 255, 255)))
                            throw new InvalidOperationException("The recorder discarded the authored label.");
                        return Result();
                    }
                    internal static UiCaptureNodeFlags ListSelection(UiCaptureNodeFlags flags, Image ring) =>
                        ObserveListSelection(flags, ring);
                    internal UiTreeCapture ReadHit(Selectable selectable)
                    {
                        HitAndFocus("/button", selectable, "Authored", null);
                        return Result();
                    }
                    internal UiTreeCapture ReadFocus(RectTransform ring)
                    {
                        Add("/focus", null, "FocusRing", ring, State(UiCaptureNodeFlags.FocusIndicator, "Authored"), default);
                        return Result();
                    }
                    private UiTreeCapture Result() => new UiTreeCapture(observations.Select(item => new UiCapturedNode(item.Path,
                        item.Id, item.Kind, item.Bounds, item.Content, item.Paint, "kit", false, item.Rendered, item.Clipped, false)).ToList(), false, 40);
                    private static Graphic EffectiveColor(Graphic graphic) => graphic;
                    private static UiThemeColor ToRgb(Graphic graphic) => new UiThemeColor(0, 0, 0);
                    private static T? Named<T>(RectTransform rect, string name) where T : Component => null;

                    /* production recorder */
                }
                internal sealed record UiGalleryFontFact(string Path, string Family, string Style, float Size, float Ascender, float Descender, float Height);
                internal static class UiGalleryCaptureFixture
                {
                    internal static string? SafeDisplayText(string text, string authored, bool uppercase) => text == authored ? authored : null;
                    internal static string? SafeAuthoredFieldValue(string value, string text, string authored) => value == authored && text == authored ? authored : null;
                }
            }

            namespace UnityEngine
            {
                internal readonly record struct Vector2(float x, float y)
                {
                    internal static float Distance(Vector2 a, Vector2 b) => MathF.Sqrt((a.x-b.x)*(a.x-b.x) + (a.y-b.y)*(a.y-b.y));
                }
                internal readonly record struct Vector3(float x, float y, float z = 0)
                {
                    internal static Vector3 zero => new();
                    internal static Vector3 up => new(0, 1);
                }
                internal readonly record struct Vector4(float x, float y, float z, float w) { internal static Vector4 zero => new(); }
                internal readonly record struct Rect(float xMin, float yMin, float width, float height)
                {
                    internal float xMax => xMin + width;
                    internal float yMax => yMin + height;
                }
                internal static class Mathf
                {
                    internal static float Abs(float value) => MathF.Abs(value);
                    internal static float Min(float a, float b) => MathF.Min(a, b);
                    internal static float Max(float a, float b) => MathF.Max(a, b);
                }
                internal sealed class Camera { }
                internal static class RectTransformUtility { internal static Vector2 WorldToScreenPoint(Camera? camera, Vector3 point) => new(point.x, point.y); }
                internal sealed class GameObject { internal bool activeInHierarchy = true; }
                internal class Transform
                {
                    private readonly List<Component> components = new();
                    internal readonly Transform? parent;
                    internal readonly GameObject gameObject = new();
                    internal Transform(Transform? parent) { this.parent = parent; }
                    internal T Attach<T>(T value) where T : Component { value.transform = this; components.Add(value); return value; }
                    internal T? GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault();
                    internal T[] GetComponents<T>() where T : Component => components.OfType<T>().ToArray();
                    internal bool TryGetComponent<T>(out T value) where T : Component { value = GetComponent<T>()!; return value != null; }
                    internal bool IsChildOf(Transform? ancestor)
                    {
                        for (var target = this; target != null; target = target.parent) if (target == ancestor) return true;
                        return false;
                    }
                }
                internal sealed class RectTransform : Transform
                {
                    internal readonly UiCaptureRect bounds;
                    internal RectTransform(Transform? parent, UiCaptureRect bounds) : base(parent) { this.bounds = bounds; }
                    internal Rect rect => new(0, 0, bounds.Width, bounds.Height);
                    internal Vector3 TransformPoint(Vector3 local) => new(bounds.X + local.x, 1080 - bounds.Bottom + local.y);
                    internal void GetWorldCorners(Vector3[] corners)
                    {
                        corners[0] = TransformPoint(new Vector3(0, 0));
                        corners[1] = TransformPoint(new Vector3(0, bounds.Height));
                        corners[2] = TransformPoint(new Vector3(bounds.Width, bounds.Height));
                        corners[3] = TransformPoint(new Vector3(bounds.Width, 0));
                    }
                }
                internal class Component
                {
                    internal Transform transform = null!;
                    internal T? GetComponent<T>() where T : Component => transform.GetComponent<T>();
                    internal T[] GetComponents<T>() where T : Component => transform.GetComponents<T>();
                }
                internal class Behaviour : Component { internal bool isActiveAndEnabled = true; }
                internal sealed class Canvas : Behaviour { }
                internal sealed class CanvasRenderer { internal bool cull; }
            }

            namespace UnityEngine.UI
            {
                internal sealed class RectMask2D : Behaviour { internal Vector4 padding; }
                internal sealed class ScrollRect : Behaviour { internal bool vertical; internal RectTransform? viewport, content; }
                internal sealed class GraphicRaycaster : Behaviour { }
                internal class Selectable : Behaviour
                {
                    internal Graphic? targetGraphic;
                    internal bool interactable = true;
                    internal bool IsInteractable() => interactable;
                }
                internal class Graphic : Behaviour
                {
                    internal readonly CanvasRenderer canvasRenderer = new();
                    internal Canvas? canvas;
                    internal RectTransform rectTransform => (RectTransform)transform;
                    internal Vector4 raycastPadding;
                    internal bool raycastTarget = true, allowsPointer = true;
                    internal int depth = 1;
                    internal float a;
                    internal Vector2 LastPoint { get; private set; }
                    internal bool Raycast(Vector2 point, Camera? camera) { LastPoint = point; return allowsPointer; }
                }
                internal class Image : Graphic
                {
                    internal float alphaHitTestMinimumThreshold;
                    internal Image(Canvas canvas) { this.canvas = canvas; }
                }
                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal float? ResolvedCornerRadius = null;
                    internal float ResolvedBorderWidth;
                    internal TopiaForgeRoundedImage(Canvas canvas) : base(canvas) { }
                }
            }

            namespace TMPro
            {
                [Flags] internal enum FontStyles { UpperCase = 1 }
                internal sealed class TMP_Text : Graphic
                {
                    internal string text = "Authored";
                    internal FontStyles fontStyle;
                    internal readonly Font font = new();
                    internal float fontSize = 20;
                    internal readonly TextInfo textInfo = new();
                    internal void ForceMeshUpdate() { }
                }
                internal sealed class Font { internal readonly Face faceInfo = new(); }
                internal sealed class Face { internal string familyName = "Fixture", styleName = "Regular"; internal float ascentLine = 20, descentLine; }
                internal sealed class TextInfo
                {
                    internal int lineCount = 1, characterCount = 1;
                    internal Line[] lineInfo = { new Line() };
                    internal Character[] characterInfo = { new Character() };
                }
                internal sealed class Line { internal float ascender = 20, descender; }
                internal sealed class Character { internal bool isVisible; internal Vector3 bottomLeft, topRight; }
            }
            """;
    }
}
