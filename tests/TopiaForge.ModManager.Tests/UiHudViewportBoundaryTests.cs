using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
                "src/TopiaForge.ModManager/UnityUiSurface.Layout.cs",
                "src/TopiaForge.ModManager/UiHudAnchors.cs",
                "src/TopiaForge.Mods.UnityUi/Core/TopiaForgeHudLayout.cs",
                "src/TopiaForge.Mods.UnityUi/Layout/TopiaForgeAnchors.cs",
            };
            // Exercise physical projection, the real box solver and retained refresh through controlled Unity metrics.
            var root = Program.FindRepoRoot();
            var trees = sources.Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)), path: file))
                .Append(CSharpSyntaxTree.ParseText(Probe, path: "HudViewportBoundary.cs"));
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
        }

        private const string Probe = """
            using System;
            using System.Collections.Generic;
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
                    public string name = "";
                    internal object? Component;
                    public void SetActive(bool shown) => activeSelf = shown;
                    public T AddComponent<T>() where T : new() => new T();
                    public T? GetComponent<T>() where T : class => Component as T;
                }
                public class RectTransform
                {
                    public Vector2 anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta, offsetMin, offsetMax;
                    public GameObject gameObject = new GameObject();
                    public int childCount => 0;
                    public Rect rect => new Rect(0, 0, sizeDelta.x, sizeDelta.y);
                    public Canvas? Canvas;
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
                public static class LayoutRebuilder { public static void ForceRebuildLayoutImmediate(RectTransform value) { } }
                public static class LayoutUtility { public static float GetPreferredHeight(RectTransform value) => value.rect.height; }
            }

            namespace TMPro
            {
                public sealed class TMP_Text
                {
                    public TMP_Text(string text, float height) { this.text = text; Height = height; }
                    public string text;
                    public float Height;
                    public bool Wrap = true;
                    public Vector2 GetPreferredValues(string value, float width, float height)
                    {
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
                public static class TopiaForgeTokens { public const float SafeMargin = 18f, ScrollGutter = 12f; }
                public static class TopiaForgeTheme { public static event Action? Changed; }
                internal enum TopiaForgeTone { Neutral }
                internal static class TopiaForgeToasts
                {
                    internal static readonly List<string> Plain = new();
                    internal static void ShowPlain(string text, TopiaForgeTone tone) => Plain.Add(text);
                }
                public sealed class UiHost { public float EffectiveUiScale = 1f; }
                public sealed class TopiaForgeLayerSlot { internal void SelectTheme(UiThemeDescriptor descriptor) { } }
                public sealed class ProbeTheme { public int Surface => 0; }
                public class TopiaForgeWidget
                {
                    public RectTransform Rect { get; set; } = new RectTransform();
                    public GameObject Go => Rect.gameObject;
                    public ProbeTheme ResolvedTheme = new ProbeTheme();
                    public void SetShown(bool shown) => Go.activeSelf = shown;
                }
                public sealed class TopiaForgeLabel : TopiaForgeWidget
                {
                    public TopiaForgeLabel(string text, TopiaForgeTextStyle role)
                    { Go.Component = new TMPro.TMP_Text(text, role == TopiaForgeTextStyle.Heading ? 24f : 20f); }
                    public TopiaForgeLabel PlainText() => this;
                    public TopiaForgeLabel Ellipsis() { Go.GetComponent<TMPro.TMP_Text>()!.Wrap = false; return this; }
                }
                public sealed class TopiaForgeContainer : TopiaForgeWidget
                {
                    public TopiaForgeContainer Column(TopiaForgeGap gap, TopiaForgeGap padding) => new TopiaForgeContainer();
                    public TopiaForgeContainer FreeImage(string name) => new TopiaForgeContainer();
                    public TopiaForgeLabel Label(string text, TopiaForgeTextStyle role) => new TopiaForgeLabel(text, role);
                    public void SetColor(int color) { }
                    public void Destroy() => SetShown(false);
                }
            }

            namespace TopiaForge.ModManager
            {
                internal static class UiThemeScope { internal static UiThemeDescriptor? Ready(UiThemeDescriptor? value) => value; }
                internal sealed partial class OwnerUiService
                {
                    public static void ProbeHudViewport()
                    {
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
                            Check(surface.NoticeShown && surface.LayoutFailure?.Path == "/0/0" && !surface.OriginalShown &&
                                surface.Bounds.X == 48d && surface.Bounds.Width == 1824d && TopiaForgeToasts.Plain.Count == 0, "The native HUD displays its retained layout notice inside physical screen bounds. " +
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
                        FullyOccupiedHudUsesOnePlainToast();
                        DockMarginsFollowTheAssignedUnits();
                        Console.WriteLine("Native HUD viewport boundary passed for 24-unit SDK margins, retained notices, one plain toast per unfit episode and 4,096 settled frames at 0 B.");
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

                        peer.SetDockShown(false);
                        subject.Refresh();
                        Check(subject.PanelShown && subject.NoticeShown && !subject.NoticeQueued &&
                            TopiaForgeToasts.Plain.Count == 1, "An open measured slot shows the panel notice within the same unfit episode.");
                        peer.SetDockShown(true);
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
                    private sealed class ProbeLifetime { public bool IsStopping => false; }
                    private sealed class ProbePresence { public bool IsLive => true; }
                    private sealed class ProbeLogger
                    {
                        public void Warn(string message) { }
                        public void Error(Exception error, string message) => throw new InvalidOperationException(message, error);
                    }
                    private sealed class ProbeLayoutRenderer
                    {
                        private readonly UiNode source;
                        public int Calls;
                        internal ProbeLayoutRenderer(UiNode source) => this.source = source;
                        internal void Rearrange(UiLayoutConstraints box, UiThemeDescriptor? theme)
                        {
                            Calls++;
                            UiLayoutEngine.Arrange(source, box, (_, _, _) => new UiLayoutLeafMeasurement(new UiLayoutExtent(22d, 20d), UiLayoutPaintKind.None),
                                1d, highContrast: false);
                        }
                    }

                    private sealed partial class UnityUiSurface
                    {
                        private readonly UnityUiHudPlacement placement;
                        private readonly int dockHandle;
                        private readonly TopiaForgeContainer compositionParent = new TopiaForgeContainer();
                        private readonly TopiaForgeContainer compositionRoot = new TopiaForgeContainer();
                        private readonly TopiaForgeWidget widget = new TopiaForgeWidget();
                        private readonly TopiaForgeLabel body = new TopiaForgeLabel("", TopiaForgeTextStyle.Body);
                        private readonly UiNode admitted;
                        private readonly ProbeLayoutRenderer layoutRenderer;
                        private readonly object? window, fullscreen;
                        private readonly ProbeLifetime lifetime = new ProbeLifetime();
                        private readonly ProbePresence presence = new ProbePresence();
                        private readonly ProbeLogger logger = new ProbeLogger();
                        private readonly ProbeViewportCallback callbacks = new ProbeViewportCallback();
                        private readonly UiThemeDescriptor? declaredTheme;
                        private readonly bool remoteComposition;
                        private readonly int bodyVersion;
                        private TopiaForgeContainer? layoutFallback;
                        private ContentStamp committedStamp;
                        private string Id => "hud";
                        private bool IsVisible => true;
                        private GameObject CanvasRoot => widget.Go;
                        internal UnityUiSurface(UnityUiHudPlacement placement, UiHost host, Canvas canvas, float width, float height,
                            UiNode source, string title = "Readiness", string bodyText = "", TopiaForgeCorner corner = TopiaForgeCorner.TopLeft)
                        {
                            admitted = source;
                            layoutRenderer = new ProbeLayoutRenderer(source);
                            this.placement = placement;
                            widget.Rect.Canvas = canvas;
                            widget.Rect.sizeDelta = new Vector2(width, height);
                            compositionParent.Rect = widget.Rect;
                            compositionParent.Go.Component = new VerticalLayoutGroup();
                            dockHandle = placement.Add(host, widget.Rect, corner, height);
                            hudTitleText = title;
                            body.Go.GetComponent<TMPro.TMP_Text>()!.text = bodyText;
                            hudTitle = new TopiaForgeLabel(hudTitleText, TopiaForgeTextStyle.Heading).Ellipsis();
                        }
                        internal bool Fits()
                        {
                            try { layoutRenderer.Rearrange(MeasureContentArea(compositionParent, widget), null); return true; }
                            catch (UiLayoutException) { return false; }
                        }
                        internal bool ShowOriginal()
                        {
                            placement.SetShown(dockHandle, true);
                            Refresh();
                            return OriginalShown;
                        }
                        internal void Refresh() => RefreshChangedLayout();
                        private void RefreshRetainedComposition(TopiaForgeContainer root, string clockEpoch) => UpdateRetainedLayout(root);
                        internal UiLayoutRect Bounds => OwnerUiService.Bounds(widget.Rect);
                        internal UiLayoutException? LayoutFailure => layoutProblem;
                        internal string? NoticeCaption => fallbackTitle?.Go.GetComponent<TMPro.TMP_Text>()?.text;
                        internal string? NoticeMessage => fallbackMessage?.Go.GetComponent<TMPro.TMP_Text>()?.text;
                        internal bool NoticeShown => layoutFallback?.Go.activeSelf == true;
                        internal bool NoticeQueued => hudNoticeOverflowing;
                        internal bool PanelShown => widget.Go.activeSelf;
                        internal void SetDockShown(bool shown) => placement.SetShown(dockHandle, shown);
                        internal bool OriginalShown => compositionRoot.Go.activeSelf;
                        internal int LayoutCalls => layoutRenderer.Calls;
                        private readonly record struct ContentStamp(TopiaForgeWidget View, string ClockEpoch, int BodyVersion, int DockVersion)
                        {
                            internal bool Matches(in ContentStamp other) => this == other;
                        }
                    }
                }
            }
            """;
    }
}
