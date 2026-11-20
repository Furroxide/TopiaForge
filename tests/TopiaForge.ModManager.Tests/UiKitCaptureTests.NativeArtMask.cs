using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void NativeCoverRequiresParticipatingMask()
        {
            var path = Path.Combine(Program.FindRepoRoot(), "src/TopiaForge.ModManager/UiGalleryRecorder/UiGalleryNativeTree.Art.cs");
            var method = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>().Single(member => member.Identifier.Text == "CaptureImage");
            var boundary = CSharpSyntaxTree.ParseText(NativeArtMaskBoundary.Replace("/* recorder */", method.ToFullString()));
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var compilation = CSharpCompilation.Create("UiGalleryNativeArtMaskBoundary", new[] { boundary },
                platform.Select(file => MetadataReference.CreateFromFile(file)), new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var output = new MemoryStream();
            var emit = compilation.Emit(output);
            Check(emit.Success, "The native image recorder compiles against the masking boundary. " + string.Join("\n",
                emit.Diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var probe = System.Reflection.Assembly.Load(output.ToArray()).GetType("NativeArtMaskProbe", throwOnError: true)!;
            var observes = (Func<bool, bool, bool, int>)probe.GetMethod("Observe")!.CreateDelegate(typeof(Func<bool, bool, bool, int>));
            Check(observes(true, true, true) == 3, "Cover records the masked sprite aperture and separate source projection.");
            Check(observes(false, true, true) == 0 && observes(true, false, true) == 0,
                "Cover rejects an absent or disabled RectMask2D.");
            Check(observes(true, true, false) == 0, "Cover cannot certify a graphic that opts out of RectMask2D masking.");
        }

        private const string NativeArtMaskBoundary = """
            using System;

            public sealed class NativeArtMaskProbe
            {
                private readonly bool onlyUnavailableImages;
                private int facts;
                private const UiCaptureNodeFlags ArtMark = UiCaptureNodeFlags.RequiresLabel;
                private const UiCaptureNodeFlags ArtDecoration = UiCaptureNodeFlags.Decorative;
                private const UiCaptureNodeFlags ArtEdge = UiCaptureNodeFlags.Decorative;

                public static int Observe(bool hasMask, bool enabled, bool maskable)
                {
                    var method = new NativeArtMaskProbe();
                    var frame = new RectTransform { Mask = hasMask ? new RectMask2D(enabled) : null };
                    frame.Sprite = new RectTransform { Graphic = new Image { maskable = maskable } };
                    var tree = new RectTransform { Frame = frame };
                    return method.CaptureImage(new UiImage(), tree, "/art", null) ? method.facts : 0;
                }

                /* recorder */

                private void Add(string path, string? id, string kind, RectTransform bounds, UiCaptureNodeFlags content,
                    UiCaptureNodePaint paint, bool visible = true, bool localFallback = false, RectTransform? authoredFrame = null)
                {
                    if (kind == "UiImage" || kind == "ImageSprite" && visible && authoredFrame != null ||
                        kind == "ImageSourceProjection" && !visible) facts++;
                }

                private static UiCaptureNodeFlags State(UiCaptureNodeFlags flags, string label) => flags;
                private static UiCaptureNodePaint ArtPaint(Image graphic, UiThemeColor? backdrop) => default;
                private static UiThemeColor? Over(Image graphic, UiThemeColor? backdrop) => backdrop;
                private static float VerticalScale(RectTransform rect) => 1;
                private static T? Named<T>(RectTransform parent, string name) where T : class => null;
                private static bool Text(string path, string? id, string kind, TMP_Text? text, string label,
                    UiTextStyle style, UiThemeColor? backdrop, bool localFallback = false) => true;

                private sealed class RectTransform
                {
                    internal RectTransform? Frame;
                    internal RectTransform? Sprite;
                    internal RectMask2D? Mask;
                    internal Image? Graphic;
                    internal RectTransform? Find(string name) => name == "Image frame" ? Frame : name == "Sprite" ? Sprite : null;
                    internal T? GetComponent<T>() where T : class => Graphic as T;
                    internal bool TryGetComponent<T>(out T component) where T : class
                    {
                        component = (Mask as T)!;
                        return component != null;
                    }
                }

                private class Image
                {
                    internal bool enabled = true;
                    internal bool maskable;
                    internal object sprite = new object();
                    internal RectTransform rectTransform = new RectTransform();
                }
                private sealed class TopiaForgeRoundedImage : Image { internal float ResolvedBorderWidth => 2; }
                private sealed record RectMask2D(bool isActiveAndEnabled);
                private sealed class TMP_Text { }
                private sealed class UiImage
                {
                    internal bool Decorative => false;
                    internal string SemanticLabel => "Supply card";
                    internal string Id => "cover";
                    internal int? Tint => null;
                    internal UiImageFit Fit => UiImageFit.Cover;
                }
                private enum UiImageFit { Cover }
                private enum UiTextStyle { Body }
                [Flags] private enum UiCaptureNodeFlags { Decorative = 1, RequiresLabel = 2, MissingAsset = 4 }
                private readonly record struct UiThemeColor;
                private readonly record struct UiCaptureNodePaint(UiThemeColor? Ink, UiThemeColor? Fill, float Radius, float Border);
            }
            """;
    }
}
