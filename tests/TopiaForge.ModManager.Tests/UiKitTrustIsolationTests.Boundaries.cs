namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitTrustIsolationTests
    {
        private const string Boundaries = """
            using System;
            using System.Collections.Generic;
            using UnityEngine;
            using UnityEngine.UI;

            namespace UnityEngine
            {
                public readonly record struct Color(float r, float g, float b, float a)
                {
                    public static Color clear => default;
                    public static Color white => new(1, 1, 1, 1);
                }
                public readonly record struct Color32(byte r, byte g, byte b, byte a)
                {
                    public static explicit operator Color32(Color color) =>
                        new Color32(Channel(color.r), Channel(color.g), Channel(color.b), Channel(color.a));

                    private static byte Channel(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
                }

                public readonly record struct Vector2(float x, float y)
                {
                    public static Vector2 zero => default;
                    public static Vector2 one => new(1, 1);
                }

                public readonly record struct Rect(float width, float height);
                public sealed class Sprite { }
                public class MonoBehaviour
                {
                    public GameObject gameObject { get; internal set; } = null!;
                    public Transform transform => gameObject.transform;
                }
                public enum RenderMode { ScreenSpaceOverlay }
                public sealed class Canvas
                {
                    public GameObject gameObject { get; internal set; } = null!;
                    public RenderMode renderMode;
                    public int sortingOrder;
                }
                public static class Application { public static event Action? quitting; }
                public static class Object
                {
                    public static void Destroy(GameObject root) => root.SetActive(false);
                    public static void DontDestroyOnLoad(GameObject root) { }
                }

                public class Transform
                {
                    private readonly List<Transform> children = new();
                    private Transform? parent;
                    public Transform(GameObject gameObject) => this.gameObject = gameObject;
                    public GameObject gameObject { get; }
                    internal Transform? Parent => parent;
                    public int childCount => children.Count;
                    public Transform GetChild(int index) => children[index];
                    public Transform Find(string name) => children.Find(child => child.gameObject.name == name)!;
                    public T? GetComponent<T>() where T : class => gameObject.GetComponent<T>();
                    public void SetParent(Transform value, bool worldPositionStays)
                    {
                        parent?.children.Remove(this);
                        parent = value;
                        parent.children.Add(this);
                    }
                    public bool IsChildOf(Transform other) => this == other || (parent?.IsChildOf(other) ?? false);
                }

                public sealed class RectTransform(GameObject gameObject) : Transform(gameObject)
                {
                    public Vector2 anchorMin, anchorMax, pivot, sizeDelta, offsetMin, offsetMax;
                    public Rect rect;
                }

                public sealed class GameObject
                {
                    private readonly Dictionary<Type, object> components = new();
                    public GameObject(string name, params Type[] types)
                    {
                        this.name = name;
                        transform = new RectTransform(this);
                    }
                    public string name;
                    public Transform transform { get; }
                    public bool activeSelf { get; private set; } = true;
                    public void SetActive(bool value) => activeSelf = value;
                    public T AddComponent<T>() where T : new()
                    {
                        var component = new T();
                        if (component is Canvas canvas) canvas.gameObject = this;
                        if (component is MonoBehaviour behaviour) behaviour.gameObject = this;
                        components[typeof(T)] = component!;
                        return component;
                    }
                    public T? GetComponent<T>() where T : class => components.TryGetValue(typeof(T), out var value) ? (T)value : null;
                }
            }

            namespace TMPro
            {
                public sealed class TMP_FontAsset { }
            }

            namespace UnityEngine.UI
            {
                public sealed class CanvasScaler
                {
                    public enum ScaleMode { ScaleWithScreenSize }
                    public ScaleMode uiScaleMode;
                    public Vector2 referenceResolution;
                    public float matchWidthOrHeight;
                }
                public sealed class GraphicRaycaster { public bool enabled; }
                public sealed class ContentSizeFitter { public enum FitMode { PreferredSize } public FitMode verticalFit; }
                public sealed class ScrollRect { }
                public interface ILayoutElement
                {
                    float minWidth { get; }
                    float preferredWidth { get; }
                    float flexibleWidth { get; }
                    float minHeight { get; }
                    float preferredHeight { get; }
                    float flexibleHeight { get; }
                    int layoutPriority { get; }
                    void CalculateLayoutInputHorizontal();
                    void CalculateLayoutInputVertical();
                }
                public sealed class LayoutElement
                {
                    public bool ignoreLayout;
                    public float minHeight, preferredHeight;
                    public int layoutPriority = 1;
                }
                public sealed class RectOffset { public int vertical; }
                public class VerticalLayoutGroup : MonoBehaviour
                {
                    protected readonly List<RectTransform> rectChildren = new();
                    public readonly RectOffset padding = new();
                    public float spacing;
                    public bool NeedsLayout;
                    public void CalculateLayoutInputHorizontal()
                    {
                        rectChildren.Clear();
                        for (var index = 0; index < transform.childCount; index++)
                            if (transform.GetChild(index) is RectTransform child && child.gameObject.activeSelf &&
                                child.GetComponent<LayoutElement>()?.ignoreLayout != true) rectChildren.Add(child);
                        NeedsLayout = false;
                    }
                    public virtual void CalculateLayoutInputVertical() { }
                    public void ViewportChanged() => OnRectTransformDimensionsChange();
                    protected virtual void OnRectTransformDimensionsChange() { }
                    protected void SetDirty() => NeedsLayout = true;
                }
                public static class LayoutUtility
                {
                    public static float GetPreferredHeight(RectTransform rect) => rect.GetComponent<LayoutElement>()?.preferredHeight ?? 0f;
                }
                public sealed class Image
                {
                    public enum Type { Sliced }
                    public Type type;
                    public Sprite? sprite;
                    public Color color;
                    public bool raycastTarget;
                }
                public struct ColorBlock
                {
                    public Color normalColor, disabledColor, highlightedColor, selectedColor, pressedColor;
                    public float colorMultiplier;
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                public interface ITopiaForgeStateStore { }
                internal sealed class TopiaForgeMemoryStateStore : ITopiaForgeStateStore { }
                internal sealed class TopiaForgeFileStateStore(string path) : ITopiaForgeStateStore { }
                internal interface ITopiaForgeThemeAware { void ApplyTheme(TopiaForgeResolvedTheme theme); }
                internal sealed class TopiaForgePlainTextRoot { }
                public enum TopiaForgeButtonStyle { Ghost, Danger, Filled }
                public enum TopiaForgeKey { }
                public class TopiaForgeWidget(UiHost host, TopiaForgeScheme scheme, GameObject root)
                {
                    public UiHost Host { get; } = host;
                    public GameObject Go { get; } = root;
                    public TopiaForgeScheme Scheme { get; } = scheme;
                    internal TopiaForgeLayerSlot? ThemeSlot
                    {
                        get
                        {
                            for (var transform = Go.transform; transform != null; transform = transform.Parent)
                                if (transform.gameObject.GetComponent<TopiaForgeLayerSlot>() is { } slot) return slot;
                            return null;
                        }
                    }
                }
                public sealed class TopiaForgeContainer(UiHost host, TopiaForgeScheme scheme, GameObject root) : TopiaForgeWidget(host, scheme, root)
                {
                    public GameObject CreateChildGameObject(string name)
                    {
                        var child = new GameObject(name);
                        child.transform.SetParent(Go.transform, false);
                        return child;
                    }
                    public TopiaForgeContainer Row(TopiaForgeGap gap) => new(host, Scheme, CreateChildGameObject("Row"));
                    public TopiaForgeContainer Label(string text, TopiaForgeTextStyle style) => this;
                    public void PlainText() { }
                    public void Spacer() { }
                    public void Button(string text, Action action, TopiaForgeButtonStyle style) { }
                }
                public sealed class TopiaForgeHudLayer(UiHost host, TopiaForgeContainer root) : TopiaForgeWidget(host, root.Scheme, root.Go) { }
                public sealed class TopiaForgeWindow(UiHost host, TopiaForgeContainer layer, string id, string title, float width, float height)
                    : TopiaForgeWidget(host, layer.Scheme, layer.Go)
                {
                    public GameObject CanvasRoot => Go;
                    public void Teardown() { }
                    public Action? Lose() => null;
                }
                public sealed class TopiaForgeFullscreenTool(UiHost host, TopiaForgeContainer layer, string title)
                    : TopiaForgeWidget(host, layer.Scheme, layer.Go)
                {
                    public GameObject CanvasRoot => Go;
                    public void Teardown() { }
                    public Action? Lose() => null;
                }
                public sealed class TopiaForgeOverlay(UiHost host, TopiaForgeContainer layer)
                    : TopiaForgeWidget(host, layer.Scheme, layer.Go)
                {
                    public GameObject CanvasRoot => Go;
                    public void Teardown() { }
                }
                internal static class TopiaForgeOverlayStack { internal static void RefreshCoveredFocus() { } }
                internal static class TopiaForgeRuntime
                {
                    internal static void Ensure() { }
                }
                internal static class TopiaForgeEventSystems
                {
                    internal static void Track(GraphicRaycaster raycaster) { }
                }
                internal static class TopiaForgeCallbacks
                {
                    internal static void Invoke(Action? callback, string reason) => callback?.Invoke();
                }
                internal static class TopiaForgeLog
                {
                    internal static void Info(string message) { }
                    internal static void Warn(string message) => throw new InvalidOperationException(message);
                    internal static void Error(Exception error, string message) => throw new InvalidOperationException(message, error);
                }
                internal static class TopiaForgeFonts { internal const string ResolvedTier = "headless"; }
                internal static class TopiaForgeInput
                {
                    internal const bool LegacyAvailable = false;
                    internal static bool BackHeld() => false;
                    internal static bool BackPressedThisFrame() => false;
                }
                internal static class TopiaForgeInputOwnership
                {
                    internal static bool IsLiveRoot(object root) => ((GameObject)root).activeSelf;
                    internal static bool HoldsSelection(object root) => false;
                }
                internal static class TopiaForgeUi { internal static void OnHostDisposed(UiHost host) { } }
                internal static class TopiaForgeHotkeys
                {
                    internal static object Register(string id, TopiaForgeKey key, Action action) => new object();
                    internal static void Unregister(object key) { }
                }
                internal sealed class TopiaForgeCursorLease
                {
                    internal void Acquire() { }
                    internal void Release() { }
                }
                internal static class TopiaForgeIntent
                {
                    internal static IDisposable? PlayerAction(string? owner) => null;
                }
                internal static class TopiaForgeMotion
                {
                    internal static void ModalIn(TopiaForgeContainer dialog) { }
                    internal static void ModalOut(TopiaForgeContainer dialog, Action finished) => finished();
                }
                internal static class TopiaForgeTween { internal static void Cancel(TopiaForgeWidget widget) { } }
                internal static class TopiaForgeToasts { internal static void Show(string text, TopiaForgeTone tone) { } }
                internal static class TopiaForgeLayout
                {
                    internal static void ConfigureColumn(VerticalLayoutGroup column, TopiaForgeGap spacing, TopiaForgeGap padding)
                    {
                        column.spacing = (int)spacing;
                        column.padding.vertical = 2 * (int)padding;
                    }
                }
                internal static class TopiaForgeSprites
                {
                    internal static Sprite Fill(TopiaForgeRadius radius) => new();
                    internal static Sprite Ring(TopiaForgeRadius radius, float width) => new();
                }
                internal static class TopiaForgeAnchors { internal static void Stretch(RectTransform rect) { } }
            }
            """;
    }
}
