using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Mod loadout: toolbar + virtualized package list with state badges + detail pane.
    /// Removal goes through the kit's destructive-confirm modal (replaces the old
    /// two-click CONFIRM REMOVE); the staged/restart-required flow is unchanged.
    /// </summary>
    internal sealed class InstalledTab : IManagerTab
    {
        private readonly ManagerUiState uiState;
        private TopiaForgeContainer? detailPane;

        public InstalledTab(ManagerUiState uiState)
        {
            this.uiState = uiState;
        }

        public string Title => "INSTALLED";

        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            content.Label("MOD LOADOUT", TopiaForgeTextStyle.Display).FixedHeight(34f);
            content.Label("Select a package to inspect, enable, disable, or stage removal.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(22f);

            var toolbar = content.Row(TopiaForgeGap.Sm);
            toolbar.FixedHeight(TopiaForgeTokens.ControlHeight);
            toolbar.Button("ENABLE / DISABLE", () => context.RunAction(() => context.Plugin.ToggleEnabled(uiState.SelectedModId)), TopiaForgeButtonStyle.Outline);
            toolbar.Button("REFRESH", () =>
            {
                context.Plugin.RefreshPackages(saveState: false);
                context.Refresh();
            }, TopiaForgeButtonStyle.Outline);
            toolbar.Button("OPEN MODS", () => context.Plugin.OpenFolder(context.Plugin.Paths.Root), TopiaForgeButtonStyle.Ghost);

            var split = content.Row(TopiaForgeGap.Sm, TopiaForgeGap.None, expandChildWidth: false);
            split.Flex(1f, 1f);

            var packages = context.Plugin.Packages;
            var list = split.ListView<ModPackage>();
            list.Flex(0.58f, 1f);
            list.Bind((row, package, index) =>
            {
                var manifest = package.Manifest;
                var state = package.State;
                if (manifest == null || state == null)
                {
                    row.Title.SetText("INVALID  //  " + System.IO.Path.GetFileName(package.PackagePath));
                    row.Subtitle.SetText(string.Empty);
                    row.Badge.Set("INVALID", TopiaForgeTone.Danger);
                    return;
                }

                row.Title.SetText(manifest.Name);
                var loaded = context.Plugin.LoadedModIds.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase);
                row.Subtitle.SetText(manifest.Version + (loaded ? "  //  LOADED" : string.Empty));
                if (state.UninstallPending)
                {
                    row.Badge.Set("PENDING REMOVE", TopiaForgeTone.Danger);
                }
                else if (state.RestartRequired)
                {
                    row.Badge.Set("RESTART", TopiaForgeTone.Warning);
                }
                else if (context.Plugin.GetLoadFailure(manifest.Id) != null)
                {
                    row.Badge.Set("LOAD FAILED", TopiaForgeTone.Danger);
                }
                else
                {
                    row.Badge.Set(state.Enabled ? "ENABLED" : "DISABLED", state.Enabled ? TopiaForgeTone.Success : TopiaForgeTone.Neutral);
                }
            });
            list.OnSelected(index =>
            {
                var package = packages[index];
                uiState.SelectedModId = package.Manifest?.Id ?? package.PackagePath;
                BuildDetail(context, package);
            });
            list.SetItems(packages);

            var detailPanel = split.Panel(TopiaForgePanelStyle.Plain);
            detailPanel.Flex(0.42f, 1f);
            detailPane = detailPanel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md);
            detailPane.Stretch();

            var selectedIndex = -1;
            for (var index = 0; index < packages.Count; index++)
            {
                if (string.Equals(packages[index].Manifest?.Id, uiState.SelectedModId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = index;
                    break;
                }
            }

            if (selectedIndex >= 0)
            {
                list.Select(selectedIndex);
            }
            else
            {
                BuildDetail(context, null);
            }
        }

        private void BuildDetail(ManagerTabContext context, ModPackage? package)
        {
            if (detailPane == null)
            {
                return;
            }

            foreach (Transform child in detailPane.Go.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            if (package == null)
            {
                detailPane.Label("No mod selected.", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Muted);
                return;
            }

            if (package.Manifest == null || package.State == null)
            {
                detailPane.Label("Invalid package: " + string.Join("; ", package.Errors.ToArray()), TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Danger);
                return;
            }

            var manifest = package.Manifest;
            var state = package.State;
            detailPane.Label(manifest.Name + " " + manifest.Version, TopiaForgeTextStyle.Title);
            detailPane.Label(manifest.Description, TopiaForgeTextStyle.Body);

            var badges = detailPane.Row(TopiaForgeGap.Xs);
            badges.FixedHeight(24f);
            badges.Badge(state.Enabled ? "ENABLED" : "DISABLED", state.Enabled ? TopiaForgeTone.Success : TopiaForgeTone.Neutral);
            if (context.Plugin.LoadedModIds.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                badges.Badge("LOADED", TopiaForgeTone.Accent);
            }

            if (state.RestartRequired)
            {
                badges.Badge("RESTART REQUIRED", TopiaForgeTone.Warning);
            }

            if (state.UninstallPending)
            {
                badges.Badge("UNINSTALL PENDING", TopiaForgeTone.Danger);
            }

            if (context.Plugin.LoadOrder.Errors.TryGetValue(manifest.Id, out var errors))
            {
                detailPane.Label("Dependency errors: " + string.Join("; ", errors.ToArray()), TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger);
            }

            var loadFailure = context.Plugin.GetLoadFailure(manifest.Id);
            if (loadFailure != null)
            {
                badges.Badge("LOAD FAILED", TopiaForgeTone.Danger);
                detailPane.Label("Load failure: " + loadFailure, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger);
            }

            detailPane.Label("Capabilities: " + string.Join(", ", manifest.Capabilities.ToArray()), TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var actions = detailPane.Row(TopiaForgeGap.Sm);
            actions.FixedHeight(TopiaForgeTokens.ControlHeight);
            actions.Button("REMOVE", () => context.Host.Modal.Destructive(
                "REMOVE MOD",
                manifest.Name + " " + manifest.Version + " will be staged for removal and uninstalled on the next restart.",
                "REMOVE",
                () => context.RunAction(() => context.Plugin.Uninstall(manifest.Id))), TopiaForgeButtonStyle.Danger);
        }
    }
}
