using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The policy runs against an enabled-system model, and the production adapter runs against Unity contract doubles.
    /// Source checks pin the remaining canvas, driver and focus wiring. These tests do not prove UI Toolkit input routing
    /// or START DEMO behavior in the game; those require the in-game regression check.
    /// </summary>
    internal static partial class UiKitEventSystemTests
    {
        public static void Run()
        {
            TheGameOwnEventSystemAlwaysComesFirst();
            AHudMadeBeforeTheFirstSceneLeavesTheMenuInCharge();
            AWindowMadeBeforeTheFirstSceneStepsAsideForTheMenu();
            AKitWindowAlwaysHasExactlyOneEventSystemAcrossScenes();
            TheGameEventSystemIsNeverTouched();
            OnlyCanvasesThatTakeInputAskForAnEventSystem();
            TheKitRemovesOnlyItsOwnEventSystem();
            TheDriverChecksEveryFrameAndOnEverySceneLoad();
            AFocusedButtonGetsAnEventSystemAtOnce();
            TheProductionAdapterMeetsItsContract();
            Console.WriteLine("UiKitEventSystemTests passed.");
        }

        private static void TheGameOwnEventSystemAlwaysComesFirst()
        {
            Assert(Decide(kitTakesInput: true, kitOwnsOne: false, otherIsActive: false) == TopiaForgeEventSystemAction.CreateOwn,
                "a kit canvas that takes input with no event system active gets the kit's own");
            Assert(Decide(kitTakesInput: false, kitOwnsOne: false, otherIsActive: false) == TopiaForgeEventSystemAction.None,
                "a canvas that takes no input, such as a HUD made in OnLoad, never makes an event system");
            Assert(Decide(kitTakesInput: true, kitOwnsOne: false, otherIsActive: true) == TopiaForgeEventSystemAction.None,
                "with the game's own active, the kit makes none, even for a window that takes input");
            Assert(Decide(kitTakesInput: true, kitOwnsOne: true, otherIsActive: true) == TopiaForgeEventSystemAction.RetireOwn,
                "the kit's own goes as soon as the game's is active, even while a kit window takes input");
            Assert(Decide(kitTakesInput: false, kitOwnsOne: true, otherIsActive: true) == TopiaForgeEventSystemAction.RetireOwn,
                "the kit's own goes as soon as the game's is active");
            Assert(Decide(kitTakesInput: true, kitOwnsOne: true, otherIsActive: false) == TopiaForgeEventSystemAction.None,
                "the kit keeps its own while it is the only one");
            Assert(Decide(kitTakesInput: false, kitOwnsOne: true, otherIsActive: false) == TopiaForgeEventSystemAction.None,
                "the kit keeps its own while no other is active, so a window shown again needs no new one");
        }

        // The regression: the multiplayer template's counter HUD, made once in OnLoad.
        private static void AHudMadeBeforeTheFirstSceneLeavesTheMenuInCharge()
        {
            var unity = new UnityModel();
            unity.KitTakesInput = false;
            unity.Frame();
            Assert(unity.Enabled.Count == 0, "a HUD made before the game's first scene makes no event system");

            var menu = unity.LoadScene(withEventSystem: true);
            unity.Frame();
            Assert(ReferenceEquals(unity.Current, menu) && unity.Enabled.Count == 1,
                "the main menu's own event system remains current");
        }

        private static void AWindowMadeBeforeTheFirstSceneStepsAsideForTheMenu()
        {
            var unity = new UnityModel();
            unity.KitTakesInput = true;
            unity.Frame();
            Assert(unity.Current != null && unity.Current.Kit && unity.Enabled.Count == 1,
                "a window shown before the game's first scene gets the kit's own event system");

            var menu = unity.LoadScene(withEventSystem: true);
            Assert(ReferenceEquals(unity.Current, menu) && unity.Enabled.Count == 1,
                "the scene load removes the kit's own at once, so the menu's own runs from its first frame");
            unity.Frame();
            Assert(ReferenceEquals(unity.Current, menu) && unity.Enabled.Count == 1, "the menu's own stays in charge");
        }

        // Never zero while a kit surface takes input, and never two once the frame's checks have run.
        private static void AKitWindowAlwaysHasExactlyOneEventSystemAcrossScenes()
        {
            var unity = new UnityModel();
            var menu = unity.LoadScene(withEventSystem: true);
            unity.KitTakesInput = true;
            unity.Frame();
            Assert(ReferenceEquals(unity.Current, menu) && unity.Enabled.Count == 1, "a window at the menu uses the menu's own");

            unity.UnloadScene(menu);
            Assert(unity.Enabled.Count == 0, "the model's menu took its event system with it");
            unity.Frame();
            Assert(unity.Current != null && unity.Current.Kit && unity.Enabled.Count == 1,
                "while a load runs, or in a level without one, the open window gets the kit's own the next frame");

            var level = unity.LoadScene(withEventSystem: false);
            unity.FramesUntilOwnedRecheck();
            Assert(unity.Current != null && unity.Current.Kit && unity.Enabled.Count == 1,
                "a level without an event system keeps the kit's own for the window");

            unity.UnloadScene(level);
            var next = unity.LoadScene(withEventSystem: true);
            Assert(ReferenceEquals(unity.Current, next) && unity.Enabled.Count == 1,
                "the next level's own takes over at its load and the kit's own goes");

            var late = unity.AddGameEventSystemLater();
            unity.UnloadScene(next);
            unity.Frame();
            Assert(ReferenceEquals(unity.Current, late) && unity.Enabled.Count == 1,
                "an event system the game makes outside a scene load is used at once when it is the only one");
        }

        // Nothing the game owns is changed: the kit never disables or destroys an event system it did not make.
        private static void TheGameEventSystemIsNeverTouched()
        {
            var unity = new UnityModel();
            var menu = unity.LoadScene(withEventSystem: true);
            unity.KitTakesInput = true;

            menu.SetEnabledByGame(false);
            unity.Frame();
            Assert(unity.Current != null && unity.Current.Kit && unity.Enabled.Count == 1,
                "the game disabling its own while a kit window takes input gives the window the kit's own");

            menu.SetEnabledByGame(true);
            Assert(unity.Enabled.Count == 2 && unity.Current != null && unity.Current.Kit,
                "the model enables the game's own again behind the kit's own, as Unity does");
            unity.FramesUntilOwnedRecheck();
            Assert(ReferenceEquals(unity.Current, menu) && unity.Enabled.Count == 1,
                "within a second the kit's own goes and the game's own runs again");
            Assert(menu.Enabled && !menu.Destroyed, "the kit never disabled or destroyed the game's own event system");
        }

        // A canvas whose raycaster is off takes no input: before the fix every kit canvas, HUDs included, made one.
        private static void OnlyCanvasesThatTakeInputAskForAnEventSystem()
        {
            var layers = Read("src", "TopiaForge.Mods.UnityUi", "Runtime", "TopiaForgeLayers.cs");
            var create = Regex.Match(layers, @"public static GameObject CreateCanvas\([^)]*\)\s*\{(?<body>[\s\S]*?)\n        \}",
                RegexOptions.CultureInvariant);
            Check(create.Success, "TopiaForgeLayers.CreateCanvas was not found.");
            var body = create.Groups["body"].Value;
            Check(!body.Contains("EnsureEventSystem", StringComparison.Ordinal),
                "CreateCanvas must not make an event system for every canvas: a HUD made in OnLoad took the main menu's input.");
            RequireMatch(body, @"raycaster\.enabled = interactive;\s*TopiaForgeEventSystems\.Track\(raycaster\);",
                "Every kit canvas's raycaster must be followed, so a canvas that takes input gets an event system when none is active.");

            var chat = Read("src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeChatLog.Chrome.cs");
            RequireMatch(chat, @"raycaster = TopiaForgeComponents\.GetOrAdd<GraphicRaycaster>\(Go\);\s*TopiaForgeEventSystems\.Track\(raycaster\);",
                "The chat log's own raycaster takes input while it is open, so it must be followed too.");

            foreach (var file in new[] { "TopiaForgeWindow.cs", "TopiaForgeFullscreenTool.cs" })
            {
                RequireSource(Read("src", "TopiaForge.Mods.UnityUi", "Widgets", file),
                    "TopiaForgeEventSystems.Track(canvasRoot.GetComponent<GraphicRaycaster>(), Go);",
                    "A hidden window or fullscreen tool must tie its canvas raycaster to its visible content root.");
            }
        }

        private static void TheKitRemovesOnlyItsOwnEventSystem()
        {
            var systems = Read("src", "TopiaForge.Mods.UnityUi", "Runtime", "TopiaForgeEventSystems.cs");
            RequireSource(systems, "TopiaForgeEventSystemPolicy.Decide(KitTakesInput(), ownedEventSystem != null, FindOtherActive() != null)",
                "The kit must decide through the tested policy.");
            var removals = Regex.Matches(systems, @"(?<target>[\w.]+)\.SetActive\(false\)|Destroy\((?<target>[\w.]+)\)|(?<target>[\w.]+)\.enabled\s*=\s*false",
                RegexOptions.CultureInvariant);
            Check(removals.Count == 2, "The kit must deactivate and destroy its own event system in one place; found " + removals.Count + ".");
            foreach (Match removal in removals)
            {
                Check(removal.Groups["target"].Value == "owned",
                    "The kit may remove only the event system it made, never the game's: " + removal.Value);
            }

            RequireMatch(systems, @"var owned = ownedEventSystem;\s*ownedEventSystem = null;",
                "Only the kit's own event system may be removed.");
            RequireSource(systems, "candidate.gameObject != ownedEventSystem",
                "Another event system must be one the kit did not make.");
        }

        private static void TheDriverChecksEveryFrameAndOnEverySceneLoad()
        {
            var runtime = Read("src", "TopiaForge.Mods.UnityUi", "Runtime", "TopiaForgeRuntime.cs");
            var systems = Read("src", "TopiaForge.Mods.UnityUi", "Runtime", "TopiaForgeEventSystems.cs");
            RequireMatch(runtime, @"private void Update\(\)\s*\{[\s\S]*?TopiaForgeEventSystems\.Tick\(\);",
                "The kit's driver must check the event systems every frame.");
            RequireSource(systems, "SceneManager.sceneLoaded += OnSceneLoaded;", "A scene load must remove the kit's own at once.");
            RequireSource(systems, "SceneManager.sceneLoaded -= OnSceneLoaded;", "The kit must stop watching scenes when it shuts down.");
            RequireMatch(systems, @"else if \(EventSystem\.current == null && KitTakesInput\(\)\)\s*\{\s*Reconcile\(\);",
                "With no event system active, a kit canvas that takes input must get one.");
            RequireMatch(systems, @"if \(sceneRecheck \|\| Time\.unscaledTimeAsDouble >= nextCheckAt\)\s*\{\s*Reconcile\(\);",
                "While the kit's own exists, it must look for another after each scene load and at least once a second.");
        }

        // A dialog focuses its default button in the frame it appears, the multiplayer prompts' CANCEL among them. Unity
        // drops a selection made while no event system is active, so Focus cannot wait for the driver's next frame.
        private static void AFocusedButtonGetsAnEventSystemAtOnce()
        {
            var button = Read("src", "TopiaForge.Mods.UnityUi", "Widgets", "TopiaForgeButton.cs");
            RequireMatch(button, @"public void Focus\(\)\s*\{\s*TopiaForgeEventSystems\.EnsureEventSystem\(\);\s*button\.Select\(\);",
                "TopiaForgeButton.Focus must make an event system active before it selects, or a dialog opens with nothing focused.");
        }

        private static TopiaForgeEventSystemAction Decide(bool kitTakesInput, bool kitOwnsOne, bool otherIsActive) =>
            TopiaForgeEventSystemPolicy.Decide(kitTakesInput, kitOwnsOne, otherIsActive);

        /// <summary>
        /// Unity's list of enabled event systems, in the order they were enabled; the first is the one that runs. The kit's
        /// checks run as <c>TopiaForgeEventSystems</c> runs them: on a scene load while it owns one, every frame with none
        /// active, and once a second while it owns one.
        /// </summary>
        private sealed class UnityModel
        {
            private const int OwnedRecheckFrames = 60;
            private readonly List<StandInEventSystem> sceneSystems = new List<StandInEventSystem>();
            private StandInEventSystem? owned;
            private int framesSinceCheck;

            public List<StandInEventSystem> Enabled { get; } = new List<StandInEventSystem>();

            public bool KitTakesInput { get; set; }

            public StandInEventSystem? Current => Enabled.Count > 0 ? Enabled[0] : null;

            public StandInEventSystem LoadScene(bool withEventSystem)
            {
                var scene = new StandInEventSystem(this, kit: false);
                if (withEventSystem)
                {
                    sceneSystems.Add(scene);
                    Enabled.Add(scene);
                }

                if (owned != null)
                {
                    Reconcile();
                }

                return scene;
            }

            public StandInEventSystem AddGameEventSystemLater()
            {
                var system = new StandInEventSystem(this, kit: false);
                Enabled.Add(system);
                return system;
            }

            public void UnloadScene(StandInEventSystem scene)
            {
                scene.Destroyed = true;
                sceneSystems.Remove(scene);
                Enabled.Remove(scene);
            }

            public void Frame()
            {
                if (owned != null)
                {
                    if (++framesSinceCheck >= OwnedRecheckFrames)
                    {
                        Reconcile();
                    }
                }
                else if (Current == null && KitTakesInput)
                {
                    Reconcile();
                }
            }

            public void FramesUntilOwnedRecheck()
            {
                for (var frame = 0; frame < OwnedRecheckFrames; frame++)
                {
                    Frame();
                }
            }

            private void Reconcile()
            {
                framesSinceCheck = 0;
                var other = Enabled.Exists(system => !system.Kit);
                switch (TopiaForgeEventSystemPolicy.Decide(KitTakesInput, owned != null, other))
                {
                    case TopiaForgeEventSystemAction.CreateOwn:
                        owned = new StandInEventSystem(this, kit: true);
                        Enabled.Add(owned);
                        break;
                    case TopiaForgeEventSystemAction.RetireOwn:
                        owned!.Destroyed = true;
                        Enabled.Remove(owned);
                        owned = null;
                        break;
                }
            }
        }

        private sealed class StandInEventSystem
        {
            private readonly UnityModel unity;

            public StandInEventSystem(UnityModel unity, bool kit)
            {
                this.unity = unity;
                Kit = kit;
            }

            public bool Kit { get; }

            public bool Destroyed { get; set; }

            public bool Enabled { get; private set; } = true;

            public void SetEnabledByGame(bool enabled)
            {
                Enabled = enabled;
                if (enabled)
                {
                    unity.Enabled.Add(this);
                }
                else
                {
                    unity.Enabled.Remove(this);
                }
            }
        }

        private static string Read(params string[] parts)
        {
            var segments = new string[parts.Length + 1];
            segments[0] = Program.FindRepoRoot();
            Array.Copy(parts, 0, segments, 1, parts.Length);
            return File.ReadAllText(Path.Combine(segments));
        }

        private static void RequireSource(string source, string expected, string message) =>
            Check(source.Contains(expected, StringComparison.Ordinal), message);

        private static void RequireMatch(string source, string pattern, string message) =>
            Check(Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant), message);

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
