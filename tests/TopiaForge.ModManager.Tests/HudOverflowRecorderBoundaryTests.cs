using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
            var sources = new[]
            {
                testing + "UiCaptureViewport.cs", testing + "UiCapturedNode.cs", testing + "UiTreeCapture.cs",
                "src/TopiaForge.ModManager/UiGalleryRecorder/UiGalleryNativeTree.LayoutNotice.cs",
            }.Select(file => File.ReadAllText(Path.Combine(root, file))).Append(Boundary)
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
                    private readonly (RectTransform transform, bool _) canvas;
                    private readonly List<Observation> observations = new();
                    private static readonly UiCaptureNodeContent EmptyContent = new("", "", 0f, 0f, 0);
                    internal float? SurfaceAlpha { get; private set; }

                    private UiGalleryNativeTree()
                    {
                        canvas = (new RectTransform(), false);
                        surface = new RectTransform(canvas.transform);
                    }

                    public static void Probe()
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

                    private UiTreeCapture CompleteCapture() => new UiTreeCapture(observations.Select(item =>
                        new UiCapturedNode(item.path, item.id, item.kind, item.bounds, item.content, item.paint,
                            UiThemePackParser.Default.Id, true, item.rendered, item.clipped, item.occluded)).ToList(), false);
                    private sealed record Observation(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect clip,
                        UiCaptureNodeContent content, UiCaptureNodePaint paint, bool rendered, bool clipped, bool occluded);
                    private static UiThemeColor? Over(Graphic graphic, UiThemeColor? backdrop) => backdrop;
                    private static (float a, bool _) EffectiveColor(Graphic value) => (1f, false);
                    private void Add(string path, string id, string kind, RectTransform rect, UiCaptureNodeContent content, UiCaptureNodePaint paint) { }
                    private bool Text(string path, string? id, string kind, TMP_Text? label, string authored, UiTextStyle style,
                        UiThemeColor? fill, UiCaptureNodeFlags flags) => true;
                }
            }

            namespace UnityEngine
            {
                internal sealed class RectTransform(RectTransform? parent = null, object? component = null)
                {
                    private readonly RectTransform? parent = parent;
                    private readonly Dictionary<string, RectTransform> children = new();
                    internal (float width, float height) rect => (300f, 80f);
                    internal bool IsChildOf(RectTransform ancestor) => parent == ancestor || parent?.IsChildOf(ancestor) == true;
                    internal T? GetComponent<T>() where T : class => component as T;
                    internal RectTransform? Find(string name) => children.GetValueOrDefault(name);
                    internal void Add(string name, object item) => children.Add(name, new RectTransform(this, item));
                }
            }

            namespace UnityEngine.UI { internal sealed class Graphic { } }
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
