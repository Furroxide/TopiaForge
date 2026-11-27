namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiOverlayNativeTests
    {
        internal const string Boundaries = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;

            namespace UnityEngine
            {
                public class Object
                {
                    public string name = "";
                    public static void DontDestroyOnLoad(GameObject root) => root.Persistent = true;
                }
                public class Component : Object
                {
                    public GameObject gameObject = null!;
                    public Transform transform => gameObject.transform;
                    public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
                }
                public class MonoBehaviour : Component
                {
                    public bool enabled = true;
                    public bool isActiveAndEnabled => enabled && gameObject.activeInHierarchy;
                }
                public class Transform : Component
                {
                    public Vector3 localScale;
                    internal Transform? Parent;
                    internal readonly List<GameObject> Children = new List<GameObject>();
                    public bool IsChildOf(Transform candidate) => this == candidate || Parent?.IsChildOf(candidate) == true;
                }
                public sealed class RectTransform : Transform
                {
                    public Vector2 anchorMin, anchorMax, offsetMin, offsetMax, sizeDelta, anchoredPosition;
                }
                public sealed partial class GameObject : Object
                {
                    private readonly List<Component> components = new List<Component>();
                    public GameObject(string name, params Type[] _) { this.name = name; Rect = new RectTransform { gameObject = this }; }
                    public RectTransform Rect { get; }
                    public Transform transform => Rect;
                    public bool activeSelf { get; private set; } = true;
                    public bool activeInHierarchy => activeSelf && (Rect.Parent?.gameObject.activeInHierarchy ?? true);
                    internal bool Persistent;
                    internal int ComponentQueries;
                    public void SetActive(bool active) => activeSelf = active;
                    internal GameObject Child(string name)
                    {
                        var child = new GameObject(name);
                        child.Rect.Parent = Rect;
                        Rect.Children.Add(child);
                        return child;
                    }
                    internal GameObject Find(string name) => Rect.Children.Find(child => child.name == name)!;
                    public T AddComponent<T>() where T : Component
                    {
                        var component = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        component.gameObject = this;
                        components.Add(component);
                        return component;
                    }
                    public T GetComponent<T>() where T : Component
                    {
                        foreach (var component in components)
                            if (component is T typed) return typed;
                        return null!;
                    }
                    internal T? GetComponentInParent<T>() where T : Component
                    {
                        for (GameObject? current = this; current != null; current = current.Rect.Parent?.gameObject)
                            if (current.GetComponent<T>() is { } component) return component;
                        return null;
                    }
                    public void GetComponentsInChildren<T>(bool includeInactive, List<T> results) where T : Component
                    {
                        ComponentQueries++;
                        results.Clear();
                        Collect(includeInactive, results);
                    }
                    private void Collect<T>(bool includeInactive, List<T> results) where T : Component
                    {
                        if (!includeInactive && !activeInHierarchy) return;
                        foreach (var component in components)
                            if (component is T typed) results.Add(typed);
                        foreach (var child in Rect.Children) child.Collect(includeInactive, results);
                    }
                }
                public partial struct Vector2
                {
                    public float x, y;
                    public Vector2(float x, float y) { this.x = x; this.y = y; }
                    public static Vector2 zero => default;
                    public static Vector2 one => new Vector2(1, 1);
                }
                public readonly record struct Vector3(float x, float y, float z)
                {
                    public static Vector3 one => new Vector3(1f, 1f, 1f);
                    public static Vector3 operator *(Vector3 source, float scale) =>
                        new Vector3(source.x * scale, source.y * scale, source.z * scale);
                }
                public readonly record struct Rect(float x, float y, float width, float height)
                {
                    public float xMin => x;
                    public float xMax => x + width;
                    public float yMin => y;
                    public float yMax => y + height;
                    public Vector2 center => new Vector2(x + width / 2f, y + height / 2f);
                }
                public record struct Color(float r, float g, float b, float a)
                {
                    public static Color clear => default;
                }
                public readonly record struct Color32(byte r, byte g, byte b, byte a)
                {
                    public static implicit operator Color(Color32 source) =>
                        new Color(source.r / 255f, source.g / 255f, source.b / 255f, source.a / 255f);
                }
                public class Sprite : Object { }
                public static class Screen
                {
                    public static int width = 1920, height = 1080;
                    public static Rect safeArea = new Rect(0, 0, width, height);
                }
                public enum RenderMode { ScreenSpaceOverlay }
                public sealed partial class Canvas : Component { public RenderMode renderMode; public int sortingOrder; }
                public sealed class CanvasGroup : Component { public float alpha = 1f; public bool interactable = true; }
                public enum CursorLockMode { None, Locked }
                public static class Cursor { public static CursorLockMode lockState; public static bool visible; }
                public static partial class Time { public static float unscaledTime; public static double unscaledTimeAsDouble; }
                public static class Application { public static event Action? quitting; public static void Quit() => quitting?.Invoke(); }
                public static class Mathf
                {
                    public const float PI = MathF.PI;
                    public static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
                    public static float Max(float a, float b) => Math.Max(a, b);
                    public static float Sin(float value) => MathF.Sin(value);
                    public static float Sqrt(float value) => MathF.Sqrt(value);
                    public static float Ceil(float value) => MathF.Ceiling(value);
                }
            }

            namespace UnityEngine.UI
            {
                public class Graphic : MonoBehaviour { public Color color; public bool raycastTarget = true; }
                public class Image : Graphic
                {
                    public enum Type { Sliced }
                    public Type type;
                    public RectTransform rectTransform => gameObject.Rect; public Sprite? sprite;
                }
                public sealed class GraphicRaycaster : MonoBehaviour { }
                public sealed class RectMask2D : Component { }
                public sealed class LayoutElement : Component { public bool ignoreLayout; }
                public sealed class ContentSizeFitter : Component
                {
                    public enum FitMode { PreferredSize }
                    public FitMode verticalFit;
                }
                public class Selectable : MonoBehaviour
                {
                    internal bool Interactable = true;
                    public bool interactable { get => Interactable; set => Interactable = value; }
                    public bool IsInteractable() => interactable && gameObject.GetComponentInParent<CanvasGroup>()?.interactable != false;
                    public void Select() => EventSystem.current?.SetSelectedGameObject(gameObject);
                }
                public sealed class CanvasScaler : Component
                {
                    public enum ScaleMode { ScaleWithScreenSize }
                    public ScaleMode uiScaleMode;
                    public Vector2 referenceResolution;
                    public float matchWidthOrHeight;
                }
            }

            namespace UnityEngine.EventSystems
            {
                public sealed class EventSystem
                {
                    public static EventSystem? current = new EventSystem();
                    public GameObject? currentSelectedGameObject;
                    public void SetSelectedGameObject(GameObject? selection) => currentSelectedGameObject = selection;
                }
            }

            namespace TMPro
            {
                public sealed class TMP_FontAsset
                {
                    internal float Advance;
                    internal string? Coverage;
                    internal TMP_FontAsset? Fallback;
                    internal (float pointSize, float ascentLine, float descentLine, float scale) faceInfo => default;

                    internal float AdvanceFor(char glyph, float fallback) =>
                        Coverage != null && Coverage.IndexOf(glyph) < 0
                            ? Fallback?.AdvanceFor(glyph, fallback) ?? fallback : Advance > 0f ? Advance : fallback;
                }
                [Flags] public enum FontStyles { UpperCase = 1 }
                public sealed class TMP_TextInfo { public int lineCount; }
                public enum TextOverflowModes { Ellipsis }
                public sealed class TextMeshProUGUI : TMP_Text { public TextOverflowModes overflowMode; }
                public partial class TMP_Text : Component
                {
                    public string text = string.Empty;
                    internal bool richText = true;
                    internal Color color;
                    public TMP_FontAsset? font = new TMP_FontAsset();
                    internal TopiaForgeTextStyle Style;
                    internal FontStyles fontStyle;
                    internal float fontSize, characterSpacing;
                    internal static int LayoutRequests;
                    public RectTransform rectTransform => gameObject.Rect;
                    public Vector2 GetPreferredValues() => GetPreferredValues(text, float.PositiveInfinity, 0f);
                    public Vector2 GetPreferredValues(string value, float width, float height)
                    {
                        LayoutRequests++;
                        var advance = Style == TopiaForgeTextStyle.Banner ? 35f : 20f;
                        var longest = 0f;
                        var lineCount = 0;
                        foreach (var line in value.Split('\n'))
                        {
                            var length = 0f;
                            foreach (var character in line)
                                length += (font?.AdvanceFor(character, advance) ?? advance) * (character == 'W' ? 1.5f : 1f) + characterSpacing;
                            lineCount += Math.Max(1, (int)MathF.Ceiling(length / width));
                            longest = Math.Max(longest, Math.Min(width, length));
                        }
                        return new Vector2(longest, lineCount * (Style == TopiaForgeTextStyle.Banner ? 56f : 30f));
                    }
                    public TMP_TextInfo GetTextInfo(string value) => new TMP_TextInfo
                    {
                        lineCount = (int)GetPreferredValues(value, rectTransform.sizeDelta.x, 0f).y /
                            (Style == TopiaForgeTextStyle.Banner ? 56 : 30),
                    };
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                public enum TopiaForgeScheme { Paper, Hud }
                public sealed partial class UiHost
                {
                    private readonly List<TopiaForgeOverlay> overlays = new List<TopiaForgeOverlay>();
                    public string OwnerId => "overlay-test";
                    public float EffectiveUiScale = 1f;
                    public float MotionIntensity = 1f;
                    public float EffectiveMotion => EffectiveReducedMotion ? 0f : MotionIntensity;
                    public bool EffectiveReducedMotion;
                    public TopiaForgeResolvedTheme Theme = new TopiaForgeResolvedTheme(null);
                    internal readonly List<TopiaForgeWidget> Widgets = new List<TopiaForgeWidget>();
                    internal GameObject? LastLayer;
                    public event Action<GameObject>? LayerLost;
                    internal void Register(TopiaForgeOverlay overlay) => overlays.Add(overlay);
                    internal TopiaForgeContainer KitLayer(string name, TopiaForgeLayerBand band, TopiaForgeScheme scheme,
                        bool interactive, bool persistent)
                    {
                        LastLayer = TopiaForgeLayers.CreateKitCanvas(name, band, interactive, persistent);
                        return new TopiaForgeContainer(this, scheme, LastLayer);
                    }
                    internal TopiaForgeContainer TrustLayer(bool persistent, bool overlay)
                    {
                        LastLayer = TopiaForgeLayers.CreateTrustCanvas("Trust", persistent, overlay);
                        return new TopiaForgeContainer(this, TopiaForgeScheme.Paper, LastLayer);
                    }
                    internal void Lose(GameObject root)
                    {
                        root.SetActive(false);
                        TopiaForgeLayers.ReleaseDestroyed(root, LayerLost);
                    }
                    internal void SetSurfaceTheme(GameObject root, UiThemeDescriptor descriptor,
                        Func<UiTextStyle, TMPro.TMP_FontAsset?>? fonts = null)
                    {
                        root.GetComponent<TopiaForgeLayerSlot>().SelectTheme(descriptor, fonts);
                        foreach (var widget in Widgets)
                        {
                            if (!widget.Go.transform.IsChildOf(root.transform)) continue;
                            if (widget is TopiaForgeLabel label) label.ApplyFont();
                            if (widget is ITopiaForgeThemeAware themed) themed.ApplyTheme(widget.ResolvedTheme);
                        }
                    }
                    public void DestroyLayer(GameObject root)
                    {
                        for (var index = overlays.Count - 1; index >= 0; index--)
                        {
                            var overlay = overlays[index];
                            if (overlay.CanvasRoot != root) continue;
                            overlays.RemoveAt(index);
                            overlay.Teardown();
                        }
                        root.SetActive(false);
                        foreach (var widget in Widgets)
                            if (widget.Go.transform.IsChildOf(root.transform)) TopiaForgeTween.Cancel(widget);
                        TopiaForgeLayers.Release(root);
                    }
                }
                public class TopiaForgeWidget
                {
                    public TopiaForgeWidget(UiHost host, TopiaForgeScheme scheme, GameObject go)
                    { Host = host; Scheme = scheme; Go = go; host.Widgets.Add(this); }
                    public UiHost Host { get; }
                    public TopiaForgeScheme Scheme { get; }
                    public GameObject Go { get; }
                    public RectTransform Rect => Go.Rect;
                    public TopiaForgeResolvedTheme ResolvedTheme => Go.GetComponentInParent<TopiaForgeLayerSlot>()?.Resolved ?? Host.Theme;
                    internal CanvasGroup EnsureCanvasGroup() => Go.GetComponent<CanvasGroup>() ?? Go.AddComponent<CanvasGroup>();
                    public void SetShown(bool shown) { if (Go.activeSelf != shown) Go.SetActive(shown); }
                    public void ReleaseFocus() => TopiaForgeFocus.Release(Go);
                    public void Destroy() => Host.DestroyLayer(Go);
                }
                public partial class TopiaForgeContainer : TopiaForgeWidget
                {
                    public TopiaForgeContainer(UiHost host, TopiaForgeScheme scheme, GameObject go) : base(host, scheme, go) { }
                    public GameObject CreateChildGameObject(string name) => Go.Child(name);
                    public TopiaForgeContainer Stack(string name) => new TopiaForgeContainer(Host, Scheme, CreateChildGameObject(name));
                    public TopiaForgeLabel Label(string value, TopiaForgeTextStyle style)
                    {
                        var label = new TopiaForgeLabel(Host, Scheme, CreateChildGameObject("Label"));
                        var text = label.Go.AddComponent<TMPro.TextMeshProUGUI>();
                        text.text = value;
                        text.Style = style;
                        label.ApplyFont();
                        return label;
                    }
                }
                public sealed partial class TopiaForgeLabel : TopiaForgeWidget
                {
                    public TopiaForgeLabel(UiHost host, TopiaForgeScheme scheme, GameObject go) : base(host, scheme, go) { }
                    public TopiaForgeLabel AlignCenter() => this;
                    public TopiaForgeLabel PlainText() { Go.GetComponent<TMPro.TMP_Text>().richText = false; return this; }
                    internal void SetResolvedInk(Color color) => Go.GetComponent<TMPro.TMP_Text>().color = color;
                    internal void ApplyFont()
                    {
                        var text = Go.GetComponent<TMPro.TMP_Text>();
                        var style = text.Style == TopiaForgeTextStyle.Banner ? UiTextStyle.Banner : UiTextStyle.Title;
                        text.font = ResolvedTheme.Fonts?.Invoke(style) ?? new TMPro.TMP_FontAsset();
                    }
                }
                public interface ITopiaForgeThemeAware { void ApplyTheme(TopiaForgeResolvedTheme theme); }
                public sealed partial class TopiaForgeResolvedTheme
                {
                    internal TopiaForgeResolvedTheme(UiThemeDescriptor? descriptor) => Descriptor = descriptor;
                    internal UiThemeDescriptor? Descriptor { get; }
                    internal Func<UiTextStyle, TMPro.TMP_FontAsset?>? Fonts;
                    internal Func<UiTextStyle, TMPro.TMP_FontAsset?>? RegisteredFont => Fonts;
                    public bool HighContrast;
                    public Color Surface => new Color(0.1f, 0.2f, 0.3f, 1f);
                    public Color Outline, OutlineStrong, Shadow;
                    public Color ShadowStrong;
                    internal Color BoundaryOn(UiPaintRole _) => OutlineStrong;
                }
                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal float Border;
                    internal void SetShape(float? cornerRadius, float borderWidth = 0) => Border = borderWidth;
                }
                internal static class TopiaForgeLayout
                {
                    internal static void ApplyColumn(GameObject gameObject, TopiaForgeGap gap, TopiaForgeGap padding) { }
                    internal static void ConfigureColumn(TopiaForgeModalColumn column, TopiaForgeGap gap, TopiaForgeGap padding) { }
                }
                internal sealed class TopiaForgeModalColumn : Component
                {
                    internal float MotionReserve;
                    internal void LimitScrollBody(RectTransform viewport, float motionReserve) => MotionReserve = motionReserve;
                }
                internal static class TopiaForgeAnchors
                {
                    internal static void Stretch(RectTransform rect) { }
                    internal static void Place(RectTransform rect, float x, float y, float width, float height)
                    {
                        rect.anchoredPosition = new Vector2(x, -y);
                        rect.sizeDelta = new Vector2(width, height);
                    }
                }
                internal static partial class TopiaForgeSprites { internal static Sprite Glyph(UiGlyph glyph) => new Sprite(); }
                internal static class TopiaForgeRuntime { internal static void Ensure() { } }
                internal static class TopiaForgeEventSystems
                {
                    internal static void EnsureEventSystem() { EventSystem.current ??= new EventSystem(); }
                    internal static void Track(GraphicRaycaster raycaster) { }
                }
                internal sealed partial class TopiaForgeLayerSlot : Component
                {
                    internal GameObject? Root;
                    internal TopiaForgeLayerBand Band;
                    internal Action<GameObject>? Lost;
                    internal TopiaForgeResolvedTheme? Resolved;
                    internal void SelectTheme(UiThemeDescriptor descriptor, Func<UiTextStyle, TMPro.TMP_FontAsset?>? fonts = null) =>
                        Resolved = new TopiaForgeResolvedTheme(descriptor) { Fonts = fonts };
                }
                internal static class TopiaForgeTheme { internal static float UiScale => 1f; internal static float EffectiveMotion => 1f; }
                internal static class TopiaForgeLog
                {
                    internal static void Info(string message) { }
                    internal static void Error(string message) => throw new InvalidOperationException(message);
                    internal static void Error(Exception error, string message) => throw new InvalidOperationException(message, error);
                    internal static void Warn(string message) => throw new InvalidOperationException(message);
                }
                internal static class TopiaForgeCallbacks
                {
                    internal static void Invoke(Action? action, string name) => action?.Invoke();
                    internal static void Resume(object? intent, Action? action, string name) => action?.Invoke();
                }
                internal static class TopiaForgeComponents
                {
                    internal static T GetOrAdd<T>(GameObject go) where T : Component => go.GetComponent<T>() ?? go.AddComponent<T>();
                }
                internal static class TopiaForgeIntent
                {
                    internal static IDisposable? PlayerAction(string? owner) => null;
                    internal static object? Capture() => null;
                }
                internal sealed class TopiaForgeModalInstance : ITopiaForgeDismissable
                {
                    internal string OwnerId => "modal";
                    internal bool IsTrust { get; set; }
                    public TopiaForgeLayerBand Band => TopiaForgeLayerBand.Modal;
                    internal int Dismissals { get; private set; }
                    public void Dismiss() => Dismissals++;
                }
            }
            """;
    }
}
