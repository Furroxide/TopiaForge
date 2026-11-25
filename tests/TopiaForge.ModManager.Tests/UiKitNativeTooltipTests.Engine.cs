namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitNativeTooltipTests
    {
        private const string TooltipEngineBoundary = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Reflection;

            namespace UnityEngine
            {
                public class Object
                {
                    internal bool Destroyed;
                    public static bool operator ==(Object? first, Object? second)
                    {
                        if (ReferenceEquals(first, second)) return true;
                        if (first is null) return second!.Destroyed;
                        return second is null && first.Destroyed;
                    }
                    public static bool operator !=(Object? first, Object? second) => !(first == second);
                    public override bool Equals(object? other) => ReferenceEquals(this, other);
                    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
                    public static void Destroy(Object target)
                    {
                        if (target is GameObject go) go.DestroyTree();
                        else target.Destroyed = true;
                    }
                }
                public class Component : Object
                {
                    public GameObject gameObject { get; internal set; } = null!;
                    public RectTransform transform => gameObject.transform;
                    internal void Notify(string name) => GetType()
                        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(this, null);
                }
                public class MonoBehaviour : Component
                {
                    private bool running = true;
                    public bool enabled
                    {
                        get => running;
                        set
                        {
                            if (value == running) return;
                            var wasActive = isActiveAndEnabled;
                            running = value;
                            if (wasActive && !isActiveAndEnabled) Notify("OnDisable");
                        }
                    }
                    public bool isActiveAndEnabled => running && gameObject.activeInHierarchy;
                }
                public sealed class GameObject : Object
                {
                    private readonly List<Component> components = new();
                    public GameObject(string name, params Type[] added)
                    {
                        this.name = name;
                        transform = AddComponent<RectTransform>();
                    }
                    public string name { get; }
                    public RectTransform transform { get; }
                    public bool activeSelf { get; private set; } = true;
                    public bool activeInHierarchy => !Destroyed && activeSelf && (transform.parent?.gameObject.activeInHierarchy ?? true);
                    public T AddComponent<T>() where T : Component
                    {
                        var result = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        result.gameObject = this;
                        components.Add(result);
                        return result;
                    }
                    public T? GetComponent<T>() where T : Component
                    {
                        foreach (var component in components)
                            if (component is T match && !match.Destroyed) return match;
                        return null;
                    }
                    public T? GetComponentInParent<T>(bool includeInactive = false) where T : Component
                    {
                        for (var item = (Transform?)transform; item != null; item = item.parent)
                            if ((includeInactive || item.gameObject.activeInHierarchy) && item.gameObject.GetComponent<T>() is T result)
                                return result;
                        return null;
                    }
                    public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : Component
                    {
                        var result = new List<T>();
                        Collect(includeInactive, result);
                        return result.ToArray();
                    }
                    private void Collect<T>(bool includeInactive, List<T> result) where T : Component
                    {
                        if (!includeInactive && !activeInHierarchy) return;
                        result.AddRange(components.OfType<T>());
                        foreach (var child in transform.Children) child.gameObject.Collect(includeInactive, result);
                    }
                    public void SetActive(bool value)
                    {
                        if (value == activeSelf) return;
                        var notified = value ? Array.Empty<MonoBehaviour>() : GetComponentsInChildren<MonoBehaviour>()
                            .Where(item => item.isActiveAndEnabled).ToArray();
                        activeSelf = value;
                        foreach (var item in notified) item.Notify("OnDisable");
                    }
                    internal void DestroyTree()
                    {
                        if (Destroyed) return;
                        SetActive(false);
                        Destroyed = true;
                        foreach (var component in components.ToArray())
                        {
                            component.Notify("OnDestroy");
                            component.Destroyed = true;
                        }
                        foreach (var child in transform.Children.ToArray()) Destroy(child.gameObject);
                        transform.parent?.Children.Remove(transform);
                    }
                }
                public class Transform : Component
                {
                    internal List<Transform> Children { get; } = new();
                    public Transform? parent { get; private set; }
                    public int childCount => Children.Count;
                    public int GetSiblingIndex() => parent?.Children.IndexOf(this) ?? 0;
                    internal Vector3 Offset { get; set; }
                    public Vector3 position => (parent?.position ?? default) + Offset;
                    public Vector3 TransformPoint(Vector3 local) => position + local;
                    public Vector3 InverseTransformPoint(Vector3 world) => world - position;
                    public void SetParent(Transform target, bool worldPositionStays)
                    {
                        parent?.Children.Remove(this);
                        parent = target;
                        target.Children.Add(this);
                    }
                    public void SetAsLastSibling()
                    {
                        parent?.Children.Remove(this);
                        parent?.Children.Add(this);
                    }
                    public bool TryGetComponent<T>(out T result) where T : Component
                    {
                        result = gameObject.GetComponent<T>()!;
                        return result != null;
                    }
                }
                public sealed class RectTransform : Transform
                {
                    public Vector2 anchorMin { get; set; }
                    public Vector2 anchorMax { get; set; }
                    public Vector2 pivot { get; set; } = new(.5f, .5f);
                    public Vector2 sizeDelta { get; set; }
                    public Vector2 anchoredPosition
                    {
                        get => new(Offset.x, Offset.y);
                        set => Offset = new Vector3(value.x, value.y, 0f);
                    }
                    public Rect rect => new(-pivot.x * sizeDelta.x, -pivot.y * sizeDelta.y, sizeDelta.x, sizeDelta.y);
                    public void GetWorldCorners(Vector3[] corners)
                    {
                        var local = rect;
                        corners[0] = TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
                        corners[1] = TransformPoint(new Vector3(local.xMin, local.yMax, 0f));
                        corners[2] = TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
                        corners[3] = TransformPoint(new Vector3(local.xMax, local.yMin, 0f));
                    }
                }
                public struct Vector2 : IEquatable<Vector2>
                {
                    public Vector2(float x, float y) { this.x = x; this.y = y; }
                    public float x, y;
                    public static Vector2 zero => default;
                    public bool Equals(Vector2 other) => x == other.x && y == other.y;
                    public override bool Equals(object? other) => other is Vector2 value && Equals(value);
                    public override int GetHashCode() => HashCode.Combine(x, y);
                    public static bool operator ==(Vector2 first, Vector2 second) => first.Equals(second);
                    public static bool operator !=(Vector2 first, Vector2 second) => !first.Equals(second);
                }
                public readonly record struct Vector3(float x, float y, float z)
                {
                    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
                    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
                }
                public readonly record struct Vector4(float x, float y, float z, float w);
                public readonly record struct Rect(float xMin, float yMin, float width, float height)
                {
                    public float xMax => xMin + width;
                    public float yMax => yMin + height;
                    public Vector2 size => new(width, height);
                    public static Rect MinMaxRect(float minX, float minY, float maxX, float maxY) =>
                        new(minX, minY, maxX - minX, maxY - minY);
                }
                public readonly record struct Color(int Id)
                {
                    public static Color clear => default;
                }
                public sealed class Canvas : MonoBehaviour
                {
                    public int sortingOrder { get; set; }
                    public Canvas rootCanvas => transform.parent?.gameObject.GetComponentInParent<Canvas>(true)?.rootCanvas ?? this;
                }
                public sealed class CanvasGroup : MonoBehaviour { public float alpha = 1f; }
                public static class Mathf
                {
                    public static float Min(float a, float b) => Math.Min(a, b);
                    public static float Max(float a, float b) => Math.Max(a, b);
                    public static float Abs(float a) => Math.Abs(a);
                    public static float Clamp(float value, float low, float high) => Math.Clamp(value, low, high);
                }
                public static class Time { public static double unscaledTimeAsDouble; }
            }

            namespace UnityEngine.EventSystems
            {
                using UnityEngine;
                public class BaseEventData { }
                public sealed class PointerEventData : BaseEventData { }
                public interface IPointerEnterHandler { void OnPointerEnter(PointerEventData data); }
                public interface IPointerExitHandler { void OnPointerExit(PointerEventData data); }
                public interface ISelectHandler { void OnSelect(BaseEventData data); }
                public interface IDeselectHandler { void OnDeselect(BaseEventData data); }
                public sealed class EventSystem
                {
                    public static EventSystem? current;
                    public GameObject? currentSelectedGameObject { get; private set; }
                    public void Select(GameObject? selected, BaseEventData data)
                    {
                        if (currentSelectedGameObject == selected) return;
                        currentSelectedGameObject?.GetComponent<TopiaForge.Mods.UnityUi.TopiaForgeScopedTooltipFocus>()?.OnDeselect(data);
                        currentSelectedGameObject = selected;
                        selected?.GetComponent<TopiaForge.Mods.UnityUi.TopiaForgeScopedTooltipFocus>()?.OnSelect(data);
                    }
                }
            }

            namespace UnityEngine.UI
            {
                using UnityEngine;
                public class Graphic : MonoBehaviour
                {
                    public Color color { get; set; }
                    public bool raycastTarget { get; set; } = true;
                    public RectTransform rectTransform => transform;
                }
                public class Image : Graphic { }
                public struct Navigation
                {
                    public enum Mode { None, Automatic }
                    public Mode mode;
                }
                public class Selectable : MonoBehaviour
                {
                    public enum Transition { None, ColorTint }
                    public Navigation navigation = new() { mode = Navigation.Mode.Automatic };
                    public Graphic? targetGraphic;
                    public Transition transition;
                    public bool interactable = true;
                    public bool IsInteractable() => interactable;
                }
                public sealed class LayoutElement : MonoBehaviour { public float minWidth, minHeight; }
                public sealed class RectMask2D : MonoBehaviour { public Vector4 padding { get; set; } }
            }

            namespace TMPro
            {
                using UnityEngine;
                using UnityEngine.UI;
                public sealed class TMP_FontAsset { }
                public enum TextAlignmentOptions { TopLeft }
                public enum TextWrappingModes { Normal }
                public enum TextOverflowModes { Ellipsis }
                public enum FontStyles { Bold }
                public sealed class TextMeshProUGUI : Graphic
                {
                    public string text { get; set; } = string.Empty;
                    public TMP_FontAsset? font { get; set; }
                    public bool richText { get; set; } = true;
                    public bool parseCtrlCharacters { get; set; } = true;
                    public TextAlignmentOptions alignment { get; set; }
                    public TextWrappingModes textWrappingMode { get; set; }
                    public TextOverflowModes overflowMode { get; set; }
                    public FontStyles fontStyle { get; set; }
                    public float fontSize { get; set; }
                    public float characterSpacing { get; set; }
                    public int Measurements { get; private set; }
                    public Vector2 GetPreferredValues(string value, float maxWidth, float maxHeight)
                    {
                        Measurements++;
                        var natural = Math.Max(1, value.Length) * fontSize / 2f;
                        return new Vector2(Math.Min(natural, maxWidth), MathF.Ceiling(natural / maxWidth) * fontSize);
                    }
                }
            }
            """;
    }
}
