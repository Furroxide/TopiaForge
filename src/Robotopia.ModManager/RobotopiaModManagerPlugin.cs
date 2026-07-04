using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using Robotopia.ModManager.Core;
using Robotopia.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Robotopia.ModManager
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class RobotopiaModManagerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "robotopia.modmanager";
        public const string PluginName = "QuantumWorks";
        public const string PluginVersion = RobotopiaVersions.LoaderVersion;

        private readonly PackageInstaller packageInstaller = new PackageInstaller();
        private readonly ModRegistry registry = new ModRegistry();
        private readonly DependencyResolver dependencyResolver = new DependencyResolver();
        private ManagerPaths paths = null!;
        private ManagerState state = null!;
        private ManagerFileLogger managerLogger = null!;
        private ModRuntime runtime = null!;
        private ManagerOverlay overlay = null!;
        private MenuButtonInjector menuButtonInjector = null!;
        private IReadOnlyList<ModPackage> packages = Array.Empty<ModPackage>();
        private LoadOrderResult loadOrder = new LoadOrderResult(Array.Empty<ModPackage>(), new Dictionary<string, IReadOnlyList<string>>());
        private bool ready;

        public ManagerPaths Paths => paths;
        public ManagerState State => state;
        public IReadOnlyList<ModPackage> Packages => packages;
        public LoadOrderResult LoadOrder => loadOrder;
        public IReadOnlyCollection<string> LoadedModIds => runtime?.LoadedModIds ?? Array.Empty<string>();

        /// <summary>Why a mod failed/was skipped at load time (null when it loaded or wasn't attempted).</summary>
        public string? GetLoadFailure(string id) => runtime?.GetLoadFailure(id);

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            paths = new ManagerPaths(BepInEx.Paths.BepInExRootPath);
            paths.EnsureCreated();
            managerLogger = new ManagerFileLogger(paths.ManagerLogFile, Logger);

            try
            {
                managerLogger.Info("QuantumWorks starting.");

                state = JsonUtil.LoadFile(paths.StateFile, new ManagerState());
                try
                {
                    registry.ApplyPendingUninstalls(paths, state);
                }
                catch (Exception ex)
                {
                    managerLogger.Error(ex, "Failed to apply pending uninstalls.");
                }

                InstallInboxAtStartup();
                registry.PruneSupersededVersions(paths, state,
                    pruned => managerLogger.Info("Pruned superseded package version: " + pruned + "."));
                RefreshPackages(saveState: true);
                LogExcludedPackages();
                runtime = new ModRuntime(paths, managerLogger);
                runtime.Load(loadOrder.OrderedPackages);
                state.ClearAppliedRestartRequirements();
                SaveState();

                overlay = new ManagerOverlay(this, managerLogger);
                menuButtonInjector = new MenuButtonInjector(overlay, managerLogger);
                SceneManager.sceneLoaded += OnSceneLoaded;
                ready = true;
                managerLogger.Info("QuantumWorks ready. Press F10 to open the overlay.");
            }
            catch (Exception ex)
            {
                // Stay inert (ready == false) rather than crashing the game. OnDestroy still unloads any mods
                // that did load before the failure (it gates on runtime, not ready).
                managerLogger.Error(ex, "QuantumWorks failed to initialize.");
            }
        }

        private void Update()
        {
            if (!ready)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.F10))
            {
                overlay.Toggle();
            }

            runtime.DispatchUpdate(Time.deltaTime);
            overlay.Tick();
            menuButtonInjector.Update();
        }

        private void OnDestroy()
        {
            if (runtime == null)
            {
                return;
            }

            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (overlay != null)
            {
                overlay.Dispose();
            }

            runtime.UnloadAll();
            SaveState();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            runtime.DispatchSceneLoaded(scene.name);
            menuButtonInjector.ResetForScene(scene.name);
        }

        /// <summary>
        /// Installs everything waiting in the package-inbox before any mod loads, so a freshly staged
        /// dev-install (or a file the user dropped in) is live on the very next launch — no F10 install
        /// step, and no window where an updated loader runs against binary-stale installed packages.
        /// </summary>
        private void InstallInboxAtStartup()
        {
            try
            {
                SweepConsumedInboxFiles();

                // Nothing is loaded yet, so the installs apply to this launch (no restart flag).
                var results = packageInstaller.InstallInbox(paths, state, restartRequired: false);
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

        // *.robotopiamod.installed files are the rename fallback for inbox files that were locked at
        // consume time; they are dead weight once the lock is gone.
        private void SweepConsumedInboxFiles()
        {
            if (!Directory.Exists(paths.PackageInbox))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(paths.PackageInbox, "*.robotopiamod.installed", SearchOption.TopDirectoryOnly))
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
            packages = registry.Scan(paths, state);
            loadOrder = dependencyResolver.Resolve(packages);
            if (saveState)
            {
                SaveState();
            }
        }

        public string InstallPackage(string packagePath)
        {
            var result = packageInstaller.Install(packagePath, paths, state, restartRequired: true);
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
            var results = packageInstaller.InstallInbox(paths, state, restartRequired: true);
            if (results.Count == 0)
            {
                return "No .robotopiamod files found in package-inbox.";
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

            var modRoot = Path.Combine(paths.Packages, mod.Id);
            if (Directory.Exists(modRoot))
            {
                Directory.Delete(modRoot, true);
            }

            state.Remove(id);
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

            return Directory.GetFiles(paths.PackageInbox, "*.robotopiamod", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string ReadRecentLogLines(int maxLines)
        {
            if (!File.Exists(paths.ManagerLogFile))
            {
                return "No manager log exists yet.";
            }

            var lines = File.ReadAllLines(paths.ManagerLogFile);
            return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - maxLines)).ToArray());
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

        public IWorldGamemodeService? GetWorldService()
        {
            return runtime?.GetService<IWorldGamemodeService>();
        }

        public (bool Ok, string Message) LaunchGamemode(string entryId)
        {
            var service = GetWorldService();
            if (service == null)
            {
                return (false, "World/gamemode service unavailable. Enable the Robotopia Worlds mod.");
            }

            try
            {
                var result = service.LaunchMenuEntry(entryId);
                managerLogger.Info("Gamemode launch '" + entryId + "': " + result.Message);
                return (result.Ok, result.Message);
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Failed to launch gamemode '" + entryId + "'.");
                return (false, "Failed to launch: " + ex.Message);
            }
        }
    }
}
