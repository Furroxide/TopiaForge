using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The Unity side of what TopiaForge surfaces take from the game, checked in its sources, since the Input System filter,
    /// the kit's navigation and the surfaces need Unity to run. UiKitInputOwnershipTests runs the Unity-free core they use.
    /// </summary>
    internal static class UiKitInputOwnershipConventionTests
    {
        public static void Run()
        {
            SurfacesPushWhatTheyTake();
            EveryFirstPartyPushNamesItsClaim();
            TheKeyCaptureLeavesEscapeToTheStack();
            TheFilterHoldsWhatItTakesAtRest();
            TheFilterReturnsAtOnceAtRest();
            TheFilterStartsAndStopsWithTheKit();
            BackIsReadOnceAndSurvivesAThrowingSurface();
            TheKitStillReadsWhatItTook();
            NothingTheGameOwnsIsChanged();
            NoDeviceNameReachesALog();
            TheKitNavigatesAtTheModulesPaceAndNeverCancels();
            KitTextFieldsTakeTheKeyboard();
            AModsActionsYieldOnlyToWhatHoldsTheirControls();
            TheGamesNavigationKeysAreTakenWithTheKitsOwn();
            Console.WriteLine("UiKitInputOwnershipConventionTests passed.");
        }

        /// <summary>
        /// A mod's input action yields binding by binding to what holds each control (InputActionYield), never as a whole to a
        /// pointer over UI or a selected kit control: the ui template's own key toggled nothing at the main menu or over its
        /// window while either counted as focus. The game's own UI holds the keys only while one of its text fields has the
        /// keyboard: Unity keeps the game's menu panel selected after a click into it, and every mod key waited in the level.
        /// </summary>
        private static void AModsActionsYieldOnlyToWhatHoldsTheirControls()
        {
            var service = Manager("UnityInputService.cs");
            Require(Between(service, "public void Sample()", "private static UiInputFocus UiFocus()"), "var focus = UiFocus();",
                "a frame's focus is read once for every action");
            var focus = Between(service, "private static UiInputFocus UiFocus()", "public void Dispose()");
            Require(focus, "TopiaForgeGameTextFocus.Current.HasKeyboard(selected)",
                "only one of the game's own text fields with the keyboard holds the keys");
            Assert(!focus.Contains("IsKitObject", StringComparison.Ordinal) && !focus.Contains("activeInHierarchy", StringComparison.Ordinal),
                "a game control merely selected no longer holds them");
            Require(Kit("Runtime", "TopiaForgeGameTextFocus.cs"), "new TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus.Find())",
                "the running game's UI Toolkit is read through the kit's reflection reader, so the loader takes no UIElements reference");
            Require(focus, "TopiaForgeDismissStack.HoldsFocus || TopiaForgeInputOwnership.KitTextFieldHasKeyboard()",
                "the overlay, a modal, a fullscreen tool, a key capture or a kit text field hold every control");
            Require(focus, "TopiaForgeDismissStack.Claims", "and the open windows hold what they claim");
            Require(focus, "TopiaForgeNavigateAction.Bindings", "mod actions yield to the same navigation bindings as the game's event filter");
            Require(service, "value = ReadBindings(definition.SuppressWhileUiFocused, focus);",
                "an action that opted out of yielding reads every binding");
            var read = Between(service, "private float ReadBindings(bool yielding, UiInputFocus focus)", "private bool TakenByTopiaForge(int index)");
            Require(read, "value += InputActionYield.Read(ref owners[index], binding, ReadBinding(binding), focus, yielding,",
                "each binding yields on its own, and its press keeps the owner it began with until its release");
            Require(read, "yielding && TakenByTopiaForge(index));", "a press TopiaForge took from the game stays TopiaForge's for mods too");
            var taken = Between(service, "private bool TakenByTopiaForge(int index)", "private static int[] Takeable(");
            Require(taken, "TopiaForgeInputOwnership.KeyTaken(control)", "a key TopiaForge took is looked up by the filter's own number");
            Require(taken, "TopiaForgeInputOwnership.PadButtonTaken(control == TakeableBack)", "and a controller's B or A");
            Require(Between(service, "private static int[] Takeable(", "private static List<InputBinding> CopyBindings("),
                "TopiaForgeInputOwnership.KeyNumber(binding.Control)", "each key binding is numbered once, as the filter numbers keys");
            var rebind = Between(service, "public OperationResult<bool> Rebind(", "public OperationResult<bool> ResetBindings()");
            Require(rebind, "owners = new InputPressOwner[copy.Count];", "a rebind starts every binding afresh");
            Require(rebind, "takeable = Takeable(copy);", "and numbers its keys again");
            Require(Between(Ownership(), "internal static int KeyNumber(string name)", "internal static bool SharedKeysReachTheGame"),
                "Enum.TryParse(TopiaForgeNavigationBindings.InputSystemName(name), true, out InputSystemKey key)",
                "an SDK key name is numbered through the Input System's own name for it");
            Assert(!service.Contains("uiFocused ? 0f", StringComparison.Ordinal), "no action yields as a whole any more");

            var kitObject = Between(Ownership(), "internal static bool IsKitObject(GameObject target)", "internal static bool KitTextFieldHasKeyboard()");
            Require(kitObject, "target.activeInHierarchy", "an inactive object is nobody's");
            Require(kitObject, "order >= TopiaForgeLayerBands.DefaultHudBase && order < TopiaForgeLayerBands.DefaultCeiling",
                "a kit object is one whose canvas sits in the kit's own sorting bands");
        }

        /// <summary>
        /// While a surface claims navigation, the filter takes whatever else the game's navigate action binds, as it takes the
        /// arrows: W, A, S and D walked the native main menu and Unity's module moved the selection on them under an open
        /// overlay. The kit follows that action once a frame, reading it only, and no move leaves the surface, whoever sends it.
        /// UiKitInputOwnershipTests runs the follower and the guard against contract doubles.
        /// </summary>
        private static void TheGamesNavigationKeysAreTakenWithTheKitsOwn()
        {
            var filter = Ownership();
            var keyboard = Between(filter, "private static void FilterKeyboard(", "private static void Examine(Keyboard keyboard");
            var claimed = keyboard.IndexOf("if ((claims & TopiaForgeInputClaim.Navigation) == 0)", StringComparison.Ordinal);
            var gameKeys = keyboard.IndexOf("Examine(keyboard, keyboard[(InputSystemKey)bindings.KeyAt(index)], eventPtr, claims, frame);", StringComparison.Ordinal);
            Assert(claimed >= 0 && gameKeys > claimed, "the game's navigation keys are examined only while a surface claims navigation");
            Require(keyboard, "for (var index = 0; index < bindings.KeyCount; index++)", "every one of them, none while the game's menus do not listen");
            var examine = Between(filter, "private static void Examine(Keyboard keyboard", "private static void FilterGamepad(");
            Require(examine, "bindings.Direction(code, out var x, out var y);", "each moves the way the game's binding points");
            Require(examine, "bindings.RoleOf(code)", "and plays a direction's part");
            var pad = Between(filter, "private static void FilterGamepad(", "private static void Examine(Gamepad gamepad, ButtonControl");
            Require(pad, "if (bindings.RightStickMoves || Gate.IsTaken(gamepad.deviceId, RightStick))",
                "the right stick is examined while the game's navigation binds it, and until its release once taken, so it never stays held");
            Require(pad, "Examine(gamepad, gamepad.rightStick, RightStick, bindings.FollowedRole, eventPtr, claims, frame);",
                "and plays the part the followed bindings give it, so over gameplay a window leaves it to the camera");
            var shared = Between(filter, "internal static bool SharedKeysReachTheGame", "internal static void Install()");
            Require(shared, "bindings.Listening && bindings.Gameplay", "the shared keys reach the game while its menus and its gameplay both listen");
            Require(shared, "(TopiaForgeDismissStack.Claims & (TopiaForgeInputClaim.SharedNavigation | TopiaForgeInputClaim.Keyboard)) == 0",
                "and no surface takes them");
            Require(Between(filter, "internal static void Reset()", "internal static void ForgetHeld()"), "TopiaForgeNavigateAction.Reset();",
                "the kit's teardown forgets the game's navigation");

            var update = Between(Kit("Runtime", "TopiaForgeRuntime.cs"), "private void Update()", "TopiaForgeToasts.Tick(");
            var follow = update.IndexOf("TopiaForgeNavigateAction.Follow();", StringComparison.Ordinal);
            Assert(follow >= 0 && follow < update.IndexOf("TopiaForgeNavigation.Tick(", StringComparison.Ordinal),
                "the driver follows the game's navigate action every frame, before the kit navigates");
            var follower = Kit("Runtime", "TopiaForgeNavigateAction.cs");
            Require(follower, "events.currentInputModule as InputSystemUIInputModule", "the action is the one Unity's UI module moves with");
            Require(follower, "Bindings.Listening = current != null && current.enabled;", "and only while it is enabled");
            Require(follower, "Bindings.Gameplay = current != null && GameplayListens(current);",
                "whether the game's gameplay listens too is read every frame, from its gameplay maps, never from its menu map");
            Assert(!Between(filter, "private static void OnEvent(", "private static void OnDeviceChange(").Contains("TopiaForgeNavigateAction.Follow", StringComparison.Ordinal),
                "the filter never reads the game's actions while an event passes");

            var navigation = Kit("Runtime", "TopiaForgeNavigation.cs");
            Require(navigation, "selected.AddComponent<TopiaForgeMoveGuard>();", "the control selected inside a surface carries the move guard");
            Require(navigation, "internal sealed class TopiaForgeMoveGuard : MonoBehaviour, IMoveHandler", "which sees every move sent to it");
            Require(navigation, "TopiaForgeNavigation.Contain(gameObject, eventData);", "and tells the kit who sent it");
        }

        private static void SurfacesPushWhatTheyTake()
        {
            Require(Kit("Widgets", "TopiaForgeModal.cs"), "TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Navigation, canvasRoot);",
                "a kit modal takes menu navigation from the game while it shows");
            Require(Kit("Widgets", "TopiaForgeFullscreenTool.cs"), "TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Navigation, Go);",
                "so does a fullscreen tool");
            Require(Kit("Widgets", "TopiaForgeWindow.cs"), "TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.NavigationWhileSelected, Go);",
                "a window takes it only while one of its controls is selected");
            Require(Manager("Overlay", "ManagerOverlay.cs"), "TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Navigation, root);",
                "the manager overlay takes it while it shows, so the menu beneath never moves or presses");
            Require(Kit("Widgets", "TopiaForgeKeybindField.cs"), "TopiaForgeDismissStack.Push(escape, TopiaForgeInputClaim.Keyboard, Go);",
                "a key capture takes the keyboard");
        }

        /// <summary>
        /// A surface the kit or the manager pushes says what it takes: a bare Push leaves its keys to the game. A first-party
        /// surface added later, or one a branch brings, such as voice chat's push-to-talk capture, fails here until it does.
        /// </summary>
        private static void EveryFirstPartyPushNamesItsClaim()
        {
            var bare = new Regex(@"TopiaForgeDismissStack\.Push\(\s*[A-Za-z_][A-Za-z0-9_.]*\s*\)", RegexOptions.CultureInvariant);
            var checkedFiles = 0;
            foreach (var file in FirstPartySources())
            {
                checkedFiles++;
                var match = bare.Match(File.ReadAllText(file));
                if (match.Success)
                {
                    throw new InvalidOperationException(
                        "UI input ownership convention failed: " + Path.GetFileName(file) + " pushes '" + match.Value
                        + "' without a claim; push with TopiaForgeInputClaim and the root it shows in.");
                }
            }

            Assert(checkedFiles > 100, "the kit and the manager sources were found (" + checkedFiles + " files)");
        }

        private static IEnumerable<string> FirstPartySources()
        {
            var root = Program.FindRepoRoot();
            return new[] { Path.Combine(root, "src", "TopiaForge.Mods.UnityUi"), Path.Combine(root, "src", "TopiaForge.ModManager") }
                .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
                .Where(IsSource);
        }

        private static void TheKeyCaptureLeavesEscapeToTheStack()
        {
            var field = Kit("Widgets", "TopiaForgeKeybindField.cs");
            var poll = field.Substring(field.IndexOf("internal sealed class TopiaForgeKeybindCapture", StringComparison.Ordinal));
            Assert(!poll.Contains("EscapePressedThisFrame", StringComparison.Ordinal),
                "the capture's own poll never reads Escape, so one Escape cannot both cancel it and close the window around it");
            Require(field, "escape = new TopiaForgeKeyCapture(() => CompleteCapture(TopiaForgeKey.None));",
                "the stack's Escape ends the capture without a key");
            Require(Between(field, "internal void CompleteCapture", "Repaint();"), "TopiaForgeDismissStack.Remove(escape);",
                "however a capture ends, it leaves the stack");
            Require(Between(poll, "private void OnDisable()", "private void Update()"), "Field.CompleteCapture(TopiaForgeKey.None);",
                "a field hidden or destroyed mid-capture ends the capture, so it never keeps the keyboard");
        }

        private static void TheFilterHoldsWhatItTakesAtRest()
        {
            var filter = Ownership();
            var holds = new Regex(@"if \(Gate\.Take\([^;]*?\)\)\s*\{\s*\w+\.ResetToDefaultStateInEvent\(eventPtr\);", RegexOptions.CultureInvariant);
            Assert(holds.Matches(filter).Count == 3, "each key, button and direction the gate takes is held at rest in its event (3 sites)");
            Assert(Regex.Matches(filter, @"ResetToDefaultStateInEvent\(").Count == 3, "and nothing else in an event is changed");
            Require(filter, "key.ReadValueFromEvent(eventPtr, out float value)", "a key's value is read from the event itself");
            Require(filter, "var before = key.isPressed ? 1f : 0f;", "and its value before it from the device, which a taken press never reaches");
        }

        private static void TheFilterReturnsAtOnceAtRest()
        {
            var onEvent = Between(Ownership(), "private static void OnEvent(", "private static void OnDeviceChange(");
            var other = onEvent.IndexOf("if (stopped || kind == TopiaForgeInputDevice.Other)", StringComparison.Ordinal);
            var claims = onEvent.IndexOf("TopiaForgeDismissStack.Claims", StringComparison.Ordinal);
            var examines = onEvent.IndexOf("if (!Gate.Examines(kind, claims))", StringComparison.Ordinal);
            var filters = onEvent.IndexOf("FilterKeyboard(", StringComparison.Ordinal);
            Assert(other >= 0 && other < claims, "a mouse, pen or touch event returns before anything is looked up");
            Assert(claims < examines && examines < filters, "and every other event returns at once while nothing is claimed or held");
        }

        private static void TheFilterStartsAndStopsWithTheKit()
        {
            var filter = Ownership();
            Require(Between(filter, "internal static void Install()", "internal static void Reset()"), "InputSystem.onEvent += Filter;",
                "the filter starts listening as the kit's driver starts");
            var reset = Between(filter, "internal static void Reset()", "internal static void ForgetHeld()");
            Require(reset, "InputSystem.onEvent -= Filter;", "and stops as it stops");
            Require(reset, "InputSystem.onDeviceChange -= DeviceChanged;", "device changes too");
            Require(reset, "Gate.ForgetAll();", "letting go of every taken press");
            Require(Between(filter, "private static void Stop(", "internal sealed class TopiaForgeTextEntry"), "Gate.ForgetAll();",
                "a failure lets go of every taken press too, so the game keeps its input");

            var runtime = Kit("Runtime", "TopiaForgeRuntime.cs");
            Require(Between(runtime, "private void OnEnable()", "private void OnDisable()"), "TopiaForgeInputOwnership.Install();",
                "the driver starts the filter");
            Require(Between(runtime, "private void OnDisable()", "private void OnApplicationFocus("), "TopiaForgeInputOwnership.Reset();",
                "and stops it, so no filter outlives the kit");
            Require(Between(runtime, "private void OnApplicationFocus(", "private void OnDestroy()"), "TopiaForgeInputOwnership.ForgetHeld();",
                "a lost focus lets go of every press, since its release never arrives");
            var update = Between(runtime, "private void Update()", "TopiaForgeToasts.Tick(");
            Assert(update.IndexOf("TopiaForgeDismissStack.TickEscape();", StringComparison.Ordinal)
                < update.IndexOf("TopiaForgeNavigation.Tick(Time.unscaledDeltaTime);", StringComparison.Ordinal)
                && update.Contains("TopiaForgeNavigation.Tick(", StringComparison.Ordinal),
                "Back runs before the kit's navigation in each frame");

            var shutdown = Between(Kit("TopiaForgeUi.cs"), "public static void Shutdown()", "internal static void OnHostDisposed(");
            Require(shutdown, "TopiaForgeDismissStack.Reset();", "the kit's teardown empties the stack");
            Require(shutdown, "TopiaForgeInputOwnership.Reset();", "stops the filter");
            Require(shutdown, "TopiaForgeNavigation.Reset();", "and the kit's navigation");
            Require(Between(Kit("Runtime", "TopiaForgeDismissStack.cs"), "internal static void Reset()", "}"), "Order.Clear();",
                "the stack's teardown forgets every surface and a held Back");
        }

        private static void BackIsReadOnceAndSurvivesAThrowingSurface()
        {
            var stack = Kit("Runtime", "TopiaForgeDismissStack.cs");
            var tick = Between(stack, "internal static void TickEscape()", "private static string? OwnerOf(");
            Require(tick, "Order.TakeOnBack(TopiaForgeInput.BackHeld(), TopiaForgeInput.BackPressedThisFrame());",
                "Back is one press however many backends report it, and its surface leaves the stack before it closes");
            Assert(Regex.IsMatch(tick, @"\btry\s*\{\s*top\.Dismiss\(\);\s*\}\s*catch \(Exception exception\)"),
                "a surface that throws as it closes is logged and the frame goes on");
            Assert(!stack.Contains("The game\n    /// still sees the key", StringComparison.Ordinal)
                && !stack.Contains("still sees the key (documented limitation", StringComparison.Ordinal),
                "the stack no longer documents that the game sees the key");

            var input = Kit("Runtime", "TopiaForgeInput.cs");
            var held = Between(input, "internal static bool BackHeld()", "internal static bool BackPressedThisFrame()");
            Require(held, "Input.GetKey(KeyCode.Escape)", "a held Escape counts through legacy input");
            Require(held, "TopiaForgeInputOwnership.BackHeld", "and through the presses TopiaForge took, a controller's B among them");
            var pressed = Between(input, "internal static bool BackPressedThisFrame()", "private static void Probe()");
            Require(pressed, "Input.GetKeyDown(KeyCode.Escape)", "a pressed Escape counts through legacy input");
            Require(pressed, "TopiaForgeInputOwnership.BackPressedThisFrame", "and through the presses TopiaForge took");
        }

        /// <summary>
        /// A press TopiaForge takes never reaches the device, so every first-party read of a key's press through the Input
        /// System also reads the presses TopiaForge took: the kit's Escape check, hotkeys and key captures, and any capture
        /// added later. A mouse button is never taken.
        /// </summary>
        private static void TheKitStillReadsWhatItTook()
        {
            Require(Between(Kit("Runtime", "TopiaForgeInput.cs"), "public static bool EscapePressedThisFrame()", "internal static bool BackHeld()"),
                "TopiaForgeInputOwnership.KeyPressedThisFrame(UnityEngine.InputSystem.Key.Escape)",
                "the kit's Escape check sees an Escape TopiaForge took from the game");
            Require(Between(Kit("Runtime", "TopiaForgeHotkeys.cs"), "public static bool WasPressedThisFrame(", "private static bool IsAlwaysActive("),
                "TopiaForgeInputOwnership.KeyPressedThisFrame(mapped)",
                "and so do hotkeys and key captures, with the Input System alone");
            var read = new Regex(@"[^;{}]*\.wasPressedThisFrame\b[^;{}]*", RegexOptions.CultureInvariant);
            var reads = 0;
            foreach (var file in FirstPartySources())
            {
                var source = File.ReadAllText(file);
                if (!source.Contains(".wasPressedThisFrame", StringComparison.Ordinal))
                {
                    continue;
                }

                var code = Regex.Replace(source, @"//[^\n]*", string.Empty, RegexOptions.CultureInvariant);
                foreach (Match statement in read.Matches(code))
                {
                    if (statement.Value.Contains("Mouse.current", StringComparison.Ordinal) || statement.Value.Contains("mouse.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    reads++;
                    Assert(statement.Value.Contains("TopiaForgeInputOwnership.KeyPressedThisFrame(", StringComparison.Ordinal),
                        Path.GetFileName(file) + " reads a key's press without the presses TopiaForge took ('"
                        + Regex.Replace(statement.Value.Trim(), @"\s+", " ") + "'); add || TopiaForgeInputOwnership.KeyPressedThisFrame(key)");
                }
            }

            Assert(reads >= 2, "the Input System's key reads were found (" + reads + ")");
        }

        private static void NothingTheGameOwnsIsChanged()
        {
            var sources = new[]
            {
                Ownership(),
                Kit("Runtime", "TopiaForgeNavigation.cs"),
                Kit("Runtime", "TopiaForgeNavigateAction.cs"),
                Kit("Runtime", "TopiaForgeGameTextFocus.cs"),
                Kit("Core", "TopiaForgeUiToolkitFocus.cs"),
                Kit("Runtime", "TopiaForgeDismissStack.cs"),
                Manager("Overlay", "ManagerOverlay.cs"),
            };
            foreach (var forbidden in new[]
            {
                ".Disable()", ".Enable()", "SwitchCurrentActionMap", "ApplyBindingOverride", "actionsAsset", "DeactivateInput",
                "ActivateInput", "bindingMask", ".devices =", "handled = true", "QueueStateEvent", "InputActionMap",
            })
            {
                Assert(sources.All(source => !source.Contains(forbidden, StringComparison.Ordinal)),
                    "no action map, module, binding or device of the game's is changed ('" + forbidden + "'), so nothing has to be restored");
            }
        }

        private static void NoDeviceNameReachesALog()
        {
            foreach (var source in new[] { Ownership(), Kit("Runtime", "TopiaForgeNavigateAction.cs") })
            {
                var logs = 0;
                for (var at = source.IndexOf("TopiaForgeLog.", StringComparison.Ordinal); at >= 0; at = source.IndexOf("TopiaForgeLog.", at + 1, StringComparison.Ordinal))
                {
                    // The whole statement, however many lines its message takes.
                    var statement = source.Substring(at, source.IndexOf(';', at) - at);
                    logs++;
                    foreach (var forbidden in new[] { "device", "displayName", "description", "product", ".Message", "exception)" })
                    {
                        Assert(!statement.Contains(forbidden, StringComparison.Ordinal),
                            "the filter logs no device name or exception text, which can carry a person's name ('" + forbidden + "')");
                    }
                }

                Assert(logs > 0, "the failure logs were found");
                Require(source, "exception.GetType().Name", "it logs only a failure's type");
            }
        }

        private static void TheKitNavigatesAtTheModulesPaceAndNeverCancels()
        {
            var navigation = Kit("Runtime", "TopiaForgeNavigation.cs");
            Require(navigation, "system.currentInputModule as InputSystemUIInputModule", "the kit navigates only for the Input System's UI module");
            Require(navigation, "module.moveRepeatDelay", "with that module's own repeat delay");
            Require(navigation, "module.moveRepeatRate", "and repeat rate");
            Assert(!Regex.IsMatch(navigation, @"\d\.\d+f"), "and no timing of its own");
            Require(navigation, "ExecuteEvents.moveHandler", "it sends moves");
            Require(navigation, "ExecuteEvents.submitHandler", "and submits");
            Assert(!navigation.Contains("cancelHandler", StringComparison.Ordinal), "never a cancel: Back stays the dismiss stack's");
            Require(navigation, "TopiaForgeDismissStack.FocusRoot", "and only inside the topmost surface that takes navigation");
            Require(navigation, "module.moveRepeatRate,\n                root);",
                "the driver sees each surface handover even when navigation stays claimed throughout");
        }

        private static void KitTextFieldsTakeTheKeyboard()
        {
            Require(Kit("Widgets", "TopiaForgeInputField.cs"), "Go.AddComponent<TopiaForgeTextEntry>();", "every kit text field is marked");
            Require(Kit("Widgets", "TopiaForgeInputField.cs"), "input = Go.AddComponent<TopiaForgeTextInput>();",
                "and starts typing only once the keys held as it was reached are released");
            Require(Between(Ownership(), "internal static bool AnyKeyHeld", "internal static bool KeyTaken("),
                "Gate.HoldsAnyOn(TopiaForgeInputDevice.Keyboard)", "a key TopiaForge took, which the device reads at rest, counts as held");
            var focus = Between(Ownership(), "internal static bool KitTextFieldHasKeyboard()", "private static void Stop(");
            Require(focus, "TryGetComponent<TopiaForgeTextEntry>(out _)", "only a kit text field takes the keyboard, never the game's own");
            Require(focus, "selectionText.isFocused", "and only while it has the keyboard");
            Require(Between(Ownership(), "private static void OnEvent(", "private static void OnDeviceChange("),
                "if (kind == TopiaForgeInputDevice.Keyboard && KitTextFieldHasKeyboard())", "it takes keys, never a controller");
        }

        private static string Ownership() => Kit("Runtime", "TopiaForgeInputOwnership.cs");

        private static string Kit(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi" }.Concat(parts).ToArray()));

        private static string Manager(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { Program.FindRepoRoot(), "src", "TopiaForge.ModManager" }.Concat(parts).ToArray()));

        private static string Between(string source, string start, string end)
        {
            var from = source.IndexOf(start, StringComparison.Ordinal);
            Assert(from >= 0, "'" + start + "' is in the source");
            var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert(to > from, "'" + end + "' follows '" + start + "'");
            return source.Substring(from, to - from);
        }

        private static bool IsSource(string file)
        {
            var separator = Path.DirectorySeparatorChar;
            return !file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                && !file.Contains(separator + "bin" + separator, StringComparison.Ordinal);
        }

        private static void Require(string source, string text, string message)
        {
            Assert(source.Contains(text, StringComparison.Ordinal), message + " ('" + text + "')");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("UI input ownership convention failed: " + message);
            }
        }
    }
}
