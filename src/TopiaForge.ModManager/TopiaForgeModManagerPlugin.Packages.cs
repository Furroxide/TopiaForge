using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        private void InstallInboxAtStartup()
        {
            try
            {
                SweepConsumedInboxFiles();

                // Nothing is loaded yet, so the installs apply to this launch (no restart flag).
                var results = packageInstaller.InstallInbox(
                    paths,
                    state,
                    restartRequired: false,
                    validationContext);
                if (results.Count == 0)
                {
                    return;
                }

                foreach (var result in results)
                {
                    var fileName = Path.GetFileName(result.FilePath);
                    if (result.Superseded)
                    {
                        managerLogger.Info("Inbox package " + fileName + " superseded by a newer version in the inbox.");
                    }
                    else if (result.Install!.Ok)
                    {
                        managerLogger.Info("Installed mod package from inbox: " + result.Install.Manifest!.Id
                            + " " + result.Install.Manifest.Version + ".");
                    }
                    else
                    {
                        managerLogger.Warn("Inbox package " + fileName + " failed to install: "
                            + string.Join("; ", result.Install.Errors) + " (file left in the inbox).");
                    }

                    if (result.ConsumeError != null)
                    {
                        managerLogger.Warn("Inbox file " + fileName + " could not be removed after install: "
                            + result.ConsumeError);
                    }
                }

                SaveState();
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Startup inbox install failed.");
            }
        }

        // *.topiaforgemod.installed files are the rename fallback for inbox files that were locked at
        // consume time; they are dead weight once the lock is gone.
        private void SweepConsumedInboxFiles()
        {
            if (!Directory.Exists(paths.PackageInbox))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(paths.PackageInbox, "*.topiaforgemod.installed", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Still locked; retried next launch.
                }
            }
        }

        private void LogExcludedPackages()
        {
            foreach (var package in packages)
            {
                if (!package.IsValid)
                {
                    var id = package.Manifest?.Id ?? Path.GetFileName(package.PackagePath);
                    managerLogger.Warn("Package " + id + " (" + package.PackagePath + ") is invalid and will not load: "
                        + (package.Errors.Count > 0 ? string.Join("; ", package.Errors) : "manifest or state missing"));
                }
            }

            foreach (var entry in loadOrder.Errors)
            {
                managerLogger.Warn("Package " + entry.Key + " excluded from load order: " + string.Join("; ", entry.Value));
            }
        }

        public void RefreshPackages(bool saveState)
        {
            var scanState = state;
            if (launchProfile != null)
            {
                scanState = launchProfile.CreateEffectiveState(state);
                // Seed state entries for package directories missing from the
                // current state file, then reapply the exact profile policy.
                registry.Scan(paths, scanState, validationContext);
                launchProfile.ApplyTo(scanState);
            }

            packages = registry.Scan(paths, scanState, validationContext);
            loadOrder = dependencyResolver.Resolve(packages);
            if (saveState)
            {
                SaveState();
            }
        }

        public string InstallPackage(string packagePath)
        {
            var result = packageInstaller.Install(
                packagePath,
                paths,
                state,
                restartRequired: true,
                validationContext);
            if (!result.Ok)
            {
                var message = string.Join("; ", result.Errors);
                managerLogger.Warn("Package install failed: " + message);
                return message;
            }

            managerLogger.Info("Installed mod package: " + result.Manifest!.Id + " " + result.Manifest.Version);
            RefreshPackages(saveState: true);
            return "Installed " + result.Manifest.Name + ". Restart Robotopia to load it.";
        }

        public string InstallInboxPackages()
        {
            var results = packageInstaller.InstallInbox(
                paths,
                state,
                restartRequired: true,
                validationContext);
            if (results.Count == 0)
            {
                return "No .topiaforgemod files found in package-inbox.";
            }

            var messages = new List<string>();
            foreach (var result in results)
            {
                var fileName = Path.GetFileName(result.FilePath);
                if (result.Superseded)
                {
                    messages.Add(fileName + ": superseded by a newer version in the inbox.");
                }
                else if (result.Install!.Ok)
                {
                    managerLogger.Info("Installed mod package: " + result.Install.Manifest!.Id
                        + " " + result.Install.Manifest.Version);
                    messages.Add(fileName + ": Installed " + result.Install.Manifest.Name
                        + ". Restart Robotopia to load it.");
                }
                else
                {
                    var message = string.Join("; ", result.Install.Errors);
                    managerLogger.Warn("Package install failed: " + message);
                    messages.Add(fileName + ": " + message);
                }
            }

            RefreshPackages(saveState: true);
            return string.Join(Environment.NewLine, messages.ToArray());
        }

        public string ToggleEnabled(string id)
        {
            var mod = state.Find(id);
            if (mod == null)
            {
                return "Unknown mod: " + id;
            }

            mod.Enabled = !mod.Enabled;
            if (mod.Enabled)
            {
                mod.QuarantineReason = string.Empty;
                mod.QuarantinedAtUtc = string.Empty;
            }
            mod.RestartRequired = true;
            mod.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
            SaveState();
            RefreshPackages(saveState: false);
            return (mod.Enabled ? "Enabled " : "Disabled ") + mod.Name + ". Restart required.";
        }

        public string Uninstall(string id)
        {
            var mod = state.Find(id);
            if (mod == null)
            {
                return "Unknown mod: " + id;
            }

            if (runtime.IsLoaded(id))
            {
                mod.Enabled = false;
                mod.UninstallPending = true;
                mod.RestartRequired = true;
                SaveState();
                RefreshPackages(saveState: false);
                return "Uninstall staged for " + mod.Name + ". Restart required.";
            }

            registry.RemoveInstalledPackage(paths, state, id);
            SaveState();
            RefreshPackages(saveState: false);
            return "Uninstalled " + mod.Name + ".";
        }

        public IReadOnlyList<string> GetInboxPackages()
        {
            if (!Directory.Exists(paths.PackageInbox))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(paths.PackageInbox, "*.topiaforgemod", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string ReadRecentLogLines(int maxLines)
        {
            if (!File.Exists(paths.ManagerLogFile))
            {
                return "No manager log exists yet.";
            }

            var tail = BoundedTextFile.ReadTail(
                paths.ManagerLogFile,
                Math.Max(1, Math.Min(maxLines, 2000)),
                maxBytes: 4 * 1024 * 1024);
            return tail.Truncated
                ? "[manager.log output truncated to bounded tail]" + Environment.NewLine + tail.Text
                : tail.Text;
        }

        public void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Failed to open folder: " + path);
            }
        }

        public void SaveState()
        {
            JsonUtil.SaveFile(paths.StateFile, state);
        }
    }
}
