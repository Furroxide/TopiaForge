using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        // Input System key numbers.
        private const int D = 18;
        private const int S = 33;

        /// <summary>
        /// The game's navigate action binds W, A, S and D as well as the arrows, and its menus and Unity's UI module both move
        /// on them: under a surface that claims navigation each is the surface's, moving it the way the game's binding points,
        /// while the game's menus listen. W walked the native main menu and the menu bar under an open manager overlay.
        /// </summary>
        private static void TheGamesNavigationKeysAreTheSurfacesToo()
        {
            var rig = new Rig();
            BindTheGamesNavigation(rig.Navigation);
            rig.Navigation.Listening = true;
            Assert(rig.Key(W, true), "with nothing of TopiaForge's open, W reaches the game");
            rig.Key(W, false);

            var overlay = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, new Root());
            foreach (var (key, x, y) in new[] { (W, 0f, 1f), (A, -1f, 0f), (S, 0f, -1f), (D, 1f, 0f) })
            {
                Assert(!rig.Key(key, true), "under the overlay, the game's navigation key " + key + " reaches neither its menu nor Unity's module");
                rig.Gate.Direction(out var movedX, out var movedY);
                Assert(movedX == x && movedY == y,
                    "and moves the overlay the way the game's binding points (key " + key + ": " + movedX + ", " + movedY + ")");
                rig.Key(key, false);
            }

            Assert(rig.Key(E, true), "a key the game's navigation does not bind still reaches the game");
            rig.Key(E, false);

            // A window takes navigation only while one of its controls is selected: otherwise W walks.
            rig.Order.Remove(overlay);
            var shopRoot = new Root();
            rig.Order.Push(new Surface(TopiaForgeLayerBand.Window), TopiaForgeInputClaim.NavigationWhileSelected, shopRoot);
            Assert(rig.Key(W, true), "with none of a window's controls selected, W walks");
            rig.Key(W, false);
            rig.Selection = shopRoot;
            Assert(!rig.Key(W, true), "with one selected, W moves the window, as the arrows do");
            rig.Key(W, false);

            // In a level the game's menu navigation is disabled, and W is gameplay's even then.
            Assert(!rig.Key(W, true), "W pressed while the game's menus listen is the window's");
            rig.Navigation.Listening = false;
            Assert(!rig.Key(W, true), "and stays TopiaForge's, held, after they stop listening");
            rig.Key(W, false);
            Assert(rig.Key(W, true), "then, with the game's menus not listening, W is gameplay's though a window's control is selected");
            rig.Key(W, false);
            AssertEqual(rig.GamePresses(KeyboardId, W), 3, "the game saw W only while nothing claimed it or its menus did not listen");
            AssertEqual(rig.GamePresses(KeyboardId, A) + rig.GamePresses(KeyboardId, S) + rig.GamePresses(KeyboardId, D), 0,
                "and none of A, S and D pressed under the overlay");
        }

        private static void TheNavigationBindingsFollowWhatTheGameBinds()
        {
            var bindings = new TopiaForgeNavigationBindings();
            AssertEqual(TopiaForgeNavigationBindings.KitRole(Escape), TopiaForgeInputRole.Back, "Escape is Back");
            foreach (var arrow in new[] { UpArrow, DownArrow, LeftArrow, RightArrow })
            {
                AssertEqual(bindings.RoleOf(arrow), TopiaForgeInputRole.Direction, "an arrow moves (key " + arrow + ")");
            }

            foreach (var submit in new[] { Enter, NumpadEnter, Space })
            {
                AssertEqual(bindings.RoleOf(submit), TopiaForgeInputRole.Submit, "Enter, the keypad's Enter and Space submit (key " + submit + ")");
            }

            AssertEqual(bindings.RoleOf(Tab), TopiaForgeInputRole.Tab, "Tab is Tab");
            bindings.Direction(UpArrow, out var upX, out var upY);
            bindings.Direction(LeftArrow, out var leftX, out var leftY);
            Assert(upX == 0f && upY == 1f && leftX == -1f && leftY == 0f, "the arrows point their own way");

            BindTheGamesNavigation(bindings);
            Assert(bindings.KeyCount == 0 && !bindings.Moves(W) && bindings.RoleOf(W) == TopiaForgeInputRole.Key,
                "nothing of the game's moves while its navigate action is disabled");
            bindings.Direction(W, out var restX, out var restY);
            Assert(restX == 0f && restY == 0f && !bindings.RightStickMoves, "neither W nor the right stick");

            bindings.Listening = true;
            AssertEqual(bindings.KeyCount, 4, "W, A, S and D move while it listens, and the arrows stay the kit's own");
            AssertEqual(string.Join(",", Enumerable.Range(0, bindings.KeyCount).Select(bindings.KeyAt)), W + "," + S + "," + A + "," + D,
                "in the order the action binds them");
            Assert(bindings.RoleOf(W) == TopiaForgeInputRole.Direction && bindings.RightStickMoves, "W moves, and so does the right stick");

            bindings.BindKey(UpArrow, "down");
            bindings.BindKey(W, "down");
            bindings.Direction(UpArrow, out upX, out upY);
            bindings.Direction(W, out var wX, out var wY);
            Assert(bindings.KeyCount == 4 && upY == 1f && wY == 1f && upX == 0f && wX == 0f,
                "a kit key keeps its own part, and a key followed once keeps its first binding");

            bindings.BindKey(V, null);
            bindings.BindKey(B, "Right");
            bindings.Direction(V, out var vX, out var vY);
            bindings.Direction(B, out var bX, out var bY);
            Assert(bindings.Moves(V) && vX == 0f && vY == 0f, "a key bound with no direction is still the game's navigation: taken, moving nowhere");
            Assert(bX == 1f && bY == 0f, "a part's name is matched whatever its case");

            foreach (var invalid in new[] { -1, 0, TopiaForgeInputGate.ControlsPerDevice })
            {
                bindings.BindKey(invalid, "up");
                Assert(!bindings.Moves(invalid), "an unknown key is never followed (" + invalid + ")");
            }

            AssertEqual(bindings.KeyCount, 6, "and changes nothing");
            for (var key = 41; bindings.KeyCount < TopiaForgeNavigationBindings.MaxKeys; key++)
            {
                bindings.BindKey(key, "up");
            }

            bindings.BindKey(F10, "up");
            Assert(!bindings.Moves(F10) && bindings.Moves(W), "a key past the most followed stays the game's, and evicts none");

            bindings.Clear();
            Assert(bindings.KeyCount == 0 && !bindings.Moves(W) && !bindings.RightStickMoves, "clearing forgets every key and the right stick");
        }

        /// <summary>
        /// The kit's key numbers, and the ones these tests press, are the Input System's: the filter reads a key's number from
        /// the game's own Input System, and the kit decides by these.
        /// </summary>
        private static void TheKitsKeyNumbersAreTheInputSystems()
        {
            WithGameAssemblies(type =>
            {
                var key = type("UnityEngine.InputSystem.Key, Unity.InputSystem");
                Assert(key != null && key.IsEnum, "the game's Input System numbers its keys");
                foreach (var (name, number) in new[]
                {
                    ("Space", TopiaForgeNavigationBindings.Space), ("Enter", TopiaForgeNavigationBindings.Enter),
                    ("Tab", TopiaForgeNavigationBindings.Tab), ("Escape", TopiaForgeNavigationBindings.Escape),
                    ("LeftArrow", TopiaForgeNavigationBindings.LeftArrow), ("RightArrow", TopiaForgeNavigationBindings.RightArrow),
                    ("UpArrow", TopiaForgeNavigationBindings.UpArrow), ("DownArrow", TopiaForgeNavigationBindings.DownArrow),
                    ("NumpadEnter", TopiaForgeNavigationBindings.NumpadEnter), ("A", A), ("B", B), ("D", D), ("E", E), ("S", S),
                    ("V", V), ("W", W), ("F10", F10),
                })
                {
                    AssertEqual(Convert.ToInt32(key!.GetField(name)!.GetRawConstantValue()), number, "the kit's number for " + name + " is the Input System's");
                }
            });
        }

        private static void BindTheGamesNavigation(TopiaForgeNavigationBindings bindings)
        {
            // W, A, S and D with the arrows, as the game's navigate action binds them in a two-dimensional composite, and the
            // right stick, as such an action may bind it too.
            bindings.BindKey(W, "up");
            bindings.BindKey(UpArrow, "up");
            bindings.BindKey(S, "down");
            bindings.BindKey(DownArrow, "down");
            bindings.BindKey(A, "left");
            bindings.BindKey(LeftArrow, "left");
            bindings.BindKey(D, "right");
            bindings.BindKey(RightArrow, "right");
            bindings.BindRightStick();
        }

        /// <summary>
        /// How the kit follows the game's navigate action (TopiaForgeNavigateAction), compiled unchanged against a small
        /// Input System contract double, since it needs Unity to run: the keys the action binds with the way each points, the
        /// right stick if it binds it, nothing while it is disabled, a rebind, a legacy module, and a failure.
        /// </summary>
        private static void TheKitFollowsTheGamesNavigateAction()
        {
            var kit = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            RunProbe("NavigateActionProbe", "NavigateScenarios",
                new[]
                {
                    File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeInputClaim.cs")),
                    File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeInputGate.cs")),
                    File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeNavigationBindings.cs")),
                    File.ReadAllText(Path.Combine(kit, "Runtime", "TopiaForgeNavigateAction.cs")),
                    NavigateActionContract,
                    NavigateScenarios,
                },
                "TheGamesNavigationKeysAreFollowed", "NothingMovesWhileTheActionIsDisabled", "ARebindIsFollowed",
                "OnlyTheInputSystemsModuleIsFollowed", "AFailureStopsTheFollowingOnce", "FollowingAnUnchangedActionAllocatesNothing");
        }

        /// <summary>Compiles <paramref name="sources"/> and runs each named scenario of <paramref name="type"/>; every failure is reported.</summary>
        private static void RunProbe(string assembly, string type, string[] sources, params string[] scenarios)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(assembly,
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            Assert(result.Success, assembly + " compiles against its contract double: "
                + string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType(type, throwOnError: true)!;
            var failures = new List<string>();
            foreach (var name in scenarios)
            {
                try
                {
                    ((Action)probe.GetMethod(name)!.CreateDelegate(typeof(Action)))();
                }
                catch (InvalidOperationException exception)
                {
                    failures.Add(name + ": " + exception.Message);
                }
            }

            Assert(failures.Count == 0, assembly + ":\n" + string.Join("\n", failures));
        }

        // A contract double, not Unity's code: what TopiaForgeNavigateAction reads. An action's controls are what its bindings
        // resolved to, and each control knows the binding it came from, as InputAction.GetBindingIndexForControl tells it.
        private const string NavigateActionContract = """
            using System;
            using UnityEngine.InputSystem.Utilities;

            namespace UnityEngine
            {
                public class Object { }
                public class ScriptableObject : Object { }
                public class Component : Object { }
                public class Behaviour : Component { }
                public class MonoBehaviour : Behaviour { }
            }
            namespace UnityEngine.EventSystems
            {
                public class BaseInputModule : UnityEngine.MonoBehaviour { }
                public class EventSystem : UnityEngine.MonoBehaviour
                {
                    public static EventSystem? current { get; set; }
                    public BaseInputModule? currentInputModule { get; set; }
                }
            }
            namespace UnityEngine.InputSystem.Utilities
            {
                public readonly struct ReadOnlyArray<TValue>
                {
                    private readonly TValue[] array;
                    public ReadOnlyArray(TValue[] array) { this.array = array; }
                    public int Count => array.Length;
                    public TValue this[int index] => array[index];
                }
            }
            namespace UnityEngine.InputSystem
            {
                public enum Key { None = 0, Space = 1, Enter = 2, Tab = 3, A = 15, D = 18, S = 33, W = 37, Z = 40, Escape = 60, LeftArrow = 61, RightArrow = 62, UpArrow = 63, DownArrow = 64, NumpadEnter = 77 }
                public class InputControl
                {
                    public InputControl? parent { get; set; }
                    public InputDevice device { get; set; } = null!;
                }
                public class InputDevice : InputControl
                {
                    public InputDevice() { device = this; }
                }
                public class Keyboard : InputDevice { }
                public class Gamepad : InputDevice
                {
                    public Gamepad()
                    {
                        leftStick = new InputControl { parent = this, device = this };
                        rightStick = new InputControl { parent = this, device = this };
                        dpad = new InputControl { parent = this, device = this };
                    }
                    public InputControl leftStick { get; }
                    public InputControl rightStick { get; }
                    public InputControl dpad { get; }
                }
                public struct InputBinding
                {
                    public string name { get; set; }
                    public bool isPartOfComposite { get; set; }
                }
                public class InputAction
                {
                    public InputControl[] Controls = new InputControl[0];
                    public int[] BindingOf = new int[0];
                    public InputBinding[] Bindings = new InputBinding[0];
                    public bool Throws;
                    public bool enabled { get; set; } = true;
                    public ReadOnlyArray<InputControl> controls => Throws ? throw new InvalidOperationException("The controls of a device named Sam could not resolve.") : new ReadOnlyArray<InputControl>(Controls);
                    public ReadOnlyArray<InputBinding> bindings => new ReadOnlyArray<InputBinding>(Bindings);
                }
                public static class InputActionRebindingExtensions
                {
                    public static int GetBindingIndexForControl(this InputAction action, InputControl control)
                    {
                        var index = Array.IndexOf(action.Controls, control);
                        return index < 0 ? -1 : action.BindingOf[index];
                    }
                }
                public class InputActionReference : UnityEngine.ScriptableObject
                {
                    public InputAction? action { get; set; }
                }
            }
            namespace UnityEngine.InputSystem.Controls
            {
                public class KeyControl : UnityEngine.InputSystem.InputControl
                {
                    public UnityEngine.InputSystem.Key keyCode { get; set; }
                }
            }
            namespace UnityEngine.InputSystem.UI
            {
                public class InputSystemUIInputModule : UnityEngine.EventSystems.BaseInputModule
                {
                    public UnityEngine.InputSystem.InputActionReference? move { get; set; }
                }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                internal static class TopiaForgeLog
                {
                    public static int Warnings;
                    public static string Last = string.Empty;
                    public static void Warn(string message) { Warnings++; Last = message; }
                }
            }
            """;

        private const string NavigateScenarios = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine.EventSystems;
            using UnityEngine.InputSystem;
            using UnityEngine.InputSystem.Controls;
            using UnityEngine.InputSystem.UI;

            public static class NavigateScenarios
            {
                private static InputAction action = null!;
                private static Keyboard keyboard = null!;

                private static TopiaForgeNavigationBindings Bindings => TopiaForgeNavigateAction.Bindings;

                // A navigate action as Unity's UI module holds it: a keyboard composite of W, A, S, D and the arrows, which the
                // game's binds, and the left stick, the right stick's up part and the D-pad.
                private static void Start()
                {
                    TopiaForgeNavigateAction.Reset();
                    TopiaForgeLog.Warnings = 0;
                    keyboard = new Keyboard();
                    var gamepad = new Gamepad();
                    var controls = new List<InputControl>();
                    var bindingOf = new List<int>();
                    var bindings = new List<InputBinding> { new InputBinding { name = "Keyboard" } };
                    foreach (var (key, part) in new[] { (Key.W, "up"), (Key.UpArrow, "up"), (Key.S, "down"), (Key.DownArrow, "down"), (Key.A, "left"), (Key.LeftArrow, "left"), (Key.D, "right"), (Key.RightArrow, "right") })
                    {
                        controls.Add(new KeyControl { keyCode = key, parent = keyboard, device = keyboard });
                        bindingOf.Add(bindings.Count);
                        bindings.Add(new InputBinding { name = part, isPartOfComposite = true });
                    }

                    controls.Add(gamepad.leftStick);
                    bindingOf.Add(bindings.Count);
                    bindings.Add(new InputBinding { name = string.Empty });
                    controls.Add(new InputControl { parent = gamepad.rightStick, device = gamepad });
                    bindingOf.Add(bindings.Count);
                    bindings.Add(new InputBinding { name = "up", isPartOfComposite = true });
                    controls.Add(gamepad.dpad);
                    bindingOf.Add(bindings.Count);
                    bindings.Add(new InputBinding { name = string.Empty });
                    action = new InputAction { Controls = controls.ToArray(), BindingOf = bindingOf.ToArray(), Bindings = bindings.ToArray() };
                    EventSystem.current = new EventSystem
                    {
                        currentInputModule = new InputSystemUIInputModule { move = new InputActionReference { action = action } },
                    };
                }

                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
                }

                private static void Points(Key key, float x, float y)
                {
                    Bindings.Direction((int)key, out var movedX, out var movedY);
                    Check(movedX == x && movedY == y, key + " points the way its binding does (" + movedX + ", " + movedY + ")");
                }

                public static void TheGamesNavigationKeysAreFollowed()
                {
                    Start();
                    TopiaForgeNavigateAction.Follow();
                    Check(Bindings.KeyCount == 4, "W, A, S and D are followed, and the arrows stay the kit's own (" + Bindings.KeyCount + ")");
                    Points(Key.W, 0f, 1f);
                    Points(Key.S, 0f, -1f);
                    Points(Key.A, -1f, 0f);
                    Points(Key.D, 1f, 0f);
                    Check(Bindings.RoleOf((int)Key.W) == TopiaForgeInputRole.Direction, "W moves while the game's menus listen");
                    Check(Bindings.RightStickMoves, "and so does the right stick, which the action binds by a part");
                }

                public static void NothingMovesWhileTheActionIsDisabled()
                {
                    Start();
                    action.enabled = false;
                    TopiaForgeNavigateAction.Follow();
                    Check(Bindings.KeyCount == 0 && !Bindings.RightStickMoves, "in a level, where the game's menu navigation is disabled, W and the right stick stay gameplay's");
                    action.enabled = true;
                    TopiaForgeNavigateAction.Follow();
                    Check(Bindings.KeyCount == 4 && Bindings.RightStickMoves, "once it listens again, they move its menus");
                }

                public static void ARebindIsFollowed()
                {
                    Start();
                    TopiaForgeNavigateAction.Follow();
                    action.Controls[0] = new KeyControl { keyCode = Key.Z, parent = keyboard, device = keyboard };
                    TopiaForgeNavigateAction.Follow();
                    Check(Bindings.Moves((int)Key.Z) && !Bindings.Moves((int)Key.W), "a key rebound in the game's settings is followed in the next frame");
                    Points(Key.Z, 0f, 1f);
                }

                public static void OnlyTheInputSystemsModuleIsFollowed()
                {
                    Start();
                    TopiaForgeNavigateAction.Follow();
                    EventSystem.current = new EventSystem { currentInputModule = new BaseInputModule() };
                    TopiaForgeNavigateAction.Follow();
                    Check(Bindings.KeyCount == 0 && !Bindings.RightStickMoves, "under a legacy module only the kit's own keys navigate");
                    EventSystem.current = null;
                    TopiaForgeNavigateAction.Follow();
                    Check(Bindings.KeyCount == 0, "and so with no event system at all");
                }

                public static void AFailureStopsTheFollowingOnce()
                {
                    Start();
                    TopiaForgeNavigateAction.Follow();
                    action.Throws = true;
                    TopiaForgeNavigateAction.Follow();
                    TopiaForgeNavigateAction.Follow();
                    Check(Bindings.KeyCount == 0 && TopiaForgeLog.Warnings == 1, "a failure stops the following, logged once (" + TopiaForgeLog.Warnings + ")");
                    Check(TopiaForgeLog.Last.Contains("InvalidOperationException") && !TopiaForgeLog.Last.Contains("Sam"), "naming only the failure's type");
                    action.Throws = false;
                    TopiaForgeNavigateAction.Follow();
                    Check(Bindings.KeyCount == 0, "until the game restarts");
                    TopiaForgeNavigateAction.Reset();
                    TopiaForgeNavigateAction.Follow();
                    Check(Bindings.KeyCount == 4, "a fresh start follows again");
                }

                // The fewest bytes over one unmeasured run and up to three measured ones, as NothingIsAllocatedAtRestOrWhileHeld
                // counts them: what the runtime does while code first runs happens once, a per-frame allocation every run.
                public static void FollowingAnUnchangedActionAllocatesNothing()
                {
                    Start();
                    long Run()
                    {
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        for (var frame = 0; frame < 20000; frame++) TopiaForgeNavigateAction.Follow();
                        return GC.GetAllocatedBytesForCurrentThread() - before;
                    }

                    Run();
                    var fewest = long.MaxValue;
                    for (var pass = 0; pass < 3 && fewest != 0; pass++) fewest = Math.Min(fewest, Run());
                    Check(fewest == 0, "following an unchanged action every frame allocates nothing (" + fewest + " bytes)");
                    Check(Bindings.KeyCount == 4, "and still follows it");
                }
            }
            """;
    }
}
