using System;
using System.Collections.Generic;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;

namespace Robotopia.Sandbox.Ui
{
    /// <summary>
    /// The sandbox "Q menu": a Paper window with Props (searchable thumbnail grid — click to spawn,
    /// hover for details), NPCs (RobotKit spawn options, or an unavailable hint), Robots (the live
    /// roster with per-robot programs), and Tools (undo/freeze/cleanup + rebinds). Content containers
    /// are built once and toggled per tab so input state survives tab switches.
    /// </summary>
    internal sealed class SpawnMenuWindow : IDisposable
    {
        private static readonly (string Label, RobotColor? Color)[] TintOptions =
        {
            ("Default", null),
            ("Red", new RobotColor(0.9f, 0.25f, 0.2f)),
            ("Green", new RobotColor(0.35f, 0.85f, 0.35f)),
            ("Blue", new RobotColor(0.3f, 0.5f, 0.95f)),
            ("Gold", new RobotColor(0.95f, 0.8f, 0.25f)),
            ("Violet", new RobotColor(0.7f, 0.4f, 0.95f)),
        };

        private readonly UiHost ui;
        private readonly SandboxController controller;
        private readonly QwWindow window;
        private readonly QwContainer propsPane;
        private readonly QwContainer npcsPane;
        private readonly QwContainer robotsPane;
        private readonly QwContainer toolsPane;
        private readonly RobotRosterTab roster;
        private int selectedPane;
        private readonly QwContainer propGrid;
        private readonly QwLabel propStatus;
        private readonly PropThumbnails thumbnails;
        private readonly List<QwCard> propCards = new List<QwCard>();
        private readonly Dictionary<string, QwCard> boundCards = new Dictionary<string, QwCard>(StringComparer.Ordinal);
        private readonly List<SandboxPropDefinition> filtered = new List<SandboxPropDefinition>();
        private readonly QwLabel npcHint;
        private readonly QwContainer npcControls;
        private string propFilter = string.Empty;
        private bool npcAutonomous;
        private int npcTintIndex;
        private int npcTypeIndex;
        private QwDropdown? npcTypeDropdown;
        private int lastRobotTypeCount = -1;
        private float npcScale = 1f;
        private string npcName = string.Empty;
        private bool lastRobotsAvailable;

        public SpawnMenuWindow(UiHost ui, SandboxController controller)
        {
            this.ui = ui;
            this.controller = controller;
            npcAutonomous = string.Equals(controller.Config.DefaultRobotBrainMode, "Autonomous", StringComparison.OrdinalIgnoreCase);

            // The controller creates this while the menu is still the active scene; the sandbox scene then
            // loads in Single mode, so the canvas must be persistent (DontDestroyOnLoad) to survive the swap.
            window = ui.Window("spawnmenu", "SANDBOX", width: 560f, height: 540f, persistent: true);
            var content = window.Content;
            var tabs = content.Tabs("PROPS", "NPCS", "ROBOTS", "TOOLS");

            var paneHost = content.Stack("Panes");
            paneHost.Flex(1f, 1f);
            // A Stack applies no layout to its children; stretch each pane to fill it so exactly one
            // full-size pane is visible per selected tab.
            propsPane = paneHost.Column(QwGap.Sm, QwGap.Sm).Stretch();
            npcsPane = paneHost.Column(QwGap.Sm, QwGap.Sm).Stretch();
            robotsPane = paneHost.Column(QwGap.Sm, QwGap.Sm).Stretch();
            toolsPane = paneHost.Column(QwGap.Sm, QwGap.Sm).Stretch();

            // --- PROPS ---
            propsPane.SearchInput("Search props…", value =>
            {
                propFilter = value ?? string.Empty;
                RefreshProps();
            });
            propStatus = propsPane.Label(QwTextStyle.Caption).Tone(QwTone.Muted);
            // Cards flow into as many columns as the window is wide and wrap; the grid
            // lives in a scroll view so long catalogs scroll instead of clipping.
            propGrid = propsPane.Scroll(QwGap.Sm, QwGap.Xs).Content.Grid(118f, 148f, QwGap.Sm);
            thumbnails = new PropThumbnails(controller.Catalog, controller.Logger);
            thumbnails.Ready += (id, texture) =>
            {
                if (boundCards.TryGetValue(id, out var card))
                {
                    card.SetPreviewTexture(texture);
                }
            };
            RefreshProps();

            // --- NPCS ---
            // RobotKit is a hard dependency (the loader skips this mod without it), so an unavailable
            // service only ever means the level's robot prefab scan hasn't finished yet — keep the copy
            // friendly, never "go install a mod".
            npcHint = npcsPane.Label(
                "Robots are warming up — spawn options appear once the level is ready.",
                QwTextStyle.Body);
            npcHint.Tone(QwTone.Muted);
            npcControls = npcsPane.Column(QwGap.Sm);
            npcControls.Toggle("Autonomous brain (thinks, talks, wanders)", npcAutonomous, v => npcAutonomous = v);
            // Robot types only exist once a level's prefab scan has run; Update() refills the options then.
            npcTypeDropdown = npcControls.Dropdown(RobotTypeLabels(), npcTypeIndex, v => npcTypeIndex = v);
            var tintLabels = new string[TintOptions.Length];
            for (var index = 0; index < TintOptions.Length; index++)
            {
                tintLabels[index] = TintOptions[index].Label;
            }

            npcControls.Dropdown(tintLabels, npcTintIndex, v => npcTintIndex = v);
            npcControls.Slider("Scale", 0.5f, 2f, npcScale, v => npcScale = v);
            npcControls.Input("Name (optional)", npcName, v => npcName = v ?? string.Empty);
            npcControls.Button("SPAWN ROBOT", () => controller.SpawnRobot(
                npcAutonomous ? RobotBrainMode.Autonomous : RobotBrainMode.Dormant,
                TintOptions[npcTintIndex].Color,
                npcScale,
                npcName,
                SelectedRobotTypeId()));
            if (controller.ProgrammingAvailable)
            {
                npcControls.Label(
                    "Walk up to any robot and PROGRAM/REPROGRAM it — talk it into a task (follow, go to a marker, "
                    + "patrol). Reprogramming an autonomous robot takes over its own brain.",
                    QwTextStyle.Caption).Tone(QwTone.Muted);
                npcControls.SectionHeader("PROGRAM MARKERS");
                var markerLabels = SandboxController.MarkerLabels;
                var markerRow = npcControls.Row(QwGap.Sm);
                for (var index = 0; index < markerLabels.Length; index++)
                {
                    var markerIndex = index;
                    markerRow.Button(markerLabels[index].ToUpperInvariant(), () => controller.SpawnMarker(markerIndex),
                        QwButtonStyle.Outline);
                }

                npcControls.Label("Markers are named places robots can be told to go to or patrol between.",
                    QwTextStyle.Caption).Tone(QwTone.Muted);
            }

            lastRobotsAvailable = controller.RobotsAvailable;
            ApplyRobotsAvailability(lastRobotsAvailable);

            // --- ROBOTS ---
            roster = new RobotRosterTab(robotsPane, controller);

            // --- TOOLS ---
            toolsPane.SectionHeader("STAGE");
            var undoRow = toolsPane.Row(QwGap.Sm);
            undoRow.Button("UNDO LAST", controller.Undo, QwButtonStyle.Outline);
            var freezeRow = toolsPane.Row(QwGap.Sm);
            freezeRow.Button("FREEZE ALL", () => controller.FreezeAll(true), QwButtonStyle.Outline);
            freezeRow.Button("UNFREEZE ALL", () => controller.FreezeAll(false), QwButtonStyle.Outline);
            toolsPane.Button("CLEAN UP EVERYTHING", controller.CleanUpEverything);
            toolsPane.SectionHeader("HOTKEYS");
            toolsPane.Keybind("Spawn menu", controller.HotkeyValue(SandboxController.SandboxHotkey.SpawnMenu),
                key => controller.Rebind(SandboxController.SandboxHotkey.SpawnMenu, key));
            toolsPane.Keybind("Undo", controller.HotkeyValue(SandboxController.SandboxHotkey.Undo),
                key => controller.Rebind(SandboxController.SandboxHotkey.Undo, key));
            toolsPane.Keybind("Freeze prop", controller.HotkeyValue(SandboxController.SandboxHotkey.Freeze),
                key => controller.Rebind(SandboxController.SandboxHotkey.Freeze, key));
            toolsPane.Label("Grab and throw spawned props with the Gravity Gun (hold right mouse).", QwTextStyle.Caption)
                .Tone(QwTone.Muted);

            tabs.OnSelected(ShowPane);
            ShowPane(0);
        }

        public void Toggle()
        {
            window.Toggle();
        }

        /// <summary>Closes (hides) the menu — e.g. before the roster opens a robot chat over it.</summary>
        public void Hide()
        {
            window.Close();
        }

        /// <summary>
        /// Re-applies the search filter over the (possibly newly grown) catalog and rebinds
        /// the pooled cards. Cards join/leave the grid via SetActive — a SetVisible-hidden
        /// card would still occupy its cell (see QwCard).
        /// </summary>
        public void RefreshProps()
        {
            filtered.Clear();
            foreach (var item in controller.Catalog.Items)
            {
                if (propFilter.Length == 0
                    || item.DisplayName.IndexOf(propFilter, StringComparison.OrdinalIgnoreCase) >= 0
                    || item.Id.IndexOf(propFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    filtered.Add(item);
                }
            }

            boundCards.Clear();
            for (var index = 0; index < filtered.Count; index++)
            {
                var item = filtered[index];
                QwCard card;
                if (index < propCards.Count)
                {
                    card = propCards[index];
                }
                else
                {
                    // The pool slot index always equals the filtered index while bound.
                    var poolIndex = propCards.Count;
                    card = propGrid.Card(string.Empty, () =>
                    {
                        if (poolIndex < filtered.Count)
                        {
                            controller.SpawnProp(filtered[poolIndex]);
                        }
                    });
                    propCards.Add(card);
                }

                card.Go.SetActive(true);
                card.SetTitle(item.DisplayName);
                card.SetBadge(item.Kind == SandboxPropKind.UgcAsset ? "UGC" : "PRIM",
                    item.Kind == SandboxPropKind.UgcAsset ? QwTone.Accent : QwTone.Neutral);
                card.Tooltip(item.DisplayName + "\n" + item.Id + "\n"
                    + (item.Kind == SandboxPropKind.UgcAsset ? "UGC asset — click to spawn." : "Primitive — click to spawn."));
                var texture = thumbnails.TryGet(item.Id);
                card.SetPreviewTexture(texture);
                if (texture == null)
                {
                    thumbnails.Request(item);
                }

                boundCards[item.Id] = card;
            }

            for (var index = filtered.Count; index < propCards.Count; index++)
            {
                propCards[index].Go.SetActive(false);
            }

            propStatus.SetText(controller.Catalog.UgcAvailable
                ? "Click an item to spawn it where you look. " + filtered.Count + " items."
                : "Game prop catalog still loading — primitives are ready now.");
        }

        /// <summary>Per-frame poke from the controller: RobotKit availability can flip once a level loads.</summary>
        public void Update()
        {
            thumbnails.Update();
            if (selectedPane == 2)
            {
                roster.Update(); // roster rebinds only while its tab is the visible one
            }

            var available = controller.RobotsAvailable;
            if (available != lastRobotsAvailable)
            {
                lastRobotsAvailable = available;
                ApplyRobotsAvailability(available);
            }

            // Robot types appear once the level's prefab scan runs (and reset on scene change); refresh the
            // dropdown only when the count actually changes.
            var typeCount = controller.RobotTypes.Count;
            if (typeCount != lastRobotTypeCount)
            {
                lastRobotTypeCount = typeCount;
                npcTypeIndex = Math.Min(npcTypeIndex, Math.Max(0, typeCount - 1));
                npcTypeDropdown?.SetOptions(RobotTypeLabels(), npcTypeIndex);
            }
        }

        // The dropdown's option labels: one per robot type, or a placeholder while the level is still loading.
        private string[] RobotTypeLabels()
        {
            var types = controller.RobotTypes;
            if (types.Count == 0)
            {
                return new[] { "Robot type: default" };
            }

            var labels = new string[types.Count];
            for (var index = 0; index < types.Count; index++)
            {
                labels[index] = types[index].DisplayName + (index == 0 ? " (default)" : string.Empty);
            }

            return labels;
        }

        private string? SelectedRobotTypeId()
        {
            var types = controller.RobotTypes;
            return npcTypeIndex >= 0 && npcTypeIndex < types.Count ? types[npcTypeIndex].Id : null;
        }

        public void Dispose()
        {
            thumbnails.Dispose();
            window.Close();
        }

        private void ApplyRobotsAvailability(bool available)
        {
            npcHint.SetVisible(!available);
            npcControls.SetVisible(available);
        }

        private void ShowPane(int index)
        {
            selectedPane = index;
            propsPane.SetVisible(index == 0);
            npcsPane.SetVisible(index == 1);
            robotsPane.SetVisible(index == 2);
            toolsPane.SetVisible(index == 3);
        }
    }
}
