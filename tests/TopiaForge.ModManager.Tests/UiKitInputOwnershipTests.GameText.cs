using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        /// <summary>
        /// Whether the game's own UI is taking typing (TopiaForgeGameTextFocus), compiled unchanged with its UI Toolkit reader
        /// against a small Unity contract double. Unity selects the game's menu panel on a click into it and keeps it selected
        /// after the menu closes, so a click on RESUME GAME held every mod key through the level: only a text field with the
        /// keyboard holds them now, the game's reply field to a robot among them.
        /// </summary>
        private static void OnlyAGameTextFieldWithTheKeyboardHoldsTheKeys()
        {
            var kit = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            RunProbe("GameTextProbe", "GameTextScenarios",
                new[]
                {
                    File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeUiToolkitFocus.cs")),
                    File.ReadAllText(Path.Combine(kit, "Runtime", "TopiaForgeGameTextFocus.cs")),
                    GameTextContract,
                    GameTextScenarios,
                },
                "AClosedMenuHoldsNothing", "AFocusedMenuButtonHoldsNothing", "TheGamesReplyFieldHoldsTheKeys",
                "AGameButtonHoldsNothing", "AGameUguiTextFieldHoldsTheKeysOnlyWhileFocused", "AKitControlOrALeftoverHoldsNothing",
                "APanelThatCannotBeReadHoldsTheKeys", "AUiToolkitThatCannotBeResolvedBreaksNothing", "TheSelectionIsLookedAtAgainWhenItChanges",
                "AThrowingPanelReadHoldsKeysAndRecovers");
        }

        /// <summary>
        /// The kit reads UI Toolkit by name, so the names are checked against the game's own Unity assemblies: the panel
        /// handler (in Unity UI since Unity 6), its panel, the focus controller, the focused element, an element's parent, and
        /// TextInputBaseField, which TextField derives from.
        /// </summary>
        private static void TheGamesUiToolkitHasWhatTheKitReads()
        {
            WithGameAssemblies(type =>
            {
                var toolkit = TopiaForgeUiToolkitFocus.Find(type);
                Assert(toolkit.HandlerType?.FullName == "UnityEngine.UIElements.PanelEventHandler", "the game's Unity has UI Toolkit's panel handler");
                Assert(toolkit.Readable, "and every member a panel's focus is read through");
                var textField = type("UnityEngine.UIElements.TextField, UnityEngine.UIElementsModule");
                var baseField = textField?.BaseType;
                Assert(baseField != null && baseField.IsGenericType
                    && baseField.GetGenericTypeDefinition().FullName == "UnityEngine.UIElements.TextInputBaseField`1",
                    "and its TextField, the game's reply field's base, derives from TextInputBaseField");
            });
        }

        /// <summary>
        /// Runs <paramref name="use"/> with a resolver of assembly-qualified type names into the game's own reference
        /// assemblies, loaded apart from the harness and unloaded afterwards.
        /// </summary>
        private static void WithGameAssemblies(Action<Func<string, Type?>> use)
        {
            var managed = typeof(UiKitInputOwnershipTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "TopiaForge.RobotopiaManagedDir").Value;
            Assert(!string.IsNullOrEmpty(managed) && Directory.Exists(managed), "the game's reference assemblies are at hand");
            var context = new AssemblyLoadContext("GameUnityProbe", isCollectible: true);
            context.Resolving += (loader, name) =>
            {
                var path = Path.Combine(managed!, name.Name + ".dll");
                return File.Exists(path) ? loader.LoadFromAssemblyPath(path) : null;
            };

            try
            {
                use(qualified =>
                {
                    var comma = qualified.IndexOf(',');
                    var assembly = context.LoadFromAssemblyName(new AssemblyName(qualified.Substring(comma + 1).Trim()));
                    return assembly.GetType(qualified.Substring(0, comma).Trim(), throwOnError: false);
                });
            }
            finally
            {
                context.Unload();
            }
        }

        // A contract double, not Unity's code: what TopiaForgeGameTextFocus and its reader touch. UI Toolkit's types carry the
        // names and shapes the reader looks for: a panel handler's panel, its focus controller and focused element, and an
        // element's parent.
        private const string GameTextContract = """
            using System;
            using System.Collections.Generic;

            namespace UnityEngine
            {
                public class Object { }
                public class Component : Object
                {
                    public GameObject gameObject = null!;
                }
                public class GameObject : Object
                {
                    private readonly List<Component> components = new List<Component>();
                    public GameObject(string name) { this.name = name; }
                    public string name;
                    public bool activeInHierarchy { get; set; } = true;
                    public bool InKitBands { get; set; }
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
                    public Component GetComponent(Type type)
                    {
                        if (!typeof(Component).IsAssignableFrom(type) && !type.IsInterface)
                            throw new ArgumentException("GetComponent requires that the requested component derives from MonoBehaviour or Component or is an interface.");
                        foreach (var candidate in components) if (type.IsInstanceOfType(candidate)) return candidate;
                        return null!;
                    }
                }
            }
            namespace UnityEngine.UI
            {
                public class InputField : UnityEngine.Component { public bool isFocused { get; set; } }
            }
            namespace TMPro
            {
                public class TMP_InputField : UnityEngine.Component { public bool isFocused { get; set; } }
            }
            namespace UnityEngine.UIElements
            {
                public class Focusable { }
                public class VisualElement : Focusable
                {
                    public VisualElement? parent { get; private set; }
                    public T Add<T>(T child) where T : VisualElement { child.parent = this; return child; }
                }
                public class FocusController { public Focusable? focusedElement { get; set; } }
                public interface IPanel { FocusController focusController { get; } }
                public sealed class RuntimePanel : IPanel { public FocusController focusController { get; } = new FocusController(); }
                public class PanelEventHandler : UnityEngine.Component { public IPanel? panel { get; set; } }
                public class ThrowingPanelEventHandler : PanelEventHandler
                {
                    public bool Throws;
                    public new IPanel? panel => Throws ? throw new InvalidOperationException("Panel unavailable") : base.panel;
                }
                public class TwicePanelEventHandler : PanelEventHandler { public new string panel => string.Empty; }
                public abstract class TextInputBaseField<TValueType> : VisualElement { }
                public class TextField : TextInputBaseField<string> { }
                public class IntegerField : TextInputBaseField<int> { }
                public class Button : VisualElement { }
                public class TextElement : VisualElement { }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                internal static class TopiaForgeInputOwnership
                {
                    internal static bool IsKitObject(UnityEngine.GameObject target) => target.InKitBands;
                }
            }
            """;

        private const string GameTextScenarios = """
            using System;
            using System.Collections.Generic;
            using TMPro;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UIElements;
            using UguiInputField = UnityEngine.UI.InputField;

            public static class GameTextScenarios
            {
                private static readonly Dictionary<string, Type> UiToolkit = new Dictionary<string, Type>
                {
                    ["UnityEngine.UIElements.PanelEventHandler, UnityEngine.UI"] = typeof(PanelEventHandler),
                    ["UnityEngine.UIElements.VisualElement, UnityEngine.UIElementsModule"] = typeof(VisualElement),
                    ["UnityEngine.UIElements.TextInputBaseField`1, UnityEngine.UIElementsModule"] = typeof(TextInputBaseField<>),
                };

                private static TopiaForgeGameTextFocus Focus()
                {
                    return new TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus.Find(name => UiToolkit.TryGetValue(name, out var type) ? type : null));
                }

                // The game's menus: one UI Toolkit panel, whose handler "RishPanels" Unity selects on a click into it.
                private static GameObject Menus(out RuntimePanel panel, out VisualElement root)
                {
                    var rish = new GameObject("RishPanels");
                    panel = new RuntimePanel();
                    rish.AddComponent<PanelEventHandler>().panel = panel;
                    root = new VisualElement();
                    return rish;
                }

                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
                }

                public static void AClosedMenuHoldsNothing()
                {
                    var focus = Focus();
                    var rish = Menus(out var panel, out var root);
                    panel.focusController.focusedElement = root.Add(new Button());
                    Check(!focus.HasKeyboard(rish), "a click on RESUME GAME selects the game's panel, and its focused button takes no typing");
                    panel.focusController.focusedElement = null;
                    Check(!focus.HasKeyboard(rish), "with the pause menu closed the panel stays selected, focused on nothing, and holds no key");
                }

                public static void AFocusedMenuButtonHoldsNothing()
                {
                    var focus = Focus();
                    var rish = Menus(out var panel, out var root);
                    var settings = root.Add(new VisualElement()).Add(new VisualElement()).Add(new Button());
                    panel.focusController.focusedElement = settings;
                    Check(!focus.HasKeyboard(rish), "a SETTINGS button deep in the pause menu takes no typing");
                }

                public static void TheGamesReplyFieldHoldsTheKeys()
                {
                    var focus = Focus();
                    var rish = Menus(out var panel, out var root);
                    var card = root.Add(new VisualElement());
                    var reply = card.Add(new TextField());
                    panel.focusController.focusedElement = reply;
                    Check(focus.HasKeyboard(rish), "the reply field, focused, holds every key while the player types to a robot");
                    panel.focusController.focusedElement = reply.Add(new VisualElement()).Add(new TextElement());
                    Check(focus.HasKeyboard(rish), "and so does the text element inside it, where focus lands without composite fields");
                    panel.focusController.focusedElement = root.Add(new IntegerField());
                    Check(focus.HasKeyboard(rish), "a number field takes typing too");
                    panel.focusController.focusedElement = card.Add(new TextElement());
                    Check(!focus.HasKeyboard(rish), "a label takes none");
                }

                public static void AGameButtonHoldsNothing()
                {
                    var focus = Focus();
                    Check(!focus.HasKeyboard(new GameObject("Game button")), "a game control that is no text field holds nothing");
                    Check(!focus.HasKeyboard(null), "and nothing selected holds nothing");
                }

                public static void AGameUguiTextFieldHoldsTheKeysOnlyWhileFocused()
                {
                    var focus = Focus();
                    var name = new GameObject("Save name");
                    var text = name.AddComponent<TMP_InputField>();
                    Check(!focus.HasKeyboard(name), "a game TextMesh Pro field selected but not typing holds nothing");
                    text.isFocused = true;
                    Check(focus.HasKeyboard(name), "and every key while it has the keyboard");
                    var legacy = new GameObject("Seed");
                    legacy.AddComponent<UguiInputField>().isFocused = true;
                    Check(focus.HasKeyboard(legacy), "so does a game uGUI field");
                }

                public static void AKitControlOrALeftoverHoldsNothing()
                {
                    var focus = Focus();
                    var kitField = new GameObject("Kit field") { InKitBands = true };
                    kitField.AddComponent<TMP_InputField>().isFocused = true;
                    Check(!focus.HasKeyboard(kitField), "a kit text field is the kit's own concern, never the game's");
                    var rish = Menus(out var panel, out var root);
                    panel.focusController.focusedElement = root.Add(new TextField());
                    rish.activeInHierarchy = false;
                    Check(!focus.HasKeyboard(rish), "an inactive leftover selection holds nothing");
                }

                public static void APanelThatCannotBeReadHoldsTheKeys()
                {
                    var unreadable = new TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus.Find(name => name.StartsWith("UnityEngine.UIElements.PanelEventHandler,", StringComparison.Ordinal) ? typeof(PanelEventHandler) : null));
                    var rish = Menus(out var panel, out var root);
                    panel.focusController.focusedElement = root.Add(new Button());
                    Check(unreadable.HasKeyboard(rish), "a panel whose focus cannot be read holds the keys, as every selection did, so a text field never loses them");
                    Check(!unreadable.HasKeyboard(new GameObject("Game button")), "while a game control that is no panel still holds nothing");
                    var noToolkit = new TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus.Find(_ => null));
                    Check(!noToolkit.HasKeyboard(rish), "and a Unity without UI Toolkit has no panel to hold anything");
                }

                // Nothing a later Unity could change about UI Toolkit stops a mod's input: the reads degrade, never throw.
                public static void AUiToolkitThatCannotBeResolvedBreaksNothing()
                {
                    var throwing = new TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus.Find(_ => throw new TypeLoadException("The type could not load.")));
                    var rish = Menus(out var panel, out var root);
                    panel.focusController.focusedElement = root.Add(new Button());
                    Check(!throwing.HasKeyboard(rish) && !throwing.HasKeyboard(new GameObject("Game button")), "a type that cannot load counts as no UI Toolkit");

                    var twice = new GameObject("RishPanels");
                    ((PanelEventHandler)twice.AddComponent<TwicePanelEventHandler>()).panel = panel;
                    var ambiguous = new TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus.Find(name =>
                        name.StartsWith("UnityEngine.UIElements.PanelEventHandler,", StringComparison.Ordinal) ? typeof(TwicePanelEventHandler)
                        : UiToolkit.TryGetValue(name, out var type) ? type : null));
                    Check(ambiguous.HasKeyboard(twice), "a member declared twice leaves the panel unread, so it holds the keys");

                    var notAComponent = new TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus.Find(name =>
                        name.StartsWith("UnityEngine.UIElements.PanelEventHandler,", StringComparison.Ordinal) ? typeof(VisualElement)
                        : UiToolkit.TryGetValue(name, out var type) ? type : null));
                    Check(!notAComponent.HasKeyboard(rish), "and a handler type that is no component is never looked up on the selection");
                }

                public static void TheSelectionIsLookedAtAgainWhenItChanges()
                {
                    var focus = Focus();
                    var rish = Menus(out var panel, out var root);
                    var reply = root.Add(new TextField());
                    Check(!focus.HasKeyboard(rish), "the panel focused on nothing holds nothing");
                    panel.focusController.focusedElement = reply;
                    Check(focus.HasKeyboard(rish), "its focus is read again every frame");
                    var button = new GameObject("Game button");
                    Check(!focus.HasKeyboard(button), "another selection is looked at afresh");
                    Check(focus.HasKeyboard(rish), "and so is the panel when it is selected again");
                    ((PanelEventHandler)rish.GetComponent(typeof(PanelEventHandler))).panel = new RuntimePanel();
                    Check(!focus.HasKeyboard(rish), "a panel the handler serves anew is read anew");
                }

                public static void AThrowingPanelReadHoldsKeysAndRecovers()
                {
                    var focus = new TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus.Find(name =>
                        name.StartsWith("UnityEngine.UIElements.PanelEventHandler,", StringComparison.Ordinal) ? typeof(ThrowingPanelEventHandler)
                        : UiToolkit.TryGetValue(name, out var type) ? type : null));
                    var selected = new GameObject("Game panel");
                    var handler = selected.AddComponent<ThrowingPanelEventHandler>();
                    var panel = new RuntimePanel();
                    ((PanelEventHandler)handler).panel = panel;
                    panel.focusController.focusedElement = new TextField();
                    Check(focus.HasKeyboard(selected), "a readable focused field holds keys");
                    handler.Throws = true;
                    Check(focus.HasKeyboard(selected), "a getter failure cannot throw into mod input or surrender the field's keys");
                    handler.Throws = false;
                    panel.focusController.focusedElement = null;
                    Check(!focus.HasKeyboard(selected), "the next readable closed field releases keys without changing the selection");
                    ((PanelEventHandler)handler).panel = null;
                    Check(!focus.HasKeyboard(selected), "a detached panel releases keys too");
                }
            }
            """;
    }
}
