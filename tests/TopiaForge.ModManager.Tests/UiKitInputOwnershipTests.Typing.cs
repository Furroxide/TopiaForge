using System.IO;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        /// <summary>
        /// A kit text field asked to start while a key is held waits until every key is released, so a held key that reaches
        /// it types nothing, and a field reached with no key held starts at once, as it always did.
        /// </summary>
        private static void AKitTextFieldStartsOnceTheKeysHeldAsItIsReachedAreReleased()
        {
            var start = new TopiaForgeTypingStart();
            Assert(start.Ask(keysHeld: false), "reached with no key held, the field starts at once");
            AssertEqual(start.Tick(false, true, true), TopiaForgeTypingStep.Pass, "and nothing waits");

            Assert(!start.Ask(keysHeld: true), "reached while a key is held, it does not start");
            for (var frame = 0; frame < 30; frame++)
            {
                AssertEqual(start.Tick(true, true, false), TopiaForgeTypingStep.Pass, "and nothing starts while the key is held (frame " + frame + ")");
            }

            Assert(!start.Ask(keysHeld: false), "nor when it is selected again before the key is released");
            AssertEqual(start.Tick(false, true, false), TopiaForgeTypingStep.Start, "once every key is released, it starts");
            AssertEqual(start.Tick(false, true, true), TopiaForgeTypingStep.Pass, "once");

            start.Ask(keysHeld: true);
            AssertEqual(start.Tick(false, false, false), TopiaForgeTypingStep.Pass, "a field the selection left meanwhile never starts");
            Assert(start.Ask(keysHeld: false), "and is asked afresh the next time");

            start.Submitted(keysHeld: true);
            AssertEqual(start.Tick(true, true, false), TopiaForgeTypingStep.Wait, "a start the field marked itself, as a submit does, waits too");
            AssertEqual(start.Tick(false, true, false), TopiaForgeTypingStep.Start, "and runs once the keys are released");
            start.Submitted(keysHeld: false);
            AssertEqual(start.Tick(false, true, false), TopiaForgeTypingStep.Pass, "a submit with no key held runs as the field's own");

            start.Ask(keysHeld: true);
            AssertEqual(start.Tick(true, true, true), TopiaForgeTypingStep.Pass, "a field started some other way meanwhile has nothing waiting");
            Assert(start.Ask(keysHeld: false), "any more");
            start.Ask(keysHeld: true);
            start.Reset();
            Assert(!start.Waiting && start.Ask(keysHeld: false), "a hidden field forgets the start that waited");
        }

        /// <summary>
        /// The kit's text input (TopiaForgeTextInput), compiled unchanged against a small contract double of TextMesh Pro's
        /// field, since it needs Unity to run. TextMesh Pro types what the game window's own key messages carry, which the
        /// kit's Input System filter never sees: a W held as navigation reached the field selected all of its text, and the
        /// W's auto-repeat replaced it.
        /// </summary>
        private static void TheKitsTextInputTypesNothingFromAKeyHeldAsItIsReached()
        {
            var kit = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            RunProbe("TextInputProbe", "TextInputScenarios",
                new[]
                {
                    File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeTypingStart.cs")),
                    File.ReadAllText(Path.Combine(kit, "Runtime", "TopiaForgeTextInput.cs")),
                    TextInputContract,
                    TextInputScenarios,
                },
                "AHeldKeyThatReachesTheFieldTypesNothing", "AMoveThatPassesOverTheFieldLeavesItAlone", "AMoveTheKitUndidLeavesNoFieldTyping",
                "AFieldReachedWithNoKeyHeldStartsAtOnce", "AClickWhileAKeyIsHeldWaitsForItsRelease", "ASubmitWhileItsKeyIsHeldWaitsForItsRelease",
                "AFieldHiddenWhileItWaitsForgetsIt", "AClickOnASelectedIdleFieldWaitsForHeldKeys");
        }

        // A contract double, not TextMesh Pro's code: what the kit's text input relies on of TextMesh Pro's field, as the game's
        // own carries it. A selection, a click or a submit marks a start, which runs in the field's late update and selects the
        // field again if the selection left it meanwhile; once started, the field types what the window's key messages carry.
        private const string TextInputContract = """
            using System.Collections.Generic;

            namespace UnityEngine
            {
                public class Object { }
                public class Component : Object { public GameObject gameObject = null!; }
                public class Behaviour : Component { }
                public class MonoBehaviour : Behaviour { }
                public class GameObject : Object
                {
                    public readonly List<Component> Components = new List<Component>();
                    public T AddComponent<T>() where T : Component, new()
                    {
                        var component = new T { gameObject = this };
                        Components.Add(component);
                        return component;
                    }
                }
            }
            namespace UnityEngine.EventSystems
            {
                public class BaseEventData { }
                public class PointerEventData : BaseEventData
                {
                    public enum InputButton { Left = 0, Right = 1, Middle = 2 }
                    public InputButton button { get; set; }
                }
                public class EventSystem : Behaviour
                {
                    public static EventSystem? current { get; set; }
                    public GameObject? currentSelectedGameObject { get; private set; }
                    public void SetSelectedGameObject(GameObject? selected)
                    {
                        if (ReferenceEquals(selected, currentSelectedGameObject)) return;
                        var previous = currentSelectedGameObject;
                        currentSelectedGameObject = selected;
                        if (previous != null) foreach (var component in previous.Components) if (component is UnityEngine.UI.Selectable left) left.OnDeselect(new BaseEventData());
                        if (selected != null) foreach (var component in selected.Components) if (component is UnityEngine.UI.Selectable reached) reached.OnSelect(new BaseEventData());
                    }
                }
            }
            namespace UnityEngine.UI
            {
                public class Selectable : MonoBehaviour
                {
                    public virtual void OnSelect(UnityEngine.EventSystems.BaseEventData eventData) { }
                    public virtual void OnDeselect(UnityEngine.EventSystems.BaseEventData eventData) { }
                    protected virtual void OnDisable() { }
                    public void Hide() => OnDisable();
                }
            }
            namespace TMPro
            {
                public class TMP_InputField : UnityEngine.UI.Selectable
                {
                    private bool pending;
                    private bool focused;
                    private bool activateOnSelect = true;
                    public string text = string.Empty;
                    public bool isFocused => focused;
                    public virtual bool shouldActivateOnSelect { get => activateOnSelect; set => activateOnSelect = value; }
                    public override void OnSelect(UnityEngine.EventSystems.BaseEventData eventData) { if (shouldActivateOnSelect) ActivateInputField(); }
                    public override void OnDeselect(UnityEngine.EventSystems.BaseEventData eventData) { focused = false; }
                    public virtual void OnPointerClick(UnityEngine.EventSystems.PointerEventData eventData)
                    {
                        if (eventData.button == UnityEngine.EventSystems.PointerEventData.InputButton.Left) ActivateInputField();
                    }
                    public virtual void OnSubmit(UnityEngine.EventSystems.BaseEventData eventData) { if (!focused) pending = true; }
                    public void ActivateInputField() => pending = true;
                    public void DeactivateInputField() => focused = false;
                    protected virtual void LateUpdate()
                    {
                        if (pending && !focused)
                        {
                            var events = UnityEngine.EventSystems.EventSystem.current!;
                            if (!ReferenceEquals(events.currentSelectedGameObject, gameObject)) events.SetSelectedGameObject(gameObject);
                            focused = true;
                        }

                        pending = false;
                    }
                    protected override void OnDisable() { focused = false; pending = false; }
                    public void KeyMessage(char character) { if (focused) text += character; }
                    public void RunLateUpdate() => LateUpdate();
                }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                internal static class TopiaForgeInputOwnership { internal static bool AnyKeyHeld; }
            }
            """;

        private const string TextInputScenarios = """
            using System;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;

            public static class TextInputScenarios
            {
                private static EventSystem system = null!;
                private static TopiaForgeTextInput field = null!;
                private static GameObject toggle = null!;

                // A window's "Show notifications" toggle is selected; its Greeting field, above it, says Hello.
                private static void Start()
                {
                    system = new EventSystem();
                    EventSystem.current = system;
                    toggle = new GameObject();
                    toggle.AddComponent<UnityEngine.UI.Selectable>();
                    field = new GameObject().AddComponent<TopiaForgeTextInput>();
                    field.text = "Hello";
                    TopiaForgeInputOwnership.AnyKeyHeld = false;
                    system.SetSelectedGameObject(toggle);
                }

                // One frame: the window's key messages, then the field's late update.
                private static void Frame(string typed = "")
                {
                    foreach (var character in typed) field.KeyMessage(character);
                    field.RunLateUpdate();
                }

                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
                }

                public static void AHeldKeyThatReachesTheFieldTypesNothing()
                {
                    Start();
                    TopiaForgeInputOwnership.AnyKeyHeld = true;
                    system.SetSelectedGameObject(field.gameObject);
                    for (var frame = 0; frame < 30; frame++) Frame("w");
                    Check(field.text == "Hello" && !field.isFocused, "a W held as navigation reached the field types nothing, its auto-repeat included (" + field.text + ")");
                    TopiaForgeInputOwnership.AnyKeyHeld = false;
                    Frame();
                    Check(field.isFocused && field.text == "Hello", "once it is released, the field takes the keyboard");
                    Frame("w");
                    Check(field.text == "Hellow", "and the next fresh press types");
                }

                public static void AMoveThatPassesOverTheFieldLeavesItAlone()
                {
                    Start();
                    TopiaForgeInputOwnership.AnyKeyHeld = true;
                    system.SetSelectedGameObject(field.gameObject);
                    Frame("w");
                    system.SetSelectedGameObject(toggle);
                    Frame("w");
                    TopiaForgeInputOwnership.AnyKeyHeld = false;
                    for (var frame = 0; frame < 5; frame++) Frame();
                    Check(!field.isFocused && system.currentSelectedGameObject == toggle, "a held key's repeated move past the field never leaves it typing");
                    Check(field.text == "Hello", "nor types into it");
                }

                public static void AMoveTheKitUndidLeavesNoFieldTyping()
                {
                    Start();
                    TopiaForgeInputOwnership.AnyKeyHeld = true;
                    system.SetSelectedGameObject(field.gameObject);
                    system.SetSelectedGameObject(toggle);
                    for (var frame = 0; frame < 5; frame++) Frame("w");
                    TopiaForgeInputOwnership.AnyKeyHeld = false;
                    for (var frame = 0; frame < 5; frame++) Frame();
                    Check(system.currentSelectedGameObject == toggle, "a move TopiaForge undid in its own frame never comes back to the field");
                    Check(!field.isFocused && field.text == "Hello", "which never types");
                }

                public static void AFieldReachedWithNoKeyHeldStartsAtOnce()
                {
                    Start();
                    system.SetSelectedGameObject(field.gameObject);
                    Frame();
                    Check(field.isFocused, "with no key held, the field takes the keyboard in the frame it is reached");
                    Frame("hi");
                    Check(field.text == "Hellohi", "and types at once");
                }

                public static void AClickWhileAKeyIsHeldWaitsForItsRelease()
                {
                    Start();
                    TopiaForgeInputOwnership.AnyKeyHeld = true;
                    system.SetSelectedGameObject(field.gameObject);
                    field.OnPointerClick(new PointerEventData { button = PointerEventData.InputButton.Left });
                    for (var frame = 0; frame < 10; frame++) Frame("w");
                    Check(!field.isFocused && field.text == "Hello", "a click while W is held, walking, waits for W's release");
                    TopiaForgeInputOwnership.AnyKeyHeld = false;
                    Frame();
                    Check(field.isFocused, "then the field takes the keyboard");
                }

                // A completed edit keeps the selection, so clicking again has no OnSelect to guard the start.
                public static void AClickOnASelectedIdleFieldWaitsForHeldKeys()
                {
                    Start();
                    system.SetSelectedGameObject(field.gameObject);
                    Frame();
                    field.DeactivateInputField();
                    Frame();
                    Check(!field.isFocused && system.currentSelectedGameObject == field.gameObject, "the completed edit keeps the selection without the keyboard");
                    TopiaForgeInputOwnership.AnyKeyHeld = true;
                    field.OnPointerClick(new PointerEventData { button = PointerEventData.InputButton.Left });
                    for (var frame = 0; frame < 10; frame++) Frame("w");
                    Check(!field.isFocused && field.text == "Hello", "clicking an already-selected idle field waits for the held key without a new selection event");
                    TopiaForgeInputOwnership.AnyKeyHeld = false;
                    Frame();
                    Check(field.isFocused && field.text == "Hello", "releasing the key starts the clicked field without typing it");
                    Frame("w");
                    Check(field.text == "Hellow", "the next fresh key types into the field");
                }

                // Enter ended an edit, so the field keeps the selection without the keyboard; Enter again starts it.
                public static void ASubmitWhileItsKeyIsHeldWaitsForItsRelease()
                {
                    Start();
                    system.SetSelectedGameObject(field.gameObject);
                    Frame();
                    field.DeactivateInputField();
                    Frame();
                    TopiaForgeInputOwnership.AnyKeyHeld = true;
                    Check(!field.isFocused && system.currentSelectedGameObject == field.gameObject, "the field is selected and does not have the keyboard");
                    field.OnSubmit(new BaseEventData());
                    for (var frame = 0; frame < 10; frame++) Frame("\r");
                    Check(!field.isFocused, "an Enter held after it asked the field to start does not start it");
                    TopiaForgeInputOwnership.AnyKeyHeld = false;
                    Frame();
                    Check(field.isFocused, "until it is released");
                }

                public static void AFieldHiddenWhileItWaitsForgetsIt()
                {
                    Start();
                    TopiaForgeInputOwnership.AnyKeyHeld = true;
                    system.SetSelectedGameObject(field.gameObject);
                    field.Hide();
                    TopiaForgeInputOwnership.AnyKeyHeld = false;
                    Frame();
                    Check(!field.isFocused, "a field hidden while it waited does not start when the keys are released");
                }
            }
            """;
    }
}
