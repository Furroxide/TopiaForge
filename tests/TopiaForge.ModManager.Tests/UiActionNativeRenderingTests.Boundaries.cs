namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiActionNativeRenderingTests
    {
        private const string ActionBoundary = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Reflection;
            using TMPro;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    private const bool GalleryCaptureEnabled = false;
                    private sealed class UiCallbackGate
                    {
                        internal Func<double> Clock = () => 0d;
                        internal Func<string> ClockEpoch = () => "first";
                    }
                    private sealed partial class UnityUiLayoutRenderer
                    {
                        private UiSurfaceInputContext? input;
                        private UiSurfaceArtwork? artwork;
                        private sealed class NativeNode
                        {
                            internal NativeNode(TopiaForgeContainer container) { Container = container; }
                            internal TopiaForgeContainer Container { get; }
                            internal NativeKeyGlyph? KeyGlyph { get; set; }
                            internal NativeActionSlot? ActionSlot { get; set; }
                        }

                        private sealed class NodeIdentity : IEqualityComparer<UiNode>
                        {
                            internal static readonly NodeIdentity Instance = new();
                            public bool Equals(UiNode? a, UiNode? b) => ReferenceEquals(a, b);
                            public int GetHashCode(UiNode node) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node);
                        }

                        /* layout paint */
                    }

                    private sealed partial class UnityUiSurface
                    {
                        private bool remoteComposition;
                        private TopiaForge.ModManager.Core.AdmittedUiCatalogue? remoteCatalogue;
                        private UiThemeAcquisitions? remoteAcquisitions;
                        private UiThemeScope? localThemeScope;
                        private UiSurfaceArtwork? artwork;
                        private readonly (bool IsLive, int _) presence = (true, 0);
                        private int artworkVersion;
                        private readonly Log logger = new();
                        private sealed class Log { internal void Error(Exception error, string message) => throw new InvalidOperationException(message, error); }
                    }
                }

                internal sealed class UiSurfaceInputContext
                {
                    private readonly IInputAction? action;
                    internal UiSurfaceInputContext(IInputAction? action, string owner) { this.action = action; }
                    internal UiNativeActionBinding BindAction(string name, IReadOnlyList<UiInputBindingHint> fallback) =>
                        new UiNativeActionBinding(() => action?.Name == name ? action : null, fallback);
                }
                internal sealed class UiThemeScope
                {
                    internal void TryDescribe(string pack, out UiThemeDescriptor? descriptor) => throw new NotSupportedException();
                    internal UiThemeDemand? DemandSurface(UiThemeDescriptor pack, Action<OperationResult<bool>> settled, bool retryFailed) =>
                        throw new NotSupportedException();
                }
                internal sealed class UiThemeAcquisitions
                {
                    internal Sprite? Sprite { get; set; }
                    internal int Demands { get; private set; }
                    internal UiThemeDemand Demand(UiThemeDescriptor pack, Action<OperationResult<bool>> settled, bool retryFailed)
                    {
                        Demands++;
                        return new UiThemeDemand(this);
                    }
                }
                internal sealed class UiThemeDemand : IDisposable
                {
                    private readonly UiThemeAcquisitions source;
                    internal UiThemeDemand(UiThemeAcquisitions source) { this.source = source; }
                    internal UiThemeDemand? Registration => source.Sprite == null ? null : this;
                    internal OperationResult<bool>? Outcome => null;
                    internal T? Image<T>(string asset) where T : class => source.Sprite as T;
                    public void Dispose() { }
                }
            }

            namespace UnityEngine
            {
                internal readonly record struct Vector2(float x, float y);
                internal record struct Color(float r, float g, float b, float a)
                {
                    internal static Color white => new(1f, 1f, 1f, 1f);
                }
                internal readonly record struct Color32(byte R, byte G, byte B, byte A)
                {
                    public static implicit operator Color(Color32 value) =>
                        new Color(value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f);
                }
                internal sealed record Sprite(string Id);
                internal sealed class RectTransform
                {
                    internal UiLayoutRect Bounds;
                    internal int Writes;
                    internal double X => Bounds.X;
                    internal double Y => Bounds.Y;
                    internal double Width => Bounds.Width;
                    internal double Height => Bounds.Height;
                    internal double Right => Bounds.Right;
                    internal double Bottom => Bounds.Bottom;
                }
                internal class Component { internal GameObject gameObject = null!; }
                internal class MonoBehaviour : Component
                {
                    private Action? lateUpdate;
                    internal void BindFrame() =>
                        lateUpdate = GetType().GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic)?.CreateDelegate<Action>(this);
                    internal void TickTest() => lateUpdate?.Invoke();
                }
                internal sealed class GameObject
                {
                    private readonly List<Component> components = new();
                    private readonly List<GameObject> children = new();
                    internal string name;
                    internal bool Active = true;
                    internal RectTransform Rect { get; } = new();
                    internal GameObject(string name) { this.name = name; }
                    internal GameObject Child(string name)
                    {
                        var child = new GameObject(name);
                        children.Add(child);
                        return child;
                    }
                    internal IEnumerable<GameObject> Parts(string name) => children.Where(child => child.name == name);
                    internal GameObject Part(string name, int index = 0) => Parts(name).ElementAt(index);
                    internal IEnumerable<Graphic> AllGraphics() => components.OfType<Graphic>().Concat(children.SelectMany(child => child.AllGraphics()));
                    internal T AddComponent<T>() where T : Component
                    {
                        var component = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        component.gameObject = this;
                        components.Add(component);
                        if (component is MonoBehaviour frame) frame.BindFrame();
                        return component;
                    }
                    internal T GetComponent<T>() where T : Component
                    {
                        for (var index = 0; index < components.Count; index++)
                            if (components[index] is T result) return result;
                        return null!;
                    }
                }
            }

            namespace UnityEngine.UI
            {
                internal class Graphic : Component
                {
                    internal RectTransform rectTransform => gameObject.Rect;
                    internal bool enabled = true;
                    internal bool raycastTarget = true;
                    internal Color color;
                }
                internal class Image : Graphic { internal Sprite? sprite; internal bool preserveAspect; }
                internal sealed class RectMask2D : Component { }
            }

            namespace TMPro
            {
                internal sealed class TMP_Text : Graphic
                {
                    private readonly char[] buffer = new char[256];
                    private int length;
                    private bool formatted;
                    private string raw = "";
                    internal int Writes { get; private set; }
                    internal Func<float> FontHeight = () => 18f;
                    internal string Text => formatted ? new string(buffer, 0, length) : raw;
                    internal string text
                    {
                        get => Text;
                        set { raw = value; formatted = false; Writes++; }
                    }
                    internal void SetCharArray(char[] source, int start, int count)
                    {
                        source.AsSpan(start, count).CopyTo(buffer);
                        length = count;
                        formatted = true;
                        Writes++;
                    }
                    internal Vector2 GetPreferredValues(string value, float width, float maxHeight = 0f)
                    {
                        if (value.Length == 0) return default;
                        var height = FontHeight();
                        var widest = 0f;
                        var line = 0f;
                        var lines = 1;
                        foreach (var character in value)
                        {
                            var advance = height * (char.IsWhiteSpace(character) ? .3f : .5f);
                            if (character == '\n' || line > 0f && line + advance > width)
                            {
                                widest = Math.Max(widest, line);
                                lines++;
                                line = 0f;
                                if (character == '\n') continue;
                            }
                            line += advance;
                        }
                        return new Vector2(Math.Max(widest, line), lines * height);
                    }
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal enum TopiaForgeScheme { Hud }
                internal enum TopiaForgeTextStyle { Body, Label, Caption }
                internal sealed class TopiaForgeResolvedTheme
                {
                    internal bool HighContrast { get; set; }
                    internal UiThemeDescriptor? Descriptor { get; set; }
                    internal TopiaForgeScheme Scheme => TopiaForgeScheme.Hud;
                    internal Color Shadow => new Color(0f, 0f, 0f, .25f);
                }
                internal sealed class FixtureHost
                {
                    internal bool EffectiveReducedMotion { get; set; }
                    internal float EffectiveUiScale { get; set; } = 1f;
                }
                internal sealed class TopiaForgeContainer
                {
                    internal TopiaForgeContainer(TopiaForgeResolvedTheme theme, float scale, bool reduced) :
                        this(new GameObject("Surface"), new FixtureHost { EffectiveUiScale = scale, EffectiveReducedMotion = reduced }, theme) { }
                    private TopiaForgeContainer(GameObject go, FixtureHost host, TopiaForgeResolvedTheme theme)
                    { Go = go; Host = host; ResolvedTheme = theme; }
                    internal GameObject Go { get; }
                    internal FixtureHost Host { get; }
                    internal TopiaForgeResolvedTheme ResolvedTheme { get; }
                    internal RectTransform Rect => Go.Rect;
                    internal TopiaForgeContainer CreateChild(string name) => new TopiaForgeContainer(Go.Child(name), Host, ResolvedTheme);
                    internal GameObject CreateChildGameObject(string name) => Go.Child(name);
                    internal void SetShown(bool shown) => Go.Active = shown;
                    internal TopiaForgeLabel Label(string value, TopiaForgeTextStyle style)
                    {
                        var descriptor = ResolvedTheme.Descriptor ?? UiThemePackParser.Default;
                        var role = descriptor.GetText(style == TopiaForgeTextStyle.Label ? UiTextStyle.Label :
                            style == TopiaForgeTextStyle.Caption ? UiTextStyle.Caption : UiTextStyle.Body);
                        return new TopiaForgeLabel(Go.Child("Label"), value,
                            () => role.ReferenceSize * Math.Max(1f, Host.EffectiveUiScale) / Host.EffectiveUiScale);
                    }
                }
                internal sealed class TopiaForgeLabel
                {
                    private readonly TMP_Text label;
                    private string value;
                    internal TopiaForgeLabel(GameObject go, string value, Func<float> height)
                    {
                        Go = go;
                        this.value = value;
                        label = go.AddComponent<TMP_Text>();
                        label.raycastTarget = false;
                        label.text = value;
                        label.FontHeight = height;
                    }
                    internal GameObject Go { get; }
                    internal RectTransform Rect => Go.Rect;
                    internal TopiaForgeLabel PlainText() => this;
                    internal TopiaForgeLabel AlignTopLeft() => this;
                    internal void SetText(string next)
                    {
                        if (value == next) return;
                        value = next;
                        label.text = next;
                    }
                    internal void SetResolvedInk(Color color) => label.color = color;
                    internal void SetShown(bool visible) => Go.Active = visible;
                }
                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal float? Radius { get; private set; }
                    internal float Stroke { get; private set; }
                    internal void SetShape(float? radius, float stroke = 0f) { Radius = radius; Stroke = stroke; }
                }
                internal static class TopiaForgeSprites
                {
                    private static readonly Dictionary<UiGlyph, Sprite> Glyphs = Enum.GetValues<UiGlyph>()
                        .ToDictionary(glyph => glyph, glyph => new Sprite(glyph.ToString()));
                    internal static Sprite Glyph(UiGlyph glyph) => Glyphs[glyph];
                }
                internal static class TopiaForgeInputOwnership
                {
                    internal static UiInputDevice LastViewerDevice => UiInputDevice.KeyboardMouse;
                }
                internal static class TopiaForgeAnchors
                {
                    internal static void Place(RectTransform rect, float x, float y, float width, float height)
                    {
                        rect.Bounds = new UiLayoutRect(x, y, width, height);
                        rect.Writes++;
                    }
                    internal static void Stretch(RectTransform rect) { }
                }
            }
            """;
    }
}
