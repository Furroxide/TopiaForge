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
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class TopiaForgeModManagerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "io.github.furroxide.topiaforge.modmanager";
        public const string PluginName = "TopiaForge";
        public const string PluginVersion = TopiaForgeVersions.BepInExPluginVersion;

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
        private readonly Stopwatch startupStopwatch = new Stopwatch();
        private readonly List<LastRunStage> startupStages = new List<LastRunStage>();
        private readonly Dictionary<int, SceneLoadMode> loadedSceneModes =
            new Dictionary<int, SceneLoadMode>();
        private readonly HashSet<int> suppressNextActivation = new HashSet<int>();
        private readonly HashSet<int> lifecycleActivationPublishedAtLoad = new HashSet<int>();
        private int lastActiveSceneHandle;
        private StartupJournal? startupJournal;
        private StartupRecoveryDecision startupRecovery = StartupRecoveryDecision.None;
        private string startupStartedAtUtc = string.Empty;
        private bool startupCompleted;
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
            startupStartedAtUtc = DateTime.UtcNow.ToString("O");
            startupStopwatch.Restart();
            DontDestroyOnLoad(gameObject);
            paths = new ManagerPaths(BepInEx.Paths.BepInExRootPath);
            paths.EnsureCreated();
            managerLogger = new ManagerFileLogger(paths.ManagerLogFile, Logger);

            try
            {
                var stageStart = startupStopwatch.ElapsedMilliseconds;
                managerLogger.Info("TopiaForge starting.");
                TryBeginStartupJournal();

                if (InstalledGameVersionReader.TryRead(BepInEx.Paths.GameRootPath, out var gameVersion, out var versionError))
                {
                    validationContext = ManifestValidationContext.ForCurrentRuntime(
                        gameVersion: gameVersion,
                        requireKnownGameVersion: true);
                    managerLogger.Info("Detected Robotopia game version " + gameVersion + ".");
                }
                else
                {
                    validationContext = ManifestValidationContext.ForCurrentRuntime(requireKnownGameVersion: true);
                    managerLogger.Warn("Robotopia game version could not be established: " + versionError
                        + " Mods with a game compatibility constraint will not load.");
                }
                RecordStartupStage("environment", stageStart);

                stageStart = startupStopwatch.ElapsedMilliseconds;
                state = JsonUtil.LoadPersistentFile(paths.StateFile, new ManagerState());
                state.Normalize();
                launchProfile = ConsumeLaunchProfile();
                ApplyStartupRecovery();
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
                }
                else
                {
                    // Keep this process's exact profile snapshot immutable and leave newly delivered
                    // packages in the inbox for the next normal launch.
                    managerLogger.Info("Deferring package inbox installation for profile launch.");
                    managerLogger.Info("Preserving installed package versions for profile launch.");
                }
                RecordStartupStage("state-and-packages", stageStart);

                stageStart = startupStopwatch.ElapsedMilliseconds;
                RefreshPackages(saveState: true);
                LogExcludedPackages();
                RecordStartupStage("validation-and-ordering", stageStart);

                stageStart = startupStopwatch.ElapsedMilliseconds;
                runtime = new ModRuntime(
                    paths,
                    managerLogger,
                    validationContext,
                    startupJournal == null ? null : new StartupJournalLoadObserver(startupJournal, managerLogger));
                runtime.Load(loadOrder.OrderedPackages);
                // Every mod's OnLoad has now run, so gamemodes contributed by mods that load after Worlds
                // (all of them -- they declare loadAfter: worlds) are registered and can be launched into.
                ArmWorldLaunch();
                RecordStartupStage("mod-loading", stageStart);
                if (launchProfile == null)
                {
                    state.ClearAppliedRestartRequirements();
                }
                else
                {
                    managerLogger.Info("Preserving canonical restart requirements after profile launch.");
                }
                SaveState();

                stageStart = startupStopwatch.ElapsedMilliseconds;
                overlay = new ManagerOverlay(this, managerLogger);
                menuButtonInjector = new MenuButtonInjector(overlay, managerLogger);
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                SceneManager.activeSceneChanged += OnActiveSceneChanged;
                DeliverInitialScene();
                ready = true;
                RecordStartupStage("manager-ui", stageStart);
                startupCompleted = true;
                TryMarkStartupComplete();
                WriteLastRunReport(null);
                managerLogger.Info(
                    "TopiaForge ready. Use the GAMEMODES and TOPIAFORGE buttons on the main menu, or press F10.");
            }
            catch (Exception ex)
            {
                // Stay inert (ready == false) rather than crashing the game. OnDestroy still unloads any mods
                // that did load before the failure (it gates on runtime, not ready).
                managerLogger.Error(ex, "TopiaForge failed to initialize.");
                WriteLastRunReport(ex);
            }
        }

        private void TryBeginStartupJournal()
        {
            try
            {
                startupJournal = StartupJournal.Begin(paths.StartupJournalFile, out startupRecovery);
            }
            catch (Exception ex)
            {
                startupJournal = null;
                startupRecovery = StartupRecoveryDecision.None;
                managerLogger.Warn("Startup recovery journal is unavailable: " + ex.Message);
            }
        }

        private void ApplyStartupRecovery()
        {
            if (!string.IsNullOrEmpty(startupRecovery.QuarantineModId))
            {
                managerLogger.Warn(
                    "Automatically quarantined " + startupRecovery.QuarantineModId + ": " + startupRecovery.Reason);
            }

            if (startupRecovery.SafeMode)
            {
                managerLogger.Warn("Automatic startup recovery enabled safe mode: " + startupRecovery.Reason);
            }

            // Recovery is evaluated after consuming the launcher's one-shot profile. It must therefore be
            // layered over that profile: ambiguous crashes force temporary safe mode, while precise blame
            // removes only the quarantined owner from an exact profile. The policy clones caller input so all
            // unrelated profile selections remain intact.
            launchProfile = StartupRecoveryPolicy.Apply(
                launchProfile,
                state,
                startupRecovery,
                DateTime.UtcNow);
        }

        private void TryMarkStartupComplete()
        {
            try
            {
                startupJournal?.MarkStartupComplete();
            }
            catch (Exception ex)
            {
                managerLogger.Warn("Startup journal could not record completion: " + ex.Message);
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
            UpdatePendingWorldLaunch(Time.deltaTime);
            overlay.Tick();
            menuButtonInjector.Update();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
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
                TopiaForgeUi.Shutdown();
            }
            catch (Exception ex)
            {
                LogCleanupFailure(ex, "TopiaForgeUi global teardown failed.");
            }

            if (startupCompleted)
            {
                try
                {
                    startupJournal?.MarkCleanExit();
                }
                catch (Exception ex)
                {
                    LogCleanupFailure(ex, "Startup journal could not record a clean exit.");
                }
            }

            try
            {
                managerLogger?.Dispose();
            }
            catch
            {
                // All independent log sinks are already tearing down.
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
    }
}
