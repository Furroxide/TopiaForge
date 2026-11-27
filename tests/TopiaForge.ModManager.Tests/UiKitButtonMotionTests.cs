using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitButtonMotionTests
    {
        internal static void Run()
        {
            RunWidget("TopiaForgePressEffect.cs", Boundary, "ButtonMotionProbe");
            Console.WriteLine("Button chrome motion passed hover, press, reduced motion, disable, and 4,096 unchanged paints (0 bytes).");
        }

        internal static void RunWidget(string file, string boundary, string type)
        {
            var source = Path.Combine(Program.FindRepoRoot(), "src/TopiaForge.Mods.UnityUi/Widgets", file);
            var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(source), path: source), CSharpSyntaxTree.ParseText(boundary) };
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var compilation = CSharpCompilation.Create(type + "Boundary", trees,
                platform.Select(file => MetadataReference.CreateFromFile(file)), new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var output = new MemoryStream();
            var emit = compilation.Emit(output);
            if (!emit.Success)
                throw new InvalidOperationException(string.Join("\n", emit.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(output.ToArray()).GetType(type, throwOnError: true)!;
            ((Action)probe.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
        }

        private const string Boundary = """
            using System;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;

            public static class ButtonMotionProbe
            {
                public static void Run()
                {
                    var host = new UiHost();
                    var target = new Button();
                    var body = new RectTransform();
                    var shadow = new Image();
                    var motion = new TopiaForgePressEffect();
                    motion.Initialize(host, target, body, shadow);
                    var pointer = new PointerEventData();
                    var offset = new Vector2(-3, -4);
                    var ink = new Color(.6f);
                    motion.SetShadow(offset, ink);
                    Check(shadow.enabled && body.anchoredPosition == default && shadow.rectTransform.offsetMin == offset &&
                        shadow.rectTransform.offsetMax == offset, "The rest shadow uses the authored offset.");
                    motion.OnPointerEnter(pointer);
                    Check(body.anchoredPosition == new Vector2(1, 2) &&
                        shadow.rectTransform.offsetMin == new Vector2(-4, -5) &&
                        shadow.rectTransform.offsetMax == new Vector2(-2, -3), "Hover lifts the body and grows the shadow.");
                    motion.OnPointerDown(pointer);
                    Check(body.anchoredPosition == offset && !shadow.enabled, "Press seats the body on the shadow.");
                    motion.OnPointerUp(pointer);
                    Check(body.anchoredPosition == new Vector2(1, 2) && shadow.enabled, "Release restores hovered paint.");
                    host.EffectiveReducedMotion = true;
                    motion.SetShadow(offset, ink);
                    motion.OnPointerDown(pointer);
                    Check(body.anchoredPosition == default && shadow.enabled && shadow.rectTransform.offsetMin == offset,
                        "Reduced motion keeps the body and its shadow static.");
                    motion.OnPointerExit(pointer);
                    host.EffectiveReducedMotion = false;
                    target.Enabled = false;
                    motion.SetShadow(offset, ink);
                    motion.OnPointerEnter(pointer);
                    Check(body.anchoredPosition == default && !shadow.enabled, "A disabled button has no lift or shadow.");
                    target.Enabled = true;
                    motion.OnPointerExit(pointer);
                    motion.SetShadow(offset, default);
                    Check(!shadow.enabled && body.anchoredPosition == default, "A contrast or authored shadow omission stays unpainted.");
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var index = 0; index < 4096; index++) motion.SetShadow(offset, default);
                    Check(GC.GetAllocatedBytesForCurrentThread() == before, "Settled button motion allocates no memory.");
                }

                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException(message);
                }
            }

            namespace UnityEngine
            {
                internal class MonoBehaviour { }
                internal readonly record struct Color(float a);
                internal class RectTransform { internal Vector2 anchoredPosition, offsetMin, offsetMax; }
                internal readonly record struct Vector2(float x, float y)
                {
                    public static Vector2 operator *(Vector2 value, float scale) => new Vector2(value.x * scale, value.y * scale);
                    public static Vector2 operator +(Vector2 left, Vector2 right) => new Vector2(left.x + right.x, left.y + right.y);
                    public static Vector2 operator -(Vector2 left, Vector2 right) => new Vector2(left.x - right.x, left.y - right.y);
                }
                internal static class Mathf { internal static float Clamp01(float value) => Math.Clamp(value, 0f, 1f); }
            }
            namespace UnityEngine.UI
            {
                internal sealed class Button { internal bool Enabled = true; internal bool IsInteractable() => Enabled; }
                internal sealed class Image { internal RectTransform rectTransform = new(); internal Color color; internal bool enabled; }
            }
            namespace UnityEngine.EventSystems
            {
                internal sealed class PointerEventData { internal InputButton button = InputButton.Left; internal enum InputButton { Left, Right } }
                internal interface IPointerEnterHandler { void OnPointerEnter(PointerEventData data); }
                internal interface IPointerExitHandler { void OnPointerExit(PointerEventData data); }
                internal interface IPointerDownHandler { void OnPointerDown(PointerEventData data); }
                internal interface IPointerUpHandler { void OnPointerUp(PointerEventData data); }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                internal sealed class UiHost { internal bool EffectiveReducedMotion; internal float EffectiveMotion = 1f; }
            }
            """;
    }
}
