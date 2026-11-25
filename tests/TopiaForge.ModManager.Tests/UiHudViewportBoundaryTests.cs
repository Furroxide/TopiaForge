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
    internal static class UiHudViewportBoundaryTests
    {
        internal static void Run()
        {
            var sources = new[]
            {
                "src/TopiaForge.ModManager/UnityUiHudPlacement.cs",
                "src/TopiaForge.ModManager/UnityUiHudPlacement.Viewport.cs",
                "src/TopiaForge.ModManager/UnityUiHud.cs",
                "src/TopiaForge.ModManager/UnityUiSurface.Layout.cs",
                "src/TopiaForge.ModManager/UiHudAnchors.cs",
                "src/TopiaForge.Mods.UnityUi/Core/TopiaForgeHudLayout.cs",
                "src/TopiaForge.Mods.UnityUi/Layout/TopiaForgeAnchors.cs",
            };
            // Exercise physical projection, the real box solver and retained refresh through controlled Unity metrics.
            var root = Program.FindRepoRoot();
            var probeSource = Probe
                .Replace("/* service creation */", Members("UnityUiService.cs", "OwnerUiService", "CreateSurface"))
                .Replace("/* surface chrome */", Members("UnityUiSurface.cs", "UnityUiSurface", "AttachDock", "SetBody"))
                .Replace("/* surface presence */", Members("UnityUiSurface.Presence.cs", "UnityUiSurface",
                    "Present", "ShowNativeView", "HideNativeView"));
            var trees = sources.Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)), path: file))
                .Append(CSharpSyntaxTree.ParseText(probeSource, path: "HudViewportBoundary.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) ||
                    Path.GetFileName(path) == "netstandard.dll")
                .Append(typeof(UiNode).Assembly.Location)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            if (!result.Success)
                throw new InvalidOperationException("HUD viewport boundary: " + string.Join("\n",
                    result.Diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.ModManager.OwnerUiService", throwOnError: true)!;
            ((Action)probe.GetMethod("ProbeHudViewport", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate(typeof(Action)))();

            string Members(string file, string type, params string[] names)
            {
                var path = Path.Combine(root, "src/TopiaForge.ModManager", file);
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
                return string.Join("\n", syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Single(declaration => declaration.Identifier.Text == type).Members.OfType<MethodDeclarationSyntax>()
                    .Where(member => names.Contains(member.Identifier.Text)));
            }
        }

        private const string Probe = """
            using System;
            using System.Collections.Generic;
            using System.Reflection;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UI;

            namespace UnityEngine
            {
                public record struct Vector2(float x, float y)
                {
                    public static Vector2 zero => new Vector2(0, 0);
                    public static Vector2 one => new Vector2(1, 1);
                }
                public record struct Vector3(float x, float y, float z = 0);
                public record struct Rect(float xMin, float yMin, float width, float height) { public float yMax => yMin + height; }
                public class MonoBehaviour { }
                public class Camera { }
                public enum RenderMode { ScreenSpaceOverlay, ScreenSpaceCamera }
                public sealed class Canvas
                {
                    public Canvas rootCanvas => this;
                    public Rect pixelRect = new Rect(0, 0, Screen.width, Screen.height);
                    public float Scale = 1f;
                    public Camera? worldCamera => null;
                    public RenderMode renderMode => RenderMode.ScreenSpaceOverlay;
                }
                public static class Screen { public static int width = 1920, height = 1080; }
                public static class RectTransformUtility
                {
                    public static Vector2 WorldToScreenPoint(Camera? camera, Vector3 point) => new Vector2(point.x, point.y);
                }
                public sealed class GameObject
                {
                    public bool activeSelf = true;
                    public bool activeInHierarchy => activeSelf;
                    public string name = "";
                    internal object? Component;
                    private object? added;
                    internal Action<bool>? ActiveChanged;
                    public void SetActive(bool shown)
                    {
                        if (shown == activeSelf) return;
                        activeSelf = shown;
                        ActiveChanged?.Invoke(shown);
                    }
                    public T AddComponent<T>() where T : new()
                    {
                        var value = new T();
                        added = value;
                        return value;
                    }
                    public T? GetComponent<T>() where T : class => Component as T ?? added as T;
                }
                public class RectTransform
                {
                    public Vector2 anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta, offsetMin, offsetMax;
                    public GameObject gameObject = new GameObject();
                    public int childCount => 0;
                    public Rect rect => new Rect(0, 0, sizeDelta.x, sizeDelta.y);
                    public Canvas? Canvas;
                    internal Action? RebuildLayout;
                    public object? GetChild(int index) => null;
                    public T? GetComponentInParent<T>(bool includeInactive) where T : class => Canvas as T;
                    public void GetWorldCorners(Vector3[] buffer)
                    {
                        var canvas = Canvas!;
                        var region = canvas.pixelRect;
                        var scale = canvas.Scale;
                        var width = sizeDelta.x * scale;
                        var height = sizeDelta.y * scale;
                        var x = region.xMin + anchorMin.x * region.width + anchoredPosition.x * scale - width * pivot.x;
                        var y = region.yMin + anchorMin.y * region.height + anchoredPosition.y * scale - height * pivot.y;
                        buffer[0] = new Vector3(x, y);
                        buffer[1] = new Vector3(x, y + height);
                        buffer[2] = new Vector3(x + width, y + height);
                        buffer[3] = new Vector3(x + width, y);
                    }
                    public void SetAsFirstSibling() { }
                }
            }

            namespace UnityEngine.UI
            {
                public sealed class VerticalLayoutGroup
                {
                    public readonly Padding padding = new Padding();
                    public readonly float spacing = 8f;
                }
                public sealed class Padding { public int horizontal = 24, vertical = 24; }
                public static class LayoutRebuilder
                {
                    public static void ForceRebuildLayoutImmediate(RectTransform value)
                    {
                        if (value.gameObject.activeInHierarchy) value.RebuildLayout?.Invoke();
                    }
                }
                public static class LayoutUtility { public static float GetPreferredHeight(RectTransform value) => value.rect.height; }
            }

            namespace TMPro
            {
                public sealed class TMP_FontAsset { }
                public sealed class TMP_Text
                {
                    public TMP_Text(string text, float height) { this.text = text; Height = height; }
                    public string text;
                    public float Height;
                    public int MeasureCalls;
                    public bool Wrap = true;
                    public Vector2 GetPreferredValues(string value, float width, float height)
                    {
                        MeasureCalls++;
                        var full = value.Length * 9f;
                        return new Vector2(Math.Min(full, width), (Wrap ? (float)Math.Ceiling(full / width) : 1f) * Height);
                    }
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                public enum TopiaForgeCorner { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }
                public enum TopiaForgeGap { None = 0, Sm = 8, Md = 12 }
                public enum TopiaForgeTextStyle { Body, Heading }
                public enum TopiaForgePanelStyle { HudPanel }
                public enum TopiaForgeScheme { Paper }
                public static class TopiaForgeTokens { public const float SafeMargin = 18f, ScrollGutter = 12f; }
                public static class TopiaForgeTheme { public static event Action? Changed; }
                internal enum TopiaForgeTone { Neutral }
                internal static class TopiaForgeToasts
                {
                    internal static readonly List<string> Plain = new();
                    internal static void ShowPlain(string text, TopiaForgeTone tone) => Plain.Add(text);
                }
                public sealed class UiHost
                {
                    public string OwnerId => "example.hud-probe";
                    public float EffectiveUiScale = 1f;
                    public Canvas Canvas { get; } = new Canvas();
                    public TopiaForgeHudLayer HudLayer(string id, bool persistent) => new TopiaForgeHudLayer(this);
                    public TopiaForgeWindow Window(string id, string title, float width, float height, TopiaForgeScheme scheme,
                        bool persistent) => throw new NotSupportedException();
                    public TopiaForgeFullscreenTool FullscreenTool(string id, string title, TopiaForgeScheme scheme,
                        bool persistent) => throw new NotSupportedException();
                }
                public sealed class TopiaForgeLayerSlot
                {
                    internal void SelectTheme(UiThemeDescriptor descriptor, Func<UiTextStyle, TMPro.TMP_FontAsset?>? fonts) { }
                }
                public sealed class ProbeTheme { public int Surface => 0; }
                public class TopiaForgeWidget
                {
                    public RectTransform Rect { get; set; } = new RectTransform();
                    public GameObject Go => Rect.gameObject;
                    public ProbeTheme ResolvedTheme = new ProbeTheme();
                    public void SetShown(bool shown) => Go.SetActive(shown);
                }
                public sealed class TopiaForgeLabel : TopiaForgeWidget
                {
                    public TopiaForgeLabel(string text, TopiaForgeTextStyle role)
                    { Go.Component = new TMPro.TMP_Text(text, role == TopiaForgeTextStyle.Heading ? 24f : 20f); }
                    public TopiaForgeLabel PlainText() => this;
                    public TopiaForgeLabel Ellipsis() { Go.GetComponent<TMPro.TMP_Text>()!.Wrap = false; return this; }
                    public void SetText(string text) => Go.GetComponent<TMPro.TMP_Text>()!.text = text;
                }
                public class TopiaForgeContainer : TopiaForgeWidget
                {
                    public TopiaForgeContainer Column(TopiaForgeGap gap, TopiaForgeGap padding)
                    {
                        var child = new TopiaForgeContainer();
                        child.Rect.Canvas = Rect.Canvas;
                        child.Rect.sizeDelta = Rect.sizeDelta;
                        child.Go.Component = new VerticalLayoutGroup();
                        return child;
                    }
                    public TopiaForgeContainer FreeImage(string name) => new TopiaForgeContainer();
                    public TopiaForgeLabel Label(string text, TopiaForgeTextStyle role) => new TopiaForgeLabel(text, role);
                    public TopiaForgeContainer Stretch() => this;
                    public ProbeScroll Scroll(TopiaForgeGap gap, TopiaForgeGap padding) => throw new NotSupportedException();
                    public void SetColor(int color) { }
                    public void Destroy() => SetShown(false);
                }
                public sealed class ProbeScroll
                {
                    public TopiaForgeContainer Content => throw new NotSupportedException();
                    public ProbeScroll Flex(float x, float y) => this;
                }
                public sealed class TopiaForgeHudLayer : TopiaForgeContainer
                {
                    public TopiaForgeHudLayer(UiHost host) { Rect.Canvas = host.Canvas; }
                    public ProbePanel? ActivePanel { get; private set; }
                    public ProbePanel Panel(TopiaForgePanelStyle style) => ActivePanel = new ProbePanel(Rect.Canvas!);
                }
                public sealed class ProbePanel : TopiaForgeContainer
                {
                    public ProbePanel(Canvas canvas)
                    {
                        Rect.Canvas = canvas;
                        Go.Component = new VerticalLayoutGroup();
                    }
                    public ProbePanel Size(float width, float height) { Rect.sizeDelta = new Vector2(width, height); return this; }
                }
                public sealed class TopiaForgeWindow : TopiaForgeWidget
                {
                    public TopiaForgeContainer Content => throw new NotSupportedException();
                    public void Show() { }
                    public void Close() { }
                }
                public sealed class TopiaForgeFullscreenTool : TopiaForgeWidget
                {
                    public TopiaForgeContainer Content => throw new NotSupportedException();
                    public void Show() { }
                    public void Close() { }
                }
            }

            namespace TopiaForge.ModManager
            {
                internal static class UnityMainThreadGuard { internal static void AssertCurrent() { } }
                internal sealed class UiGameplayGate { internal bool IsGameplay => true; }
                internal readonly record struct GalleryPreviewPalette(UiThemeDescriptor Descriptor, TopiaForgeScheme Scheme);
                internal readonly record struct UiRemoteThemeSelection(UiWireEnvelope? Envelope, ProbePackage? Package);
                internal sealed record UiSurfaceInputContext(object? Registry, string OwnerPackageId)
                {
                    internal UiSurfaceInputContext ForRemote(UiWireEnvelope? envelope) =>
                        this with { OwnerPackageId = envelope?.OwnerPackageId ?? string.Empty };
                }
                internal readonly record struct ProbePackage(ProbeManifest Manifest);
                internal readonly record struct ProbeManifest(string Name, object? AdmittedUi = null);
                internal static class UiThemeScope { internal static UiThemeDescriptor? Ready(UiThemeDescriptor? value) => value; }
                internal sealed partial class OwnerUiService
                {
                    private readonly UiHost host;
                    private readonly object? uiInput = null;
                    private readonly UnityUiHudPlacement hudPlacement;
                    private readonly object? themeScope = null;
                    private readonly object themePackages = new object();
                    private readonly ProbeLifetime lifetime = new ProbeLifetime();
                    private readonly ProbeLogger logger = new ProbeLogger();
                    private readonly UiGameplayGate gameplay = new UiGameplayGate();
                    private readonly HashSet<string> surfaceIds = new();
                    private readonly List<UnityUiSurface> surfaces = new();
                    private readonly string hudNoticePackageName = "Example Pack";
                    private readonly Func<double> clock = () => 0d;
                    private readonly Func<string> clockEpoch = () => "epoch";

                    private OwnerUiService(UiHost host, UnityUiHudPlacement hudPlacement)
                    {
                        this.host = host;
                        this.hudPlacement = hudPlacement;
                    }

                    private OperationResult<bool> DescribePresentationTheme(UiSurfaceRequest request, out UiThemeDescriptor? theme)
                    {
                        theme = null;
                        return OperationResult<bool>.Success(true);
                    }
                    private static UiThemeDescriptor? ResolveRemoteTheme(UiRemoteThemeSelection selection) => null;
                    private void AbortSurfaceCreation(UnityUiSurface? surface, TopiaForgeWidget? root, string id) => surfaceIds.Remove(id);
                    private void ReleaseSurface(UnityUiSurface surface) { }
                    private void ObserveGalleryFixture(UnityUiSurface surface, UiSurfaceRequest request,
                        UiRemoteThemeSelection? selection, UiThemeDescriptor? theme, GalleryPreviewPalette? preview) { }

                    /* service creation */

                    public static void ProbeHudViewport()
                    {
                        new UnityUiSurface(new TopiaForgeWindow()).VerifyScrollingLayout();
                        new UnityUiSurface(new TopiaForgeFullscreenTool()).VerifyScrollingLayout();
                        using (var placement = new UnityUiHudPlacement())
                        {
                            var canvas = new Canvas();
                            var host = new UiHost();
                            var stack = new UiStack(new UiNode[]
                            {
                                new UiPositioned(new UiText("Ready"), left: 920, top: 12, width: 64)
                            }, width: 1000, height: 56);
                            var surface = new UnityUiSurface(placement, host, canvas, 1080, 220, stack);
                            Check(surface.Fits() && surface.ShowOriginal(), "The real staging constraints admit the wide HUD at scale 1.");
                            SetScale(host, canvas, placement, 2f);
                            Check(!surface.Fits(), "The real staging constraints refuse the authored 1080-unit HUD at double scale.");
                            surface.Refresh();
                            var safe = UiHudViewport.SafeArea(new UiLayoutRect(0, 0, Screen.width, Screen.height), 2d);
                            Check(surface.NoticeShown && surface.LayoutFailure?.Path == "/0/0" && !surface.OriginalShown &&
                                safe.Contains(surface.Bounds) && ClearsGameHudLabels(surface.Bounds) && TopiaForgeToasts.Plain.Count == 0,
                                "The native HUD displays its retained layout notice clear of the game labels. " +
                                $"Notice {surface.NoticeShown}, original {surface.OriginalShown}, path {surface.LayoutFailure?.Path}, width {surface.Bounds.Width}.");
                            Check(surface.NoticeCaption == "Readiness" && surface.NoticeMessage == UiHudLayoutNotice.Text,
                                "The native notice includes its kit message and the owning surface title.");
                            var calls = surface.LayoutCalls;
                            for (var i = 0; i < 100; i++) surface.Refresh();
                            var start = GC.GetAllocatedBytesForCurrentThread();
                            for (var i = 0; i < 4096; i++) surface.Refresh();
                            var bytes = GC.GetAllocatedBytesForCurrentThread() - start;
                            Check(surface.LayoutCalls == calls && bytes == 0, "Stable native layout notice checks allocate zero bytes and remeasure nothing.");
                            SetScale(host, canvas, placement, 1f);
                            surface.Refresh();
                            Check(surface.OriginalShown && !surface.NoticeShown && surface.Bounds.Width == 1080d,
                                "A compatible viewport restores the original tree and authored width.");
                            surface.Refresh();
                            calls = surface.LayoutCalls;
                            surface.ChangeBinding();
                            surface.Refresh();
                            surface.Refresh();
                            Check(surface.LayoutCalls == calls + 1, "A changed key cap refreshes retained HUD geometry once with the same viewport.");
                            canvas.pixelRect = new Rect(0, 0, 900, 1080);
                            Check(!surface.Fits(), "The real staging constraints include the canvas's physical aperture.");
                        }
                        using (var placement = new UnityUiHudPlacement())
                        {
                            var canvas = new Canvas();
                            var host = new UiHost();
                            var peer = new UnityUiSurface(placement, host, canvas, 340, 430, new UiText("Peer"));
                            var surface = new UnityUiSurface(placement, host, canvas, 80, 80, new UiText("OK"));
                            Check(peer.ShowOriginal() && surface.ShowOriginal(), "Both original HUDs fit at scale 1.");
                            SetScale(host, canvas, placement, 2f);
                            var protectedChat = new RectTransform { Canvas = canvas };
                            TopiaForgeAnchors.Place(protectedChat, 460, 9, 490, 150);
                            placement.SetProtectedNoticeArea(protectedChat);
                            var other = peer.Bounds;
                            surface.Refresh();
                            var shown = surface.Bounds;
                            var safe = UiHudViewport.SafeArea(new UiLayoutRect(0, 0, Screen.width, Screen.height), 2);
                            Check(surface.NoticeShown && safe.Contains(shown) && shown.Width > 160 &&
                                !shown.Intersects(other) && !shown.Intersects(Bounds(protectedChat)) && other.Equals(peer.Bounds) &&
                                TopiaForgeToasts.Plain.Count == 0,
                                "The native alternate slot avoids both peer HUD and chat, preserves captions and leaves its neighbour still.");
                            SetScale(host, canvas, placement, 1f);
                            surface.Refresh();
                            Check(surface.OriginalShown && surface.Bounds.Width == 80 && surface.Bounds.Height == 80,
                                "The alternate notice restores the compact authored slot and its tree.");
                        }
                        BodyOnlySurfaceUsesMeasuredChrome();
                        NarrowerHudNoticeUsesActualWidth();
                        FullyOccupiedHudUsesOnePlainToast();
                        TwoQueuedNativeHudRectsDoNotOccupyPaint();
                        DockMarginsFollowTheAssignedUnits();
                        LayoutNoticeUsesAnAvailableWiderSlot();
                        HudArtReadinessRestoresPlacement();
                        ClampedHudRetainsItsPendingArtworkNotice();
                        NativeArtworkUsesTheNarrowOpening();
                        NativeArtworkUsesTheWideOpening();
                        NativeArtworkKeepsOneUnfitEpisode();
                        Console.WriteLine("Native HUD viewport boundary passed for retained HUD placement, 300- and 260-unit notice openings, wide artwork recovery, safe overflow episodes, 24-unit margins and 4,096 settled frames at 0 B.");
                    }

                    private static void BodyOnlySurfaceUsesMeasuredChrome()
                    {
                        using var dock = new UnityUiHudPlacement();
                        var host = new UiHost();
                        var canvas = host.Canvas;
                        SetScale(host, canvas, dock, 2f);
                        var pixels = canvas.pixelRect;
                        canvas.pixelRect = default;
                        var owner = new OwnerUiService(host, dock);
                        var request = new UiSurfaceRequest("body-only", "Readiness", new string('P', 212),
                            UiSurfaceKind.Hud, 1080, 104).WithHudAnchor(UiHudAnchor.TopLeft).WithHudVisibility(UiHudVisibility.Always);
                        var created = owner.CreateSurface(request);
                        Check(created.TryGetValue(out var handle) && handle is UnityUiSurface,
                            "The actual CreateSurface route accepts a legacy body-only HUD.");
                        using var surface = (UnityUiSurface)handle!;
                        var tick = surface.LayoutFrame();
                        Check(!surface.NoticeShown && TopiaForgeToasts.Plain.Count == 0,
                            "The initial unmeasured canvas defers the body-only check without a false toast.");
                        canvas.pixelRect = pixels;
                        tick();
                        Check(surface.NoticeShown && !surface.BodyShown && surface.Bounds.Width <= 1824d &&
                            surface.NoticeMessage == UiHudLayoutNotice.Text && TopiaForgeToasts.Plain.Count == 0,
                            "The body-only frame observer shows readable fallback inside the actual scaled viewport.");

                        surface.SetBody("Ready");
                        tick();
                        Check(surface.BodyShown && !surface.NoticeShown && surface.Bounds.Width == 1824d,
                            "A real SetBody change restores the original HUD chrome in the physical aperture.");
                        var measurements = surface.BodyMeasureCalls;
                        for (var index = 0; index < 64; index++) { surface.SetBody("Ready"); tick(); }
                        var allocated = GC.GetAllocatedBytesForCurrentThread();
                        for (var index = 0; index < 4096; index++) { surface.SetBody("Ready"); tick(); }
                        Check(GC.GetAllocatedBytesForCurrentThread() == allocated && surface.BodyMeasureCalls == measurements,
                            "Unchanged body-only updates and frame observations allocate 0 B and never measure again.");

                        surface.SetBody(request.Body);
                        tick();
                        Check(surface.NoticeShown && !surface.BodyShown, "A later overlong body uses the same retained notice.");
                        SetScale(host, canvas, dock, 1f);
                        tick();
                        Check(surface.BodyShown && !surface.NoticeShown && surface.Bounds.Width == 1080d,
                            "Scale recovery restores full authored geometry and the existing body text.");
                    }

                    private static void NarrowerHudNoticeUsesActualWidth()
                    {
                        using var dock = new UnityUiHudPlacement();
                        var host = new UiHost();
                        var canvas = new Canvas();
                        SetScale(host, canvas, dock, 2f);
                        var left = new UnityUiSurface(dock, host, canvas, 400, 430, new UiText("Peer"));
                        var right = new UnityUiSurface(dock, host, canvas, 212, 430, new UiText("Peer"),
                            corner: TopiaForgeCorner.TopRight);
                        var subject = new UnityUiSurface(dock, host, canvas, 80, 80, new UiText("Ready"),
                            corner: TopiaForgeCorner.Top);
                        Check(left.ShowOriginal() && right.ShowOriginal(), "Both real peers are visible in the scale-2 native dock.");
                        Check(subject.ShowOriginal(), "The compact composed HUD initially fits between the peers.");
                        var beforeLeft = left.Bounds;
                        var beforeRight = right.Bounds;
                        subject.SetBody(new string('B', 100));
                        subject.Refresh();
                        Check(subject.NoticeShown && !subject.NoticeQueued && !subject.OriginalShown &&
                            subject.Bounds.X == beforeLeft.Right && subject.Bounds.Width == 600d &&
                            subject.Bounds.Right == beforeRight.X && subject.Bounds.Height == 192d &&
                            left.Bounds.Equals(beforeLeft) && right.Bounds.Equals(beforeRight) && TopiaForgeToasts.Plain.Count == 0,
                            "The native notice remeasures the extra body line in the 300-unit opening and leaves both peer bounds intact.");
                        right.SetDockShown(false);
                        subject.Refresh();
                        Check(subject.Bounds.Width == 720d && subject.Bounds.Height == 152d && left.Bounds.Equals(beforeLeft),
                            "The newly available native slot restores the preferred 360-unit width and single measured line.");

                        left.DropViewport();
                        left.SetDockShown(false);
                        left.SetDockShown(true);
                        subject.Refresh();
                        Check(subject.NoticeQueued && !subject.PanelShown && subject.Bounds.Width == 160d &&
                            subject.Bounds.Height == 160d,
                            "An unavailable peer projection returns the queued HUD to its authored allocation.");
                    }

                    private static void FullyOccupiedHudUsesOnePlainToast()
                    {
                        using var placement = new UnityUiHudPlacement();
                        var canvas = new Canvas();
                        var host = new UiHost();
                        var privateTitle = "private title " + new string('F', 240);
                        var stack = new UiStack(new UiNode[]
                        {
                            new UiPositioned(new UiText("private content"), left: 920, top: 0, width: 64),
                        }, width: 1000, height: 20);
                        var subject = new UnityUiSurface(placement, host, canvas, 1080, 110, stack, privateTitle,
                            "private body", TopiaForgeCorner.TopRight);
                        subject.SetHudNoticeSource("Example\nPack\u202E", UiHudAnchor.TopRight, UiHudVisibility.Always);
                        var peer = new UnityUiSurface(placement, host, canvas, 912, 396, new UiText("Reserved"),
                            corner: TopiaForgeCorner.TopRight);
                        Check(subject.ShowOriginal() && peer.ShowOriginal(), "Both authored HUDs fit at scale 1.");
                        SetScale(host, canvas, placement, 2f);
                        peer.Refresh();
                        var occupied = peer.Bounds;
                        Check(occupied.X == 48d && occupied.Y == 284d && occupied.Width == 1824d,
                            "The lower peer has the measured scaled width and preceding 8-reference-unit gap.");
                        subject.Refresh();
                        Check(!subject.PanelShown && !subject.NoticeShown && !subject.OriginalShown && subject.NoticeQueued &&
                            TopiaForgeToasts.Plain.Count == 1 && TopiaForgeToasts.Plain[0] == "Example Pack · HUD at top right" &&
                            peer.Bounds.Equals(occupied), "A fully occupied native viewport sends one package-and-dock Plain toast and keeps the neighbour still.");

                        var calls = subject.LayoutCalls;
                        for (var index = 0; index < 64; index++) subject.Refresh();
                        var start = GC.GetAllocatedBytesForCurrentThread();
                        for (var index = 0; index < 4096; index++) subject.Refresh();
                        Check(calls == subject.LayoutCalls && GC.GetAllocatedBytesForCurrentThread() == start &&
                            TopiaForgeToasts.Plain.Count == 1, "A queued native notice sends nothing and allocates zero bytes at rest.");

                        peer.SetDockPaint(false);
                        subject.Refresh();
                        Check(subject.PanelShown && subject.NoticeShown && !subject.NoticeQueued &&
                            TopiaForgeToasts.Plain.Count == 1 && peer.Bounds.Equals(occupied),
                            "Hiding peer paint exposes a slot and leaves its authored dock reserved within the same unfit episode.");
                        peer.SetDockPaint(true);
                        subject.Refresh();
                        Check(subject.NoticeQueued && !subject.PanelShown && TopiaForgeToasts.Plain.Count == 1,
                            "Losing the slot again does not send another toast during the same unfit episode.");
                        SetScale(host, canvas, placement, 1f);
                        subject.Refresh();
                        Check(subject.PanelShown && subject.OriginalShown && !subject.NoticeQueued &&
                            subject.Bounds.Width == 1080d && subject.Bounds.Height == 110d,
                            "A compatible viewport restores the authored HUD and ends its unfit episode.");
                        SetScale(host, canvas, placement, 2f);
                        peer.Refresh();
                        subject.Refresh();
                        Check(subject.NoticeQueued && TopiaForgeToasts.Plain.Count == 2,
                            "A later independent unfit episode gets its own notice.");
                    }

                    private static void TwoQueuedNativeHudRectsDoNotOccupyPaint()
                    {
                        using var placement = new UnityUiHudPlacement();
                        var canvas = new Canvas();
                        var host = new UiHost();
                        var first = new UnityUiSurface(placement, host, canvas, 1920, 1080, new UiText("One"));
                        var second = new UnityUiSurface(placement, host, canvas, 1920, 1080, new UiText("Two"),
                            corner: TopiaForgeCorner.BottomRight);
                        var painted = new UnityUiSurface(placement, host, canvas, 120, 128, new UiText("Peer"),
                            corner: TopiaForgeCorner.TopRight);
                        Check(first.ShowOriginal() && second.ShowOriginal() && painted.ShowOriginal(),
                            "The two broad authored HUDs and a small peer initially paint.");
                        var peerBounds = painted.Bounds;
                        first.SetBody(new string('A', 20000));
                        first.Refresh();
                        Check(first.NoticeQueued && !first.PanelShown && second.PanelShown,
                            "The first HUD must queue while the second paints across its safe area.");
                        second.SetBody(new string('B', 20000));
                        for (var frame = 0; frame < 4; frame++)
                        {
                            first.Refresh();
                            second.Refresh();
                        }

                        Check(first.PanelShown && second.PanelShown && first.NoticeShown && second.NoticeShown &&
                            !first.Bounds.Intersects(second.Bounds) && !first.Bounds.Intersects(peerBounds) &&
                            !second.Bounds.Intersects(peerBounds) && painted.Bounds.Equals(peerBounds),
                            "Two unfit native HUDs paint disjoint notices beside the existing peer once their authored paint is gone.");
                    }

                    private static void ClampedHudRetainsItsPendingArtworkNotice()
                    {
                        using var placement = new UnityUiHudPlacement();
                        var canvas = new Canvas();
                        var host = new UiHost();
                        var peer = new UnityUiSurface(placement, host, canvas, 180, 384, new UiText("Ready"));
                        var surface = new UnityUiSurface(placement, host, canvas, 140, 150, new UiText("Retained artwork"));
                        Check(surface.ShowOriginal(), "The artwork surface has a fitting original viewport.");
                        surface.SetImageUnavailable("/0");
                        Check(surface.NoticeMessage == UiImageNotice.Text, "Pending artwork has a specific readable notice.");
                        Check(peer.ShowOriginal(), "The peer shows before the scale change.");
                        SetScale(host, canvas, placement, 2f);
                        peer.Refresh();
                        surface.Refresh();
                        Check(surface.NoticeMessage == UiImageNotice.Text && surface.NoticeShown &&
                            !surface.Bounds.Intersects(peer.Bounds),
                            "A clamped retained HUD keeps its pending artwork notice in a measured alternative slot.");

                        surface.SetImageUnavailable(null);
                        Check(surface.NoticeMessage == UiHudLayoutNotice.Text && surface.LayoutFailure != null,
                            "Ready artwork leaves the layout-only diagnosis while the physical aperture is still too small.");
                        peer.SetDockShown(false);
                        surface.Refresh();
                        Check(surface.OriginalShown && !surface.NoticeShown, "Recovering the viewport restores the retained content.");
                    }

                    private static void NativeArtworkUsesTheNarrowOpening()
                    {
                        TopiaForgeToasts.Plain.Clear();
                        using var dock = new UnityUiHudPlacement();
                        var host = new UiHost();
                        var canvas = new Canvas();
                        SetScale(host, canvas, dock, 2f);
                        var left = new UnityUiSurface(dock, host, canvas, 400, 430, new UiText("Left"));
                        var right = new UnityUiSurface(dock, host, canvas, 252, 430, new UiText("Right"),
                            corner: TopiaForgeCorner.TopRight);
                        var subject = new UnityUiSurface(dock, host, canvas, 80, 80, new UiText("Ready"),
                            title: new string('W', 20), corner: TopiaForgeCorner.Top, remote: true);
                        subject.SetHudNoticeSource("Example Pack", UiHudAnchor.Top, UiHudVisibility.Always);
                        Check(left.ShowOriginal() && right.ShowOriginal() && subject.ShowOriginal(),
                            "Both peers and the compact authored HUD initially paint at scale 2.");
                        var leftBounds = left.Bounds;
                        var rightBounds = right.Bounds;
                        subject.SetImageUnavailable("/0");
                        Check(subject.NoticeShown && subject.NoticeMessage == UiImageNotice.Text &&
                            !subject.NoticeQueued && subject.Bounds.Width == 520d && subject.Bounds.Height == 152d &&
                            subject.Bounds.X == leftBounds.Right && subject.Bounds.Right == rightBounds.X &&
                            left.Bounds.Equals(leftBounds) && right.Bounds.Equals(rightBounds) && TopiaForgeToasts.Plain.Count == 0,
                            "The native image notice remeasures its height at 260 units and paints between unchanged peers.");
                    }

                    private static void NativeArtworkUsesTheWideOpening()
                    {
                        TopiaForgeToasts.Plain.Clear();
                        using var dock = new UnityUiHudPlacement();
                        var host = new UiHost();
                        var canvas = new Canvas();
                        var stack = new UiStack(new UiNode[]
                        {
                            new UiPositioned(new UiText("Retained body"), left: 920, top: 0, width: 64),
                        }, width: 1000, height: 20);
                        var subject = new UnityUiSurface(dock, host, canvas, 1080, 95, stack,
                            title: new string('F', 114), corner: TopiaForgeCorner.TopRight, remote: true);
                        subject.SetHudNoticeSource("Example Pack", UiHudAnchor.TopRight, UiHudVisibility.Always);
                        var peer = new UnityUiSurface(dock, host, canvas, 912, 396, new UiText("Reserved"),
                            corner: TopiaForgeCorner.TopRight);
                        Check(subject.ShowOriginal() && peer.ShowOriginal(), "The authored HUDs initially fit.");
                        subject.SetImageUnavailable("/0");
                        Check(subject.NoticeShown && TopiaForgeToasts.Plain.Count == 0,
                            "The original aperture paints the pending notice.");

                        SetScale(host, canvas, dock, 2f);
                        peer.Refresh();
                        var occupied = peer.Bounds;
                        subject.Refresh();
                        var safe = UiHudViewport.SafeArea(new UiLayoutRect(0, 0, Screen.width, Screen.height), 2d);
                        Check(occupied.Y - safe.Y == 206d && subject.NoticeShown && !subject.NoticeQueued &&
                            subject.NoticeMessage == UiImageNotice.Text && subject.Bounds.Width > 2 * UiImageNotice.RemoteHudWidth &&
                            subject.Bounds.Height == 200d && safe.Contains(subject.Bounds) &&
                            !subject.Bounds.Intersects(occupied) && peer.Bounds.Equals(occupied) && TopiaForgeToasts.Plain.Count == 0,
                            "A 103-unit band paints the measured wide native artwork notice beside its unchanged peer.");

                        var calls = subject.LayoutCalls;
                        for (var index = 0; index < 64; index++) subject.Refresh();
                        var start = GC.GetAllocatedBytesForCurrentThread();
                        for (var index = 0; index < 4096; index++) subject.Refresh();
                        Check(subject.LayoutCalls == calls && GC.GetAllocatedBytesForCurrentThread() == start &&
                            TopiaForgeToasts.Plain.Count == 0, "A settled wide native artwork notice allocates zero bytes.");
                    }

                    private static void NativeArtworkKeepsOneUnfitEpisode()
                    {
                        TopiaForgeToasts.Plain.Clear();
                        using var dock = new UnityUiHudPlacement();
                        var host = new UiHost();
                        var canvas = new Canvas();
                        var stack = new UiStack(new UiNode[]
                        {
                            new UiPositioned(new UiText("Private body"), left: 920, top: 0, width: 64),
                        }, width: 1000, height: 20);
                        var subject = new UnityUiSurface(dock, host, canvas, 1080, 90, stack,
                            title: new string('F', 114), corner: TopiaForgeCorner.TopRight, remote: true);
                        subject.SetHudNoticeSource("Example Pack", UiHudAnchor.TopRight, UiHudVisibility.Always);
                        var peer = new UnityUiSurface(dock, host, canvas, 912, 396, new UiText("Reserved"),
                            corner: TopiaForgeCorner.TopRight);
                        Check(subject.ShowOriginal() && peer.ShowOriginal(), "The authored layout initially fits.");
                        subject.SetImageUnavailable("/0");
                        Check(subject.NoticeShown && subject.NoticeMessage == UiImageNotice.Text &&
                            TopiaForgeToasts.Plain.Count == 0, "The original aperture paints the pending notice.");

                        SetScale(host, canvas, dock, 2f);
                        peer.Refresh();
                        subject.Refresh();
                        var occupied = peer.Bounds;
                        const string safe = "Example Pack · HUD at top right";
                        var aperture = UiHudViewport.SafeArea(new UiLayoutRect(0, 0, Screen.width, Screen.height), 2d);
                        Check(occupied.Y - aperture.Y == 196d && subject.NoticeQueued && !subject.PanelShown &&
                            subject.NoticeMessage == UiImageNotice.Text &&
                            TopiaForgeToasts.Plain.Count == 1 && TopiaForgeToasts.Plain[0] == safe,
                            "A 98-unit band queues one safe native artwork notice when the measured notice needs 100 units.");
                        subject.SetImageUnavailable(null);
                        Check(subject.NoticeQueued && !subject.PanelShown && subject.NoticeMessage == UiHudLayoutNotice.Text &&
                            peer.Bounds.Equals(occupied) && TopiaForgeToasts.Plain.Count == 1,
                            "Artwork readiness changes the diagnosis in the same unfit episode.");
                        subject.SetImageUnavailable("/0");
                        Check(subject.NoticeQueued && subject.NoticeMessage == UiImageNotice.Text && TopiaForgeToasts.Plain.Count == 1,
                            "A return to the image diagnosis keeps the same queued notice.");

                        SetScale(host, canvas, dock, 1f);
                        subject.SetImageUnavailable(null);
                        Check(subject.OriginalShown && subject.PanelShown && !subject.NoticeQueued,
                            "Fitting authored content ends the unfit episode.");
                        SetScale(host, canvas, dock, 2f);
                        peer.Refresh();
                        subject.Refresh();
                        Check(subject.NoticeQueued && TopiaForgeToasts.Plain.Count == 2 && TopiaForgeToasts.Plain[1] == safe,
                            "A later unfit episode queues one new safe notice.");
                    }

                    private static void LayoutNoticeUsesAnAvailableWiderSlot()
                    {
                        TopiaForgeToasts.Plain.Clear();
                        using var dock = new UnityUiHudPlacement();
                        var host = new UiHost();
                        var canvas = new Canvas();
                        var title = new string('F', 114);
                        var source = new UiStack(new UiNode[]
                        {
                            new UiPositioned(new UiText("Readiness"), left: 920, top: 0, width: 64),
                        }, width: 1000, height: 20);
                        var subject = new UnityUiSurface(dock, host, canvas, 1080, 95, source,
                            title: title, corner: TopiaForgeCorner.TopRight);
                        subject.SetHudNoticeSource("Example Pack", UiHudAnchor.TopRight, UiHudVisibility.Always);
                        var peer = new UnityUiSurface(dock, host, canvas, 912, 396, new UiText("Reserved"),
                            corner: TopiaForgeCorner.TopRight);
                        Check(subject.ShowOriginal() && peer.ShowOriginal(), "Both authored HUDs fit at scale 1.");
                        SetScale(host, canvas, dock, 2f);
                        peer.Refresh();
                        var obstacle = peer.Bounds;
                        var safe = UiHudViewport.SafeArea(new UiLayoutRect(0, 0, Screen.width, Screen.height), 2d);
                        var caption = new TMPro.TMP_Text(title, 24f);
                        var label = new TMPro.TMP_Text(UiHudLayoutNotice.Text, 20f);
                        double Measure(double width) => 24d + 8d +
                            caption.GetPreferredValues(title, (float)(width - 24d), 0f).y +
                            label.GetPreferredValues(UiHudLayoutNotice.Text, (float)(width - 24d), 0f).y;
                        const double widerWidth = 650d;
                        var measured = new UiLayoutRect(safe.X, safe.Y, widerWidth * 2d, Measure(widerWidth) * 2d);
                        var openingHeight = (obstacle.Y - safe.Y) * 0.5d;
                        Check(Measure(UiHudLayoutNotice.PreferredWidth) > openingHeight &&
                            safe.Contains(measured) && !measured.Intersects(obstacle),
                            "The shallow opening requires a wider layout notice.");

                        subject.Refresh();
                        Check(subject.NoticeShown && !subject.NoticeQueued && !subject.OriginalShown &&
                            subject.NoticeMessage == UiHudLayoutNotice.Text && safe.Contains(subject.Bounds) &&
                            !subject.Bounds.Intersects(obstacle) && obstacle.Equals(peer.Bounds) && TopiaForgeToasts.Plain.Count == 0,
                            "A readable layout notice paints when a wider safe opening exists.");
                        Check(ClearsGameHudLabels(subject.Bounds), "The alternate notice clears the game HUD labels.");

                        for (var index = 0; index < 64; index++) subject.Refresh();
                        var calls = subject.LayoutCalls;
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        for (var index = 0; index < 4096; index++) subject.Refresh();
                        Check(GC.GetAllocatedBytesForCurrentThread() == before && subject.LayoutCalls == calls,
                            "A settled wide layout notice allocates 0 B and keeps its measurement.");
                    }

                    private static bool ClearsGameHudLabels(UiLayoutRect bounds) =>
                        !bounds.Intersects(new UiLayoutRect(24, 20, 280, 48)) &&
                        !bounds.Intersects(new UiLayoutRect(820, 16, 280, 36)) &&
                        !bounds.Intersects(new UiLayoutRect(1456, 20, 440, 72));

                    private static void DockMarginsFollowTheAssignedUnits()
                    {
                        var safe = UiHudViewport.SafeArea(new UiLayoutRect(0, 0, 1920, 1080), 2d);
                        Check(safe.X == 48d && safe.Y == 48d &&
                            Math.Abs((safe.Height + 1080 * UiHudViewport.SubtitleFraction) / 2d - 492d) < 0.001d &&
                            safe.Bottom < 1080d - 48d, "A 2x HUD has 492 reference units before game reservations.");

                        var legacy = new TopiaForgeHudDock();
                        var third = legacy.Add(TopiaForgeCorner.TopLeft, 80, 2f);
                        legacy.SetShown(third, true);
                        Check(legacy.TryGetPosition(third, out var x, out var y) && x * 2f == 18f && y * 2f == -18f,
                            "Legacy native docks keep the unscaled 18-unit perimeter.");

                        using var placement = new UnityUiHudPlacement();
                        var normalCanvas = new Canvas();
                        var largeCanvas = new Canvas { Scale = 2f };
                        var first = new UnityUiSurface(placement, new UiHost(), normalCanvas, 200, 80, new UiText("First"));
                        var second = new UnityUiSurface(placement, new UiHost { EffectiveUiScale = 2f }, largeCanvas, 200, 80, new UiText("Second"));
                        first.ShowOriginal();
                        second.ShowOriginal();
                        Check(first.Bounds.X == 24d && second.Bounds.X == 48d && second.Bounds.Y == first.Bounds.Bottom + 8d,
                            "Mixed SDK scales use their own safe perimeter and the preceding panel's measured gap.");

                        var lower = new UnityUiSurface(placement, new UiHost { EffectiveUiScale = 2f }, largeCanvas,
                            200, 80, new UiText("Lower"), corner: TopiaForgeCorner.BottomRight);
                        Check(lower.ShowOriginal() && Math.Abs(lower.Bounds.Bottom - safe.Bottom) < .001d &&
                            Math.Abs(lower.Bounds.Right - safe.Right) < .001d,
                            "The bottom-right stack keeps its original pivot against the physical subtitle reserve.");
                    }

                    private static void HudArtReadinessRestoresPlacement()
                    {
                        foreach (var remote in new[] { false, true })
                        using (var placement = new UnityUiHudPlacement())
                        {
                            var surface = new UnityUiSurface(placement, new UiHost(), new Canvas(), 120, 80, new UiText("OK"), remote: remote);
                            Check(surface.ShowOriginal(), "The retained HUD begins with its original composition.");
                            var original = surface.Bounds;
                            surface.SetImageUnavailable("/0:card");
                            Check(surface.NoticeShown && !surface.OriginalShown && surface.LayoutFailure == null &&
                                surface.NoticeCaption == "Readiness" && surface.NoticeMessage == UiImageNotice.Text &&
                                surface.Bounds.Width == UiImageNotice.RemoteHudWidth &&
                                surface.Bounds.Height == UiImageNotice.RemoteHudHeight,
                                "Image readiness invalidation uses the shared alternate HUD slot and keeps the artwork diagnostic distinct.");
                            var calls = surface.LayoutCalls;
                            for (var i = 0; i < 100; i++) surface.Refresh();
                            var start = GC.GetAllocatedBytesForCurrentThread();
                            for (var i = 0; i < 4096; i++) surface.Refresh();
                            Check(surface.LayoutCalls == calls && GC.GetAllocatedBytesForCurrentThread() == start,
                                "A settled image notice reuses its measurements without allocating.");
                            surface.SetImageUnavailable(null);
                            Check(surface.OriginalShown && !surface.NoticeShown && surface.Bounds.Equals(original),
                                "Art readiness restores the original dock and content exactly.");
                        }
                    }

                    private static void SetScale(UiHost host, Canvas canvas, UnityUiHudPlacement placement, float scale)
                    {
                        host.EffectiveUiScale = scale;
                        canvas.Scale = scale;
                        placement.Refresh();
                    }

                    private static UiLayoutRect Bounds(RectTransform rect)
                    {
                        var corners = new Vector3[4];
                        rect.GetWorldCorners(corners);
                        return new UiLayoutRect(corners[0].x, Screen.height - corners[1].y,
                            corners[2].x - corners[0].x, corners[1].y - corners[0].y);
                    }

                    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

                    private sealed class ProbeViewportCallback { public string ClockEpoch() => "epoch"; }
                    private sealed class ProbeLifetime
                    {
                        public bool IsStopping => false;
                        public IDisposable Track(IDisposable resource) => resource;
                    }
                    private sealed class ProbePresence
                    {
                        public bool IsLive => true;
                        public bool IsLost => false;
                        public bool IsVisible => true;
                    }
                    private sealed class ProbeLogger
                    {
                        public void Warn(string message) { }
                        public void Error(Exception error, string message) => throw new InvalidOperationException(message, error);
                    }
                    private sealed class ProbeLayoutRenderer
                    {
                        private readonly UiNode source;
                        public int Calls;
                        internal bool BindingChanged { get; set; }
                        internal string? UnavailableImagePath { get; set; }
                        internal bool HasRetainedImageNotice => UnavailableImagePath != null;
                        internal ProbeLayoutRenderer(UiNode source) => this.source = source;
                        internal bool RefreshKeyGlyphs()
                        {
                            var changed = BindingChanged;
                            BindingChanged = false;
                            return changed;
                        }

                        internal void Rearrange(UiLayoutConstraints box, UiThemeDescriptor? theme)
                        {
                            Calls++;
                            UiLayoutEngine.Arrange(source, box, (_, _, _) => new UiLayoutLeafMeasurement(new UiLayoutExtent(22d, 20d), UiLayoutPaintKind.None),
                                1d, highContrast: false);
                        }
                    }

                    private sealed partial class UnityUiSurface : IUiSurface
                    {
                        private UnityUiHudPlacement? placement;
                        private int dockHandle;
                        private readonly TopiaForgeContainer compositionParent = new TopiaForgeContainer();
                        private readonly TopiaForgeContainer? compositionRoot;
                        private readonly TopiaForgeWidget widget = new TopiaForgeWidget();
                        private readonly TopiaForgeLabel body = new TopiaForgeLabel("", TopiaForgeTextStyle.Body);
                        private readonly RectTransform panel;
                        private readonly ProbeLayoutRenderer? layoutRenderer;
                        private readonly TopiaForgeWindow? window;
                        private readonly TopiaForgeFullscreenTool? fullscreen;
                        private readonly ProbeLifetime lifetime = new ProbeLifetime();
                        private readonly ProbePresence presence = new ProbePresence();
                        private readonly ProbeLogger logger = new ProbeLogger();
                        private readonly ProbeViewportCallback callbacks = new ProbeViewportCallback();
                        private readonly UiThemeDescriptor? declaredTheme;
                        private readonly bool remoteComposition;
                        private int bodyVersion;
                        private string? sentBody;
                        private int artworkVersion;
                        private TopiaForgeContainer? layoutFallback;
                        private ContentStamp committedStamp;
                        public string Id { get; } = "hud";
                        public bool IsVisible => presence.IsVisible;
                        private GameObject CanvasRoot => widget.Go;
                        private Rect OuterScrollViewport => default;
                        private float scrollAperture = 100f;
                        private UiLayoutConstraints MeasureScrollingContent(TopiaForgeContainer parent, double width, VerticalLayoutGroup? group) =>
                            window == null && fullscreen == null
                                ? throw new InvalidOperationException("The HUD fixture has no scroll viewport.")
                                : UiLayoutConstraints.ForVerticalScroll(width, scrollAperture);
                        private void RememberOuterScrollOffset() { }
                        private void RestoreOuterScrollOffset() { }
                        internal UnityUiSurface(TopiaForgeWidget view)
                        {
                            widget = view;
                            window = view as TopiaForgeWindow;
                            fullscreen = view as TopiaForgeFullscreenTool;
                            panel = view.Rect;
                            compositionRoot = new TopiaForgeContainer();
                            compositionParent.Rect.sizeDelta = new Vector2(740, 0);
                            view.SetShown(false);
                        }
                        internal void VerifyScrollingLayout()
                        {
                            // Unity skips layout components on an inactive hierarchy. The fixture starts with a stale aperture.
                            var retainedReveals = 0;
                            widget.Go.ActiveChanged = shown =>
                            {
                                if (shown && compositionRoot!.Go.activeSelf) retainedReveals++;
                            };
                            var contentVisible = false;
                            widget.Rect.RebuildLayout = () =>
                            {
                                Check(compositionRoot!.Go.activeSelf == contentVisible,
                                    "Measuring chrome must not change the retained tree's hierarchy visibility.");
                                scrollAperture = 356f;
                            };
                            var graph = new UiGraphCanvas("relay-map", Array.Empty<UiGraphNode>(), Array.Empty<UiGraphEdge>(),
                                _ => { }, height: 320f);
                            var area = MeasureContentArea(compositionParent, widget);
                            var measured = UiLayoutEngine.Arrange(graph, area, (_, _, _) => throw new InvalidOperationException());
                            Check(measured.Size.Height == 320d && !widget.Go.activeSelf && compositionRoot!.Go.activeSelf &&
                                retainedReveals == 0,
                                "Sizing a hidden scroll surface admits its graph and restores its hierarchy state.");

                            contentVisible = true;
                            widget.SetShown(true);
                            MeasureContentArea(compositionParent, widget);
                            Check(widget.Go.activeSelf && compositionRoot.Go.activeSelf && retainedReveals == 1,
                                "Measuring an already visible scroll surface preserves visibility.");
                            widget.SetShown(false);
                            var failed = false;
                            widget.Rect.RebuildLayout = () => throw new InvalidOperationException("failed layout measurement");
                            try { MeasureContentArea(compositionParent, widget); }
                            catch (InvalidOperationException failure) when (failure.Message == "failed layout measurement") { failed = true; }
                            Check(failed && !widget.Go.activeSelf && compositionRoot.Go.activeSelf && retainedReveals == 1,
                                "A failed hidden scroll measurement restores the hierarchy state.");
                        }
                        internal UnityUiSurface(UnityUiHudPlacement placement, UiHost host, Canvas canvas, float width, float height,
                            UiNode source, string title = "Readiness", string bodyText = "", TopiaForgeCorner corner = TopiaForgeCorner.TopLeft, bool remote = false)
                        {
                            remoteComposition = remote;
                            compositionRoot = new TopiaForgeContainer();
                            layoutRenderer = new ProbeLayoutRenderer(source);
                            this.placement = placement;
                            widget.Rect.Canvas = canvas;
                            widget.Rect.sizeDelta = new Vector2(width, height);
                            panel = widget.Rect;
                            compositionParent.Rect = widget.Rect;
                            compositionParent.Go.Component = new VerticalLayoutGroup();
                            dockHandle = placement.Add(host, widget.Rect, corner, height);
                            hudTitleText = title;
                            body.Go.GetComponent<TMPro.TMP_Text>()!.text = bodyText;
                            hudTitle = new TopiaForgeLabel(hudTitleText, TopiaForgeTextStyle.Heading).Ellipsis();
                        }
                        private UnityUiSurface(string id, TopiaForgeHudLayer layer, TopiaForgeLabel body, TopiaForgeContainer parent)
                        {
                            Id = id;
                            widget = layer;
                            panel = layer.ActivePanel!.Rect;
                            compositionParent = parent;
                            this.body = body;
                        }
                        internal static UnityUiSurface ForHud(string id, TopiaForgeHudLayer layer, TopiaForgeLabel body,
                            TopiaForgeContainer parent, bool followsGameplay, bool gameplay, ProbeLifetime lifetime, ProbeLogger logger,
                            Action<UnityUiSurface> release) => new UnityUiSurface(id, layer, body, parent);
                        internal static UnityUiSurface ForWindow(string id, TopiaForgeWindow window, TopiaForgeLabel body,
                            TopiaForgeContainer parent, ProbeLifetime lifetime, ProbeLogger logger, Action<UnityUiSurface> release) =>
                            throw new NotSupportedException();
                        internal static UnityUiSurface ForFullscreen(string id, TopiaForgeFullscreenTool window, TopiaForgeLabel body,
                            TopiaForgeContainer parent, ProbeLifetime lifetime, ProbeLogger logger, Action<UnityUiSurface> release) =>
                            throw new NotSupportedException();
                        internal void AttachLifetimeLease(IDisposable lease) { }
                        internal void InitializeTheme(UiThemeRef? theme, bool remote, UiThemeDescriptor? descriptor,
                            object? scope, object packages, object? catalogue) { }
                        internal void SetClock(Func<double> clock, Func<string> epoch) { }
                        internal void AttachInput(UiSurfaceInputContext input) { }
                        public OperationResult<bool> SetContent(UiNode content) => throw new NotSupportedException();
                        public void Show() => ShowNativeView();
                        public void Hide() => HideNativeView();
                        public void Dispose() => HideNativeView();
                        private void ResetGraphConnections(bool retire) { }

                        /* surface chrome */
                        /* surface presence */

                        internal Action LayoutFrame()
                        {
                            var frame = CanvasRoot.GetComponent<UiSurfaceLayoutFrame>();
                            Check(frame != null, "The actual HUD factory attaches its retained layout observer.");
                            return (Action)typeof(UiSurfaceLayoutFrame).GetMethod("LateUpdate",
                                BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Action), frame!);
                        }
                        internal bool Fits()
                        {
                            try { layoutRenderer!.Rearrange(MeasureContentArea(compositionParent, widget), null); return true; }
                            catch (UiLayoutException) { return false; }
                        }
                        internal bool ShowOriginal()
                        {
                            placement!.SetShown(dockHandle, true);
                            Refresh();
                            return OriginalShown;
                        }
                        internal void Refresh() => RefreshChangedLayout();
                        internal void ChangeBinding() => layoutRenderer.BindingChanged = true;
                        internal void SetImageUnavailable(string? path)
                        {
                            layoutRenderer.UnavailableImagePath = path;
                            artworkVersion++;
                            Refresh();
                        }
                        private void RefreshRetainedComposition(TopiaForgeContainer root, string clockEpoch) => UpdateRetainedLayout(root);
                        private void SuspendGraphConnections() { }
                        private void SyncGraphConnections() { }
                        internal UiLayoutRect Bounds => OwnerUiService.Bounds(panel);
                        internal UiLayoutException? LayoutFailure => layoutProblem;
                        internal string? NoticeCaption => fallbackTitle?.Go.GetComponent<TMPro.TMP_Text>()?.text;
                        internal string? NoticeMessage => fallbackMessage?.Go.GetComponent<TMPro.TMP_Text>()?.text;
                        internal bool NoticeShown => layoutFallback?.Go.activeSelf == true;
                        internal bool NoticeQueued => hudNoticeOverflowing;
                        internal bool PanelShown => panel.gameObject.activeSelf;
                        internal bool BodyShown => body.Go.activeSelf;
                        internal int BodyMeasureCalls => body.Go.GetComponent<TMPro.TMP_Text>()!.MeasureCalls;
                        internal void DropViewport() => panel.Canvas = null;
                        internal void SetDockShown(bool shown) => placement!.SetShown(dockHandle, shown);
                        internal void SetDockPaint(bool shown) => placement!.SetPanelPaintShown(dockHandle, shown);
                        internal bool OriginalShown => compositionRoot?.Go.activeSelf == true;
                        internal int LayoutCalls => layoutRenderer?.Calls ?? 0;
                        private readonly record struct ContentStamp(TopiaForgeWidget View, string ClockEpoch, int BodyVersion,
                            int ArtworkVersion, int DockVersion, Rect OuterScrollViewport)
                        {
                            internal bool Matches(in ContentStamp other) => this == other;
                        }
                    }
                }
            }
            """;
    }
}
