using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void NativeNonButtonCuesUseActualTargets()
        {
            var root = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi", "Widgets");
            var sources = new[] { File.ReadAllText(Path.Combine(root, "TopiaForgeControlCues.cs")), NonButtonCueContract };
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(reference => MetadataReference.CreateFromFile(reference));
            var probe = CSharpCompilation.Create("KitControlCueProbe", sources.Select(source => CSharpSyntaxTree.ParseText(source)),
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var image = new MemoryStream();
            var result = probe.Emit(image);
            Check(result.Success, "The actual compact and full-size cue driver compiles against the Unity contract: "
                + string.Join(", ", result.Diagnostics));
            var scenarios = Assembly.Load(image.ToArray()).GetType("NonButtonCueScenarios", throwOnError: true)!;
            ((Action)scenarios.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
        }

        private const string NonButtonCueContract = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Linq;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;

            namespace TopiaForge.Mods
            {
                [Flags]
                internal enum UiButtonCue { None = 0, Selection = 1, Press = 2 }
                internal readonly record struct UiThemeColor(byte R, byte G, byte B);
                internal readonly record struct UiThemeStateTints(float Hover, float Selected, float Pressed);

                internal static class UiControlPaint
                {
                    internal const float CompactCueRadius = 3f;
                    internal const float CompactCueLeading = 4.5f;
                    internal const float CompactCueGutter = 12f;
                    internal const float CompactCueSideInset = 1f;
                    internal const float ButtonSelectionWidth = 3f;
                    internal const float ButtonSelectionTop = 12f;
                    internal const float ButtonSelectionLeft = 8f;
                    internal const float ButtonPressInset = 3f;
                    internal static float ButtonPressStroke(bool high) => high ? 3f : 2f;
                    internal static UiButtonCue ButtonCues(UiThemeColor fill, UiThemeStateTints tints) =>
                        fill.R == 0 ? UiButtonCue.Selection | UiButtonCue.Press : UiButtonCue.None;
                }
            }

            namespace UnityEngine
            {
                public readonly record struct Vector2(float X, float Y)
                {
                    public static Vector2 zero => default;
                    public static Vector2 one => new Vector2(1f, 1f);
                }
                public readonly record struct Color(float r, float g, float b, float a);
                public readonly record struct Color32(byte r, byte g, byte b)
                {
                    public static implicit operator Color32(Color value) => new Color32(
                        (byte)Math.Round(value.r * 255f), (byte)Math.Round(value.g * 255f), (byte)Math.Round(value.b * 255f));
                }
                public static class Mathf { public static float Max(float a, float b) => Math.Max(a, b); }
                public record Coroutine(IEnumerator Body);
                public record WaitForSecondsRealtime(float Seconds);
                public class MonoBehaviour : Component
                {
                    private Coroutine? active;
                    public Coroutine StartCoroutine(IEnumerator body)
                    {
                        body.MoveNext();
                        return active = new Coroutine(body);
                    }
                    public void StopCoroutine(Coroutine work) { if (active == work) active = null; }
                    public void FinishSubmit() => active?.Body.MoveNext();
                }
                public class Component { public GameObject gameObject { get; set; } = null!; }
                public sealed class RectTransform
                {
                    public RectTransform(GameObject owner) { Owner = owner; }
                    public GameObject Owner { get; }
                    public List<GameObject> Children { get; } = new();
                    public Vector2 anchorMin, anchorMax, offsetMin, offsetMax;
                    public void SetParent(RectTransform parent, bool worldPositionStays) => parent.Children.Add(Owner);
                }
                public sealed class GameObject
                {
                    private readonly List<Component> components = new();
                    public readonly string Name;
                    public readonly RectTransform transform;
                    public bool Active { get; private set; } = true;
                    public int VisibilityCalls { get; private set; }
                    public GameObject(string name, params Type[] types) { Name = name; transform = new RectTransform(this); }
                    public void SetActive(bool value) { VisibilityCalls++; Active = value; }
                    public T AddComponent<T>() where T : Component
                    {
                        var value = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        value.gameObject = this;
                        components.Add(value);
                        return value;
                    }
                    public T GetComponent<T>() where T : Component => components.OfType<T>().Single();
                }
            }

            namespace UnityEngine.EventSystems
            {
                public class BaseEventData { }
                public sealed class PointerEventData : BaseEventData
                {
                    public enum InputButton { Left, Right }
                    public InputButton button;
                }
                public interface ISelectHandler { void OnSelect(BaseEventData data); }
                public interface IDeselectHandler { void OnDeselect(BaseEventData data); }
                public interface IPointerDownHandler { void OnPointerDown(PointerEventData data); }
                public interface IPointerUpHandler { void OnPointerUp(PointerEventData data); }
                public interface ISubmitHandler { void OnSubmit(BaseEventData data); }
            }

            namespace UnityEngine.UI
            {
                public class Selectable : MonoBehaviour
                {
                    public bool Interactable = true;
                    public ColorBlock colors;
                    public bool IsInteractable() => Interactable;
                }

                public struct ColorBlock
                {
                    public Color highlightedColor, selectedColor, pressedColor;
                }

                public class Graphic : Component
                {
                    public bool raycastTarget;
                    public Color color;
                    public RectTransform rectTransform => gameObject.transform;
                }
                public sealed class Image : Graphic { }
                public sealed class LayoutElement : Component { public bool ignoreLayout; }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal sealed class TopiaForgeRoundedImage : Graphic
                {
                    internal float Radius, Stroke;
                    internal void SetShape(float radius, float stroke) { Radius = radius; Stroke = stroke; }
                }
                internal static class TopiaForgeAnchors
                {
                    internal static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
                    {
                        rect.anchorMax = Vector2.one;
                        rect.offsetMin = new Vector2(left, bottom);
                        rect.offsetMax = new Vector2(-right, -top);
                    }
                }

                internal static class TopiaForgeCueButton
                {
                    internal const string SelectionName = "Selection bar";
                    internal const string PressName = "Press outline";
                }
            }

            public static class NonButtonCueScenarios
            {
                public static void Run()
                {
                    foreach (var compact in new[] { false, true })
                    {
                        var target = new GameObject("Selection target");
                        var selectable = target.AddComponent<Selectable>();
                        var native = TopiaForgeControlCues.Install(selectable, target.transform, compact);
                        var black = new Color(0f, 0f, 0f, 1f);
                        var white = new Color(1f, 1f, 1f, 1f);
                        native.Apply(black, white, 12f, highContrast: true);
                        Require(target.transform.Children.Count == 0, "Rest allocates no native cue objects.");
                        var input = new BaseEventData();
                        var pointer = new PointerEventData { button = PointerEventData.InputButton.Left };
                        native.OnSelect(input);
                        native.OnPointerDown(pointer);
                        Require(native.ActiveCues == (UiButtonCue.Selection | UiButtonCue.Press),
                            "Both independent cues appear while a selected control is held.");
                        var selection = target.transform.Children.Single(child => child.Name == "Selection bar");
                        var press = target.transform.Children.Single(child => child.Name == "Press outline");
                        var outline = press.GetComponent<TopiaForgeRoundedImage>();
                        var barRect = selection.transform;
                        Require(selection.Active && press.Active, "The active cues have visible native artwork.");
                        Require(selection.GetComponent<LayoutElement>().ignoreLayout && press.GetComponent<LayoutElement>().ignoreLayout &&
                            !selection.GetComponent<Image>().raycastTarget && !outline.raycastTarget,
                            "The mark objects are layout-inert and cannot capture input.");
                        Require(barRect.offsetMax.X - barRect.offsetMin.X == 3f && outline.Stroke == 3f &&
                            outline.color == white && (compact ? press.transform.offsetMax.X == 11f : press.transform.offsetMax.X == -3f),
                            "Measured stroke, fill and placement apply inside the actual target.");
                        native.OnDeselect(input);
                        Require(!selection.Active && press.Active, "Deselect keeps an ongoing pointer press.");
                        native.OnPointerUp(pointer);
                        Require(!selection.Active && !press.Active, "Release hides only the press.");
                        native.OnSubmit(input);
                        Require(press.Active, "Keyboard or controller submit exposes otherwise collapsed press paint.");
                        native.FinishSubmit();
                        Require(!press.Active, "The submit mark clears after its reading interval.");
                        native.Apply(new Color(0f, 0f, 0f, 1f), white, 10f, highContrast: true, authoredSelectionCue: true);
                        native.OnDeselect(input);
                        Require(selection.Active && !press.Active, "A retained authored cue stays visible after focus moves.");
                        native.OnPointerDown(pointer);
                        Require(selection.Active && press.Active, "An authored selection keeps its press outline independent.");
                        native.OnPointerUp(pointer);
                        native.Apply(new Color(0f, 0f, 0f, 1f), white, 10f, highContrast: true);
                        Require(!selection.Active && !press.Active, "Rebinding the retained control clears its old selection mark.");
                        selectable.Interactable = false;
                        native.OnSelect(input);
                        native.OnPointerDown(pointer);
                        Require(!selection.Active && !press.Active, "A disabled control never displays input marks.");
                        var visibility = selection.VisibilityCalls + press.VisibilityCalls;
                        for (var i = 0; i < 512; i++) native.OnPointerUp(pointer);
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        for (var i = 0; i < 4096; i++) native.OnPointerUp(pointer);
                        Require(GC.GetAllocatedBytesForCurrentThread() == before &&
                            selection.VisibilityCalls + press.VisibilityCalls == visibility, "Unchanged state is allocation- and repaint-free.");
                    }
                    Console.WriteLine("Native control cue geometry, submit and 0 B over 4,096 settled updates passed.");
                }

                private static void Require(bool result, string reason)
                {
                    if (!result) throw new InvalidOperationException(reason);
                }
            }
            """;
    }
}
