namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiToastNativeTests
    {
        private const string Boundaries = """"
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using UnityEngine;
            using TMPro;

            namespace UnityEngine
            {
                public class Component
                {
                    internal GameObject gameObject = null!;
                    public RectTransform transform => gameObject.transform;
                }
                public class Transform : Component
                {
                    internal readonly List<GameObject> Children = new List<GameObject>();
                    public void SetParent(Transform parent, bool preserveWorldPosition) => parent.Children.Add(gameObject);
                }
                public sealed class RectTransform : Transform
                {
                    public Vector2 anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta, offsetMin, offsetMax;
                    public Vector3 localScale = new Vector3(1f, 1f, 1f);
                }
                public sealed class GameObject
                {
                    private readonly List<Component> components = new List<Component>();
                    public GameObject(string name, params Type[] components)
                    {
                        Name = name;
                        transform = new RectTransform { gameObject = this };
                    }
                    internal string Name { get; }
                    public RectTransform transform { get; }
                    internal readonly CanvasGroup Group = new CanvasGroup();
                    public bool activeSelf { get; private set; } = true;
                    public void SetActive(bool shown) => activeSelf = shown;
                    public T AddComponent<T>() where T : Component, new()
                    {
                        var component = new T { gameObject = this };
                        components.Add(component);
                        return component;
                    }
                    internal T GetComponent<T>() where T : Component => (T)components.Find(component => component is T)!;
                    internal GameObject Find(string name) => transform.Children.Find(child => child.Name == name)!;
                }
                public record struct Vector2(float x, float y);
                public readonly record struct Vector3(float x, float y, float z);
                public sealed class CanvasGroup { public float alpha = 1f; }
                public static class Screen { public static int width = 1920, height = 1080; }
                public readonly record struct Color { internal Color(UiThemeColor rgb) => Rgb = rgb; internal UiThemeColor Rgb { get; } }
                public sealed record Sprite(UiGlyph? Glyph = null, float Stroke = 0f);
                public static class Mathf
                {
                    public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
                    public static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
                    public static bool Approximately(float left, float right) => Math.Abs(left - right) < .001f;
                }
            }

            namespace UnityEngine.UI
            {
                public sealed class Image : Component
                {
                    public enum Type { Sliced }
                    public Type type;
                    public Sprite? sprite;
                    public bool enabled = true;
                    public bool raycastTarget = true;
                    public Color color;
                    public RectTransform rectTransform => transform;
                }
            }

            namespace TMPro
            {
                public enum TextAlignmentOptions { Left }
                public enum TextWrappingModes { Normal }
                public enum TextOverflowModes { Ellipsis }
                public sealed class TMP_FontAsset { }
                public sealed class TextMeshProUGUI : Component
                {
                    public float fontSize;
                    public TMP_FontAsset? font;
                    public TextAlignmentOptions alignment;
                    public TextWrappingModes textWrappingMode;
                    public TextOverflowModes overflowMode;
                    public string text = "";
                    public bool richText = true;
                    public bool parseCtrlCharacters = true;
                    public Color color;
                    public RectTransform rectTransform => transform;
                    public Vector2 GetPreferredValues(string value, float width, float height)
                    {
                        var lines = 0f;
                        foreach (var line in value.Split('\n'))
                            lines += Math.Max(1f, MathF.Ceiling(line.Length * 10f / width));
                        return new Vector2(width, lines * 20f);
                    }
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                public enum TopiaForgeScheme { Hud }
                public enum TopiaForgeLayerBand { Toast }
                public enum TopiaForgeTextStyle { Label }
                public enum TopiaForgeRadius { Control }
                public enum TopiaForgeGap { Sm = 8, Md = 12 }
                public enum TopiaForgeTone { Neutral, Muted, Faint, Primary, Accent, Success, Warning, Danger }
                public static class TopiaForgeTokens
                {
                    public const float SafeMargin = 18f, ScrollGutter = 20f, LabelSize = 16f, DurationFast = .1f;
                    public const float BorderStandard = 2f, BorderStrong = 3f, DurationBase = .2f;
                }
                public static class TopiaForgeTheme
                {
                    internal static int Version;
                    internal static bool HighContrast;
                    internal static bool ReducedMotion;
                    internal static float UiScale = 1f;
                }
                public sealed class TopiaForgeResolvedTheme
                {
                    public TopiaForgeResolvedTheme(TopiaForgeScheme scheme, object? accent, bool highContrast, int version)
                        => HighContrast = highContrast;
                    private UiThemeScheme Current => UiThemePackParser.Default.SelectScheme(true, HighContrast);
                    internal bool HighContrast { get; }
                    internal Color SurfaceAlt => new Color(Current.GetPair(UiPaintRole.Raised).Fill);
                    internal Color ForegroundOn(UiPaintRole role, UiTone tone = UiTone.Neutral) => new Color(Current.ResolveForeground(tone, role));
                    internal Color BoundaryOn(UiPaintRole role) => new Color(Current.ResolveBoundary(role));
                    internal Color ToneColor(TopiaForgeTone tone) => BoundaryOn(UiPaintRole.Raised);
                }
                public sealed class TopiaForgeUiOptions { public string OwnerId = ""; }
                public static class TopiaForgeUi
                {
                    internal static UiHost LastHost = null!;
                    public static UiHost Create(TopiaForgeUiOptions options) => LastHost = new UiHost(options.OwnerId);
                }
                public sealed class UiHost : IDisposable
                {
                    public UiHost(string owner) => OwnerId = owner;
                    public string OwnerId { get; }
                    internal readonly GameObject LayerRoot = new GameObject("Toast layer");
                    internal bool Interactive;
                    internal TopiaForgeAccessibilityProfile Profile = TopiaForgeAccessibilityProfile.Default;
                    internal TopiaForgeEffectiveAccessibility EffectiveAccessibility =>
                        Profile.Resolve(TopiaForgeTheme.HighContrast, TopiaForgeTheme.UiScale, TopiaForgeTheme.ReducedMotion, 1f);
                    internal float EffectiveUiScale => EffectiveAccessibility.UiScale;
                    internal bool EffectiveReducedMotion => EffectiveAccessibility.ReducedMotion;
                    internal float EffectiveMotion => EffectiveAccessibility.MotionIntensity;
                    internal TopiaForgeResolvedTheme Theme(TopiaForgeScheme scheme) =>
                        new TopiaForgeResolvedTheme(scheme, null, EffectiveAccessibility.HighContrast, TopiaForgeTheme.Version);
                    public TopiaForgeContainer Layer(string name, TopiaForgeLayerBand band, TopiaForgeScheme scheme,
                        bool interactive, bool persistent)
                    {
                        Interactive = interactive;
                        return new TopiaForgeContainer(this, LayerRoot);
                    }
                    public void Dispose() => LayerRoot.SetActive(false);
                }
                public class TopiaForgeWidget
                {
                    internal TopiaForgeWidget(UiHost host, GameObject go) { Host = host; Go = go; }
                    internal UiHost Host { get; }
                    internal GameObject Go { get; }
                    internal RectTransform Rect => Go.transform;
                    internal CanvasGroup EnsureCanvasGroup() => Go.Group;
                }
                public sealed class TopiaForgeContainer : TopiaForgeWidget
                {
                    internal TopiaForgeContainer(UiHost host, GameObject go) : base(host, go) { }
                    internal TopiaForgeContainer Stack(string name)
                    {
                        var child = new GameObject(name);
                        child.transform.SetParent(Rect, false);
                        return new TopiaForgeContainer(Host, child);
                    }
                }
                public static class TopiaForgeAnchors
                {
                    internal static void Stretch(RectTransform target, float left = 0f, float bottom = 0f, float right = 0f, float top = 0f)
                    {
                        target.offsetMin = new Vector2(left, bottom);
                        target.offsetMax = new Vector2(-right, -top);
                    }
                }
                public static class TopiaForgeFonts { internal static TMP_FontAsset? For(TopiaForgeTextStyle style) => null; }
                public static class TopiaForgeSprites
                {
                    internal static Sprite Fill(TopiaForgeRadius radius) => new Sprite();
                    internal static Sprite Ring(TopiaForgeRadius radius, float stroke) => new Sprite(Stroke: stroke);
                    internal static Sprite Glyph(UiGlyph glyph) => new Sprite(glyph);
                }
                public static class TopiaForgeTmp { internal static TextMeshProUGUI Create(GameObject go) => go.AddComponent<TextMeshProUGUI>(); }
                public static class TopiaForgeTypography
                {
                    internal static void ApplyControlText(TextMeshProUGUI text, TopiaForgeResolvedTheme theme, TopiaForgeTextStyle style, float scale) { }
                }
                internal static class TopiaForgeCallbacks
                {
                    internal static void Invoke(Action? action, string description) => action?.Invoke();
                    internal static void Resume(object? intent, Action? action, string description) => action?.Invoke();
                }
                internal static class TopiaForgeIntent { internal static object? Capture() => null; }
                public static class TopiaForgeRuntime { internal static void Ensure() { } }
                public static class TopiaForgeLog { internal static void Warn(string text) { } }
            }
            """";
    }
}
