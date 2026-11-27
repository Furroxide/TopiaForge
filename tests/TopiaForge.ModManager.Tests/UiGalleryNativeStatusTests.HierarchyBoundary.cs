namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryNativeStatusTests
    {
        private const string HierarchyBoundary = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using TMPro;
            using TopiaForge.ModManager;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class UiGalleryNativeTree
                {
                    private bool Control(UiNode fixture, RectTransform native, string path, UiThemeColor? backdrop) =>
                        throw new NotSupportedException("This fixture has no input controls.");
                    private bool CaptureTooltip(UiTooltip fixture, RectTransform native, string path, UiThemeColor? backdrop) =>
                        throw new NotSupportedException("This fixture has no tooltip.");
                    private bool CaptureArt(UiNode fixture, RectTransform native, string path, UiThemeColor? backdrop) =>
                        throw new NotSupportedException("This fixture has no artwork.");
                    private bool CaptureAction(UiNode fixture, RectTransform native, string path, UiThemeColor? backdrop) =>
                        throw new NotSupportedException("This fixture has no action controls.");
                    private bool CaptureImageNotice(UiSurfaceRequest fixture, RectTransform native, string? queuedNotice) =>
                        throw new NotSupportedException("This fixture has no image fallback.");
                    private bool GraphConnectionsLauncher(TopiaForge.Mods.UnityUi.TopiaForgeGraphConnections connections) =>
                        throw new NotSupportedException("This fixture has no graph.");
                    private bool GraphConnectionsList(UiNode fixture, TopiaForge.Mods.UnityUi.TopiaForgeGraphConnections connections,
                        UiThemeColor? backdrop) => throw new NotSupportedException("This fixture has no graph.");
                    private static UiCaptureNodeFlags Flags(Button button) => throw new NotSupportedException();
                    private void HitAndFocus(string path, Button button, string text, UiThemeColor? backdrop) => throw new NotSupportedException();
                    private UiCaptureNodePaint Decoration(RectTransform view, Graphic fill) => throw new NotSupportedException();
                    private UiCaptureNodePaint ArtPaint(Graphic mark, UiThemeColor? backdrop) => throw new NotSupportedException();
                }

                internal static class UiGalleryCaptureFixture
                {
                    /* authored text gates */
                }

                internal sealed class UnityUiLayoutDivider : Graphic { }
            }

            namespace UnityEngine
            {
                internal readonly record struct Vector2(float x, float y)
                {
                    internal static float Distance(Vector2 a, Vector2 b) =>
                        MathF.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
                }
                internal readonly record struct Vector2Int(int x, int y) { internal static Vector2Int zero => default; }
                internal readonly record struct Vector3(float x, float y, float z = 0f)
                {
                    internal static Vector3 zero => default;
                    internal static Vector3 up => new(0f, 1f);
                }
                internal readonly record struct Vector4(float x, float y, float z, float w) { internal static Vector4 zero => default; }
                internal readonly record struct Rect(float width, float height)
                {
                    internal float xMin => 0f;
                    internal float yMin => 0f;
                    internal float xMax => width;
                    internal float yMax => height;
                }
                internal readonly record struct Color(UiThemeColor Rgb, float a = 1f)
                {
                    public static Color operator *(Color a, Color b) => new Color(new UiThemeColor(
                        Product(a.Rgb.R, b.Rgb.R), Product(a.Rgb.G, b.Rgb.G), Product(a.Rgb.B, b.Rgb.B)), a.a * b.a);
                    private static byte Product(byte a, byte b) => (byte)MathF.Round(a * b / 255f);
                    internal static Color Lerp(Color a, Color b, float t) => new Color(new UiThemeColor(
                        Mix(a.Rgb.R, b.Rgb.R, t), Mix(a.Rgb.G, b.Rgb.G, t), Mix(a.Rgb.B, b.Rgb.B, t)), a.a + (b.a - a.a) * t);
                    private static byte Mix(byte a, byte b, float t) => (byte)MathF.Round(a + (b - a) * t);
                }
                internal readonly record struct Color32(byte r, byte g, byte b, byte a)
                {
                    public static implicit operator Color(Color32 value) => new(new UiThemeColor(value.r, value.g, value.b), value.a / 255f);
                    public static explicit operator Color32(Color value) => new(value.Rgb.R, value.Rgb.G, value.Rgb.B, (byte)MathF.Round(255f * value.a));
                }
                internal static class Screen { internal const int width = 1920, height = 1080; }
                internal static class Mathf
                {
                    internal static float Abs(float value) => MathF.Abs(value);
                    internal static float Min(float a, float b) => MathF.Min(a, b);
                    internal static float Max(float a, float b) => MathF.Max(a, b);
                    internal static bool Approximately(float a, float b) => MathF.Abs(a - b) < .0001f;
                }
                internal sealed class Camera { }
                internal enum RenderMode { ScreenSpaceOverlay }
                internal static class RectTransformUtility
                {
                    internal static Vector2 WorldToScreenPoint(Camera? camera, Vector3 point) => new(point.x, point.y);
                }
                internal sealed class GameObject
                {
                    private readonly Transform transform;
                    internal GameObject(Transform transform) { this.transform = transform; }
                    internal bool activeSelf = true;
                    internal bool activeInHierarchy => activeSelf && (transform.parent?.gameObject.activeInHierarchy ?? true);
                }
                internal class Transform
                {
                    private readonly List<Transform> children = new();
                    private readonly List<Component> components = new();
                    internal Transform(Transform? parent, string name)
                    {
                        this.parent = parent;
                        this.name = name;
                        gameObject = new GameObject(this);
                        parent?.children.Add(this);
                    }
                    internal string name { get; }
                    internal Transform? parent { get; }
                    internal GameObject gameObject { get; }
                    internal int childCount => children.Count;
                    internal Transform GetChild(int index) => children[index];
                    internal Transform? Find(string name) => children.FirstOrDefault(child => child.name == name);
                    internal T Attach<T>(T value) where T : Component { value.transform = this; components.Add(value); return value; }
                    internal T? GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault();
                    internal bool TryGetComponent<T>(out T component) where T : Component { component = GetComponent<T>()!; return component != null; }
                    internal IEnumerable<T> GetComponentsInChildren<T>(bool includeInactive) where T : Component =>
                        (includeInactive || gameObject.activeInHierarchy ? components.OfType<T>() : Enumerable.Empty<T>())
                        .Concat(children.SelectMany(child => child.GetComponentsInChildren<T>(includeInactive)));
                    internal bool IsChildOf(Transform? ancestor)
                    {
                        for (var current = this; current != null; current = current.parent) if (current == ancestor) return true;
                        return false;
                    }
                }
                internal sealed class RectTransform : Transform
                {
                    internal RectTransform(Transform? parent, string name, float x, float y, float width, float height) : base(parent, name) =>
                        Frame = new UiCaptureRect(x, y, width, height);
                    internal UiCaptureRect Frame { get; set; }
                    internal Rect rect => new(Frame.Width, Frame.Height);
                    internal Vector3 TransformPoint(Vector3 local) => new(Frame.X + local.x, Screen.height - Frame.Bottom + local.y);
                    internal void GetWorldCorners(Vector3[] corners)
                    {
                        corners[0] = TransformPoint(new Vector3(0, 0));
                        corners[1] = TransformPoint(new Vector3(0, Frame.Height));
                        corners[2] = TransformPoint(new Vector3(Frame.Width, Frame.Height));
                        corners[3] = TransformPoint(new Vector3(Frame.Width, 0));
                    }
                }
                internal class Component
                {
                    internal Transform transform = null!;
                    internal string name => transform.name;
                    internal T? GetComponent<T>() where T : Component => transform.GetComponent<T>();
                }
                internal class Behaviour : Component
                {
                    internal bool enabled = true;
                    internal bool isActiveAndEnabled => enabled && transform.gameObject.activeInHierarchy;
                }
                internal sealed class Canvas : Behaviour
                {
                    internal RenderMode renderMode => RenderMode.ScreenSpaceOverlay;
                    internal Camera? worldCamera => null;
                    internal float scaleFactor => 1f;
                }
                internal sealed class CanvasRenderer
                {
                    internal bool cull;
                    internal Color GetColor() => new(new UiThemeColor(255, 255, 255));
                }
            }

            namespace UnityEngine.UI
            {
                internal sealed class RectMask2D : Behaviour { internal Vector4 padding; internal Vector2Int softness; }
                internal sealed class ScrollRect : Behaviour { internal bool vertical; internal RectTransform? viewport, content; }
                internal class Graphic : Behaviour
                {
                    internal Color color;
                    internal readonly CanvasRenderer canvasRenderer = new();
                    internal RectTransform rectTransform => (RectTransform)transform;
                }
                internal class Image : Graphic { internal UiGlyph? sprite; }
                internal sealed class Button : Behaviour { internal Graphic? targetGraphic; }
            }

            namespace TMPro
            {
                [Flags] internal enum FontStyles { UpperCase = 1 }
                internal sealed class TMP_FontAsset
                {
                    internal TMP_FontAsset(string name) { faceInfo = new FaceInfo(name, "Regular", 1, 0); }
                    internal FaceInfo faceInfo { get; }
                }
                internal readonly record struct FaceInfo(string familyName, string styleName, float ascentLine, float descentLine);
                internal readonly record struct Character(char character, bool isVisible, Vector3 bottomLeft, Vector3 topRight);
                internal readonly record struct Line(float ascender, float descender);
                internal sealed class TextInfo
                {
                    internal int lineCount => lineInfo.Length;
                    internal int characterCount => characterInfo.Length;
                    internal Line[] lineInfo = Array.Empty<Line>();
                    internal Character[] characterInfo = Array.Empty<Character>();
                }
                internal sealed class TMP_Text : Graphic
                {
                    internal string text = "";
                    internal float fontSize;
                    internal FontStyles fontStyle;
                    internal TMP_FontAsset? font;
                    internal readonly TextInfo textInfo = new();
                    internal void ForceMeshUpdate()
                    {
                        textInfo.lineInfo = new[] { new Line(fontSize, 0f) };
                        textInfo.characterInfo = text.Select((character, index) => new Character(character, !char.IsWhiteSpace(character),
                            new Vector3(index * 6f, 0), new Vector3(index * 6f + 6f, fontSize))).ToArray();
                    }
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal float? ResolvedCornerRadius;
                    internal float ResolvedBorderWidth;
                }
                internal sealed class TopiaForgeGraphConnections
                {
                    internal bool IsOpen => false;
                }
                internal enum TopiaForgeScheme { Hud }
                internal enum TopiaForgeTextStyle { Body, Label, Mono }
                internal readonly record struct TopiaForgeEffectiveAccessibility(float UiScale);
                internal sealed record TopiaForgeResolvedTheme(UiThemeDescriptor Descriptor)
                {
                    internal TopiaForgeScheme Scheme => TopiaForgeScheme.Hud;
                    internal bool HighContrast => false;
                    internal Func<UiTextStyle, TMP_FontAsset?>? RegisteredFont => null;
                }
                internal static class TopiaForgeFonts
                {
                    private static readonly TMP_FontAsset Body = new("Fixture Body");
                    private static readonly TMP_FontAsset Mono = new("Fixture Mono");
                    internal static TMP_FontAsset ForThemeRole(string font, TopiaForgeTextStyle role) =>
                        role == TopiaForgeTextStyle.Mono ? Mono : Body;
                }
                internal static class TopiaForgeSprites { internal static UiGlyph? Glyph(UiGlyph glyph) => glyph; }
            }
            """;
    }
}
