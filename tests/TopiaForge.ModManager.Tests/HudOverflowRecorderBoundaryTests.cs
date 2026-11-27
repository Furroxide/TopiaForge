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
    internal static class HudOverflowRecorderBoundaryTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var options = new CSharpParseOptions(preprocessorSymbols: new[] { "TOPIAFORGE_NATIVE_CAPTURE" });
            const string testing = "src/TopiaForge.Mods.Testing/";
            const string recorder = "src/TopiaForge.ModManager/UiGalleryRecorder/UiGalleryNativeTree";
            var artRecorder = "using TopiaForge.Mods; using TMPro; using UnityEngine; using UnityEngine.UI; " +
                "namespace TopiaForge.ModManager { internal sealed partial class UiGalleryNativeTree { " +
                RecorderMethod(recorder + ".cs", "TryCapture") + RecorderMethod(recorder + ".Art.cs", "CaptureImageNotice") + " } }";
            var sources = new[]
            {
                testing + "UiCaptureViewport.cs", testing + "UiCapturedNode.cs", testing + "UiTreeCapture.cs",
                recorder + ".LayoutNotice.cs",
            }.Select(file => File.ReadAllText(Path.Combine(root, file))).Append(artRecorder).Append(Boundary)
                .Select(source => CSharpSyntaxTree.ParseText(source, options));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(UiNode).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager.Tests", sources, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            if (!result.Success)
                throw new InvalidOperationException("Native queued notice recorder: " + string.Join("\n", result.Diagnostics
                    .Where(issue => issue.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.ModManager.UiGalleryNativeTree")!.GetMethod("Probe")!;
            probe.CreateDelegate<Action>()();

            string RecorderMethod(string file, string name) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)))
                .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.Text == name).ToFullString();
        }

        private const string Boundary = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using TopiaForge.Mods;
            using TMPro;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class UiGalleryNativeTree
                {
                    private readonly RectTransform surface;
                    private readonly Canvas canvas;
                    private readonly List<Observation> observations = new();
                    private static readonly UiCaptureNodeContent EmptyContent = new("", "", 0f, 0f, 0);
                    private bool onlyUnavailableImages;
                    private bool countdownFixture;
                    internal float? SurfaceAlpha { get; private set; }
                    private int? PresentationOrder { get; set; }
                    private bool? PresentationTakesInput { get; set; }

                    private UiGalleryNativeTree()
                    {
                        canvas = new Canvas();
                        surface = new RectTransform(canvas.transform);
                    }

                    public static void Probe()
                    {
                        ProbeLayoutNotice();
                        ProbeArtworkNotice();
                    }

                    private static void ProbeLayoutNotice()
                    {
                        var native = new UiGalleryNativeTree();
                        var request = new UiSurfaceRequest("panel", "private title", "private body", UiSurfaceKind.Hud, content: new UiText("Readiness"));
                        var notice = new RectTransform(native.surface);
                        notice.Add("Layout fallback background", new Graphic());
                        var label = new TMP_Text();
                        notice.Add("LayoutNoticeMessage", label);
                        var failure = new UiLayoutException(UiLayoutFailure.ClippedContent, "/0", "Expected clipped fixture");
                        const string safe = "Admitted Pack · HUD at top right";
                        if (!native.TryCaptureLayoutNotice(request, native.surface, notice, failure, out var captured, safe) ||
                            captured?.Nodes.SingleOrDefault(row => row.Kind == UiHudOverflowNotice.CaptureKind) is not
                                { IsRendered: false, Clipped: false, SemanticLabel: safe } ||
                            captured.Nodes.Any(row => row.IsRendered || row.SemanticLabel.Contains("private", StringComparison.Ordinal)) ||
                            captured.CheckAccessibility().Any(issue => issue.Kind != UiAccessibilityIssueKind.ClippedContent))
                            throw new InvalidOperationException("The native recorder emits a safe queued diagnostic for the undrawn HUD.");
                        label.isActiveAndEnabled = true;
                        if (native.TryCaptureLayoutNotice(request, native.surface, notice, failure, out _, safe))
                            throw new InvalidOperationException("A drawn native notice cannot be certified as queued overflow.");
                    }

                    private static void ProbeArtworkNotice()
                    {
                        var native = new UiGalleryNativeTree();
                        var art = new UiImage(new UiImageRef("preview-art", "card"), "Private artwork", 1f, 1f, id: "card");
                        var request = new UiSurfaceRequest("art", "Private title", "Private body", UiSurfaceKind.Hud, content: art);
                        var composition = new RectTransform(native.surface);
                        composition.Add("Art", new object());
                        var notice = new RectTransform(native.surface);
                        notice.Add("Layout fallback background", new Graphic());
                        var label = new TMP_Text();
                        notice.Add("Label", label);
                        const string safe = "Admitted Pack · HUD at top right";
                        if (!native.TryCapture(request, native.surface, composition, notice, out var captured, queuedNotice: safe) ||
                            captured?.Nodes.SingleOrDefault(row => row.Kind == UiHudOverflowNotice.CaptureKind) is not
                                { IsRendered: false, Clipped: false, SemanticLabel: safe, Text: "" } ||
                            captured.Nodes.SingleOrDefault(row => row.Id == "card") is not
                                { Width: 1f, Height: 1f, IsRendered: false } ||
                            captured.Nodes.Any(row => row.IsRendered || row.Kind == "ImageFallback") ||
                            captured.CheckAccessibility().Single().Kind != UiAccessibilityIssueKind.MissingAsset)
                            throw new InvalidOperationException("The native artwork recorder keeps a hidden art box and a safe queued diagnostic.");
                        label.isActiveAndEnabled = true;
                        if (native.TryCapture(request, native.surface, composition, notice, out _, queuedNotice: safe))
                            throw new InvalidOperationException("A drawn native image notice cannot be certified as queued overflow.");
                        label.isActiveAndEnabled = false;
                        var window = new UiSurfaceRequest("art", "Private title", "Private body", UiSurfaceKind.Window, content: art);
                        if (native.TryCapture(window, native.surface, composition, notice, out _, queuedNotice: safe))
                            throw new InvalidOperationException("Only a HUD can be certified as queued overflow.");
                    }

                    private UiTreeCapture CompleteCapture() => new UiTreeCapture(observations.Select(item =>
                        new UiCapturedNode(item.path, item.id, item.kind, item.bounds, item.content, item.paint,
                            UiThemePackParser.Default.Id, true, item.rendered, item.clipped, item.occluded)).ToList(), false);
                    private sealed record Observation(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect clip,
                        UiCaptureNodeContent content, UiCaptureNodePaint paint, bool rendered, bool clipped,
                        bool localFallback = false, bool kitPaint = false, bool occluded = false);
                    private static bool SingleChild(RectTransform parent, out RectTransform? child) =>
                        (child = parent.Find("Art")) != null;
                    private static UiThemeColor? AncestorFill(RectTransform child) => null;
                    private bool WindowTitle(UiSurfaceRequest request, RectTransform root, UiThemeColor? fill) => true;
                    private bool GraphConnectionsLauncher(TopiaForgeGraphConnections connections) => true;
                    private bool GraphConnectionsList(UiNode tree, TopiaForgeGraphConnections connections, UiThemeColor? fill) => true;
                    private bool Visit(UiNode node, RectTransform root, string path, UiThemeColor? fill)
                    {
                        if (node is not UiImage image || !onlyUnavailableImages) return false;
                        var box = new UiCaptureRect(42f, 35f, image.Width!.Value, image.Height!.Value);
                        var content = new UiCaptureNodeContent("", image.SemanticLabel, 0f, 0f, UiCaptureNodeFlags.MissingAsset);
                        observations.Add(new Observation(path, image.Id, nameof(UiImage), box, default, content, default,
                            rendered: false, clipped: false));
                        return true;
                    }
                    private static UiThemeColor? Over(Graphic graphic, UiThemeColor? backdrop) => backdrop;
                    private static (float a, bool _) EffectiveColor(Graphic value) => (1f, false);
                    private static UiCaptureRect Bounds(RectTransform rect) => default;
                    private static (bool AnyClipping, bool _) Clip(RectTransform rect, UiCaptureRect bounds) => (false, false);
                    private void Add(string path, string id, string kind, RectTransform rect, UiCaptureNodeContent content,
                        UiCaptureNodePaint paint, bool localFallback = false, bool kitPaint = false) { }
                    private bool Text(string path, string? id, string kind, TMP_Text? label, string authored, UiTextStyle style,
                        UiThemeColor? fill, UiCaptureNodeFlags flags = UiCaptureNodeFlags.None,
                        bool localFallback = false, bool kitPaint = false) => true;
                }

                internal sealed class TopiaForgeGraphConnections { internal bool IsOpen => false; }
                internal static class UiGalleryCaptureFixture
                {
                    internal const string PresentationHudId = "gallery-presentation-hud";
                }
            }

            namespace UnityEngine
            {
                internal sealed class Canvas
                {
                    internal readonly RectTransform transform = new RectTransform();
                    internal int sortingOrder => 0;
                    internal T? GetComponent<T>() where T : class => null;
                }

                internal sealed class RectTransform(RectTransform? parent = null, object? component = null)
                {
                    internal readonly RectTransform? parent = parent;
                    private readonly Dictionary<string, RectTransform> children = new();
                    internal (float width, float height) rect => (300f, 80f);
                    internal bool IsChildOf(RectTransform ancestor) => parent == ancestor || parent?.IsChildOf(ancestor) == true;
                    internal T? GetComponent<T>() where T : class => component as T;
                    internal RectTransform? Find(string name) => children.GetValueOrDefault(name);
                    internal void Add(string name, object item) => children.Add(name, new RectTransform(this, item));
                }
            }

            namespace UnityEngine.UI
            {
                internal sealed class Graphic { }
                internal sealed class GraphicRaycaster { internal bool isActiveAndEnabled => false; }
            }
            namespace TMPro
            {
                internal sealed class TMP_Text
                {
                    internal bool isActiveAndEnabled;
                    internal UnityEngine.RectTransform rectTransform = new();
                }
            }
            """;
    }
}
