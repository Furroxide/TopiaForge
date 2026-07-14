using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Gamemode cards with PLAY actions; launching closes the overlay on success.</summary>
    internal sealed class GamemodesTab : IManagerTab
    {
        public string Title => "GAMEMODES";

        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            content.Label("SELECT GAMEMODE", TopiaForgeTextStyle.Display).FixedHeight(34f);
            content.Label("Launches close this overlay so the world stays in view.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(22f);

            var service = context.Plugin.GetWorldService();
            if (service == null)
            {
                content.Label("World/gamemode service unavailable. Enable TopiaForge Worlds and restart.", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Warning);
                return;
            }

            var entries = service.MenuEntries;
            if (entries.Count == 0)
            {
                content.Label("No gamemodes are registered yet.", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Muted);
                return;
            }

            var worlds = service.Worlds;
            if (worlds.Count == 0)
            {
                content.Label("No worlds are registered yet.", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Muted);
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

            content.Label("LAUNCH TARGET", TopiaForgeTextStyle.Heading).FixedHeight(24f);
            var controls = content.Panel(TopiaForgePanelStyle.Plain);
            controls.FixedHeight(92f);
            var controlsRow = controls.Row(TopiaForgeGap.Md, TopiaForgeGap.Md, expandChildWidth: true);
            controlsRow.Stretch();

            var worldColumn = controlsRow.Column(TopiaForgeGap.Xs);
            worldColumn.Flex(2f, 0f);
            worldColumn.Label("WORLD", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(18f);

            TopiaForgeDropdown? loadModeDropdown = null;
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
            worldDropdown.FixedHeight(TopiaForgeTokens.ControlHeight);

            var modeColumn = controlsRow.Column(TopiaForgeGap.Xs);
            modeColumn.Flex(1f, 0f);
            modeColumn.Label("LOAD MODE", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(18f);
            loadModeDropdown = modeColumn.Dropdown(LoadModeLabels(loadModeOptions), selectedLoadModeIndex, next =>
            {
                selectedLoadModeIndex = next;
                selectedLoadMode = loadModeOptions[selectedLoadModeIndex];
            });
            loadModeDropdown.SetEnabled(loadModeOptions.Count > 1);
            loadModeDropdown.FixedHeight(TopiaForgeTokens.ControlHeight);

            var scroll = content.Scroll(TopiaForgeGap.Sm);
            foreach (var entry in entries)
            {
                var entryId = entry.Id;
                var gamemodeId = entry.GamemodeId;
                var card = scroll.Content.Panel(TopiaForgePanelStyle.Plain);
                card.FixedHeight(64f);
                var row = card.Row(TopiaForgeGap.Md, TopiaForgeGap.Md, expandChildWidth: false);
                row.Stretch();
                var text = row.Column(TopiaForgeGap.Xs);
                text.Flex(1f, 0f);
                text.Label(entry.Title.ToUpperInvariant(), TopiaForgeTextStyle.Heading);
                text.Label(entry.Description, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
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
                        TopiaForgeToasts.Show(message, TopiaForgeTone.Danger, 5f);
                        context.Refresh();
                    }
                });
                play.Fixed(110f, TopiaForgeTokens.ControlHeight);
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
