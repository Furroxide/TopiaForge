namespace TopiaForge.ModManager.Tests
{
    internal static partial class ChatViewContractTests
    {
        private const string Contracts = """
            using System;
            using System.Collections.Generic;
            namespace UnityEngine
            {
                public class GameObject { public Transform transform = new Transform(); }
                public class Transform
                {
                    public Transform? parent;
                    public bool IsChildOf(Transform other) => parent == other || parent?.IsChildOf(other) == true;
                }
            }
            namespace UnityEngine.EventSystems
            {
                public class EventSystem
                {
                    public static EventSystem current = new EventSystem();
                    public UnityEngine.GameObject? currentSelectedGameObject;
                    public void SetSelectedGameObject(UnityEngine.GameObject? next) => currentSelectedGameObject = next;
                }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                using UnityEngine;
                using UnityEngine.EventSystems;
                public enum TopiaForgeLayerBand { Hud, Window }
                public enum TopiaForgeGap { Sm }
                public enum TopiaForgeTextStyle { Caption }
                public enum TopiaForgeTone { Muted, Warning }
                public enum TopiaForgeButtonStyle { Ghost }
                public enum TopiaForgeChatLineKind { Player, Error, System }
                public static class TopiaForgeTokens { public const float ControlHeight = 32; }
                public interface ITopiaForgeDismissable { TopiaForgeLayerBand Band { get; } void Dismiss(); }
                public static class TopiaForgeDismissStack
                {
                    private static readonly HashSet<ITopiaForgeDismissable> Entries = new HashSet<ITopiaForgeDismissable>();
                    public static int Count => Entries.Count;
                    public static void Push(ITopiaForgeDismissable entry) => Entries.Add(entry);
                    public static void Remove(ITopiaForgeDismissable entry) => Entries.Remove(entry);
                }
                public class TopiaForgeCursorLease
                {
                    public static bool Active;
                    public void SetActive(bool active) => Active = active;
                    public void Release() => Active = false;
                }
                public class Widget
                {
                    public GameObject Go = new GameObject();
                    public void SetShown(bool value) { }
                    public void ReleaseFocus() => TopiaForgeFocus.Release(Go);
                }
                public static class Layout
                {
                    public static T Stretch<T>(this T widget) where T : Widget => widget;
                    public static T Dynamic<T>(this T widget) where T : Widget => widget;
                    public static T Flex<T>(this T widget, float x, float y) where T : Widget => widget;
                    public static T FixedHeight<T>(this T widget, float value) where T : Widget => widget;
                }
                public class TopiaForgeContainer : Widget
                {
                    private static readonly Dictionary<string, (GameObject, Action)> Buttons = new Dictionary<string, (GameObject, Action)>();
                    private T Child<T>(T child) where T : Widget { child.Go.transform.parent = Go.transform; return child; }
                    public TopiaForgeContainer Column(TopiaForgeGap gap) => Child(new TopiaForgeContainer());
                    public TopiaForgeContainer Row(TopiaForgeGap gap) => Child(new TopiaForgeContainer());
                    public TopiaForgeChatLog ChatLog(int capacity, int maximum) => Child(new TopiaForgeChatLog());
                    public TopiaForgeInputField Input(string placeholder, string value, Action<string> change) => Child(new TopiaForgeInputField());
                    public void Button(string label, Action callback, TopiaForgeButtonStyle style = default)
                    { var button = Child(new Widget()); Buttons[label] = (button.Go, callback); }
                    public static void Click(string label)
                    { var button = Buttons[label]; EventSystem.current.SetSelectedGameObject(button.Item1); button.Item2(); }
                    public TopiaForgeLabel Label(string text, TopiaForgeTextStyle style) => Child(new TopiaForgeLabel());
                }
                public class TopiaForgeChatLog : Widget
                {
                    public TopiaForgeChatLog ClosedLines(int value) => this;
                    public void Push(string author, string text, TopiaForgeChatLineKind kind) { }
                    public void SetOpen(bool value) { }
                    public void Clear() { }
                }
                public class TopiaForgeInputField : Widget
                {
                    public static TopiaForgeInputField? Last;
                    public TopiaForgeInputField() { Last = this; }
                    public bool IsFocused;
                    public string Text = "";
                    public TopiaForgeInputField PlainText() => this;
                    public void SetCharacterLimit(int maximum) { }
                    public void OnSubmit(Action<string> callback) { }
                    public void SetText(string text) => Text = text;
                    public void Focus() { IsFocused = true; EventSystem.current.SetSelectedGameObject(Go); }
                    public void Blur() { IsFocused = false; ReleaseFocus(); }
                }
                public class TopiaForgeLabel : Widget
                {
                    public TopiaForgeLabel PlainText() => this;
                    public void SetText(string text) { }
                    public void SetTone(TopiaForgeTone tone) { }
                }
            }
            """;
    }
}
