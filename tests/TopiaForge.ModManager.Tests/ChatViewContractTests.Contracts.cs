namespace TopiaForge.ModManager.Tests
{
    internal static partial class ChatViewContractTests
    {
        private const string Contracts = """
            using System;
            using System.Collections.Generic;
            namespace TopiaForge.Mods.UnityUi
            {
                using UnityEngine;
                using UnityEngine.EventSystems;
                public enum TopiaForgeLayerBand { Hud, Window, Modal }
                public enum TopiaForgeGap { Sm }
                public enum TopiaForgeTextStyle { Caption }
                public enum TopiaForgeTone { Muted, Warning }
                public enum TopiaForgeButtonStyle { Ghost }
                public enum TopiaForgeChatLineKind { Player, Error, System }
                public static class TopiaForgeTokens { public const float ControlHeight = 32; }
                public interface ITopiaForgeDismissable { TopiaForgeLayerBand Band { get; } void Dismiss(); }
                public static class TopiaForgeDismissStack
                {
                    internal static readonly TopiaForgeDismissOrder Order = new TopiaForgeDismissOrder(
                        root => ((GameObject)root).activeInHierarchy,
                        root => EventSystem.current?.currentSelectedGameObject?.transform.IsChildOf(((GameObject)root).transform) == true);
                    public static GameObject? PushedRoot;
                    public static int Count => Order.Count;
                    public static void Push(ITopiaForgeDismissable entry) => Push(entry, TopiaForgeInputClaim.Back, null);
                    internal static void Push(ITopiaForgeDismissable entry, TopiaForgeInputClaim claim, GameObject? root)
                    { PushedRoot = root; Order.Push(entry, claim, root); }
                    public static void Remove(ITopiaForgeDismissable entry) => Order.Remove(entry);
                }
                public class TopiaForgeCursorLease
                {
                    public static bool Active;
                    public void SetActive(bool active) => Active = active;
                    public void Release() => Active = false;
                }
                public partial class Widget
                {
                    public GameObject Go = new GameObject();
                    public void SetShown(bool value) => Go.activeSelf = value;
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
                    public static TopiaForgeChatLog? Last;
                    public TopiaForgeChatLog() { Last = this; }
                    public TopiaForgeChatLog ClosedLines(int value) => this;
                    public void Push(string author, string text, TopiaForgeChatLineKind kind) { }
                    public void SetOpen(bool value) { }
                    public void Clear() { }
                }
                public partial class TopiaForgeInputField : Widget
                {
                    public static TopiaForgeInputField? Last;
                    private readonly TopiaForgeTextInput input;
                    private Action<string>? submit;
                    public TopiaForgeInputField() { Last = this; input = Go.AddComponent<TopiaForgeTextInput>(); }
                    public bool IsFocused => input.isFocused;
                    public string Text => input.text;
                    public TopiaForgeInputField PlainText() => this;
                    public void SetCharacterLimit(int maximum) { }
                    public void OnSubmit(Action<string> callback) => submit = callback;
                    public void SetText(string text) => input.text = text;
                    public void Advance(string typed = "")
                    { foreach (var character in typed) input.KeyMessage(character); input.RunLateUpdate(); }
                    public void Submit() => submit!(Text);
                    public void EndEditKeepingSelection() => input.DeactivateInputField();
                    public void Blur() { input.DeactivateInputField(); ReleaseFocus(); input.Hide(); }
                }
                public class TopiaForgeLabel : Widget
                {
                    public static TopiaForgeLabel? Last;
                    public TopiaForgeLabel() { Last = this; }
                    public TopiaForgeLabel PlainText() => this;
                    public void SetText(string text) { }
                    public void SetTone(TopiaForgeTone tone) { }
                }
            }
            """;
    }
}
