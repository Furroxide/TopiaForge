namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiOverlayNativeTests
    {
        private const string InputWidgetBoundaries = """
            using System;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.UI;

            namespace UnityEngine
            {
                public static partial class Time { public static int frameCount; }
                public sealed partial class Canvas : Component { public Canvas rootCanvas => this; }
                public partial struct Vector2 { public float magnitude => MathF.Sqrt(x * x + y * y); }
                public enum KeyCode { None, Escape }
                public static class Input
                {
                    public static bool EscapeHeld, EscapePressed;
                    public static bool anyKey => EscapeHeld;
                    public static bool GetKey(KeyCode code) => code == KeyCode.Escape && EscapeHeld;
                    public static bool GetKeyDown(KeyCode code) => code == KeyCode.Escape && EscapePressed;
                }
                public sealed partial class GameObject
                {
                    public bool TryGetComponent<T>(out T component) where T : Component
                    {
                        component = GetComponent<T>();
                        return component != null;
                    }
                }
            }

            namespace TMPro
            {
                public class TMP_InputField : Component { public bool isFocused; }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                // Viewer-device sampling is exercised with its production observer in UiKitNativeBindingTests.
                internal static partial class TopiaForgeInputOwnership
                {
                    private static readonly ViewerInputBoundary ViewerInput = new();
                    private static void ObserveViewerInput(UnityEngine.InputSystem.LowLevel.InputEventPtr input,
                        UnityEngine.InputSystem.InputDevice device) { }
                    private static void ResetViewerInput() { }

                    private sealed class ViewerInputBoundary
                    {
                        internal void Forget(int deviceId) { }
                        internal void ForgetHeld() { }
                    }
                }

                internal static class TopiaForgeNavigateAction
                {
                    internal static readonly TopiaForgeNavigationBindings Bindings = new();
                    internal static void Reset() { }
                }
                public enum TopiaForgeButtonStyle { Outline }
                public enum TopiaForgeTone { Neutral, Muted, Warning }
                public enum TopiaForgeIcon { Warning }

                public partial class TopiaForgeContainer
                {
                    public bool Visible => Go.activeSelf;
                    public TopiaForgeResolvedTheme Theme => ResolvedTheme;
                    public TopiaForgeContainer CreateChild(string name) => Stack(name);
                    public TopiaForgeContainer Row(TopiaForgeGap gap = TopiaForgeGap.None) => Stack("Row");
                    public TopiaForgeImage Image(string name) => new(Host, Scheme, CreateChildGameObject(name));
                    public TopiaForgeButton Button(string label, Action pressed, TopiaForgeButtonStyle style) =>
                        new(Host, Scheme, CreateChildGameObject(label));
                    public void SetVisible(bool visible) => SetShown(visible);
                }
                public sealed partial class TopiaForgeLabel
                {
                    public TopiaForgeLabel Ellipsis() => this;
                    public TopiaForgeLabel Tone(TopiaForgeTone tone) => this;
                    public TopiaForgeLabel AlignTopLeft() => this;
                    public TopiaForgeLabel Stretch(float left, float right) => this;
                    public void SetBackdrop(UiPaintRole role) { }
                    public void SetText(string value) => Go.GetComponent<TMPro.TMP_Text>().text = value;
                    public void SetTone(TopiaForgeTone tone) { }
                }
                public sealed class TopiaForgeImage : TopiaForgeWidget
                {
                    public TopiaForgeImage(UiHost host, TopiaForgeScheme scheme, GameObject go) : base(host, scheme, go) { }
                    public TopiaForgeImage Icon(TopiaForgeIcon icon) => this;
                    public TopiaForgeImage Fixed(float width, float height) => this;
                    public void SetColor(Color value) { }
                }
                public sealed class TopiaForgeButton : TopiaForgeWidget
                {
                    public TopiaForgeButton(UiHost host, TopiaForgeScheme scheme, GameObject go) : base(host, scheme, go) =>
                        Button = go.AddComponent<Selectable>();
                    public Selectable Button { get; }
                    public void SetBackdrop(UiPaintRole role) { }
                    public void SetEnabled(bool value) => Button.interactable = value;
                }
                public sealed partial class TopiaForgeResolvedTheme
                {
                    public Color SurfaceSunken => Surface;
                    public Color ForegroundOn(UiPaintRole role, UiTone tone) => Outline;
                }
                internal static partial class TopiaForgeSprites
                {
                    internal static Sprite Fill(TopiaForgeRadius radius) => new();
                    internal static Sprite Ring(TopiaForgeRadius radius, float width) => new();
                }
                internal static class InputWidgetLayout
                {
                    internal static T FixedHeight<T>(this T widget, float height) where T : TopiaForgeWidget => widget;
                    internal static T FillWidth<T>(this T widget) where T : TopiaForgeWidget => widget;
                }
            }
            """;
    }
}
