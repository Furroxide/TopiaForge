using System.IO;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        /// <summary>
        /// The kit's pointer upkeep (TopiaForgePointer), compiled unchanged against a small contract double of the Input
        /// System, the UI module and the event system, since it needs Unity to run. Robotopia's pause menu turned its whole
        /// menu map off as it closed, the module's point and click actions with it, so after any pause no click reached a
        /// TopiaForge surface; and the module has no wheel action, so the wheel never scrolled a kit scroll view.
        /// </summary>
        private static void TheMouseKeepsWorkingOnTheKitsSurfaces()
        {
            var kit = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            RunProbe("PointerProbe", "PointerScenarios",
                new[]
                {
                    File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgePointerHold.cs")),
                    File.ReadAllText(Path.Combine(kit, "Runtime", "TopiaForgePointer.cs")),
                    PointerContract,
                    PointerScenarios,
                },
                "AtTheGamesMenusNothingChanges", "AfterThePauseMenuASurfaceShowingTheCursorGetsThePointer",
                "ThePauseMenuOpeningMeanwhileKeepsItsPointer", "ASurfaceThatDoesNotShowTheCursorChangesNothing",
                "TheWheelScrollsOnlyTheKitsControlUnderThePointer", "AModuleWithItsOwnWheelScrollsByItself",
                "ANewModuleIsFollowedAndTheOldPutBack", "TheKitPutsTheGameBackAsItStops", "AFailureStopsOnceAndPutsTheGameBack",
                "AFrameAtRestAllocatesNothing");
        }

        // A contract double, not Unity's code: what TopiaForgePointer reads and calls. An action map turns all its actions on
        // or off, as PlayerInput.SwitchCurrentActionMap does, and every change is announced through onActionChange.
        private const string PointerContract = """
            using System;
            using System.Collections.Generic;

            namespace UnityEngine
            {
                public class Object { }
                public class Component : Object { }
                public class Behaviour : Component { }
                public class MonoBehaviour : Behaviour { }
                public class ScriptableObject : Object { }
                public class GameObject : Object
                {
                    public GameObject? Parent;
                    public UnityEngine.EventSystems.IScrollHandler? Scroll;
                }
                public struct Vector2
                {
                    public float x;
                    public float y;
                    public Vector2(float x, float y) { this.x = x; this.y = y; }
                    public static Vector2 operator *(Vector2 value, float scale) => new Vector2(value.x * scale, value.y * scale);
                }
                public struct Vector3
                {
                    public float x;
                    public float y;
                    public float z;
                    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
                    public static implicit operator Vector2(Vector3 value) => new Vector2(value.x, value.y);
                }
                public static class Input
                {
                    public static Vector2 mouseScrollDelta;
                    public static Vector3 mousePosition;
                }
            }
            namespace UnityEngine.EventSystems
            {
                public interface IEventSystemHandler { }
                public interface IScrollHandler : IEventSystemHandler { void OnScroll(PointerEventData eventData); }
                public class BaseInputModule : UnityEngine.MonoBehaviour { }
                public class BaseEventData
                {
                    public BaseEventData(EventSystem system) { }
                    public void Reset() { }
                }
                public class PointerEventData : BaseEventData
                {
                    public PointerEventData(EventSystem system) : base(system) { }
                    public UnityEngine.Vector2 position { get; set; }
                    public UnityEngine.Vector2 scrollDelta { get; set; }
                }
                public struct RaycastResult { public UnityEngine.GameObject gameObject; }
                public class EventSystem : UnityEngine.MonoBehaviour
                {
                    public static EventSystem? current { get; set; }
                    public BaseInputModule? currentInputModule { get; set; }
                    public List<UnityEngine.GameObject> Under = new List<UnityEngine.GameObject>();
                    public UnityEngine.Vector2 RaycastAt;
                    public void RaycastAll(PointerEventData eventData, List<RaycastResult> raycastResults)
                    {
                        RaycastAt = eventData.position;
                        foreach (var hit in Under) raycastResults.Add(new RaycastResult { gameObject = hit });
                    }
                }
                public static class ExecuteEvents
                {
                    public delegate void EventFunction<T>(T handler, BaseEventData eventData);
                    public static EventFunction<IScrollHandler> scrollHandler { get; } = (handler, data) => handler.OnScroll((PointerEventData)data);
                    public static UnityEngine.GameObject? ExecuteHierarchy<T>(UnityEngine.GameObject root, BaseEventData eventData, EventFunction<T> callbackFunction)
                        where T : IEventSystemHandler
                    {
                        for (var at = root; at != null; at = at.Parent)
                        {
                            if (at.Scroll is T handler) { callbackFunction(handler, eventData); return at; }
                        }
                        return null;
                    }
                }
            }
            namespace UnityEngine.InputSystem
            {
                public enum InputActionChange { ActionEnabled, ActionDisabled, ActionMapEnabled, ActionMapDisabled, ActionPerformed }
                public class InputActionMap
                {
                    public readonly List<InputAction> Actions = new List<InputAction>();
                    public bool enabled { get { foreach (var action in Actions) if (action.enabled) return true; return false; } }
                    public void Enable() { foreach (var action in Actions) action.Set(true); InputSystem.Announce(this, InputActionChange.ActionMapEnabled); }
                    public void Disable() { foreach (var action in Actions) action.Set(false); InputSystem.Announce(this, InputActionChange.ActionMapDisabled); }
                }
                public class InputAction
                {
                    public InputAction(InputActionMap? map) { actionMap = map; map?.Actions.Add(this); }
                    public InputActionMap? actionMap { get; }
                    public bool enabled { get; private set; }
                    public int Enables;
                    public int Disables;
                    public bool Throws;
                    public void Set(bool on) => enabled = on;
                    public void Enable()
                    {
                        if (Throws) throw new InvalidOperationException("The action of a device named Sam could not resolve.");
                        if (enabled) return;
                        enabled = true; Enables++; InputSystem.Announce(this, InputActionChange.ActionEnabled);
                    }
                    public void Disable()
                    {
                        if (!enabled) return;
                        enabled = false; Disables++; InputSystem.Announce(this, InputActionChange.ActionDisabled);
                    }
                }
                public class InputActionReference : UnityEngine.ScriptableObject
                {
                    public InputAction? action { get; set; }
                }
                public static class InputSystem
                {
                    public static event Action<object, InputActionChange>? onActionChange;
                    public static int Watchers => onActionChange?.GetInvocationList().Length ?? 0;
                    public static void Announce(object changed, InputActionChange change) => onActionChange?.Invoke(changed, change);
                }
            }
            namespace UnityEngine.InputSystem.UI
            {
                public class InputSystemUIInputModule : UnityEngine.EventSystems.BaseInputModule
                {
                    public UnityEngine.InputSystem.InputActionReference? point { get; set; }
                    public UnityEngine.InputSystem.InputActionReference? leftClick { get; set; }
                    public UnityEngine.InputSystem.InputActionReference? rightClick { get; set; }
                    public UnityEngine.InputSystem.InputActionReference? middleClick { get; set; }
                    public UnityEngine.InputSystem.InputActionReference? scrollWheel { get; set; }
                    public UnityEngine.InputSystem.InputActionReference? move { get; set; }
                    public float scrollDeltaPerTick { get; set; } = 6f;
                }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                public static class TopiaForgeCursor { public static int ActiveLeases; }
                public static class TopiaForgeInput { public static bool LegacyAvailable = true; }
                internal static class TopiaForgeInputOwnership
                {
                    internal static readonly HashSet<UnityEngine.GameObject> Kit = new HashSet<UnityEngine.GameObject>();
                    internal static bool IsKitObject(UnityEngine.GameObject target) => Kit.Contains(target);
                }
                internal static class TopiaForgeLog
                {
                    public static int Warnings;
                    public static string Last = string.Empty;
                    public static void Warn(string message) { Warnings++; Last = message; }
                }
            }
            """;

        private const string PointerScenarios = """
            using System;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.InputSystem;
            using UnityEngine.InputSystem.UI;

            public static class PointerScenarios
            {
                private static InputActionMap menus = null!;
                private static InputAction point = null!;
                private static InputAction click = null!;
                private static InputAction navigate = null!;
                private static InputAction submit = null!;
                private static InputAction cancel = null!;
                private static InputSystemUIInputModule module = null!;
                private static EventSystem system = null!;

                private sealed class ScrollView : IScrollHandler
                {
                    public int Scrolled;
                    public Vector2 Delta;
                    public void OnScroll(PointerEventData eventData) { Scrolled++; Delta = eventData.scrollDelta; }
                }

                // The game's menu map holds the UI module's point, click, navigate, submit and cancel actions and no wheel, as
                // Robotopia's does; it is on, as at the main menu.
                private static void Start()
                {
                    TopiaForgePointer.Reset();
                    TopiaForgeLog.Warnings = 0;
                    TopiaForgeCursor.ActiveLeases = 0;
                    TopiaForgeInput.LegacyAvailable = true;
                    TopiaForgeInputOwnership.Kit.Clear();
                    Input.mouseScrollDelta = new Vector2(0f, 0f);
                    menus = new InputActionMap();
                    point = new InputAction(menus);
                    click = new InputAction(menus);
                    navigate = new InputAction(menus);
                    submit = new InputAction(menus);
                    cancel = new InputAction(menus);
                    menus.Enable();
                    module = new InputSystemUIInputModule
                    {
                        point = new InputActionReference { action = point },
                        leftClick = new InputActionReference { action = click },
                        move = new InputActionReference { action = navigate },
                    };
                    system = new EventSystem { currentInputModule = module };
                    EventSystem.current = system;
                }

                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
                }

                private static int Changes() => point.Enables + point.Disables + click.Enables + click.Disables;

                public static void AtTheGamesMenusNothingChanges()
                {
                    Start();
                    TopiaForgeCursor.ActiveLeases = 1;
                    for (var frame = 0; frame < 5; frame++) TopiaForgePointer.Tick();
                    TopiaForgeCursor.ActiveLeases = 0;
                    TopiaForgePointer.Tick();
                    Check(Changes() == 0 && point.enabled && click.enabled, "at the game's menus, where its pointer is on, a kit window changes nothing");
                    Check(InputSystem.Watchers == 0, "and watches nothing");
                }

                public static void AfterThePauseMenuASurfaceShowingTheCursorGetsThePointer()
                {
                    Start();
                    menus.Disable();
                    TopiaForgeCursor.ActiveLeases = 1;
                    TopiaForgePointer.Tick();
                    Check(point.enabled && click.enabled, "after the pause menu closed, a kit window showing the cursor gets the game's pointer and click back");
                    Check(!navigate.enabled && !submit.enabled && !cancel.enabled, "but never the game's navigate, submit or cancel");
                    for (var frame = 0; frame < 5; frame++) TopiaForgePointer.Tick();
                    Check(point.Enables == 1 && click.Enables == 1, "once");
                    TopiaForgeCursor.ActiveLeases = 0;
                    TopiaForgePointer.Tick();
                    Check(!point.enabled && !click.enabled && !menus.enabled, "the last window closing puts the game's menu map back exactly as the game left it");
                    Check(InputSystem.Watchers == 0, "and the kit stops watching it");
                }

                public static void ThePauseMenuOpeningMeanwhileKeepsItsPointer()
                {
                    Start();
                    menus.Disable();
                    TopiaForgeCursor.ActiveLeases = 1;
                    TopiaForgePointer.Tick();
                    menus.Enable();
                    TopiaForgePointer.Tick();
                    TopiaForgeCursor.ActiveLeases = 0;
                    TopiaForgePointer.Tick();
                    Check(point.enabled && click.enabled && navigate.enabled, "the pause menu the game opened under the window keeps its pointer as the window closes");

                    TopiaForgeCursor.ActiveLeases = 1;
                    menus.Disable();
                    TopiaForgePointer.Tick();
                    Check(point.enabled && click.enabled && !navigate.enabled, "closing it under a window that shows the cursor gives the window the pointer again");
                    TopiaForgeCursor.ActiveLeases = 0;
                    TopiaForgePointer.Tick();
                    Check(!point.enabled && !click.enabled, "and it goes back off as that window closes");
                }

                public static void ASurfaceThatDoesNotShowTheCursorChangesNothing()
                {
                    Start();
                    menus.Disable();
                    for (var frame = 0; frame < 5; frame++) TopiaForgePointer.Tick();
                    Check(Changes() == 0 && !point.enabled, "a HUD or any surface that does not show the cursor changes nothing of the game's");
                    Input.mouseScrollDelta = new Vector2(0f, 1f);
                    var row = new GameObject { Scroll = new ScrollView() };
                    TopiaForgeInputOwnership.Kit.Add(row);
                    system.Under.Add(row);
                    TopiaForgePointer.Tick();
                    Check(((ScrollView)row.Scroll!).Scrolled == 0, "nor scrolls anything");
                }

                public static void TheWheelScrollsOnlyTheKitsControlUnderThePointer()
                {
                    Start();
                    TopiaForgeCursor.ActiveLeases = 1;
                    var view = new ScrollView();
                    var list = new GameObject { Scroll = view };
                    var row = new GameObject { Parent = list };
                    TopiaForgeInputOwnership.Kit.Add(row);
                    TopiaForgeInputOwnership.Kit.Add(list);
                    system.Under.Add(row);
                    Input.mousePosition = new Vector3(640f, 360f, 0f);
                    TopiaForgePointer.Tick();
                    Check(view.Scrolled == 0, "a still wheel scrolls nothing");
                    Input.mouseScrollDelta = new Vector2(0f, -1f);
                    TopiaForgePointer.Tick();
                    Check(view.Scrolled == 1 && view.Delta.y == -6f && view.Delta.x == 0f,
                        "a notch scrolls the kit's list under the pointer by as much as the module's own wheel would (" + view.Delta.y + ")");
                    Check(system.RaycastAt.x == 640f && system.RaycastAt.y == 360f, "looking where the pointer is");

                    var menu = new GameObject { Scroll = new ScrollView() };
                    system.Under.Clear();
                    system.Under.Add(menu);
                    system.Under.Add(row);
                    TopiaForgePointer.Tick();
                    Check(((ScrollView)menu.Scroll!).Scrolled == 0 && view.Scrolled == 1, "with the game's own menu on top, nothing scrolls: the game never sees the wheel");
                    system.Under.Clear();
                    TopiaForgePointer.Tick();
                    Check(view.Scrolled == 1, "and with nothing under the pointer, nothing does");

                    TopiaForgeInput.LegacyAvailable = false;
                    system.Under.Add(row);
                    TopiaForgePointer.Tick();
                    Check(view.Scrolled == 1, "without Unity's legacy input, which counts the wheel in notches, the kit leaves the wheel alone");
                }

                public static void AModuleWithItsOwnWheelScrollsByItself()
                {
                    Start();
                    module.scrollWheel = new InputActionReference { action = new InputAction(menus) };
                    TopiaForgeCursor.ActiveLeases = 1;
                    var view = new ScrollView();
                    var row = new GameObject { Scroll = view };
                    TopiaForgeInputOwnership.Kit.Add(row);
                    system.Under.Add(row);
                    Input.mouseScrollDelta = new Vector2(0f, 1f);
                    TopiaForgePointer.Tick();
                    Check(view.Scrolled == 0, "a module with a wheel of its own scrolls by itself, so the kit never scrolls twice");
                }

                public static void ANewModuleIsFollowedAndTheOldPutBack()
                {
                    Start();
                    menus.Disable();
                    TopiaForgeCursor.ActiveLeases = 1;
                    TopiaForgePointer.Tick();
                    var nextMenus = new InputActionMap();
                    var nextPoint = new InputAction(nextMenus);
                    var nextClick = new InputAction(nextMenus);
                    system.currentInputModule = new InputSystemUIInputModule
                    {
                        point = new InputActionReference { action = nextPoint },
                        leftClick = new InputActionReference { action = nextClick },
                    };
                    TopiaForgePointer.Tick();
                    Check(!point.enabled && !click.enabled, "a new scene's module: the old actions go back off as the game left them");
                    Check(nextPoint.enabled && nextClick.enabled, "and the new module's are taken while the window shows the cursor");
                    EventSystem.current = null;
                    TopiaForgePointer.Tick();
                    Check(!nextPoint.enabled && !nextClick.enabled, "with no event system left, nothing the kit turned on stays on");
                }

                public static void TheKitPutsTheGameBackAsItStops()
                {
                    Start();
                    menus.Disable();
                    TopiaForgeCursor.ActiveLeases = 1;
                    TopiaForgePointer.Tick();
                    TopiaForgePointer.Reset();
                    Check(!point.enabled && !click.enabled && InputSystem.Watchers == 0, "the kit's teardown puts the game's pointer back and stops watching");
                }

                public static void AFailureStopsOnceAndPutsTheGameBack()
                {
                    Start();
                    menus.Disable();
                    click.Throws = true;
                    TopiaForgeCursor.ActiveLeases = 1;
                    TopiaForgePointer.Tick();
                    TopiaForgePointer.Tick();
                    Check(TopiaForgeLog.Warnings == 1, "a failure is logged once (" + TopiaForgeLog.Warnings + ")");
                    Check(TopiaForgeLog.Last.Contains("InvalidOperationException") && !TopiaForgeLog.Last.Contains("Sam"), "naming only its type");
                    Check(!point.enabled && InputSystem.Watchers == 0, "and what the kit had turned on goes back off");
                    click.Throws = false;
                    TopiaForgePointer.Tick();
                    Check(!point.enabled && !click.enabled, "until the game restarts, the kit leaves the game's pointer alone");
                }

                // The fewest bytes over one unmeasured run and up to three measured ones, as NothingIsAllocatedAtRestOrWhileHeld
                // counts them: what the runtime does while code first runs happens once, a per-frame allocation every run.
                public static void AFrameAtRestAllocatesNothing()
                {
                    Start();
                    menus.Disable();
                    TopiaForgeCursor.ActiveLeases = 1;
                    long Run()
                    {
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        for (var frame = 0; frame < 20000; frame++) TopiaForgePointer.Tick();
                        return GC.GetAllocatedBytesForCurrentThread() - before;
                    }

                    Run();
                    var fewest = long.MaxValue;
                    for (var pass = 0; pass < 3 && fewest != 0; pass++) fewest = Math.Min(fewest, Run());
                    Check(fewest == 0, "a frame with a window showing the cursor and the pointer held allocates nothing (" + fewest + " bytes)");
                    Check(point.enabled && click.enabled, "and keeps the pointer");
                }
            }
            """;
    }
}
