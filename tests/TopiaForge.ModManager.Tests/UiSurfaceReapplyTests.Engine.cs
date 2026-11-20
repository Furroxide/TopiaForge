namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiSurfaceReapplyTests
    {
        private const string EngineBoundaries = """
            using System;
            using System.Collections.Generic;

            namespace UnityEngine
            {
                public static class Screen
                {
                    public static int width = 1920;
                    public static int height = 1080;
                }
                public struct Rect { public float xMin, yMin, xMax, yMax, width, height; }
                public readonly record struct Vector2(float x, float y)
                {
                    public static Vector2 zero => default;
                    public static implicit operator Vector3(Vector2 value) => new(value.x, value.y, 0f);
                }
                public readonly record struct Vector3(float x, float y, float z);
                public struct Color
                {
                    public float a;
                    public static Color clear => default;
                    public static implicit operator Color32(Color value) => default;
                }
                public readonly record struct Color32(byte R, byte G, byte B, byte A)
                {
                    public static implicit operator Color(Color32 value) => new() { a = value.A / 255f };
                }
                public static class Mathf { public static float Min(float a, float b) => Math.Min(a, b); }
                public class Transform
                {
                    internal Transform(GameObject owner) => gameObject = owner;
                    public GameObject gameObject { get; }
                    public Transform? parent { get; private set; }
                    public int childCount => gameObject.Children.Count;
                    public Transform GetChild(int index) => gameObject.Children[index].transform;
                    public T? GetComponent<T>() where T : MonoBehaviour => gameObject.GetComponent<T>();
                    public T? GetComponentInChildren<T>(bool includeInactive) where T : MonoBehaviour
                    {
                        var components = gameObject.GetComponentsInChildren<T>(includeInactive);
                        return components.Length == 0 ? null : components[0];
                    }
                    public T? GetComponentInParent<T>(bool includeInactive) where T : MonoBehaviour =>
                        gameObject.GetComponentInParent<T>(includeInactive);
                    public int GetSiblingIndex() => parent?.gameObject.Children.IndexOf(gameObject) ?? 0;
                    public bool IsChildOf(Transform root)
                    {
                        for (Transform? current = this; current != null; current = current.parent)
                            if (current == root) return true;
                        return false;
                    }
                    public void SetParent(Transform? value, bool worldPositionStays)
                    {
                        if (parent == value) return;
                        var wasActive = gameObject.activeInHierarchy;
                        parent?.gameObject.Children.Remove(gameObject);
                        parent = value;
                        parent?.gameObject.Children.Add(gameObject);
                        gameObject.ParentChanges++;
                        if (wasActive && !gameObject.activeInHierarchy) gameObject.DisableTree();
                    }
                    public void SetSiblingIndex(int index)
                    {
                        if (parent == null) return;
                        parent.gameObject.Children.Remove(gameObject);
                        parent.gameObject.Children.Insert(index, gameObject);
                    }
                }
                public sealed class RectTransform : Transform
                {
                    internal RectTransform(GameObject owner) : base(owner) { }
                    public Rect rect = new() { width = 840, height = 460 };
                    public Vector2 anchorMin, anchorMax, pivot, offsetMin, offsetMax, anchoredPosition;
                }
                public abstract class MonoBehaviour
                {
                    public GameObject gameObject { get; internal set; } = null!;
                    public RectTransform transform => gameObject.transform;
                    public bool enabled = true;
                    public T? GetComponent<T>() where T : MonoBehaviour => gameObject.GetComponent<T>();
                }
                public sealed class GameObject
                {
                    internal readonly List<GameObject> Children = new();
                    private readonly List<MonoBehaviour> components = new();
                    private bool shown = true;
                    internal Action? Queried;
                    internal int ParentChanges, DisableCalls;
                    public GameObject() => transform = new RectTransform(this);
                    public RectTransform transform { get; }
                    public bool activeSelf => shown;
                    public bool activeInHierarchy => shown && (transform.parent?.gameObject.activeInHierarchy ?? true);
                    public bool Shown
                    {
                        get => shown;
                        set
                        {
                            var wasActive = activeInHierarchy;
                            shown = value;
                            if (wasActive && !activeInHierarchy) DisableTree();
                        }
                    }
                    internal void DisableTree()
                    {
                        DisableCalls++;
                        if (GetComponent<UI.Selectable>() is { } control) control.Focused = false;
                        foreach (var child in Children)
                            if (child.activeSelf) child.DisableTree();
                    }
                    public T AddComponent<T>() where T : MonoBehaviour
                    {
                        var created = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        created.gameObject = this;
                        components.Add(created);
                        return created;
                    }
                    public T? GetComponent<T>() where T : MonoBehaviour
                    {
                        foreach (var component in components)
                            if (component is T value) return value;
                        return null;
                    }
                    public bool TryGetComponent<T>(out T value) where T : MonoBehaviour
                    {
                        value = GetComponent<T>()!;
                        return value != null;
                    }
                    public T? GetComponentInParent<T>(bool includeInactive) where T : MonoBehaviour
                    {
                        for (var current = (Transform?)transform; current != null; current = current.parent)
                            if ((includeInactive || current.gameObject.activeInHierarchy) && current.GetComponent<T>() is T result)
                                return result;
                        return null;
                    }
                    public T[] GetComponentsInChildren<T>(bool includeInactive) where T : MonoBehaviour
                    {
                        Queried?.Invoke();
                        var found = new List<T>();
                        Collect(this, found, includeInactive);
                        return found.ToArray();
                    }
                    private static void Collect<T>(GameObject root, List<T> found, bool includeInactive) where T : MonoBehaviour
                    {
                        if (!root.activeInHierarchy && !includeInactive) return;
                        foreach (var component in root.components)
                            if (component is T typed) found.Add(typed);
                        foreach (var child in root.Children)
                            Collect(child, found, includeInactive);
                    }
                }
                public sealed class Canvas : MonoBehaviour { public float scaleFactor = 1f; }
            }

            namespace UnityEngine.EventSystems
            {
                using UnityEngine.UI;
                public sealed class EventSystem
                {
                    public static EventSystem? current;
                    public GameObject? currentSelectedGameObject { get; private set; }
                    internal int SelectionChanges { get; private set; }
                    public void SetSelectedGameObject(GameObject? target)
                    {
                        if (target == currentSelectedGameObject) return;
                        if (currentSelectedGameObject?.GetComponent<Selectable>() is { } old) old.Focused = false;
                        currentSelectedGameObject = target;
                        if (target?.GetComponent<Selectable>() is { } selected) selected.Focused = target.activeInHierarchy;
                        SelectionChanges++;
                    }
                }
            }

            namespace UnityEngine.UI
            {
                public static class LayoutRebuilder
                {
                    public static int Requests { get; private set; }
                    public static void MarkLayoutForRebuild(RectTransform _) => Requests++;
                }
                public class Graphic : MonoBehaviour
                {
                    public Color color;
                    public bool raycastTarget;
                    public RectTransform rectTransform => transform;
                }
                public class MaskableGraphic : Graphic
                {
                    protected virtual void OnPopulateMesh(VertexHelper mesh) { }
                    protected Rect GetPixelAdjustedRect() => rectTransform.rect;
                    protected void SetVerticesDirty() { }
                }
                public sealed class VertexHelper
                {
                    public int currentVertCount => 0;
                    public void AddVert(Vector3 point, Color32 color, Vector2 uv) { }
                    public void AddTriangle(int a, int b, int c) { }
                    public void Clear() { }
                }
                public sealed class Image : Graphic { public object? sprite; }
                public sealed class RectMask2D : MonoBehaviour { }
                public class Selectable : MonoBehaviour
                {
                    internal bool Focused;
                    public bool IsInteractable() => true;
                }
                public sealed class Toggle : Selectable { }
                public sealed class Slider : Selectable { }
                public sealed class ScrollRect : MonoBehaviour
                {
                    public enum MovementType { Clamped }
                    public RectTransform viewport = null!, content = null!;
                    public bool horizontal, vertical;
                    public MovementType movementType;
                    public float scrollSensitivity;
                }
                public sealed class LayoutElement : MonoBehaviour
                {
                    public bool ignoreLayout;
                    public float minWidth, minHeight, preferredWidth, preferredHeight, flexibleWidth;
                }
                public sealed class ContentSizeFitter : MonoBehaviour { }
                public class HorizontalOrVerticalLayoutGroup : MonoBehaviour
                {
                    public BoundaryPadding padding = new();
                    public float spacing;
                    public bool childForceExpandWidth;
                }
                public sealed class VerticalLayoutGroup : HorizontalOrVerticalLayoutGroup { }
                public sealed class HorizontalLayoutGroup : HorizontalOrVerticalLayoutGroup { }
                public sealed class BoundaryPadding
                {
                    public int left, right, top, bottom;
                    public int horizontal => left + right;
                    public int vertical => top + bottom;
                }
            }

            namespace TMPro
            {
                using UnityEngine;
                using UnityEngine.UI;
                public enum TextWrappingModes { Normal, NoWrap, PreserveWhitespaceNoWrap }
                public class TMP_Text : Graphic
                {
                    public string text = string.Empty;
                    public TextWrappingModes textWrappingMode = TextWrappingModes.Normal;
                    public Vector2 GetPreferredValues(string value) => new Vector2(value.Length * 9f, 20f);
                    public Vector2 GetPreferredValues(string value, float width, float height) =>
                        new Vector2(width, 20f * MathF.Ceiling(value.Length * 9f / width));
                }
                public sealed class TMP_InputField : Selectable { }
                public sealed class TMP_Dropdown : Selectable { public TMP_Text? captionText; }
            }
            """;
    }
}
