using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        /// <summary>
        /// The kit's own navigation (TopiaForgeNavigation), compiled unchanged against a small Unity contract double, since it
        /// needs Unity to run. Unity's automatic navigation picks the nearest control in the move's direction on any canvas, so
        /// a move past a dialog's last control would select a control of the surface beneath it, and a submit in the same
        /// frame would press that control. Unity's UI module sends moves of its own, on a key the game still holds, and those
        /// stay inside the surface too.
        /// </summary>
        private static void TheKitsNavigationStaysInsideItsSurface()
        {
            var kit = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            var sources = new[]
            {
                File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeInputClaim.cs")),
                File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeInputGate.cs")),
                File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeNavigationDriver.cs")),
                File.ReadAllText(Path.Combine(kit, "Runtime", "TopiaForgeNavigation.cs")),
                NavigationUnityContract,
                NavigationScenarios,
            };
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("KitNavigationProbe",
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            Assert(result.Success, "the kit's navigation compiles against the Unity contract double: "
                + string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
            var scenarios = Assembly.Load(bytes.ToArray()).GetType("NavigationScenarios", throwOnError: true)!;
            var failures = new List<string>();
            foreach (var name in new[]
            {
                "AMoveStopsAtTheSurfacesEdge", "ASubmitWithAMovePressesOnlyTheSurfacesControl", "AHeldDirectionStaysWithItsSurface",
                "AModuleMoveStaysInsideTheSurface", "AModuleMoveStaysInsideAWindowToo", "AModuleMoveIsFreeWithNoSurfaceTakingNavigation",
                "AClickAndAMoveInOneFrameStayInside",
            })
            {
                try
                {
                    ((Action)scenarios.GetMethod(name)!.CreateDelegate(typeof(Action)))();
                }
                catch (InvalidOperationException exception)
                {
                    failures.Add(name + ": " + exception.Message);
                }
            }

            Assert(failures.Count == 0, "the kit's navigation keeps to its surface:\n" + string.Join("\n", failures));
        }

        // A contract double, not Unity's code: what TopiaForgeNavigation reads and calls, with a control's place on the screen
        // standing in for its rect. A move selects the nearest interactable, navigable control in its direction among every
        // enabled one, on any canvas, as Unity UI's automatic navigation (Selectable.FindSelectable) does.
        private const string NavigationUnityContract = """
            using System;
            using System.Collections.Generic;

            namespace UnityEngine
            {
                public struct Vector2
                {
                    public float x;
                    public float y;
                    public Vector2(float x, float y) { this.x = x; this.y = y; }
                }
                public class Component
                {
                    public GameObject gameObject = null!;
                    public Transform transform => gameObject.transform;
                }
                public class Behaviour : Component { }
                public class MonoBehaviour : Behaviour { }
                public class Transform : Component
                {
                    public Transform? parent;
                    public bool IsChildOf(Transform other)
                    {
                        for (var at = this; at != null; at = at.parent) if (ReferenceEquals(at, other)) return true;
                        return false;
                    }
                }
                public class GameObject
                {
                    private readonly List<Component> components = new List<Component>();
                    private readonly List<GameObject> children = new List<GameObject>();
                    private readonly GameObject? parent;
                    private bool active = true;
                    public GameObject(string name, GameObject? parent = null)
                    {
                        this.name = name;
                        this.parent = parent;
                        transform = new Transform { gameObject = this, parent = parent?.transform };
                        parent?.children.Add(this);
                    }
                    public string name;
                    public Transform transform { get; }
                    public bool activeInHierarchy => active && (parent == null || parent.activeInHierarchy);
                    public void SetActive(bool value) => active = value;
                    public T AddComponent<T>() where T : Component, new()
                    {
                        var component = new T { gameObject = this };
                        components.Add(component);
                        return component;
                    }
                    public bool TryGetComponent<T>(out T component)
                    {
                        foreach (var candidate in components) if (candidate is T match) { component = match; return true; }
                        component = default!;
                        return false;
                    }
                    public void GetComponents<T>(List<T> into)
                    {
                        into.Clear();
                        foreach (var candidate in components) if (candidate is T match) into.Add(match);
                    }
                    public void GetComponentsInChildren<T>(bool includeInactive, List<T> into)
                    {
                        into.Clear();
                        Collect(includeInactive, into);
                    }
                    private void Collect<T>(bool includeInactive, List<T> into)
                    {
                        if (!includeInactive && !activeInHierarchy) return;
                        foreach (var candidate in components) if (candidate is T match) into.Add(match);
                        foreach (var child in children) child.Collect(includeInactive, into);
                    }
                }
            }
            namespace UnityEngine.EventSystems
            {
                public enum MoveDirection { Left, Up, Right, Down, None }
                public interface IEventSystemHandler { }
                public interface IMoveHandler : IEventSystemHandler { void OnMove(AxisEventData eventData); }
                public interface ISubmitHandler : IEventSystemHandler { void OnSubmit(BaseEventData eventData); }
                public class BaseInputModule : Behaviour { }
                public class EventSystem : Behaviour
                {
                    public static EventSystem? current { get; set; }
                    public BaseInputModule? currentInputModule { get; set; }
                    public GameObject? currentSelectedGameObject { get; private set; }
                    public void SetSelectedGameObject(GameObject? selected) => currentSelectedGameObject = selected;
                }
                public class BaseEventData
                {
                    private readonly EventSystem system;
                    public BaseEventData(EventSystem system) { this.system = system; }
                    public bool used { get; private set; }
                    public void Reset() => used = false;
                    public GameObject? selectedObject
                    {
                        get => system.currentSelectedGameObject;
                        set => system.SetSelectedGameObject(value);
                    }
                }
                public class AxisEventData : BaseEventData
                {
                    public AxisEventData(EventSystem system) : base(system) { }
                    public MoveDirection moveDir { get; set; }
                    public Vector2 moveVector { get; set; }
                }
                public static class ExecuteEvents
                {
                    public delegate void EventFunction<T>(T handler, BaseEventData eventData);
                    public static EventFunction<IMoveHandler> moveHandler { get; } = (handler, data) => handler.OnMove((AxisEventData)data);
                    public static EventFunction<ISubmitHandler> submitHandler { get; } = (handler, data) => handler.OnSubmit(data);
                    public static bool Execute<T>(GameObject target, BaseEventData eventData, EventFunction<T> functor)
                        where T : IEventSystemHandler
                    {
                        var handlers = new List<T>();
                        target.GetComponents(handlers);
                        foreach (var handler in handlers) functor(handler, eventData);
                        return handlers.Count > 0;
                    }
                }
            }
            namespace UnityEngine.InputSystem.UI
            {
                public class InputSystemUIInputModule : UnityEngine.EventSystems.BaseInputModule
                {
                    public float moveRepeatDelay { get; set; } = 0.5f;
                    public float moveRepeatRate { get; set; } = 0.1f;
                }
            }
            namespace UnityEngine.UI
            {
                using UnityEngine.EventSystems;

                public struct Navigation
                {
                    public enum Mode { None = 0, Horizontal = 1, Vertical = 2, Automatic = 3, Explicit = 4 }
                    public Mode mode { get; set; }
                }
                public class Selectable : Behaviour, IMoveHandler
                {
                    public static readonly List<Selectable> All = new List<Selectable>();
                    public Selectable() { All.Add(this); }
                    public float X;
                    public float Y;
                    public Navigation navigation { get; set; } = new Navigation { mode = Navigation.Mode.Automatic };
                    public bool IsInteractable() => true;
                    public void OnMove(AxisEventData eventData)
                    {
                        var dx = eventData.moveDir == MoveDirection.Left ? -1f : eventData.moveDir == MoveDirection.Right ? 1f : 0f;
                        var dy = eventData.moveDir == MoveDirection.Down ? -1f : eventData.moveDir == MoveDirection.Up ? 1f : 0f;
                        Selectable? nearest = null;
                        var best = float.MaxValue;
                        foreach (var other in All)
                        {
                            if (ReferenceEquals(other, this) || !other.gameObject.activeInHierarchy || !other.IsInteractable()
                                || other.navigation.mode == Navigation.Mode.None) continue;
                            var ox = other.X - X;
                            var oy = other.Y - Y;
                            if ((ox * dx) + (oy * dy) <= 0f) continue;
                            var distance = (ox * ox) + (oy * oy);
                            if (distance < best) { best = distance; nearest = other; }
                        }
                        if (nearest != null) eventData.selectedObject = nearest.gameObject;
                    }
                }
                public class Button : Selectable, ISubmitHandler
                {
                    public int Submitted;
                    public void OnSubmit(BaseEventData eventData) => Submitted++;
                }
                public class InputField : Selectable { public bool isFocused; }
            }
            namespace TMPro
            {
                public class TMP_InputField : UnityEngine.UI.Selectable { public bool isFocused; }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                internal static class TopiaForgeDismissStack { internal static UnityEngine.GameObject? FocusRoot; }
                internal static class TopiaForgeInputOwnership
                {
                    internal static float X;
                    internal static float Y;
                    internal static bool SubmitPressedThisFrame;
                    internal static void Direction(out float x, out float y) { x = X; y = Y; }
                }
            }
            """;

        private const string NavigationScenarios = """
            using System;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.InputSystem.UI;
            using UnityEngine.UI;

            public static class NavigationScenarios
            {
                private static EventSystem system = null!;
                private static GameObject overlay = null!;
                private static GameObject dialog = null!;
                private static Button overlayTop = null!;
                private static Button overlayFooter = null!;
                private static Button turnOn = null!;
                private static Button notNow = null!;

                // The voice consent over the manager overlay: the dialog's two buttons sit in the middle of the screen, one of
                // the overlay's controls above them and one below, beneath the dialog's backdrop. "Not now" is selected.
                private static void Start()
                {
                    TopiaForgeNavigation.Reset();
                    Selectable.All.Clear();
                    system = new GameObject("EventSystem").AddComponent<EventSystem>();
                    system.currentInputModule = system.gameObject.AddComponent<InputSystemUIInputModule>();
                    EventSystem.current = system;
                    overlay = new GameObject("Manager overlay");
                    overlayTop = Control(overlay, "Overlay tab", 300f);
                    overlayFooter = Control(overlay, "Overlay footer", -400f);
                    dialog = new GameObject("Consent dialog");
                    turnOn = Control(dialog, "Turn on", 40f);
                    notNow = Control(dialog, "Not now", -40f);
                    TopiaForgeDismissStack.FocusRoot = dialog;
                    system.SetSelectedGameObject(notNow.gameObject);
                    Frame(0f, 0f, false);
                }

                private static Button Control(GameObject surface, string name, float y)
                {
                    var control = new GameObject(name, surface).AddComponent<Button>();
                    control.Y = y;
                    return control;
                }

                private static void Frame(float x, float y, bool submit)
                {
                    TopiaForgeInputOwnership.X = x;
                    TopiaForgeInputOwnership.Y = y;
                    TopiaForgeInputOwnership.SubmitPressedThisFrame = submit;
                    TopiaForgeNavigation.Tick(1f / 60f);
                }

                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
                }

                public static void AMoveStopsAtTheSurfacesEdge()
                {
                    Start();
                    Frame(0f, -1f, false);
                    Check(system.currentSelectedGameObject == notNow.gameObject,
                        "Down on the dialog's last control leaves it selected, never the overlay control beneath the dialog");
                    Frame(0f, 0f, false);
                    Frame(0f, 1f, false);
                    Check(system.currentSelectedGameObject == turnOn.gameObject, "a move inside the dialog still moves");
                }

                public static void ASubmitWithAMovePressesOnlyTheSurfacesControl()
                {
                    Start();
                    Frame(0f, -1f, true);
                    Check(overlayFooter.Submitted == 0, "Down and Enter in one frame never press the overlay control beneath the dialog");
                    Check(notNow.Submitted == 1 && turnOn.Submitted == 0, "the Enter presses the dialog's control the player is on");
                }

                // Back takes the dialog away while Down stays held, and the overlay beneath claims navigation in the same frame.
                public static void AHeldDirectionStaysWithItsSurface()
                {
                    Start();
                    system.SetSelectedGameObject(turnOn.gameObject);
                    Frame(0f, -1f, false);
                    Check(system.currentSelectedGameObject == notNow.gameObject, "Down moves inside the dialog");
                    TopiaForgeDismissStack.FocusRoot = overlay;
                    dialog.SetActive(false);
                    system.SetSelectedGameObject(overlayTop.gameObject);
                    for (var frame = 0; frame < 60; frame++) Frame(0f, -1f, false);
                    Check(system.currentSelectedGameObject == overlayTop.gameObject, "the Down held for the dialog never moves the overlay");
                    Frame(0f, 0f, false);
                    Frame(0f, -1f, false);
                    Check(system.currentSelectedGameObject == overlayFooter.gameObject, "released and pressed again, Down moves the overlay");
                }

                // Unity's UI module moves the selection itself on a key the game still holds, such as one pressed before the
                // dialog opened: it sends the move to the selected control, as the kit does.
                private static void ModuleMove(Button from, MoveDirection direction)
                {
                    var data = new AxisEventData(system) { moveDir = direction };
                    ExecuteEvents.Execute(from.gameObject, data, ExecuteEvents.moveHandler);
                }

                public static void AModuleMoveStaysInsideTheSurface()
                {
                    Start();
                    ModuleMove(notNow, MoveDirection.Down);
                    Check(system.currentSelectedGameObject == notNow.gameObject,
                        "Unity's module moving Down off the dialog's last control leaves it selected, never the overlay control beneath");
                    ModuleMove(notNow, MoveDirection.Up);
                    Check(system.currentSelectedGameObject == turnOn.gameObject, "a module move inside the dialog still moves");
                    Frame(0f, 0f, false);
                    ModuleMove(turnOn, MoveDirection.Up);
                    Check(system.currentSelectedGameObject == turnOn.gameObject, "and the control it moved to keeps the next one inside too");
                }

                // A window takes navigation only while one of its controls is selected: once the module's move has taken the
                // selection out of it, it claims nothing any more, and still the move stays inside it.
                public static void AModuleMoveStaysInsideAWindowToo()
                {
                    Start();
                    TopiaForgeDismissStack.FocusRoot = null;
                    ModuleMove(notNow, MoveDirection.Down);
                    Check(system.currentSelectedGameObject == notNow.gameObject,
                        "a module move off a window's control stays in the window, though its claim ends with the move");
                }

                public static void AModuleMoveIsFreeWithNoSurfaceTakingNavigation()
                {
                    Start();
                    TopiaForgeDismissStack.FocusRoot = null;
                    Frame(0f, 0f, false);
                    ModuleMove(notNow, MoveDirection.Down);
                    Check(system.currentSelectedGameObject == overlayFooter.gameObject, "with no surface taking navigation, a move goes where Unity sends it");
                }

                // The player clicks the dialog's first button and presses Up in the same frame: the kit moves from a control
                // that was selected after the last frame.
                public static void AClickAndAMoveInOneFrameStayInside()
                {
                    Start();
                    system.SetSelectedGameObject(turnOn.gameObject);
                    Frame(0f, 1f, false);
                    Check(system.currentSelectedGameObject == turnOn.gameObject,
                        "Up on the dialog's first control, in the frame it was clicked, leaves it selected, never the overlay tab above");
                }
            }
            """;
    }
}
