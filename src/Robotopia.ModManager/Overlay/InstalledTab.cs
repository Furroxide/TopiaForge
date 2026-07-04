using System;
using System.Linq;
using Robotopia.ModManager.Core;
using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.ModManager
{
    /// <summary>
    /// Mod loadout: toolbar + virtualized package list with state badges + detail pane.
    /// Removal goes through the kit's destructive-confirm modal (replaces the old
    /// two-click CONFIRM REMOVE); the staged/restart-required flow is unchanged.
    /// </summary>
    internal sealed class InstalledTab : IManagerTab
    {
        private readonly ManagerUiState uiState;
        private QwContainer? detailPane;

        public InstalledTab(ManagerUiState uiState)
        {
            this.uiState = uiState;
        }

        public string Title => "INSTALLED";

        public void Build(QwContainer content, ManagerTabContext context)
        {
            content.Label("MOD LOADOUT", QwTextStyle.Display).FixedHeight(34f);
            content.Label("Select a package to inspect, enable, disable, or stage removal.", QwTextStyle.Caption).Tone(QwTone.Muted).FixedHeight(22f);

            var toolbar = content.Row(QwGap.Sm);
            toolbar.FixedHeight(QwTokens.ControlHeight);
            toolbar.Button("ENABLE / DISABLE", () => context.RunAction(() => context.Plugin.ToggleEnabled(uiState.SelectedModId)), QwButtonStyle.Outline);
            toolbar.Button("REFRESH", () =>
            {
                context.Plugin.RefreshPackages(saveState: false);
                context.Refresh();
            }, QwButtonStyle.Outline);
            toolbar.Button("OPEN MODS", () => context.Plugin.OpenFolder(context.Plugin.Paths.Root), QwButtonStyle.Ghost);

            var split = content.Row(QwGap.Sm, QwGap.None, expandChildWidth: false);
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
                    row.Badge.Set("INVALID", QwTone.Danger);
                    return;
                }

                row.Title.SetText(manifest.Name);
                var loaded = context.Plugin.LoadedModIds.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase);
                row.Subtitle.SetText(manifest.Version + (loaded ? "  //  LOADED" : string.Empty));
                if (state.UninstallPending)
                {
                    row.Badge.Set("PENDING REMOVE", QwTone.Danger);
                }
                else if (state.RestartRequired)
                {
                    row.Badge.Set("RESTART", QwTone.Warning);
                }
                else if (context.Plugin.GetLoadFailure(manifest.Id) != null)
                {
                    row.Badge.Set("LOAD FAILED", QwTone.Danger);
                }
                else
                {
                    row.Badge.Set(state.Enabled ? "ENABLED" : "DISABLED", state.Enabled ? QwTone.Success : QwTone.Neutral);
                }
            });
            list.OnSelected(index =>
            {
                var package = packages[index];
                uiState.SelectedModId = package.Manifest?.Id ?? package.PackagePath;
                BuildDetail(context, package);
            });
            list.SetItems(packages);

            var detailPanel = split.Panel(QwPanelStyle.Plain);
            detailPanel.Flex(0.42f, 1f);
            detailPane = detailPanel.Column(QwGap.Sm, QwGap.Md);
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
                detailPane.Label("No mod selected.", QwTextStyle.Body).Tone(QwTone.Muted);
                return;
            }

            if (package.Manifest == null || package.State == null)
            {
                detailPane.Label("Invalid package: " + string.Join("; ", package.Errors.ToArray()), QwTextStyle.Body).Tone(QwTone.Danger);
                return;
            }

            var manifest = package.Manifest;
            var state = package.State;
            detailPane.Label(manifest.Name + " " + manifest.Version, QwTextStyle.Title);
            detailPane.Label(manifest.Description, QwTextStyle.Body);

            var badges = detailPane.Row(QwGap.Xs);
            badges.FixedHeight(24f);
            badges.Badge(state.Enabled ? "ENABLED" : "DISABLED", state.Enabled ? QwTone.Success : QwTone.Neutral);
            if (context.Plugin.LoadedModIds.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                badges.Badge("LOADED", QwTone.Accent);
            }

            if (state.RestartRequired)
            {
                badges.Badge("RESTART REQUIRED", QwTone.Warning);
            }

            if (state.UninstallPending)
            {
                badges.Badge("UNINSTALL PENDING", QwTone.Danger);
            }

            if (context.Plugin.LoadOrder.Errors.TryGetValue(manifest.Id, out var errors))
            {
                detailPane.Label("Dependency errors: " + string.Join("; ", errors.ToArray()), QwTextStyle.Caption).Tone(QwTone.Danger);
            }

            var loadFailure = context.Plugin.GetLoadFailure(manifest.Id);
            if (loadFailure != null)
            {
                badges.Badge("LOAD FAILED", QwTone.Danger);
                detailPane.Label("Load failure: " + loadFailure, QwTextStyle.Caption).Tone(QwTone.Danger);
            }

            detailPane.Label("Permissions: " + string.Join(", ", manifest.Permissions.ToArray()), QwTextStyle.Caption).Tone(QwTone.Muted);

            var actions = detailPane.Row(QwGap.Sm);
            actions.FixedHeight(QwTokens.ControlHeight);
            actions.Button("REMOVE", () => context.Host.Modal.Destructive(
                "REMOVE MOD",
                manifest.Name + " " + manifest.Version + " will be staged for removal and uninstalled on the next restart.",
                "REMOVE",
                () => context.RunAction(() => context.Plugin.Uninstall(manifest.Id))), QwButtonStyle.Danger);
        }
    }
}
