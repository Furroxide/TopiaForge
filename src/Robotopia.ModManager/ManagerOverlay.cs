using System;
using System.IO;
using System.Linq;
using Robotopia.ModManager.Core;
using Robotopia.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.ModManager
{
    internal sealed class ManagerOverlay
    {
        private readonly RobotopiaModManagerPlugin plugin;
        private readonly ManagerFileLogger logger;
        private readonly NeonCursorLease cursorLease = new NeonCursorLease();
        private GameObject? root;
        private Transform? content;
        private Text? statusText;
        private OverlayTab tab = OverlayTab.Gamemodes;
        private string selectedModId = string.Empty;
        private string packagePath = string.Empty;
        private string pendingUninstallId = string.Empty;
        private string status = "Trusted local mode: install only packages you explicitly trust. C# mods execute code.";

        public ManagerOverlay(RobotopiaModManagerPlugin plugin, ManagerFileLogger logger)
        {
            this.plugin = plugin;
            this.logger = logger;
        }

        public void Toggle()
        {
            if (root == null)
            {
                Build();
            }

            SetOpen(!root!.activeSelf);
            if (root.activeSelf)
            {
                RefreshContent();
            }
        }

        public void Show()
        {
            if (root == null)
            {
                Build();
            }

            tab = OverlayTab.Installed;
            SetOpen(true);
            RefreshContent();
        }

        public void ShowGamemodes()
        {
            if (root == null)
            {
                Build();
            }

            tab = OverlayTab.Gamemodes;
            SetOpen(true);
            RefreshContent();
        }

        public void Tick()
        {
            var open = root != null && root.activeSelf;
            cursorLease.SetActive(open);
            if (open && Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
            }
        }

        public void Dispose()
        {
            cursorLease.Release();
        }

        private void Build()
        {
            root = NeonUi.CreateOverlayCanvas("RobotopiaModManagerOverlay", 32000, true);
            var dim = NeonUi.CreateImage(root.transform, "Dim", NeonTheme.Dim);
            NeonUi.Stretch(dim.rectTransform);

            var shell = NeonUi.CreatePanel(root.transform, "Shell", NeonTheme.Ink, NeonTheme.CyanDim);
            var shellRect = shell.GetComponent<RectTransform>();
            shellRect.anchorMin = new Vector2(0.07f, 0.08f);
            shellRect.anchorMax = new Vector2(0.93f, 0.92f);
            shellRect.offsetMin = Vector2.zero;
            shellRect.offsetMax = Vector2.zero;
            NeonUi.AddHorizontalLayout(shell, 0f, 0, true);

            var nav = NeonUi.CreatePanel(shell.transform, "Nav", new Color(0.02f, 0.035f, 0.055f, 0.98f), NeonTheme.Violet);
            NeonUi.SetFixedWidth(nav, 230f);
            NeonUi.SetFlexible(nav, 0f, 1f);
            NeonUi.AddVerticalLayout(nav, 10f, 14);
            var brand = NeonUi.CreateText(nav.transform, "Brand", "QUANTUMWORKS", 20, NeonTheme.Cyan, TextAnchor.MiddleLeft, FontStyle.Bold);
            NeonUi.SetFixedHeight(brand.gameObject, 42f);
            AddTabButton(nav.transform, OverlayTab.Gamemodes, "GAMEMODES");
            AddTabButton(nav.transform, OverlayTab.Installed, "INSTALLED");
            AddTabButton(nav.transform, OverlayTab.Packages, "PACKAGES");
            AddTabButton(nav.transform, OverlayTab.Settings, "SETTINGS");
            AddTabButton(nav.transform, OverlayTab.Logs, "LOGS");
            var close = NeonUi.CreateButton(nav.transform, "Close", "CLOSE", Close, new Vector2(190f, 38f), NeonTheme.PanelAlt, NeonTheme.Danger);
            NeonUi.SetFixedHeight(close.gameObject, 38f);

            var main = NeonUi.CreatePanel(shell.transform, "Main", NeonTheme.Panel, NeonTheme.CyanDim);
            NeonUi.SetFlexible(main, 1f, 1f);
            NeonUi.AddVerticalLayout(main, 10f, 14);

            statusText = NeonUi.CreateText(main.transform, "Status", status, 14, NeonTheme.Amber, TextAnchor.MiddleLeft, FontStyle.Bold);
            NeonUi.SetFixedHeight(statusText.gameObject, 44f);

            var contentPanel = NeonUi.CreatePanel(main.transform, "Content", new Color(0.025f, 0.04f, 0.062f, 0.96f), NeonTheme.CyanDim);
            NeonUi.SetFlexible(contentPanel, 1f, 1f);
            NeonUi.AddVerticalLayout(contentPanel, 10f, 12);
            content = contentPanel.transform;
            root.SetActive(false);
        }

        private void AddTabButton(Transform parent, OverlayTab buttonTab, string label)
        {
            var button = NeonUi.CreateButton(parent, label, label, () =>
            {
                tab = buttonTab;
                pendingUninstallId = string.Empty;
                RefreshContent();
            }, new Vector2(190f, 38f), buttonTab == tab ? NeonTheme.PanelAlt : NeonTheme.PanelSoft, buttonTab == tab ? NeonTheme.Cyan : NeonTheme.CyanDim);
            NeonUi.SetFixedHeight(button.gameObject, 38f);
        }

        private void RefreshContent()
        {
            if (content == null)
            {
                return;
            }

            NeonUi.DestroyChildren(content);
            if (statusText != null)
            {
                statusText.text = status;
            }

            switch (tab)
            {
                case OverlayTab.Gamemodes:
                    BuildGamemodes();
                    break;
                case OverlayTab.Installed:
                    BuildInstalled();
                    break;
                case OverlayTab.Packages:
                    BuildPackages();
                    break;
                case OverlayTab.Settings:
                    BuildSettings();
                    break;
                case OverlayTab.Logs:
                    BuildLogs();
                    break;
            }
        }

        private void BuildGamemodes()
        {
            AddHeading("SELECT GAMEMODE", "Launches close this overlay so the world stays in view.");
            var service = plugin.GetWorldService();
            if (service == null)
            {
                AddMessage("World/gamemode service unavailable. Enable Robotopia Worlds and restart.", NeonTheme.Amber);
                return;
            }

            var entries = service.MenuEntries;
            if (entries.Count == 0)
            {
                AddMessage("No gamemodes are registered yet.", NeonTheme.TextMuted);
                return;
            }

            foreach (var entry in entries)
            {
                var entryId = entry.Id;
                var button = NeonUi.CreateButton(content!, "Gamemode_" + entryId, entry.Title.ToUpperInvariant() + "  //  " + entry.Description, () =>
                {
                    var (ok, message) = plugin.LaunchGamemode(entryId);
                    status = message;
                    if (ok)
                    {
                        Close();
                    }
                    else
                    {
                        RefreshContent();
                    }
                }, new Vector2(900f, 46f), NeonTheme.PanelAlt, NeonTheme.Acid);
                NeonUi.SetFixedHeight(button.gameObject, 46f);
            }
        }

        private void BuildInstalled()
        {
            AddHeading("MOD LOADOUT", "Select a package to inspect, enable, disable, or stage removal.");

            var split = NeonUi.CreateObject("Split", content!);
            NeonUi.SetFlexible(split, 1f, 1f);
            NeonUi.AddHorizontalLayout(split, 10f, 0, true);

            var list = NeonUi.CreatePanel(split.transform, "List", NeonTheme.PanelSoft, NeonTheme.CyanDim);
            NeonUi.SetFlexible(list, 0.58f, 1f);
            NeonUi.AddVerticalLayout(list, 8f, 10);
            var actions = NeonUi.CreateObject("Actions", list.transform);
            NeonUi.SetFixedHeight(actions, 40f);
            NeonUi.AddHorizontalLayout(actions, 8f, 0);
            NeonUi.CreateButton(actions.transform, "Toggle", "ENABLE / DISABLE", () => RunAction(() => plugin.ToggleEnabled(selectedModId)), new Vector2(170f, 36f), NeonTheme.PanelAlt, NeonTheme.Cyan);
            NeonUi.CreateButton(actions.transform, "Refresh", "REFRESH", () =>
            {
                plugin.RefreshPackages(saveState: false);
                RefreshContent();
            }, new Vector2(100f, 36f), NeonTheme.PanelAlt, NeonTheme.Cyan);
            NeonUi.CreateButton(actions.transform, "OpenMods", "OPEN MODS", () => plugin.OpenFolder(plugin.Paths.Root), new Vector2(132f, 36f), NeonTheme.PanelAlt, NeonTheme.Violet);

            foreach (var package in plugin.Packages)
            {
                var id = package.Manifest?.Id ?? package.PackagePath;
                var selected = string.Equals(id, selectedModId, StringComparison.OrdinalIgnoreCase);
                var label = PackageListLabel(package);
                var button = NeonUi.CreateButton(list.transform, "Mod_" + Sanitize(id), label, () =>
                {
                    selectedModId = id;
                    pendingUninstallId = string.Empty;
                    status = "Selected " + label;
                    RefreshContent();
                }, new Vector2(760f, 38f), selected ? new Color(0.08f, 0.16f, 0.19f, 0.98f) : NeonTheme.PanelAlt, selected ? NeonTheme.Cyan : NeonTheme.CyanDim);
                NeonUi.SetFixedHeight(button.gameObject, 38f);
            }

            var detail = NeonUi.CreatePanel(split.transform, "Detail", NeonTheme.PanelSoft, NeonTheme.Violet);
            NeonUi.SetFlexible(detail, 0.42f, 1f);
            NeonUi.AddVerticalLayout(detail, 8f, 10);
            BuildModDetail(detail.transform);
        }

        private void BuildModDetail(Transform parent)
        {
            var package = plugin.Packages.FirstOrDefault(p => string.Equals(p.Manifest?.Id, selectedModId, StringComparison.OrdinalIgnoreCase));
            if (package == null)
            {
                AddLocalMessage(parent, "No mod selected.", NeonTheme.TextMuted);
                return;
            }

            if (package.Manifest == null || package.State == null)
            {
                AddLocalMessage(parent, "Invalid package: " + string.Join("; ", package.Errors.ToArray()), NeonTheme.Danger);
                return;
            }

            var manifest = package.Manifest;
            AddLocalMessage(parent, manifest.Name + " " + manifest.Version, NeonTheme.Cyan, 21);
            AddLocalMessage(parent, manifest.Description, NeonTheme.Text, 14);
            var flags = (package.State.Enabled ? "ENABLED" : "DISABLED")
                + (plugin.LoadedModIds.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase) ? "  //  LOADED" : string.Empty)
                + (package.State.RestartRequired ? "  //  RESTART REQUIRED" : string.Empty)
                + (package.State.UninstallPending ? "  //  UNINSTALL PENDING" : string.Empty);
            AddLocalMessage(parent, flags, package.State.RestartRequired || package.State.UninstallPending ? NeonTheme.Amber : NeonTheme.Acid, 14);
            if (plugin.LoadOrder.Errors.TryGetValue(manifest.Id, out var errors))
            {
                AddLocalMessage(parent, "Dependency errors: " + string.Join("; ", errors.ToArray()), NeonTheme.Danger, 14);
            }

            AddLocalMessage(parent, "Permissions: " + string.Join(", ", manifest.Permissions.ToArray()), NeonTheme.TextMuted, 13);

            var row = NeonUi.CreateObject("DetailActions", parent);
            NeonUi.SetFixedHeight(row, 42f);
            NeonUi.AddHorizontalLayout(row, 8f, 0);
            var uninstallLabel = string.Equals(pendingUninstallId, manifest.Id, StringComparison.OrdinalIgnoreCase) ? "CONFIRM REMOVE" : "REMOVE";
            NeonUi.CreateButton(row.transform, "Uninstall", uninstallLabel, () =>
            {
                if (!string.Equals(pendingUninstallId, manifest.Id, StringComparison.OrdinalIgnoreCase))
                {
                    pendingUninstallId = manifest.Id;
                    status = "Confirm removal for " + manifest.Name + ".";
                    RefreshContent();
                    return;
                }

                RunAction(() => plugin.Uninstall(manifest.Id));
                pendingUninstallId = string.Empty;
            }, new Vector2(160f, 36f), new Color(0.16f, 0.06f, 0.07f, 0.95f), NeonTheme.Danger);
        }

        private void BuildPackages()
        {
            AddHeading("PACKAGE INBOX", "Install trusted local .robotopiamod packages only.");
            AddMessage("A package can contain executable C# code. Treat unknown packages like native binaries.", NeonTheme.Amber);
            var input = NeonUi.CreateInput(content!, "PackagePath", "Full path to .robotopiamod", packagePath, value => packagePath = value);
            NeonUi.SetFixedHeight(input.gameObject, 40f);

            var row = NeonUi.CreateObject("PackageActions", content!);
            NeonUi.SetFixedHeight(row, 42f);
            NeonUi.AddHorizontalLayout(row, 8f, 0);
            NeonUi.CreateButton(row.transform, "InstallPath", "INSTALL PATH", () => RunAction(() => plugin.InstallPackage(packagePath)), new Vector2(130f, 36f), NeonTheme.PanelAlt, NeonTheme.Acid);
            NeonUi.CreateButton(row.transform, "InstallInbox", "INSTALL INBOX", () => RunAction(() => plugin.InstallInboxPackages()), new Vector2(150f, 36f), NeonTheme.PanelAlt, NeonTheme.Cyan);
            NeonUi.CreateButton(row.transform, "OpenInbox", "OPEN INBOX", () => plugin.OpenFolder(plugin.Paths.PackageInbox), new Vector2(130f, 36f), NeonTheme.PanelAlt, NeonTheme.Violet);

            var inbox = plugin.GetInboxPackages();
            AddMessage("INBOX PACKAGES  " + inbox.Count, NeonTheme.Cyan);
            foreach (var file in inbox)
            {
                var captured = file;
                var button = NeonUi.CreateButton(content!, "Inbox_" + Sanitize(Path.GetFileNameWithoutExtension(file)), Path.GetFileName(file), () =>
                {
                    packagePath = captured;
                    RunAction(() => plugin.InstallPackage(captured));
                }, new Vector2(900f, 38f), NeonTheme.PanelAlt, NeonTheme.CyanDim);
                NeonUi.SetFixedHeight(button.gameObject, 38f);
            }
        }

        private void BuildSettings()
        {
            AddHeading("RUNTIME STATUS", "Manager paths and restart state.");
            var restart = plugin.State.Mods.Any(m => m.RestartRequired || m.UninstallPending);
            var text =
                "Mode: trusted local packages" + Environment.NewLine +
                "Restart required: " + (restart ? "YES" : "NO") + Environment.NewLine +
                "Loaded mods: " + plugin.LoadedModIds.Count + Environment.NewLine +
                "Manager root: " + plugin.Paths.Root + Environment.NewLine +
                "Package inbox: " + plugin.Paths.PackageInbox + Environment.NewLine +
                "Logs: " + plugin.Paths.Logs;
            AddMessage(text, restart ? NeonTheme.Amber : NeonTheme.Text);

            var row = NeonUi.CreateObject("SettingsActions", content!);
            NeonUi.SetFixedHeight(row, 42f);
            NeonUi.AddHorizontalLayout(row, 8f, 0);
            NeonUi.CreateButton(row.transform, "OpenRoot", "OPEN ROOT", () => plugin.OpenFolder(plugin.Paths.Root), new Vector2(130f, 36f), NeonTheme.PanelAlt, NeonTheme.Cyan);
            NeonUi.CreateButton(row.transform, "OpenConfig", "OPEN CONFIG", () => plugin.OpenFolder(plugin.Paths.Config), new Vector2(140f, 36f), NeonTheme.PanelAlt, NeonTheme.Violet);
            NeonUi.CreateButton(row.transform, "OpenLogs", "OPEN LOGS", () => plugin.OpenFolder(plugin.Paths.Logs), new Vector2(120f, 36f), NeonTheme.PanelAlt, NeonTheme.Amber);
        }

        private void BuildLogs()
        {
            AddHeading("RECENT LOGS", "Latest manager log lines.");
            var row = NeonUi.CreateObject("LogActions", content!);
            NeonUi.SetFixedHeight(row, 42f);
            NeonUi.AddHorizontalLayout(row, 8f, 0);
            NeonUi.CreateButton(row.transform, "RefreshLogs", "REFRESH", RefreshContent, new Vector2(110f, 36f), NeonTheme.PanelAlt, NeonTheme.Cyan);
            NeonUi.CreateButton(row.transform, "OpenLogs", "OPEN LOGS", () => plugin.OpenFolder(plugin.Paths.Logs), new Vector2(120f, 36f), NeonTheme.PanelAlt, NeonTheme.Amber);

            var text = NeonUi.CreateText(content!, "LogText", plugin.ReadRecentLogLines(80), 13, NeonTheme.Text, TextAnchor.UpperLeft);
            NeonUi.SetFlexible(text.gameObject, 1f, 1f);
        }

        private void AddHeading(string title, string subtitle)
        {
            var heading = NeonUi.CreateText(content!, "Heading", title, 24, NeonTheme.Cyan, TextAnchor.MiddleLeft, FontStyle.Bold);
            NeonUi.SetFixedHeight(heading.gameObject, 34f);
            var sub = NeonUi.CreateText(content!, "Subtitle", subtitle, 14, NeonTheme.TextMuted, TextAnchor.MiddleLeft, FontStyle.Bold);
            NeonUi.SetFixedHeight(sub.gameObject, 24f);
        }

        private void AddMessage(string text, Color color)
        {
            AddLocalMessage(content!, text, color);
        }

        private static void AddLocalMessage(Transform parent, string text, Color color, int size = 15)
        {
            var label = NeonUi.CreateText(parent, "Message", text, size, color, TextAnchor.UpperLeft, size >= 18 ? FontStyle.Bold : FontStyle.Normal);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            NeonUi.SetFixedHeight(label.gameObject, Mathf.Max(34f, size + 18f + (text.Length / 90) * 18f));
        }

        private void RunAction(Func<string> action)
        {
            try
            {
                status = action();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Mod manager UI action failed.");
                status = ex.Message;
            }

            RefreshContent();
        }

        private void Close()
        {
            SetOpen(false);
        }

        private void SetOpen(bool open)
        {
            if (root == null)
            {
                cursorLease.Release();
                return;
            }

            root.SetActive(open);
            cursorLease.SetActive(open);
        }

        private string PackageListLabel(ModPackage package)
        {
            if (package.Manifest == null || package.State == null)
            {
                return "INVALID  //  " + Path.GetFileName(package.PackagePath);
            }

            var loaded = plugin.LoadedModIds.Contains(package.Manifest.Id, StringComparer.OrdinalIgnoreCase) ? " LOADED" : string.Empty;
            var restart = package.State.RestartRequired ? " RESTART" : string.Empty;
            var pending = package.State.UninstallPending ? " PENDING REMOVE" : string.Empty;
            var enabled = package.State.Enabled ? "ENABLED" : "DISABLED";
            return package.Manifest.Name.ToUpperInvariant() + "  " + package.Manifest.Version + "  //  " + enabled + loaded + restart + pending;
        }

        private static string Sanitize(string value)
        {
            return new string(value.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        }

        private enum OverlayTab
        {
            Gamemodes,
            Installed,
            Packages,
            Settings,
            Logs
        }
    }
}
