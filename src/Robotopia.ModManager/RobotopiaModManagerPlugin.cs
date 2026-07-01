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
        public const string PluginVersion = "0.1.0";

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

                RefreshPackages(saveState: true);
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
            var files = Directory.Exists(paths.PackageInbox)
                ? Directory.GetFiles(paths.PackageInbox, "*.robotopiamod", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();

            if (files.Length == 0)
            {
                return "No .robotopiamod files found in package-inbox.";
            }

            var messages = new List<string>();
            foreach (var file in files)
            {
                messages.Add(Path.GetFileName(file) + ": " + InstallPackage(file));
            }

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
