using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Runs the production input sampler against control doubles, without starting Unity.</summary>
    internal static partial class GamepadBindingTests
    {
        public static void Run()
        {
            var root = Program.FindRepoRoot();
            var sources = new[]
            {
                "src/TopiaForge.ModManager/UnityInputService.cs",
                "src/TopiaForge.ModManager/UnityGamepadAxes.cs",
                "src/TopiaForge.ModManager/InputActionYield.cs",
                "src/TopiaForge.Mods.UnityUi/Core/TopiaForgeInputGate.cs",
                "src/TopiaForge.Mods.UnityUi/Core/TopiaForgeInputClaim.cs",
                "src/TopiaForge.Mods.UnityUi/Core/TopiaForgeNavigationBindings.cs",
                "src/TopiaForge.Mods.UnityUi/Runtime/TopiaForgeInputOwnership.Gamepad.cs",
            }.Select(path => File.ReadAllText(Path.Combine(root, path))).Concat(new[] { Contract, Scenarios });
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(InputBinding).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("GamepadBindingProbe", sources.Select(source => CSharpSyntaxTree.ParseText(source)),
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            if (!result.Success)
            {
                throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            }

            var probe = Assembly.Load(bytes.ToArray()).GetType("GamepadScenarios", throwOnError: true)!;
            var count = 0;
            foreach (var name in new[] { "Axes", "Keyboard", "ScaleAndRebind", "MissingAndChangingPad", "YieldAndRelease", "WindowBindings", "TakenSticks", "IsolationAndReset", "NoAllocation" })
            {
                ((Action)probe.GetMethod(name)!.CreateDelegate(typeof(Action)))();
                count++;
            }

            Console.WriteLine("GamepadBindingTests passed: " + count + " scenarios.");
        }

        // These doubles expose only the contracts used by the sampler. The production build checks the real Unity API.
        private const string Contract = """
            using System;
            namespace UnityEngine
            {
                public class GameObject { }
                public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
                public enum KeyCode { K, Escape, JoystickButton0, JoystickButton1 }
                public static class Mathf { public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max); }
                public static class Input
                {
                    public static float Movement;
                    public static bool Key;
                    public static bool GetKey(KeyCode key) => Key;
                    public static bool GetMouseButton(int button) => false;
                    public static float GetAxisRaw(string name) => name == "Horizontal" || name == "Vertical" ? Movement : throw new InvalidOperationException("Undefined legacy axis");
                    public static Vector2 mouseScrollDelta;
                }
            }
            namespace UnityEngine.EventSystems
            {
                public class EventSystem
                {
                    public static EventSystem? current;
                    public UnityEngine.GameObject? currentSelectedGameObject;
                    public bool IsPointerOverGameObject() => false;
                }
            }
            namespace UnityEngine.InputSystem
            {
                public sealed class Stick
                {
                    public UnityEngine.Vector2 Value;
                    public UnityEngine.Vector2 ReadValue() => Value;
                }
                public sealed class Trigger { public float Value; public float ReadValue() => Value; }
                public sealed class Gamepad
                {
                    public static Gamepad? current;
                    public int deviceId;
                    public readonly Stick leftStick = new(), rightStick = new();
                    public readonly Trigger leftTrigger = new(), rightTrigger = new();
                }
            }
            namespace TopiaForge.ModManager { internal static class UnityMainThreadGuard { public static void AssertCurrent() { } } }
            namespace TopiaForge.Mods.UnityUi
            {
                internal static class TopiaForgeDismissStack { public static TopiaForgeInputClaim Claims; public static bool HoldsFocus; }
                internal static class TopiaForgeNavigateAction { public static readonly TopiaForgeNavigationBindings Bindings = new(); }
                internal sealed class TopiaForgeGameTextFocus
                {
                    public static readonly TopiaForgeGameTextFocus Current = new();
                    public bool Active;
                    public bool HasKeyboard(object? selected) => Active;
                }
                internal static partial class TopiaForgeInputOwnership
                {
                    private const int LeftStick = 3, RightStick = 4;
                    internal static readonly TopiaForgeInputGate Gate = new();
                    internal static bool KitTextFieldHasKeyboard() => false;
                    internal static bool KeyTaken(int key) => false;
                    internal static bool PadButtonTaken(bool back) => false;
                    internal static int KeyNumber(string name) => -1;
                }
            }
            """;
    }
}
