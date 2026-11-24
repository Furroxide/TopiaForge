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
        private static void NativeStatusCapturesUseAppliedStyles()
        {
            var root = Program.FindRepoRoot();
            const string recorder = "src/TopiaForge.ModManager/UiGalleryRecorder/";
            const string testing = "src/TopiaForge.Mods.Testing/";
            var paint = Syntax("src/TopiaForge.ModManager/UnityUiLayoutRenderer.Art.cs");
            var methods = Syntax(recorder + "UiGalleryNativeTree.Art.cs").DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText is "CaptureBadge" or "CapturePips");
            var source = NativeStatusBoundary
                .Replace("/* capture */", string.Join("\n", methods), StringComparison.Ordinal)
                .Replace("/* apply */", paint.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Single(method => method.Identifier.ValueText == "Apply").ToFullString(), StringComparison.Ordinal)
                .Replace("/* observation */", paint.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Single(type => type.Identifier.ValueText == "UiNativeStatusPaint").ToFullString(), StringComparison.Ordinal);
            var options = new CSharpParseOptions(preprocessorSymbols: new[] { "TOPIAFORGE_NATIVE_CAPTURE" });
            var sources = new[] { testing + "UiCaptureViewport.cs", testing + "UiCapturedNode.cs", testing + "UiTreeCapture.cs",
                recorder + "UiGalleryCaptureJson.cs" }
                .Select(path => File.ReadAllText(Path.Combine(root, path))).Append(source)
                .Select(body => CSharpSyntaxTree.ParseText(body, options));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(UiNode).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compiler = CSharpCompilation.Create("TopiaForge.ModManager.Tests", sources, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var output = new MemoryStream();
            var emit = compiler.Emit(output);
            Check(emit.Success, "The shipped status paint, native recorder, and JSON writer compile together. " +
                string.Join("\n", emit.Diagnostics.Where(error => error.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var observe = Assembly.Load(output.ToArray()).GetType("TopiaForge.ModManager.UiGalleryNativeTree")!.GetMethod("Probe")!
                .CreateDelegate<Action<string>>();
            observe(RequestedStatusStyles);

            SyntaxNode Syntax(string path) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path))).GetRoot();
        }

        private const string NativeStatusBoundary = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Text;
            using System.Text.Json;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;

            namespace TopiaForge.ModManager
            {
                internal sealed class UiGalleryNativeTree
                {
                    private static readonly UiCaptureNodeContent ArtDecoration = State(UiCaptureNodeFlags.Decorative, "");
                    private static readonly UiCaptureNodeContent ArtEdge = State(UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.BoundaryMark, "");
                    private static readonly UiCaptureNodeContent ArtMark = State(UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.SemanticMark, "");
                    private readonly List<UiCapturedNode> rows = new();
                    private readonly TopiaForgeResolvedTheme theme = new();
                    internal int Width => 1920;
                    internal int Height => 1080;
                    internal float PixelsPerUnit => 1f;
                    internal float? SurfaceAlpha => 1f;
                    internal UiAccessibilityPreferences Accessibility => UiAccessibilityPreferences.Default;
                    internal TopiaForgeScheme Scheme => TopiaForgeScheme.Paper;
                    internal UiThemeDescriptor Descriptor { get; }
                    internal UiGalleryFontFact[] Fonts => Array.Empty<UiGalleryFontFact>();

                    private UiGalleryNativeTree(UiThemeDescriptor descriptor) => Descriptor = descriptor;

                    public static void Probe(string json)
                    {
                        var declared = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(json)).Value!;
                        var badge = new UiBadge("Water low", UiPaintRole.Warning, UiBadgeStyle.Outlined, style: new UiStyleRef("warning.outline"));
                        var pips = new UiPips(1, 0, "Power", role: UiPaintRole.Accent, style: new UiStyleRef("power.track"));
                        foreach (var resolved in new[] { false, true })
                        {
                            var effective = resolved ? declared : UiThemePackParser.Default;
                            var recorder = new UiGalleryNativeTree(effective);
                            var scheme = effective.SelectScheme(hud: false, highContrast: false);
                            var bounds = new UiLayoutRect(0, 0, 144, 40);
                            var badgeArt = new RectTransform();
                            badgeArt.Add("Badge fill", new TopiaForgeRoundedImage());
                            badgeArt.Add("Badge boundary", new TopiaForgeRoundedImage());
                            badgeArt.Add("Badge shadow", new TopiaForgeRoundedImage());
                            badgeArt.Add("Label", new TMP_Text());
                            var pipsArt = new RectTransform();
                            pipsArt.Add("Label", new TMP_Text());
                            pipsArt.Add("Shadow", new TopiaForgeRoundedImage());
                            pipsArt.Add("Track", new TopiaForgeRoundedImage());
                            pipsArt.Add("Clip", new RectMask2D()).Add("Pip fill", new TopiaForgeRoundedImage { isActiveAndEnabled = false });
                            pipsArt.Add("Edge", new TopiaForgeRoundedImage());

                            var badgePaint = badgeArt.Attach(new UiNativeStatusPaint());
                            var pipPaint = pipsArt.Attach(new UiNativeStatusPaint());
                            Require(!recorder.CaptureBadge(badge, badgeArt, "/0", null) &&
                                !recorder.CapturePips(pips, pipsArt, "/1", null), "Native recorders require an applied status paint observation.");
                            new PaintProbe(badge, badgePaint).Apply(bounds, UiPaintRole.Surface, scheme, recorder.theme, effective, declared);
                            new PaintProbe(pips, pipPaint).Apply(bounds, UiPaintRole.Surface, scheme, recorder.theme, effective, declared);
                            Require(recorder.CaptureBadge(badge, badgeArt, "/0", null) && recorder.CapturePips(pips, pipsArt, "/1", null),
                                "The native recorder accepts the actual badge and pip paint.");
                            var badgeRow = recorder.rows.Single(row => row.Kind == nameof(UiBadge));
                            var pipsRow = recorder.rows.Single(row => row.Kind == nameof(UiPips));
                            Require(badgeRow.RequestedStyleId == "warning.outline" && badgeRow.RequestedPaintRole == UiPaintRole.Warning &&
                                badgeRow.EffectivePaintRole == UiPaintRole.Warning && badgeRow.StyleId == (resolved ? "warning.outline" : null),
                                "Native badge captures keep the authored request and actual resolved paint.");
                            Require(pipsRow.RequestedStyleId == "power.track" && pipsRow.RequestedPaintRole == UiPaintRole.Accent &&
                                pipsRow.EffectivePaintRole == UiPaintRole.Accent && pipsRow.StyleId == (resolved ? "power.track" : null),
                                "Native pip captures keep the authored request and actual resolved paint.");

                            var tree = new UiTreeCapture(recorder.rows, highContrast: false, minimumHitSize: 40f);
                            UiGalleryCaptureJson.Save("unused", "status", "local", tree, recorder, envelope: null);
                            using var serialized = JsonDocument.Parse(TopiaForge.ModManager.Core.JsonUtil.Captured);
                            var nodes = serialized.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
                            var emitted = nodes.Single(row => row.GetProperty("kind").GetString() == nameof(UiPips));
                            Require(emitted.GetProperty("requestedStyleId").GetString() == "power.track" &&
                                emitted.GetProperty("requestedPaintRole").GetString() == "accent" &&
                                emitted.GetProperty("effectivePaintRole").GetString() == "accent",
                                "Native JSON emits exact canonical lowercase status roles.");
                            Require(nodes.Where(row => row.GetProperty("kind").GetString() is not (nameof(UiBadge) or nameof(UiPips)))
                                .All(row => !row.TryGetProperty("requestedPaintRole", out _) && !row.TryGetProperty("effectivePaintRole", out _) &&
                                    !row.TryGetProperty("requestedStyleId", out _)), "Native JSON omits status facts from subordinate paint.");
                        }
                    }

                    /* capture */

                    private void Add(string path, string? id, string kind, RectTransform bounds, UiCaptureNodeContent content,
                        UiCaptureNodePaint paint, RectTransform? authoredFrame = null) =>
                        rows.Add(new UiCapturedNode(path, id, kind, Bounds(bounds), content, paint, Descriptor.Id,
                            usedFallback: false, isRendered: true, clipped: false, occluded: false));

                    private static UiCaptureNodeContent State(UiCaptureNodeFlags flags, string label) => new("", label, 0f, 0f, flags);
                    private static UiThemeColor? Over(Graphic graphic, UiThemeColor? backdrop) => backdrop;
                    private static UiCaptureRect Bounds(RectTransform artwork) => new(0, 0, 144, 40);
                    private static float VerticalScale(RectTransform artwork) => 1f;
                    private static string ChildPath(string parent, int index) => parent + "/" + index;
                    private static UiCaptureNodePaint ArtPaint(Graphic graphic, UiThemeColor? backdrop) => default;
                    private static T? Named<T>(RectTransform parent, string name) where T : MonoBehaviour => parent.Find(name)?.GetComponent<T>();
                    private static bool Text(string path, string? id, string kind, TMP_Text? text, string label,
                        UiTextStyle style, UiThemeColor? backdrop) => text != null;
                    private static void Require(bool result, string message)
                    {
                        if (!result) throw new InvalidOperationException(message);
                    }

                    private sealed class PaintProbe(UiNode node, UiNativeStatusPaint statusPaint)
                    {
                        private readonly Image? glyph = null;
                        /* apply */
                        private static double IconExtent(UiIcon icon) => icon.Size;
                        private static void PaintGlyph(Image glyph, UiGlyph symbol, UiThemeColor ink, double x, double y, double size) { }
                        private static void PaintImage(UiImage image, UiLayoutRect bounds, UiPaintRole behind, UiThemeScheme scheme, bool hc) { }
                        private static void PaintBadge(UiBadge badge, UiLayoutRect bounds, UiPaintRole behind, UiThemeScheme scheme,
                            TopiaForgeResolvedTheme theme, UiLayoutDecoration style) { }
                        private static void PaintPips(UiPips pips, UiLayoutRect bounds, UiPaintRole behind, UiThemeScheme scheme,
                            TopiaForgeResolvedTheme theme, UiLayoutDecoration style) { }
                    }
                }

                internal sealed record UiGalleryFontFact(string Path, string Family, string Style, float SizePixels,
                    float Ascender, float Descender, float MeasuredHeight);
                /* observation */
            }

            namespace TopiaForge.ModManager.Core
            {
                internal static class JsonUtil
                {
                    internal static string Captured = "";
                    internal static string Serialize(string value) => JsonSerializer.Serialize(value);
                    internal static void SaveJsonObject(string path, string json) => Captured = json;
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal enum TopiaForgeScheme { Paper }
                internal sealed class UiHost { }
                internal sealed class TopiaForgeResolvedTheme { internal bool HighContrast => false; }
                internal static class TopiaForgeSprites { internal static object Glyph(UiGlyph glyph) => glyph; }
            }

            namespace UnityEngine
            {
                internal static class Application { internal static string version => "2640"; }
                internal class MonoBehaviour { internal RectTransform rectTransform = null!; internal bool isActiveAndEnabled = true; }
                internal class Graphic : MonoBehaviour { }
                internal class Image : Graphic { internal object? sprite; }
                internal sealed class TMP_Text : Graphic { }
                internal sealed class RectMask2D : MonoBehaviour { }
                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal float? ResolvedCornerRadius => 4f;
                    internal float ResolvedBorderWidth => 2f;
                }
                internal sealed class RectTransform
                {
                    private readonly List<(string Name, RectTransform Child)> children = new();
                    private readonly List<MonoBehaviour> components = new();
                    internal int childCount => children.Count;
                    internal RectTransform GetChild(int index) => children[index].Child;
                    internal RectTransform? Find(string name) => children.FirstOrDefault(child => child.Name == name).Child;
                    internal RectTransform Add(string name, MonoBehaviour graphic)
                    {
                        var child = new RectTransform();
                        child.Attach(graphic);
                        children.Add((name, child));
                        return child;
                    }
                    internal T Attach<T>(T component) where T : MonoBehaviour
                    {
                        component.rectTransform = this;
                        components.Add(component);
                        return component;
                    }
                    internal T? GetComponent<T>() where T : MonoBehaviour => components.OfType<T>().FirstOrDefault();
                    internal bool TryGetComponent<T>(out T found) where T : MonoBehaviour
                    {
                        found = GetComponent<T>()!;
                        return found != null;
                    }
                }
            }
            """;
    }
}
