namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiStatusNativeRenderingTests
    {
        private const string NativeBoundary = """
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
                    private sealed class UiCallbackGate
                    {
                        internal Func<double> Clock = () => 0;
                        internal Func<string> ClockEpoch = () => "offline";
                    }

                    private sealed partial class UnityUiLayoutRenderer
                    {
                        private static T AddGraphic<T>(TopiaForgeContainer parent, string name) where T : Graphic =>
                            parent.CreateChildGameObject(name).AddComponent<T>();
                        private static void Place(RectTransform native, UiLayoutRect layout) => native.Bounds = layout;
                        private static Color ColorOf(UiThemeColor color) => new Color(color);
                    }
                }
            }

            namespace UnityEngine
            {
                internal record struct Color(UiThemeColor Underlying, float a = 1f);
                internal record struct Vector2(float x, float y);
                internal record struct Vector3(float x, float y, float z = 0f);
                internal record struct Rect(float xMin, float xMax);
                internal static class Mathf { internal static float Max(float one, float two) => Math.Max(one, two); }
                internal sealed class RectTransform
                {
                    internal UiLayoutRect Bounds;
                    internal Rect rect => new Rect(0f, (float)Bounds.Width);
                }
                internal sealed class GameObject
                {
                    private readonly List<Component> components = new();
                    private readonly List<GameObject> children = new();
                    private readonly string name;
                    internal GameObject(string name) { this.name = name; }
                    internal RectTransform Rect { get; } = new RectTransform();
                    internal bool Active { get; set; } = true;

                    internal GameObject Child(string name)
                    {
                        var child = new GameObject(name);
                        children.Add(child);
                        return child;
                    }

                    internal GameObject Part(string name) => children.Single(item => item.name == name);
                    internal T? GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault();
                    internal T AddComponent<T>() where T : Component
                    {
                        var item = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        item.gameObject = this;
                        components.Add(item);
                        if (item is MonoBehaviour script) script.FindUpdate();
                        return item;
                    }
                }
                internal class Component
                {
                    internal GameObject gameObject = null!;
                    internal RectTransform rectTransform => gameObject.Rect;
                }
                internal class MonoBehaviour : Component
                {
                    private Action? update;
                    internal void FindUpdate()
                    {
                        update = GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)?.CreateDelegate<Action>(this);
                    }
                    internal void TickTest() => update?.Invoke();
                }
            }

            namespace UnityEngine.UI
            {
                internal sealed class RectMask2D : Component { }
                internal class Graphic : Component { internal bool enabled = true; internal Color color; }
                internal class Image : Graphic { internal UiGlyph? sprite; }
            }

            namespace TMPro
            {
                internal enum TextWrappingModes { Normal, NoWrap }
                internal enum TextAlignmentOptions { Left, Center }
                internal sealed class TMP_FontAsset { }
                internal struct TMP_CharacterInfo
                {
                    internal bool isVisible;
                    internal int vertexIndex, materialReferenceIndex;
                    internal Vector3 bottomLeft, bottomRight, topLeft, topRight;
                    internal float origin, xAdvance;
                }
                internal struct TMP_MeshInfo { internal Vector3[] vertices; }
                internal sealed class TMP_TextInfo
                {
                    internal int characterCount;
                    internal readonly TMP_CharacterInfo[] characterInfo = new TMP_CharacterInfo[UiCountdownFormatting.MaximumLength];
                    internal readonly TMP_MeshInfo[] meshInfo = { new TMP_MeshInfo { vertices = new Vector3[4 * UiCountdownFormatting.MaximumLength] } };
                }
                internal sealed class TMP_Text : Graphic
                {
                    private readonly char[] digits = new char[UiCountdownFormatting.MaximumLength];
                    private int length;
                    internal string text = "";
                    internal bool richText, parseCtrlCharacters;
                    internal TextWrappingModes textWrappingMode;
                    internal TextAlignmentOptions alignment;
                    internal TMP_FontAsset? font;
                    internal float fontSize = 20, characterSpacing;
                    internal TopiaForgeTextStyle Role;
                    internal Action<TMP_TextInfo>? OnPreRenderText;
                    internal TMP_TextInfo Info { get; } = new TMP_TextInfo();
                    internal int Changes { get; private set; }
                    internal string Current => new string(digits, 0, length);

                    internal Vector2 GetPreferredValues(string value, float width = float.PositiveInfinity, float height = 0)
                    {
                        var advance = 0f;
                        foreach (var character in value) advance += Advance(character);
                        var lines = textWrappingMode == TextWrappingModes.NoWrap || width <= 0 ? 1 : Math.Max(1, MathF.Ceiling(advance / width));
                        return new Vector2(MathF.Min(width, advance), lines * fontSize);
                    }

                    internal void SetCharArray(char[] buffer, int start, int count)
                    {
                        buffer.AsSpan(start, count).CopyTo(digits);
                        length = count;
                        Changes++;
                    }

                    private static float Advance(char c) => c is '1' or ':' ? 4f : 8f;
                    internal void ForceMeshUpdate(bool ignoreActiveState = false) => RenderTest();
                    internal void RenderTest()
                    {
                        Info.characterCount = length;
                        var cursor = 0f;
                        for (var index = 0; index < length; index++)
                        {
                            var advance = Advance(digits[index]);
                            var indexIntoMesh = index * 4;
                            var glyph = new TMP_CharacterInfo { isVisible = true, vertexIndex = indexIntoMesh,
                                origin = cursor, xAdvance = cursor + advance, bottomLeft = new Vector3(cursor, 0),
                                topLeft = new Vector3(cursor, 16), topRight = new Vector3(cursor + advance, 16),
                                bottomRight = new Vector3(cursor + advance, 0) };
                            Info.characterInfo[index] = glyph;
                            var vertices = Info.meshInfo[0].vertices;
                            vertices[indexIntoMesh] = glyph.bottomLeft;
                            vertices[indexIntoMesh + 1] = glyph.topLeft;
                            vertices[indexIntoMesh + 2] = glyph.topRight;
                            vertices[indexIntoMesh + 3] = glyph.bottomRight;
                            cursor += advance;
                        }
                        OnPreRenderText?.Invoke(Info);
                    }
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal sealed class TopiaForgeResolvedTheme
                {
                    internal bool HighContrast { get; }
                    internal Color Shadow => new Color(default, .5f);
                    internal TopiaForgeResolvedTheme(bool high) { HighContrast = high; }
                }
                internal sealed class StatusHost
                {
                    internal float EffectiveUiScale { get; }
                    internal StatusHost(float scale) { EffectiveUiScale = scale; }
                }
                internal sealed class TopiaForgeContainer
                {
                    internal StatusHost Host { get; }
                    internal TopiaForgeResolvedTheme ResolvedTheme { get; }
                    internal GameObject Go { get; }
                    internal RectTransform Rect => Go.Rect;
                    internal TopiaForgeContainer(TopiaForgeResolvedTheme theme, float scale) : this(new StatusHost(scale), theme, new GameObject("Status")) { }
                    private TopiaForgeContainer(StatusHost host, TopiaForgeResolvedTheme theme, GameObject go) { Host = host; ResolvedTheme = theme; Go = go; }
                    internal TopiaForgeContainer CreateChild(string name) => new TopiaForgeContainer(Host, ResolvedTheme, Go.Child(name));
                    internal GameObject CreateChildGameObject(string name) => Go.Child(name);
                    internal void SetShown(bool shown) => Go.Active = shown;
                    internal TopiaForgeLabel Label(string value, TopiaForgeTextStyle role) => new TopiaForgeLabel(Go.Child("Label"), role, value);
                }
                internal enum TopiaForgeTextStyle { Body, Label, Mono }
                internal sealed class TopiaForgeLabel
                {
                    private readonly TMP_Text label;
                    internal GameObject Go { get; }
                    internal TopiaForgeLabel(GameObject go, TopiaForgeTextStyle role, string text)
                    {
                        Go = go;
                        label = go.AddComponent<TMP_Text>();
                        label.Role = role;
                        label.text = text;
                    }
                    internal TopiaForgeLabel PlainText() { label.richText = false; return this; }
                    internal TopiaForgeLabel NoWrap() { label.textWrappingMode = TextWrappingModes.NoWrap; return this; }
                    internal TopiaForgeLabel AlignCenter() { label.alignment = TextAlignmentOptions.Center; return this; }
                }
                internal static class TopiaForgeTmp { internal static TMP_Text Create(GameObject go) => go.AddComponent<TMP_Text>(); }
                internal static class TopiaForgeSprites { internal static UiGlyph Glyph(UiGlyph glyph) => glyph; }
                internal static class TopiaForgeFonts
                {
                    private static readonly TMP_FontAsset Mono = new TMP_FontAsset();
                    internal static TMP_FontAsset? For(TopiaForgeTextStyle role) => role == TopiaForgeTextStyle.Mono ? Mono : null;
                }
                internal static class TopiaForgeTypography
                {
                    internal static void ApplyControlText(TMP_Text text, TopiaForgeResolvedTheme theme, TopiaForgeTextStyle role, float uiScale)
                    { text.Role = role; text.fontSize = 18f * Math.Max(1f, uiScale) / uiScale; }
                }
                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal float? Radius { get; private set; }
                    internal void SetShape(float? radius, float border = 0) => Radius = radius;
                }
            }
            """;
    }
}
