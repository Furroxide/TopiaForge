using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using Robotopia.ModManager.Core;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;
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
        private ProfileLaunchConfiguration? launchProfile;
        private ManifestValidationContext validationContext = ManifestValidationContext.Current;
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

                if (InstalledGameVersionReader.TryRead(BepInEx.Paths.GameRootPath, out var gameVersion, out var versionError))
                {
                    validationContext = new ManifestValidationContext(
                        gameVersion: gameVersion,
                        requireKnownGameVersion: true);
                    managerLogger.Info("Detected Robotopia game version " + gameVersion + ".");
                }
                else
                {
                    validationContext = new ManifestValidationContext(requireKnownGameVersion: true);
                    managerLogger.Warn("Robotopia game version could not be established: " + versionError
                        + " Mods with a game compatibility constraint will not load.");
                }

                state = JsonUtil.LoadPersistentFile(paths.StateFile, new ManagerState());
                launchProfile = ConsumeLaunchProfile();
                try
                {
                    registry.ApplyPendingUninstalls(paths, state);
                }
                catch (Exception ex)
                {
                    managerLogger.Error(ex, "Failed to apply pending uninstalls.");
                }

                if (launchProfile == null)
                {
                    InstallInboxAtStartup();
                    registry.PruneSupersededVersions(paths, state,
                        pruned => managerLogger.Info("Pruned superseded package version: " + pruned + "."));
                }
                else
                {
                    // Inbox installation prunes older package versions. Keep
                    // this process's profile snapshot immutable and leave the
                    // inbox untouched for the next normal launch.
                    managerLogger.Info("Deferring package inbox installation for profile launch.");
                    managerLogger.Info("Preserving installed package versions for profile launch.");
                }
                RefreshPackages(saveState: true);
                LogExcludedPackages();
                runtime = new ModRuntime(paths, managerLogger);
                runtime.Load(loadOrder.OrderedPackages);
                if (launchProfile == null)
                {
                    state.ClearAppliedRestartRequirements();
                }
                else
                {
                    managerLogger.Info("Preserving canonical restart requirements after profile launch.");
                }
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

        private ProfileLaunchConfiguration? ConsumeLaunchProfile()
        {
            var configuredPath = Environment.GetEnvironmentVariable(ProfileLaunchConfiguration.EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            try
            {
                var staging = Path.GetFullPath(paths.Staging)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(configuredPath);
                var comparison = Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!string.Equals(Path.GetDirectoryName(fullPath), staging, comparison)
                    || !Path.GetFileName(fullPath).StartsWith("launch-profile-", comparison)
                    || !fullPath.EndsWith(".json", comparison))
                {
                    throw new InvalidDataException("Profile launch file must be an immediate child of manager staging.");
                }

                try
                {
                    if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("Profile launch file cannot be a symbolic link or reparse point.");
                    }

                    var configuration = JsonUtil.LoadFile(fullPath, new ProfileLaunchConfiguration());
                    var errors = configuration.Validate();
                    if (errors.Count != 0)
                    {
                        throw new InvalidDataException(string.Join(" ", errors));
                    }

                    managerLogger.Info("Using one-shot launch profile " + configuration.ProfileId + ".");
                    return configuration;
                }
                finally
                {
                    try
                    {
                        File.Delete(fullPath);
                    }
                    catch (Exception ex)
                    {
                        managerLogger.Warn("Profile launch file could not be consumed: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Profile launch configuration was rejected; entering safe mode.");
                return new ProfileLaunchConfiguration
                {
                    SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                    ProfileId = "rejected-launch-profile",
                    SafeMode = true,
                    InheritManagerModState = false
                };
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
            SceneManager.sceneLoaded -= OnSceneLoaded;
            try
            {
                overlay?.Dispose();
            }
            catch (Exception ex)
            {
                LogCleanupFailure(ex, "Manager overlay teardown failed.");
            }

            try
            {
                menuButtonInjector?.Dispose();
            }
            catch (Exception ex)
            {
                LogCleanupFailure(ex, "Menu button teardown failed.");
            }

            if (runtime != null)
            {
                try
                {
                    runtime.UnloadAll();
                }
                catch (Exception ex)
                {
                    LogCleanupFailure(ex, "Mod runtime teardown failed.");
                }

                try
                {
                    SaveState();
                }
                catch (Exception ex)
                {
                    LogCleanupFailure(ex, "Manager state could not be saved during teardown.");
                }
            }

            try
            {
                QwUi.Shutdown();
            }
            catch (Exception ex)
            {
                LogCleanupFailure(ex, "QwUi global teardown failed.");
            }
        }

        private void LogCleanupFailure(Exception exception, string message)
        {
            if (managerLogger != null)
            {
                managerLogger.Error(exception, message);
            }
            else
            {
                Logger.LogError(message + " " + exception);
            }
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
            var scanState = state;
            if (launchProfile != null)
            {
                scanState = launchProfile.CreateEffectiveState(state);
                // Seed state entries for package directories missing from a
                // recovered/legacy state file, then reapply the exact policy.
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

        public IWorldGamemodeService? GetWorldService()
        {
            return runtime?.GetService<IWorldGamemodeService>();
        }

        public WorldLaunchSettings ReadWorldLaunchSettings()
        {
            try
            {
                return JsonUtil.LoadPersistentFile(
                    paths.GetConfigPath("robotopia.worlds"),
                    new WorldLaunchSettings());
            }
            catch (Exception ex)
            {
                managerLogger.Warn("World launch settings could not be read; using defaults: " + ex.Message);
                return new WorldLaunchSettings();
            }
        }

        public void SaveWorldLaunchSettings(WorldLaunchSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var path = paths.GetConfigPath("robotopia.worlds");
            string existingJson;
            try
            {
                existingJson = JsonUtil.LoadPersistentJsonObject(path, "{}");
            }
            catch (Exception ex)
            {
                managerLogger.Warn("World config could not be read within the bounded JSON policy; replacing it: "
                    + ex.Message);
                existingJson = "{}";
            }

            string merged;
            try
            {
                merged = settings.MergeIntoJson(existingJson);
            }
            catch (Exception ex)
            {
                // A malformed provider config was already unreadable. Recover the launch fields rather than
                // making PLAY unusable; the warning makes the loss of unrecoverable raw content explicit.
                managerLogger.Warn("World config could not be merged; replacing malformed JSON: " + ex.Message);
                merged = settings.MergeIntoJson("{}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? paths.Config);
            var tempPath = path + ".manager.tmp";
            File.WriteAllText(tempPath, merged);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(path);
                    File.Move(tempPath, path);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }
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

        public (bool Ok, string Message) LaunchGamemodeSelection(
            string entryId,
            string worldId,
            string gamemodeId,
            string loadMode)
        {
            var service = GetWorldService();
            if (service == null)
            {
                return (false, "World/gamemode service unavailable. Enable the Robotopia Worlds mod.");
            }

            try
            {
                var world = service.Worlds.FirstOrDefault(item =>
                    string.Equals(item.Id, worldId, StringComparison.OrdinalIgnoreCase));
                if (world == null)
                {
                    return (false, "Unknown world: " + worldId);
                }

                var gamemode = service.Gamemodes.FirstOrDefault(item =>
                    string.Equals(item.Id, gamemodeId, StringComparison.OrdinalIgnoreCase));
                if (gamemode == null)
                {
                    return (false, "Unknown gamemode: " + gamemodeId);
                }

                var resolvedLoadMode = WorldLaunchSettings.ReconcileLoadMode(
                    world.SupportsSceneReplacement,
                    world.SupportsAdditiveArena,
                    loadMode);
                var existing = ReadWorldLaunchSettings();
                var settings = new WorldLaunchSettings
                {
                    SelectedWorldId = worldId,
                    SelectedGamemodeId = gamemodeId,
                    LoadMode = resolvedLoadMode,
                    AutoLoadOnStart = existing.AutoLoadOnStart,
                    AllowAdditiveFallback = existing.AllowAdditiveFallback,
                    EndSessionOnMenuScene = existing.EndSessionOnMenuScene,
                    InterceptPauseMenu = existing.InterceptPauseMenu
                };
                SaveWorldLaunchSettings(settings);

                var result = service.Load(new WorldLoadRequest(
                    worldId,
                    gamemodeId,
                    settings.PreferSceneReplacement,
                    settings.AllowAdditiveFallback));
                managerLogger.Info("Gamemode launch '" + entryId + "' world '" + world.Name + "' [" + world.Id
                    + "] gamemode '" + gamemode.Name + "' [" + gamemode.Id + "] loadMode '" + settings.LoadMode
                    + "': " + result.Message);
                return (result.Ok, result.Message);
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Failed to launch gamemode '" + entryId + "' for world '" + worldId + "'.");
                return (false, "Failed to launch: " + ex.Message);
            }
        }
    }
}
