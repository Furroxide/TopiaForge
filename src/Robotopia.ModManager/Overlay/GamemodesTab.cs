using System;
using System.Collections.Generic;
using Robotopia.ModManager.Core;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;

namespace Robotopia.ModManager
{
    /// <summary>Gamemode cards with PLAY actions; launching closes the overlay on success.</summary>
    internal sealed class GamemodesTab : IManagerTab
    {
        public string Title => "GAMEMODES";

        public void Build(QwContainer content, ManagerTabContext context)
        {
            content.Label("SELECT GAMEMODE", QwTextStyle.Display).FixedHeight(34f);
            content.Label("Launches close this overlay so the world stays in view.", QwTextStyle.Caption).Tone(QwTone.Muted).FixedHeight(22f);

            var service = context.Plugin.GetWorldService();
            if (service == null)
            {
                content.Label("World/gamemode service unavailable. Enable Robotopia Worlds and restart.", QwTextStyle.Body).Tone(QwTone.Warning);
                return;
            }

            var entries = service.MenuEntries;
            if (entries.Count == 0)
            {
                content.Label("No gamemodes are registered yet.", QwTextStyle.Body).Tone(QwTone.Muted);
                return;
            }

            var worlds = service.Worlds;
            if (worlds.Count == 0)
            {
                content.Label("No worlds are registered yet.", QwTextStyle.Body).Tone(QwTone.Muted);
                return;
            }

            var settings = context.Plugin.ReadWorldLaunchSettings();
            var selectedWorldIndex = IndexOfWorld(worlds, settings.SelectedWorldId);
            if (selectedWorldIndex < 0)
            {
                selectedWorldIndex = 0;
            }

            var selectedWorld = worlds[selectedWorldIndex];
            var selectedLoadMode = WorldLaunchSettings.ReconcileLoadMode(
                selectedWorld.SupportsSceneReplacement,
                selectedWorld.SupportsAdditiveArena,
                settings.LoadMode);
            var loadModeOptions = LoadModesFor(selectedWorld);
            var selectedLoadModeIndex = Math.Max(0, loadModeOptions.IndexOf(selectedLoadMode));

            content.Label("LAUNCH TARGET", QwTextStyle.Heading).FixedHeight(24f);
            var controls = content.Panel(QwPanelStyle.Plain);
            controls.FixedHeight(92f);
            var controlsRow = controls.Row(QwGap.Md, QwGap.Md, expandChildWidth: true);
            controlsRow.Stretch();

            var worldColumn = controlsRow.Column(QwGap.Xs);
            worldColumn.Flex(2f, 0f);
            worldColumn.Label("WORLD", QwTextStyle.Caption).Tone(QwTone.Muted).FixedHeight(18f);

            QwDropdown? loadModeDropdown = null;
            var worldDropdown = worldColumn.Dropdown(WorldLabels(worlds), selectedWorldIndex, next =>
            {
                selectedWorldIndex = next;
                selectedWorld = worlds[selectedWorldIndex];
                selectedLoadMode = WorldLaunchSettings.ReconcileLoadMode(
                    selectedWorld.SupportsSceneReplacement,
                    selectedWorld.SupportsAdditiveArena,
                    selectedLoadMode);
                loadModeOptions = LoadModesFor(selectedWorld);
                selectedLoadModeIndex = Math.Max(0, loadModeOptions.IndexOf(selectedLoadMode));
                loadModeDropdown?.SetOptions(LoadModeLabels(loadModeOptions), selectedLoadModeIndex);
                loadModeDropdown?.SetEnabled(loadModeOptions.Count > 1);
            });
            worldDropdown.FixedHeight(QwTokens.ControlHeight);

            var modeColumn = controlsRow.Column(QwGap.Xs);
            modeColumn.Flex(1f, 0f);
            modeColumn.Label("LOAD MODE", QwTextStyle.Caption).Tone(QwTone.Muted).FixedHeight(18f);
            loadModeDropdown = modeColumn.Dropdown(LoadModeLabels(loadModeOptions), selectedLoadModeIndex, next =>
            {
                selectedLoadModeIndex = next;
                selectedLoadMode = loadModeOptions[selectedLoadModeIndex];
            });
            loadModeDropdown.SetEnabled(loadModeOptions.Count > 1);
            loadModeDropdown.FixedHeight(QwTokens.ControlHeight);

            var scroll = content.Scroll(QwGap.Sm);
            foreach (var entry in entries)
            {
                var entryId = entry.Id;
                var gamemodeId = entry.GamemodeId;
                var card = scroll.Content.Panel(QwPanelStyle.Plain);
                card.FixedHeight(64f);
                var row = card.Row(QwGap.Md, QwGap.Md, expandChildWidth: false);
                row.Stretch();
                var text = row.Column(QwGap.Xs);
                text.Flex(1f, 0f);
                text.Label(entry.Title.ToUpperInvariant(), QwTextStyle.Heading);
                text.Label(entry.Description, QwTextStyle.Caption).Tone(QwTone.Muted);
                var play = row.Button("PLAY", () =>
                {
                    var selectedWorldId = worlds[selectedWorldIndex].Id;
                    var (ok, message) = context.Plugin.LaunchGamemodeSelection(
                        entryId,
                        selectedWorldId,
                        gamemodeId,
                        selectedLoadMode);
                    context.SetStatus(message);
                    if (ok)
                    {
                        context.Close();
                    }
                    else
                    {
                        QwToasts.Show(message, QwTone.Danger, 5f);
                        context.Refresh();
                    }
                });
                play.Fixed(110f, QwTokens.ControlHeight);
            }
        }

        private static int IndexOfWorld(IReadOnlyList<WorldDefinition> worlds, string worldId)
        {
            for (var index = 0; index < worlds.Count; index++)
            {
                if (string.Equals(worlds[index].Id, worldId, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private static List<string> WorldLabels(IReadOnlyList<WorldDefinition> worlds)
        {
            var labels = new List<string>(worlds.Count);
            for (var index = 0; index < worlds.Count; index++)
            {
                labels.Add(worlds[index].Name);
            }

            return labels;
        }

        private static List<string> LoadModesFor(WorldDefinition world)
        {
            var modes = new List<string>(2);
            if (world.SupportsAdditiveArena)
            {
                modes.Add(WorldLaunchSettings.AdditiveArena);
            }

            if (world.SupportsSceneReplacement)
            {
                modes.Add(WorldLaunchSettings.SceneReplacement);
            }

            if (modes.Count == 0)
            {
                modes.Add(WorldLaunchSettings.AdditiveArena);
            }

            return modes;
        }

        private static List<string> LoadModeLabels(IReadOnlyList<string> modes)
        {
            var labels = new List<string>(modes.Count);
            for (var index = 0; index < modes.Count; index++)
            {
                labels.Add(modes[index] == WorldLaunchSettings.SceneReplacement
                    ? "Scene replacement"
                    : "Additive arena");
            }

            return labels;
        }
    }
}
