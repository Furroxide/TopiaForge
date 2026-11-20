using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The TopiaForgeUi kit's half of intent tracking (WS3.4), checked in its sources, since its widgets need Unity to run.
    /// The player's own input on a control opens the player's action, named after the control's owner; what runs inside
    /// one, or only happens to a control, opens nothing of its own, so a programmatic close or selection stays its code's
    /// own; a deferred completion carries what was captured where it began; and every listener is accounted for.
    /// </summary>
    internal static class UiKitIntentConventionTests
    {
        // Raised by the player's own input on a control: each opens the player's action, named after the control's owner.
        private static readonly string[] PlayerCallbacks =
        {
            "Button click", "Card click", "Dropdown change", "Input change", "Input submit", "List row click", "Slider change",
            "Toggle change", "Hotkey ", "Graph selection", "Graph connection request", "Graph connection removal", "Graph node move",
            "Graph viewport pan", "Graph viewport zoom"
        };

        // Raised inside an action opened where the input arrived, or by what only happens to a control: they open nothing.
        private static readonly string[] InheritingCallbacks =
        {
            "Window Closed", "Fullscreen tool Closed", "Modal Closed", "Modal confirmation", "Modal cancellation", "Tab selection",
            "List selection", "Table selection", "Keybind change", "Shop Purchased", "Shop PurchaseFailed", "Card hover",
            "Theme Changed", "Accessibility profile changed", "List row binding", "List row created", "Lost canvas Closed"
        };

        // Listeners that run no mod code of their own, with why.
        private static readonly (string File, string Listener, string Reason)[] QuietListeners =
        {
            ("TopiaForgeInputField.cs", "UpdateClearVisibility()", "shows or hides the clear button"),
            ("TopiaForgeInputField.cs", "focused = true", "focus only happens to a control"),
            ("TopiaForgeInputField.cs", "focused = false", "and so does losing it"),
            ("TopiaForgeListView.cs", "Refresh(force: false)", "scrolling rebinds rows"),
            ("TopiaForgeKeybindField.cs", "BeginCapture", "the key the player then presses is the action"),
            ("TopiaForgeTableView.Header.cs", "ToggleSort(captured)", "sorting raises no mod callback"),
            ("TopiaForgeGraphCanvasRuntime.cs", "Select", "the canvas's handlers open the action")
        };

        public static void Run()
        {
            var kit = KitSources();
            EveryCallbackIsListed(kit);
            PlayerInputOpensTheOwnersAction(kit);
            WhatRunsInsideAnActionOpensNothing(kit);
            DeferredCompletionsCarryWhatTheyCaptured(kit);
            EveryListenerIsAccountedFor(kit);
            LeafHandlersOpenTheAction(kit);
            TheRuntimeConnectsTheKit();
            Console.WriteLine("UiKitIntentConventionTests passed.");
        }

        /// <summary>
        /// Every callback the kit raises through TopiaForgeCallbacks is in one of the lists above, so a callback a later
        /// change adds, raised from an event as the modal cancellation is, never escapes the checks that follow.
        /// </summary>
        private static void EveryCallbackIsListed(IReadOnlyDictionary<string, string> kit)
        {
            var listed = new HashSet<string>(PlayerCallbacks.Concat(InheritingCallbacks), StringComparer.Ordinal) { "Tween completion" };
            var calls = 0;
            foreach (var (file, source) in kit)
            {
                if (file.EndsWith("TopiaForgeCallbacks.cs", StringComparison.Ordinal)) continue;
                foreach (var call in new[] { "TopiaForgeCallbacks.Invoke(", "TopiaForgeCallbacks.Player(", "TopiaForgeCallbacks.Resume(" })
                {
                    for (var index = source.IndexOf(call, StringComparison.Ordinal); index >= 0; index = source.IndexOf(call, index + call.Length, StringComparison.Ordinal))
                    {
                        // The description is the call's last argument, a literal or a literal and the name it is built with.
                        var arguments = Balanced(source, index + call.Length);
                        var end = arguments.LastIndexOf('"');
                        var start = end <= 0 ? -1 : arguments.LastIndexOf('"', end - 1);
                        var description = start < 0 ? "(no description)" : arguments.Substring(start + 1, end - start - 1);
                        Require(listed.Contains(description),
                            "'" + description + "' in " + Path.GetFileName(file) + " is listed as a player callback or as one that inherits the action around it");
                        calls++;
                    }
                }
            }

            Require(calls >= listed.Count, "every listed callback is raised somewhere (" + calls + " calls for " + listed.Count + " descriptions)");
        }

        private static void PlayerInputOpensTheOwnersAction(IReadOnlyDictionary<string, string> kit)
        {
            foreach (var description in PlayerCallbacks)
            {
                var calls = CallsNamed(kit, description).ToArray();
                Require(calls.Length > 0, "the kit raises '" + description + "'");
                foreach (var (file, method) in calls)
                {
                    Require(method == "Player", "'" + description + "' in " + file + " opens the player's action through TopiaForgeCallbacks.Player (" + method + ")");
                }
            }
        }

        private static void WhatRunsInsideAnActionOpensNothing(IReadOnlyDictionary<string, string> kit)
        {
            foreach (var description in InheritingCallbacks)
            {
                var calls = CallsNamed(kit, description).ToArray();
                Require(calls.Length > 0, "the kit raises '" + description + "'");
                foreach (var (file, method) in calls)
                {
                    Require(method == "Invoke", "'" + description + "' in " + file + " opens nothing of its own (" + method + ")");
                }
            }
        }

        private static void DeferredCompletionsCarryWhatTheyCaptured(IReadOnlyDictionary<string, string> kit)
        {
            var tween = kit.Single(entry => entry.Key.EndsWith("TopiaForgeTween.cs", StringComparison.Ordinal)).Value;
            Require(Count(tween, "TopiaForgeCallbacks.Resume(intent, onDone, \"Tween completion\")") == 2, "a tween that finishes later resumes what it captured");
            Require(Count(tween, "TopiaForgeCallbacks.Invoke(onDone, \"Tween completion\")") == 2, "one that finishes at once runs inside its caller");
            Require(Count(tween, "Intent = onDone == null ? null : TopiaForgeIntent.Capture()") == 2, "both ways of starting one capture where it began");
            Require(Count(tween, "Pool[index].Intent = null;") == 4, "and every way of ending one drops what it captured");
        }

        private static void EveryListenerIsAccountedFor(IReadOnlyDictionary<string, string> kit)
        {
            foreach (var (file, source) in kit)
            {
                var name = Path.GetFileName(file);
                var index = 0;
                while ((index = source.IndexOf(".AddListener(", index, StringComparison.Ordinal)) >= 0)
                {
                    var start = index + ".AddListener(".Length;
                    var listener = Balanced(source, start);
                    index = start;
                    if (listener.Contains("TopiaForgeCallbacks.Player(", StringComparison.Ordinal) ||
                        listener.Contains("TopiaForgeIntent.PlayerAction(", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Require(QuietListeners.Any(quiet => quiet.File == name && listener.Contains(quiet.Listener, StringComparison.Ordinal)),
                        "the listener '" + listener.Trim() + "' in " + name + " opens the player's action, or is listed with why it runs no mod code");
                }
            }
        }

        private static void LeafHandlersOpenTheAction(IReadOnlyDictionary<string, string> kit)
        {
            string Source(string name) => kit.Single(entry => Path.GetFileName(entry.Key) == name).Value;
            Require(Source("TopiaForgeDismissStack.cs").Contains("using (TopiaForgeIntent.PlayerAction(OwnerOf(top)))", StringComparison.Ordinal),
                "the player's Escape dismisses as the surface's owner");
            Require(Source("TopiaForgeKeybindField.cs").Contains("using (TopiaForgeIntent.PlayerAction(Field.Host.OwnerId))", StringComparison.Ordinal),
                "the key the player presses into a keybind field is the player's action");
            Require(Source("TopiaForgeInputField.cs").Contains("TopiaForgeCallbacks.Player(input.isFocused ? Host.OwnerId : null, onChanged", StringComparison.Ordinal),
                "typing is the player's action, and a value code sets runs in that code's own scope");
            Require(Count(Source("TopiaForgeGraphCanvas.cs"), "TopiaForgeCallbacks.Player(") == 8, "every graph handler that reaches a mod opens the action");
            Require(!Source("TopiaForgeCallbacks.cs").Contains("UnityEngine", StringComparison.Ordinal) &&
                    !Source("TopiaForgeIntent.cs").Contains("UnityEngine", StringComparison.Ordinal), "the hook stays Unity-free");
        }

        private static void TheRuntimeConnectsTheKit()
        {
            var root = Program.FindRepoRoot();
            string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
            Require(Read("src", "TopiaForge.ModManager", "ModRuntime.cs").Contains("InstallIntentKitHook();", StringComparison.Ordinal),
                "the runtime connects the kit as it starts");
            Require(Read("src", "TopiaForge.ModManager", "ModRuntime.Shutdown.cs").Contains("UninstallIntentKitHook();", StringComparison.Ordinal),
                "and disconnects it as it shuts down");
            Require(Read("src", "TopiaForge.Mods.UnityUi", "TopiaForgeUi.cs").Contains("TopiaForgeIntent.Reset();", StringComparison.Ordinal),
                "the kit's teardown resets the hook");
        }

        /// <summary>Each call that passes <paramref name="description"/>, by file, as the TopiaForgeCallbacks method nearest before it.</summary>
        private static IEnumerable<(string File, string Method)> CallsNamed(IReadOnlyDictionary<string, string> kit, string description)
        {
            foreach (var (file, source) in kit)
            {
                if (file.EndsWith("TopiaForgeCallbacks.cs", StringComparison.Ordinal)) continue;
                var index = 0;
                while ((index = source.IndexOf("\"" + description, index, StringComparison.Ordinal)) >= 0)
                {
                    var call = source.LastIndexOf("TopiaForgeCallbacks.", index, StringComparison.Ordinal);
                    var method = call < 0 ? "none" : source.Substring(call + "TopiaForgeCallbacks.".Length).Split('(')[0];
                    yield return (Path.GetFileName(file), method);
                    index += description.Length;
                }
            }
        }

        private static string Balanced(string source, int start)
        {
            var depth = 1;
            for (var index = start; index < source.Length; index++)
            {
                if (source[index] == '(') depth++;
                else if (source[index] == ')' && --depth == 0) return source.Substring(start, index - start);
            }

            throw new InvalidOperationException("An AddListener call never closes.");
        }

        private static int Count(string source, string text)
        {
            var count = 0;
            for (var index = source.IndexOf(text, StringComparison.Ordinal); index >= 0; index = source.IndexOf(text, index + text.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        private static IReadOnlyDictionary<string, string> KitSources()
        {
            var kitRoot = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            var separator = Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(kitRoot, "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains(separator + "obj" + separator) && !file.Contains(separator + "bin" + separator))
                .ToDictionary(file => file, File.ReadAllText, StringComparer.Ordinal);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UI kit intent convention failed: " + message);
        }
    }
}
