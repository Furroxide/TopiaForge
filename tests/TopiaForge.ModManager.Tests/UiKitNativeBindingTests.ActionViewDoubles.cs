namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitNativeBindingTests
    {
        private const string ActionViewDoubles = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.UI;

            namespace UnityEngine
            {
                internal sealed class RectTransform { internal float X, Y, Width, Height; }
                internal sealed record Sprite(UiGlyph Glyph);
                internal readonly record struct Color32(byte R, byte G, byte B, byte A)
                {
                    public static implicit operator Color(Color32 value) =>
                        new Color(value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f);
                }
                internal struct Color
                {
                    internal Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
                    internal float r, g, b, a;
                }
                internal sealed class GameObject
                {
                    private readonly Dictionary<Type, object> components = new();
                    internal readonly RectTransform Rect = new();
                    internal T AddComponent<T>() where T : Graphic
                    {
                        var component = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        component.rectTransform = Rect;
                        components.Add(typeof(T), component);
                        return component;
                    }
                    internal void Store<T>(T value) where T : class => components[typeof(T)] = value;
                    internal T GetComponent<T>() where T : class => (T)components[typeof(T)];
                }
            }

            namespace UnityEngine.UI
            {
                internal class Graphic
                {
                    internal RectTransform rectTransform = null!;
                    internal bool raycastTarget = true;
                    internal bool enabled = true;
                    internal Color color;
                }
                internal class Image : Graphic { internal Sprite? sprite; internal bool preserveAspect; }
            }

            namespace TMPro
            {
                internal sealed class TMP_Text : Graphic
                {
                    private readonly Func<float> scale;
                    internal TMP_Text(Func<float> scale) { this.scale = scale; }
                    internal string Text = string.Empty;
                    internal int Measures;
                    internal Vector2 GetPreferredValues(string text, float limit, float height)
                    {
                        Measures++;
                        var size = 18f * Math.Max(1f, scale()) / scale();
                        var width = size * .6f * text.Length;
                        var rows = Math.Max(1f, MathF.Ceiling(width / limit));
                        return new Vector2(Math.Min(width, limit), size * rows);
                    }
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal enum TopiaForgeTextStyle { Label }
                internal enum TopiaForgeScheme { Hud, Paper }
                internal sealed class TopiaForgeResolvedTheme
                {
                    internal TopiaForgeResolvedTheme(bool highContrast = false) { HighContrast = highContrast; }
                    internal UiThemeDescriptor? Descriptor { get; set; }
                    internal TopiaForgeScheme Scheme { get; set; } = TopiaForgeScheme.Paper;
                    internal bool HighContrast { get; }
                    internal Color Shadow => new Color(0f, 0f, 0f, .2f);
                }
                internal sealed class ViewHost { internal float EffectiveUiScale = 1f; }
                internal sealed class TopiaForgeContainer
                {
                    private readonly TopiaForgeContainer? parent;
                    private TopiaForgeResolvedTheme theme = new();
                    internal TopiaForgeContainer(TopiaForgeContainer? parent = null)
                    { this.parent = parent; Host = parent?.Host ?? new ViewHost(); }
                    internal readonly ViewHost Host;
                    internal readonly GameObject Go = new();
                    internal readonly Dictionary<string, TopiaForgeContainer> Children = new();
                    internal TopiaForgeLabel? LabelView;
                    internal TopiaForgeResolvedTheme ResolvedTheme { get => parent?.ResolvedTheme ?? theme; set => theme = value; }
                    internal RectTransform Rect => Go.Rect;
                    internal TopiaForgeContainer CreateChild(string name)
                    {
                        var child = new TopiaForgeContainer(this);
                        Children.Add(name, child);
                        return child;
                    }
                    internal TopiaForgeLabel Label(string text, TopiaForgeTextStyle style)
                    {
                        LabelView = new TopiaForgeLabel(this, style, text);
                        return LabelView;
                    }
                }
                internal sealed class TopiaForgeLabel
                {
                    internal TopiaForgeLabel(TopiaForgeContainer container, TopiaForgeTextStyle style, string value)
                    {
                        Style = style;
                        Text = new TMPro.TMP_Text(() => container.Host.EffectiveUiScale) { Text = value };
                        Go.Store(Text);
                    }
                    internal readonly GameObject Go = new();
                    internal readonly TopiaForgeTextStyle Style;
                    internal readonly TMPro.TMP_Text Text;
                    internal bool IsPlain;
                    internal RectTransform Rect => Go.Rect;
                    internal TopiaForgeLabel PlainText() { IsPlain = true; return this; }
                    internal TopiaForgeLabel AlignTopLeft() => this;
                    internal void SetText(string value) => Text.Text = value;
                    internal void SetResolvedInk(Color color) => Text.color = color;
                }
                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal float Stroke;
                    internal void SetShape(float? radius, float stroke = 0f) => Stroke = stroke;
                }
                internal static class TopiaForgeSprites { internal static Sprite Glyph(UiGlyph glyph) => new Sprite(glyph); }
                internal static class TopiaForgeAnchors
                {
                    internal static void Place(RectTransform rect, float x, float y, float width, float height)
                    { rect.X = x; rect.Y = y; rect.Width = width; rect.Height = height; }
                }
            }

            namespace TopiaForge.ModManager
            {
                internal interface IUiInputDefaultsSource
                {
                    bool TryGetUiInputDefaults(string actionName, out IReadOnlyList<InputBinding> defaults);
                }
                internal sealed partial class OwnerUiService
                {
                    private readonly OwnerHost host = new();
                    private sealed class OwnerHost { internal string OwnerId => "test.owner"; }
                }
                internal sealed class UnityInputRegistry
                {
                    private readonly Dictionary<string, LocalAction> actions = new(StringComparer.Ordinal);
                    internal LocalAction Register(string owner, string name, params InputBinding[] bindings)
                    {
                        var action = new LocalAction(name, bindings);
                        actions.Add(owner + ":" + name, action);
                        return action;
                    }
                    internal void Remove(string owner, string name) => actions.Remove(owner + ":" + name);
                    internal LocalAction? FindUiAction(string owner, string name) => FindUiActionByKey(owner + ":" + name, name);
                    internal LocalAction? FindUiActionByKey(string key, string name) =>
                        actions.TryGetValue(key, out var action) && action.Name == name ? action : null;
                    internal string? GetUiBindingConflict(IInputAction action, InputBinding binding) => null;
                }
                internal sealed class LocalAction : IInputAction
                {
                    internal LocalAction(string name, InputBinding[] bindings)
                    {
                        Definition = new InputActionDefinition(name, name, bindings);
                        Bindings = bindings;
                    }
                    internal InputActionDefinition Definition { get; }
                    public string Name => Definition.Name;
                    public IReadOnlyList<InputBinding> Bindings { get; private set; }
                    public float Value => 0f;
                    public bool IsHeld => false;
                    public bool WasPressed => false;
                    public bool WasReleased => false;
                    internal void Snapshot(params InputBinding[] bindings) => Bindings = bindings;
                    public OperationResult<bool> Rebind(IEnumerable<InputBinding> bindings)
                    { Snapshot(bindings.ToArray()); return OperationResult<bool>.Success(true); }
                    public OperationResult<bool> ResetBindings() => Rebind(Definition.DefaultBindings);
                    public void Dispose() { }
                }
            }
            """;
    }
}
