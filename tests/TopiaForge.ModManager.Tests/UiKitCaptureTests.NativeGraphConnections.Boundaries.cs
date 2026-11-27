namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private const string GraphConnectionBoundaries = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;

            namespace UnityEngine
            {
                public readonly record struct Vector2(float x, float y)
                {
                    public float magnitude => MathF.Sqrt(x * x + y * y);
                    public static Vector2 operator +(Vector2 left, Vector2 right) => new(left.x + right.x, left.y + right.y);
                    public static Vector2 operator -(Vector2 left, Vector2 right) => new(left.x - right.x, left.y - right.y);
                    public static Vector2 operator *(Vector2 value, float scale) => new(value.x * scale, value.y * scale);
                }

                public readonly record struct Quaternion(float Z)
                {
                    public static Quaternion Euler(float x, float y, float z) => new(z);
                }

                public static class Mathf
                {
                    public const float Rad2Deg = (float)(180d / Math.PI);
                    public static float Atan2(float y, float x) => MathF.Atan2(y, x);
                }
                public readonly record struct Rect(float width, float height)
                {
                    public Vector2 size => new(width, height);
                }

                public class Component
                {
                    public GameObject gameObject { get; internal set; } = null!;
                    public Transform transform => gameObject.transform;
                    public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
                }

                public class MonoBehaviour : Component { }

                public class GameObject
                {
                    private readonly List<Component> components = new();
                    public GameObject(string name, params Type[] types) => transform = AddComponent<RectTransform>();
                    public Transform transform { get; }
                    public bool activeSelf { get; private set; } = true;
                    public bool activeInHierarchy => activeSelf && (transform.parent?.gameObject.activeInHierarchy ?? true);
                    public void SetActive(bool active) => activeSelf = active;
                    public T AddComponent<T>() where T : Component
                    {
                        var value = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        value.gameObject = this;
                        components.Add(value);
                        return value;
                    }

                    public T? GetComponent<T>() where T : Component
                    {
                        foreach (var component in components)
                            if (component is T item) return item;
                        return null;
                    }

                    public bool TryGetComponent<T>(out T value) where T : Component
                    {
                        value = GetComponent<T>()!;
                        return value != null;
                    }

                    public T? GetComponentInParent<T>(bool includeInactive) where T : Component =>
                        GetComponent<T>() ?? transform.parent?.gameObject.GetComponentInParent<T>(includeInactive);

                    public void SendSelect()
                    {
                        var data = new BaseEventData();
                        foreach (var component in components)
                            if (component is ISelectHandler selectable) selectable.OnSelect(data);
                    }
                }

                public class Transform : Component
                {
                    private readonly List<Transform> children = new();
                    public Transform? parent { get; private set; }
                    public int childCount => children.Count;
                    public void SetParent(Transform target, bool worldPositionStays)
                    {
                        parent?.children.Remove(this);
                        parent = target;
                        target.children.Add(this);
                    }

                    public void SetSiblingIndex(int index)
                    {
                        parent!.children.Remove(this);
                        parent.children.Insert(index, this);
                    }

                    public bool IsChildOf(Transform ancestor)
                    {
                        for (Transform? item = this; item != null; item = item.parent)
                            if (item == ancestor) return true;
                        return false;
                    }
                }

                public sealed class RectTransform : Transform
                {
                    public Vector2 anchoredPosition { get; set; }
                    public Vector2 sizeDelta { get; set; }
                    public Quaternion localRotation { get; set; }
                    internal bool Stretched { get; set; }
                    internal Vector2 Inset { get; set; }
                    public Rect rect => Stretched && parent is RectTransform rectParent
                        ? new Rect(rectParent.rect.width - Inset.x, rectParent.rect.height - Inset.y)
                        : new Rect(sizeDelta.x, sizeDelta.y);
                }

                public sealed class CanvasGroup : Component
                {
                    public float alpha = 1f;
                    public bool interactable = true;
                    public bool blocksRaycasts = true;
                }
            }

            namespace UnityEngine.EventSystems
            {
                public enum MoveDirection { None, Left, Right, Up, Down }
                public class BaseEventData { public void Use() { } }
                public sealed class AxisEventData(MoveDirection direction) : BaseEventData { public MoveDirection moveDir = direction; }
                public sealed class PointerEventData : BaseEventData { public Vector2 scrollDelta; }
                public interface IScrollHandler { void OnScroll(PointerEventData data); }
                public interface ISelectHandler { void OnSelect(BaseEventData data); }

                public sealed class EventSystem
                {
                    public static EventSystem current { get; } = new();
                    public GameObject? currentSelectedGameObject { get; private set; }
                    public void SetSelectedGameObject(GameObject? selected)
                    {
                        if (currentSelectedGameObject == selected) return;
                        currentSelectedGameObject = selected;
                        selected?.SendSelect();
                    }
                }
            }

            namespace UnityEngine.UI
            {
                public struct Navigation
                {
                    public enum Mode { None, Automatic }
                    public Mode mode;
                }
                public class Selectable : Component, ISelectHandler
                {
                    public bool interactable = true;
                    public Navigation navigation = new Navigation { mode = Navigation.Mode.Automatic };
                    public bool IsActive() => gameObject.activeInHierarchy;
                    public bool IsInteractable() => interactable &&
                        gameObject.GetComponentInParent<CanvasGroup>(true)?.interactable != false;
                    public void Select()
                    {
                        if (IsActive() && IsInteractable()) EventSystem.current.SetSelectedGameObject(gameObject);
                    }
                    public virtual void OnSelect(BaseEventData eventData) { }
                }
                public class Button : Selectable
                {
                    public Image? targetGraphic;
                    public Click onClick = new();
                    public int colors;
                    public virtual void OnMove(AxisEventData eventData) { }
                    public void OnSubmit(BaseEventData eventData)
                    {
                        if (IsActive() && IsInteractable()) onClick.Invoke();
                    }
                }
                public sealed class Click
                {
                    private Action? action;
                    public void AddListener(Action listener) => action += listener;
                    public void Invoke() => action?.Invoke();
                }
                public class Image : Component
                {
                    public bool raycastTarget;
                    public int color;
                    public RectTransform rectTransform => (RectTransform)transform;
                }
                public sealed class ScrollRect : Component
                {
                    public void StopMovement() { }
                }
                public sealed class LayoutElement : Component
                {
                    public bool ignoreLayout;
                    public float minHeight;
                    public float preferredHeight;
                    public float minWidth;
                    public float preferredWidth;
                }
            }

            namespace TMPro
            {
                public enum TextAlignmentOptions { Left }
                public enum TextOverflowModes { Ellipsis, Overflow }
                public sealed class TextMeshProUGUI : Component
                {
                    public bool richText;
                    public bool parseCtrlCharacters;
                    public string text = string.Empty;
                    public TextOverflowModes overflowMode = TextOverflowModes.Ellipsis;
                    public int color;
                    public float CharacterAdvance = 8f;
                    public RectTransform rectTransform => (RectTransform)transform;
                    public Vector2 GetPreferredValues() => GetPreferredValues(text, float.PositiveInfinity, 0f);
                    public Vector2 GetPreferredValues(string display, float availableWidth, float availableHeight)
                    {
                        var lines = display.Split('\n');
                        var length = 0;
                        foreach (var line in lines) length = Math.Max(length, line.Length);
                        return new Vector2(length * CharacterAdvance, lines.Length * 18f);
                    }
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                using TMPro;

                public sealed class UiHost
                {
                    public float EffectiveUiScale { get; set; } = 2f;
                    public string OwnerId => "example.graphs";
                }
                public enum TopiaForgeScheme { Paper }
                public enum TopiaForgeTextStyle { Label }
                public enum TopiaForgeLayerBand { Window }
                public enum TopiaForgeInputClaim { Back }
                public interface ITopiaForgeDismissable { TopiaForgeLayerBand Band { get; } void Dismiss(); }
                public interface ITopiaForgeThemeAware { void ApplyTheme(TopiaForgeResolvedTheme theme); }
                public sealed class TopiaForgeResolvedTheme
                {
                    internal UiThemeDescriptor? Descriptor => null;
                    internal static bool CollapseGraphColors { get; set; }
                    internal bool HighContrast => false;
                    internal int Surface => 1;
                    internal int SurfaceSunken => 2;
                    internal int SelectedTint => CollapseGraphColors ? 0 : 3;
                    internal int SurfaceAlt => CollapseGraphColors ? 0 : 4;
                    internal int Text => 5;
                    internal int TextFaint => 6;
                    internal Field? FieldPaint(UiPaintRole role, UiTone tone, bool enabled) => null;
                    internal readonly struct Field
                    {
                        internal int ResolvedStates(int value) => value;
                        internal int Fill => 4;
                        internal int Content => 5;
                    }
                }

                public static class TopiaForgeDismissStack
                {
                    internal static void Push(ITopiaForgeDismissable item, TopiaForgeInputClaim claim, GameObject root) { }
                    internal static void Remove(ITopiaForgeDismissable item) { }
                }

                public abstract class TopiaForgeWidget
                {
                    private static readonly TopiaForgeResolvedTheme Resolved = new();
                    protected TopiaForgeWidget(UiHost host, TopiaForgeScheme scheme, GameObject go)
                    {
                        Host = host; Scheme = scheme; Go = go;
                    }
                    public UiHost Host { get; }
                    public TopiaForgeScheme Scheme { get; }
                    public GameObject Go { get; }
                    public RectTransform Rect => (RectTransform)Go.transform;
                    protected TopiaForgeResolvedTheme Theme => Resolved;
                    internal LayoutElement EnsureLayoutElement() => Go.GetComponent<LayoutElement>() ?? Go.AddComponent<LayoutElement>();
                    internal void SetShown(bool shown) => Go.SetActive(shown);
                    internal void ReleaseFocus()
                    {
                        if (EventSystem.current.currentSelectedGameObject?.transform.IsChildOf(Go.transform) == true)
                            EventSystem.current.SetSelectedGameObject(null);
                    }
                }

                public sealed class TopiaForgeContainer : TopiaForgeWidget
                {
                    internal TopiaForgeContainer(UiHost host, GameObject go) : base(host, TopiaForgeScheme.Paper, go) { }
                    internal GameObject CreateChildGameObject(string name)
                    {
                        var child = new GameObject(name);
                        child.transform.SetParent(Go.transform, false);
                        return child;
                    }
                }

                internal sealed class TopiaForgeWindow
                {
                    internal TopiaForgeWindow(UiHost owner)
                    {
                        TitleBar = new TopiaForgeContainer(owner, new GameObject("Title bar"));
                        TitleBar.Rect.sizeDelta = new Vector2(840, 42);
                        TitleBar.EnsureLayoutElement().preferredHeight = 42;
                        TitleBar.CreateChildGameObject("Title");
                        TitleBar.CreateChildGameObject("Close");
                        Content = new TopiaForgeContainer(owner, new GameObject("Contents"));
                        Content.Rect.sizeDelta = new Vector2(840, 400);
                        Scroll = Content.CreateChildGameObject("Scroll");
                        Scroll.AddComponent<ScrollRect>();
                        var parent = new TopiaForgeContainer(owner, Scroll);
                        ScrollContent = new TopiaForgeContainer(owner, parent.CreateChildGameObject("Authored"));
                    }
                    internal TopiaForgeContainer TitleBar { get; }
                    internal TopiaForgeContainer Content { get; }
                    internal TopiaForgeContainer ScrollContent { get; }
                    internal GameObject Scroll { get; }
                }

                internal sealed class TopiaForgeFullscreenTool
                {
                    internal TopiaForgeContainer TitleBar => throw new NotSupportedException();
                    internal TopiaForgeContainer Content => throw new NotSupportedException();
                }

                internal static class TopiaForgeAnchors
                {
                    internal static void Stretch(RectTransform rect, float left = 0, float top = 0, float right = 0, float bottom = 0)
                    {
                        rect.Stretched = true;
                        rect.Inset = new Vector2(left + right, top + bottom);
                    }
                }

                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal void SetShape(float? shape) { }
                }

                internal sealed class TopiaForgeControlCues : Component
                {
                    internal bool Applied { get; private set; }
                    internal bool AuthoredSelectionCue { get; private set; }
                    internal static TopiaForgeControlCues Install(Selectable control, RectTransform area) =>
                        control.gameObject.AddComponent<TopiaForgeControlCues>();
                    internal void Apply(int fill, int ink, float radius, bool highContrast, bool authoredSelectionCue = false)
                    {
                        Applied = true;
                        AuthoredSelectionCue = authoredSelectionCue;
                    }
                }

                internal sealed class TopiaForgeControlFocus
                {
                    internal static TopiaForgeControlFocus Install(TopiaForgeWidget widget, Selectable control) => new();
                    internal void SetBackdrop(TopiaForgeResolvedTheme theme, UiPaintRole backdrop) { }
                }

                internal static class TopiaForgeTypography
                {
                    internal static void ApplyControlText(TextMeshProUGUI text, TopiaForgeResolvedTheme theme,
                        TopiaForgeTextStyle style, float scale) { }
                }

                public sealed partial class TopiaForgeGraphCanvas : TopiaForgeWidget
                {
                    private UiGraphCanvas graph;
                    private bool enabledState = true;
                    private readonly List<TopiaForgeGraphNodeView> nodeViews = new();
                    private readonly List<TopiaForgeGraphEdgeView> edgeViews = new();
                    internal TopiaForgeGraphCanvas(TopiaForgeContainer parent, UiGraphCanvas initial)
                        : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Graph"))
                    {
                        graph = initial;
                        var header = new GameObject("Header");
                        header.transform.SetParent(Go.transform, false);
                        header.AddComponent<TopiaForgeGraphFocusRelay>().Attach(this);
                        nodeViews.Add(new TopiaForgeGraphNodeView(header.AddComponent<Button>()));
                        SetGraph(initial);
                    }

                    public UiGraphCanvas Graph => graph;
                    internal Selectable Header => nodeViews[0].FocusControl;
                    internal Navigation.Mode FirstEdgeMode => edgeViews[0].Mode;
                    internal void DirectPointerPick() => edgeViews[0].PointerPick();
                    internal bool HasCrossingEdges()
                    {
                        var first = edgeViews[0];
                        var second = edgeViews[1];
                        first.SetGeometry(new Vector2(-192, 82), new Vector2(192, -82));
                        second.SetGeometry(new Vector2(-192, -82), new Vector2(192, 82));
                        return first.Rect.anchoredPosition == second.Rect.anchoredPosition &&
                            first.Rect.localRotation.Z < 0 && second.Rect.localRotation.Z > 0;
                    }
                    internal void SetGraph(UiGraphCanvas next)
                    {
                        graph = next;
                        enabledState = next.Enabled;
                        edgeViews.Clear();
                        foreach (var edge in next.Edges) edgeViews.Add(new TopiaForgeGraphEdgeView(this, edge));
                        UpdateConnectionNavigation();
                    }

                    /* canvas action */
                }

                internal sealed class TopiaForgeGraphEdgeView
                {
                    private readonly TopiaForgeGraphCanvas owner;
                    private readonly UiGraphEdge edge;
                    private readonly Button button;
                    internal TopiaForgeGraphEdgeView(TopiaForgeGraphCanvas owner, UiGraphEdge edge)
                    {
                        this.owner = owner;
                        this.edge = edge;
                        button = new GameObject("Edge").AddComponent<Button>();
                        button.onClick.AddListener(Select);
                    }

                    internal Navigation.Mode Mode => button.navigation.mode;
                    internal RectTransform Rect => (RectTransform)button.transform;
                    internal void PointerPick() => button.onClick.Invoke();
                    /* edge actions */
                }

                internal static class TopiaForgeCallbacks
                {
                    internal static string? LastOwner;
                    internal static void Player<T>(string owner, Action<T> callback, T value, string description)
                    {
                        LastOwner = owner;
                        callback(value);
                    }
                }

                internal sealed class TopiaForgeGraphNodeView(Selectable control)
                {
                    internal Selectable FocusControl => control;
                    internal bool Active => true;
                    internal static TopiaForgeRoundedImage AddControlFill(GameObject control) => control.AddComponent<TopiaForgeRoundedImage>();
                    internal static TextMeshProUGUI CreateLabel(Transform parent, string name, TopiaForgeTextStyle style,
                        TextAlignmentOptions alignment)
                    {
                        var child = new GameObject(name);
                        child.transform.SetParent(parent, false);
                        return child.AddComponent<TextMeshProUGUI>();
                    }

                    internal static void SetControlBox(RectTransform rect, (float X, float Y, float Width, float Height) box)
                    {
                        rect.anchoredPosition = new Vector2(box.X, -box.Y);
                        rect.sizeDelta = new Vector2(box.Width, box.Height);
                    }
                }
            }
            """;
    }
}
