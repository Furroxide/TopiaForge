using System;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitDropdownPopupTests
    {
        internal static void Run()
        {
            UiKitButtonMotionTests.RunWidget("TopiaForgeDropdown.Popup.cs", Boundary, "DropdownPopupProbe");
            Console.WriteLine("Native dropdown popup passed canvas and dismiss ownership, viewport refit, teardown, and 4,096 stable frames (0 bytes).");
        }

        private const string Boundary = """
            using System;
            using System.Reflection;
            using TMPro;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;

            public static class DropdownPopupProbe
            {
                public static void Run()
                {
                    var type = typeof(TopiaForgeDropdown).GetNestedType("TopiaForgeTmpDropdown", BindingFlags.NonPublic)!;
                    var dropdown = (TMP_Dropdown)Activator.CreateInstance(type, nonPublic: true)!;
                    var frame = (Action)type.GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .CreateDelegate(typeof(Action), dropdown);
                    var owner = new Canvas(new Rect(-200, -120, 400, 240)) { Band = TopiaForgeLayerBand.Fullscreen };
                    var nested = new Canvas(new Rect(-80, -80, 160, 160)) { Root = owner };
                    dropdown.ParentCanvas = nested;
                    type.GetMethod("ConfigureItemCues", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(dropdown, new object[] { 7f, true });

                    dropdown.Open(nested);
                    Check(TopiaForgeControlCues.Last == new TopiaForgeControlCues.Paint(new Color(0.2f), new Color(0.95f), 7f, true),
                        "The popup preserves distinct item cues with the resolved palette and high-contrast shape.");
                    var popup = TopiaForgeDropdown.VisiblePopup(dropdown);
                    var blocker = dropdown.Blocker!.transform;
                    Check(popup != null && popup.Parent == owner.transform && blocker.Parent == owner.transform &&
                        popup.Order > blocker.Order && !popup.gameObject.GetComponent<Canvas>()!.overrideSorting &&
                        !blocker.gameObject.GetComponent<Canvas>()!.overrideSorting,
                        "The blocker and list stay above their owner's content with inherited canvas ordering.");
                    Check(ReferenceEquals(TopiaForgeDismissStack.Owner, dropdown) &&
                        TopiaForgeDismissStack.Claim == TopiaForgeInputClaim.Navigation &&
                        TopiaForgeDismissStack.Target == popup.gameObject &&
                        TopiaForgeDismissStack.Owner!.Band == TopiaForgeLayerBand.Fullscreen,
                        "The open list takes navigation at its owner canvas band.");
                    Check(popup.sizeDelta == new Vector2(180, 100) && popup.anchoredPosition == new Vector2(110, -20),
                        "The list fits its actual viewport with its native pivot.");

                    owner.transform.rect = new Rect(-50, -35, 100, 70);
                    frame();
                    Check(popup.sizeDelta == new Vector2(100, 70) && popup.anchoredPosition == new Vector2(0, 35),
                        "A viewport change refits the live popup within the new screen bounds.");
                    for (var i = 0; i < 100; i++) frame();
                    var writes = popup.SizeWrites;
                    var start = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 4096; i++) frame();
                    Check(popup.SizeWrites == writes && GC.GetAllocatedBytesForCurrentThread() == start,
                        "A settled viewport performs no popup writes or allocations.");

                    TopiaForgeDismissStack.Owner.Dismiss();
                    Check(dropdown.Value == 2 && dropdown.Hides == 1 && TopiaForgeDismissStack.Owner == null &&
                        TopiaForgeDropdown.VisiblePopup(dropdown) == null, "Back closes only the popup and preserves the selected value.");
                    dropdown.Open(nested);
                    dropdown.Disable();
                    Check(TopiaForgeDismissStack.Owner == null && TopiaForgeDropdown.VisiblePopup(dropdown) == null,
                        "Disabling an open popup releases its input claim and invalidates capture.");
                }

                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException(message);
                }
            }

            namespace UnityEngine
            {
                public readonly record struct Color(float Value);
                public readonly record struct Vector2(float x, float y)
                {
                    internal static Vector2 Min(Vector2 a, Vector2 b) => new Vector2(Math.Min(a.x, b.x), Math.Min(a.y, b.y));
                }
                public readonly record struct Rect(float xMin, float yMin, float Width, float Height)
                {
                    internal float xMax => xMin + Width;
                    internal float yMax => yMin + Height;
                    internal Vector2 size => new Vector2(Width, Height);
                }
                internal static class Mathf { internal static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max); }
                public sealed class RectTransform
                {
                    private static int order;
                    private Vector2 size;
                    internal RectTransform(GameObject gameObject) { this.gameObject = gameObject; }
                    internal readonly GameObject gameObject;
                    internal RectTransform? Parent;
                    internal int Order, SizeWrites;
                    internal Rect rect;
                    internal Vector2 pivot, anchorMin, anchorMax, position, anchoredPosition;
                    internal Vector2 sizeDelta { get => size; set { size = value; SizeWrites++; } }
                    internal void SetParent(RectTransform owner, bool worldPositionStays) => Parent = owner;
                    internal void SetAsLastSibling() => Order = ++order;
                    internal Vector2 InverseTransformPoint(Vector2 point) => point;
                }
                public sealed class GameObject
                {
                    internal GameObject() { transform = new RectTransform(this); }
                    internal readonly RectTransform transform;
                    internal bool activeInHierarchy = true;
                    internal Canvas? Canvas;
                    internal T? GetComponent<T>() where T : class => Canvas as T;
                }
                public sealed class Canvas
                {
                    internal Canvas(Rect bounds)
                    {
                        transform = new GameObject { Canvas = this }.transform;
                        transform.rect = bounds;
                    }
                    internal Canvas? Root;
                    internal Canvas rootCanvas => Root ?? this;
                    internal RectTransform transform;
                    internal bool overrideSorting = true;
                    internal TopiaForgeLayerBand Band;
                    internal T? GetComponent<T>() where T : class => new TopiaForgeLayerSlot(Band) as T;
                }
            }

            namespace TMPro
            {
                public class TMP_Dropdown
                {
                    protected sealed class DropdownItem
                    {
                        internal ItemToggle toggle = new();
                        internal ItemText text = new();
                    }

                    internal sealed class ItemToggle { internal ItemFill targetGraphic = new(); }
                    internal sealed class ItemFill
                    {
                        internal RectTransform rectTransform = new GameObject().transform;
                        internal Color color = new(0.2f);
                    }
                    internal sealed class ItemText { internal Color color = new(0.95f); }

                    private GameObject? list;
                    internal Canvas? ParentCanvas;
                    internal GameObject? Blocker;
                    internal int Value = 2, Hides;
                    protected T? GetComponentInParent<T>(bool includeInactive) where T : class => ParentCanvas as T;
                    protected virtual DropdownItem CreateItem(DropdownItem template) => template;
                    protected virtual GameObject CreateDropdownList(GameObject template) => template;
                    protected virtual GameObject CreateBlocker(Canvas canvas)
                    {
                        var blocker = new Canvas(canvas.transform.rect).transform.gameObject;
                        blocker.transform.SetParent(canvas.transform, false);
                        return blocker;
                    }
                    protected virtual void DestroyDropdownList(GameObject popup) => popup.activeInHierarchy = false;
                    protected virtual void DestroyBlocker(GameObject blocker) => blocker.activeInHierarchy = false;
                    protected virtual void OnDisable() => Hide();
                    protected void Hide()
                    {
                        if (Blocker != null) DestroyBlocker(Blocker);
                        if (list != null) DestroyDropdownList(list);
                        Hides++;
                    }
                    internal void Open(Canvas fallback)
                    {
                        var template = new Canvas(new Rect(0, 0, 180, 100)).transform;
                        template.position = new Vector2(190, -115);
                        template.pivot = new Vector2(0.5f, 1f);
                        list = CreateDropdownList(template.gameObject);
                        _ = CreateItem(new DropdownItem());
                        Blocker = CreateBlocker(fallback);
                    }
                    internal void Disable() => OnDisable();
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal enum TopiaForgeLayerBand { Window, Fullscreen }
                internal enum TopiaForgeInputClaim { Navigation }
                internal sealed record TopiaForgeLayerSlot(TopiaForgeLayerBand Band);
                internal interface ITopiaForgeDismissable { TopiaForgeLayerBand Band { get; } void Dismiss(); }
                internal sealed class TopiaForgeControlCues
                {
                    internal readonly record struct Paint(Color Fill, Color Ink, float Radius, bool HighContrast);
                    internal static Paint Last;
                    internal static TopiaForgeControlCues Install(TMP_Dropdown.ItemToggle toggle, RectTransform rect) => new();
                    internal void Apply(Color fill, Color ink, float radius, bool highContrast) =>
                        Last = new Paint(fill, ink, radius, highContrast);
                }

                internal static class TopiaForgeDismissStack
                {
                    internal static ITopiaForgeDismissable? Owner;
                    internal static TopiaForgeInputClaim Claim;
                    internal static GameObject? Target;
                    internal static void Push(ITopiaForgeDismissable owner, TopiaForgeInputClaim claim, GameObject target)
                    { Owner = owner; Claim = claim; Target = target; }
                    internal static void Remove(ITopiaForgeDismissable owner)
                    { if (ReferenceEquals(Owner, owner)) { Owner = null; Target = null; } }
                }
            }
            """;
    }
}
