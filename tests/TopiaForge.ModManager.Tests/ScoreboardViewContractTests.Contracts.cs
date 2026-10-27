namespace TopiaForge.ModManager.Tests
{
    internal static partial class ScoreboardViewContractTests
    {
        private const string Contracts = """
            using System;
            using System.Collections.Generic;
            using UnityEngine;
            using UnityEngine.EventSystems;
            namespace UnityEngine
            {
                public static class Application { public static bool isFocused = true; }
                public class GameObject
                {
                    public bool activeSelf = true;
                    public bool activeInHierarchy => activeSelf && (transform.parent?.Go.activeInHierarchy ?? true);
                    public readonly Transform transform;
                    public GameObject() { transform = new Transform(this); }
                }
                public class Transform
                {
                    public readonly GameObject Go; public Transform? parent; public int SiblingIndex;
                    public Transform(GameObject go) { Go = go; }
                    public bool IsChildOf(Transform root) => this == root || parent?.IsChildOf(root) == true;
                    public void SetSiblingIndex(int value) => SiblingIndex = value;
                    public int GetSiblingIndex() => SiblingIndex;
                }
                public struct Vector2 { public Vector2(float x, float y) { } public static Vector2 zero => default; }
                public class RectTransform { public Vector2 anchorMin, anchorMax, offsetMin, offsetMax; }
            }
            namespace UnityEngine.EventSystems
            {
                public class EventSystem
                {
                    public static EventSystem current = new EventSystem();
                    public GameObject? currentSelectedGameObject;
                    public void SetSelectedGameObject(GameObject? go) => currentSelectedGameObject = go;
                }
            }
            namespace UnityEngine.SceneManagement
            {
                public struct Scene { public int handle; }
                public static class SceneManager { public static int Handle; public static Scene GetActiveScene() => new Scene { handle = Handle }; }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                public enum TopiaForgeLayerBand { Hud, Window, Modal }
                public enum TopiaForgeGap { Xs, Sm, Md }
                public enum TopiaForgeTextStyle { Caption, Body, Title }
                public enum TopiaForgeTone { Primary }
                public enum TopiaForgeButtonStyle { Ghost, Outline, Danger }
                public enum TopiaForgeScheme { Hud }
                public enum TopiaForgePanelStyle { Card }
                public enum TopiaForgeTalkState { Idle }
                public static class TopiaForgeTokens { public const float ControlHeight = 32; }
                public interface ITopiaForgeDismissable { TopiaForgeLayerBand Band { get; } void Dismiss(); }
                public static class TopiaForgeDismissStack
                {
                    internal static readonly TopiaForgeDismissOrder Order = new TopiaForgeDismissOrder(
                        root => ((GameObject)root).activeInHierarchy, TopiaForgeInputOwnership.HoldsSelection);
                    internal static void Push(ITopiaForgeDismissable surface, TopiaForgeInputClaim claim, GameObject? root) => Order.Push(surface, claim, root);
                    public static void Remove(ITopiaForgeDismissable surface) => Order.Remove(surface);
                    public static ITopiaForgeDismissable? Top() => Order.Top();
                }
                public static partial class TopiaForgeInputOwnership
                { public static bool Typing; public static bool KitTextFieldHasKeyboard() => Typing; }
                public sealed class TopiaForgeGameTextFocus
                {
                    public static TopiaForgeGameTextFocus Current = new TopiaForgeGameTextFocus();
                    public bool Typing; public bool HasKeyboard(GameObject? go) => Typing;
                }
                public class TopiaForgeCursorLease
                { public static bool Active; public void SetActive(bool value) => Active = value; public void Release() => Active = false; }
                public partial class Widget
                {
                    public static readonly List<Widget> All = new List<Widget>();
                    public readonly GameObject Go = new GameObject();
                    public readonly RectTransform Rect = new RectTransform();
                    public bool Visible = true;
                    public Widget() { All.Add(this); }
                    public void SetShown(bool value) => Go.activeSelf = value;
                    public void SetVisible(bool value) => Visible = value;
                    public void ReleaseFocus() => TopiaForgeFocus.Release(Go);
                    public void Destroy() => Go.activeSelf = false;
                }
                public static class Layout
                {
                    public static T Stretch<T>(this T widget) where T : Widget => widget;
                    public static T Dynamic<T>(this T widget) where T : Widget => widget;
                    public static T Flex<T>(this T widget, float x, float y) where T : Widget => widget;
                    public static T FixedHeight<T>(this T widget, float value) where T : Widget => widget;
                    public static T FixedWidth<T>(this T widget, float value) where T : Widget => widget;
                }
                public class TopiaForgeContainer : Widget
                {
                    private T Child<T>(T widget) where T : Widget { widget.Go.transform.parent = Go.transform; return widget; }
                    public TopiaForgeContainer Column(TopiaForgeGap gap, TopiaForgeGap padding = default) => Child(new TopiaForgeContainer());
                    public TopiaForgeContainer Row(TopiaForgeGap gap) => Child(new TopiaForgeContainer());
                    public TopiaForgeButton Button(string label, Action callback, TopiaForgeButtonStyle style = default) => Child(new TopiaForgeButton(label, callback));
                    public TopiaForgeLabel Label(string text, TopiaForgeTextStyle style) => Child(new TopiaForgeLabel { Text = text });
                    public TopiaForgeScroll Scroll(TopiaForgeGap gap) => Child(new TopiaForgeScroll());
                    public TopiaForgeTalkIndicator TalkIndicator() => Child(new TopiaForgeTalkIndicator());
                    public TopiaForgeBadge Badge(string text, TopiaForgeTone tone = default) => Child(new TopiaForgeBadge());
                    public TopiaForgeSignalBars SignalBars() => Child(new TopiaForgeSignalBars());
                    public TopiaForgeSlider Slider(string label, float min, float max, float initial, Action<float> callback) => Child(new TopiaForgeSlider(callback));
                }
                public class TopiaForgeScroll : Widget
                { public TopiaForgeContainer Content = new TopiaForgeContainer(); public TopiaForgeScroll() { Content.Go.transform.parent = Go.transform; } }
                public class TopiaForgeButton : Widget
                {
                    public string Text; public bool Enabled = true; private readonly Action callback;
                    public TopiaForgeButton(string text, Action callback) { Text = text; this.callback = callback; }
                    public void Focus() => EventSystem.current.SetSelectedGameObject(Go);
                    public void Click() { if (Enabled && Go.activeInHierarchy) { Focus(); callback(); } }
                    public void SetText(string text) => Text = text;
                    public void SetEnabled(bool value) => Enabled = value;
                }
                public class TopiaForgeLabel : Widget
                {
                    public string Text = ""; public bool Plain;
                    public TopiaForgeLabel PlainText() { Plain = true; return this; }
                    public TopiaForgeLabel Ellipsis() => this;
                    public void SetText(string text) => Text = text;
                    public void SetText(string prefix, int first, string middle, int last) { }
                }
                public class TopiaForgeTalkIndicator : Widget { public void SetState(TopiaForgeTalkState state) { } }
                public class TopiaForgeSignalBars : Widget { public void SetLevel(int value, int maximum) { } }
                public class TopiaForgeBadge : Widget { }
                public class TopiaForgeSlider : Widget
                { private readonly Action<float> callback; public TopiaForgeSlider(Action<float> callback) { this.callback = callback; } public void Change(float value) => callback(value); public void SetValue(float value) { } public void SetEnabled(bool value) { } }
            }
            """;
    }
}
