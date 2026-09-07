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
        private ManagerStateStartupStore stateStore = null!;
        public bool CanSaveState => stateStore?.CanSave == true;
        public string StatePersistenceError => stateStore?.Failure ?? "Manager state is not ready.";
        private ManagerFileLogger managerLogger = null!;
        private ModRuntime runtime = null!;
        private ManagerOverlay overlay = null!;
        private MenuButtonInjector menuButtonInjector = null!;
        private ProfileLaunchConfigurationV4? launchProfile;
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
            try
            {
                launchStaging = new LaunchStagingStore(paths);
                paths.EnsureCreated();
                managerLogger = new ManagerFileLogger(paths.ManagerLogFile, Logger);
            }
            catch (Exception error) { Logger.LogError("TopiaForge storage was rejected: " + error); return; }

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
                launchProfile = ConsumeLaunchProfile();
                stateStore = ManagerStateStartupStore.Open(paths.StateFile);
                state = stateStore.State;
                if (!stateStore.CanSave)
                {
                    managerLogger.Warn(stateStore.Failure);
                    if (launchProfile != null && !launchProfile.SafeMode)
                    { rejectedLauncherCommand = true; rejectionReason = stateStore.Failure; }
                }
                ApplyStartupRecovery();
                try
                {
                    if (CanSaveState) registry.ApplyPendingUninstalls(paths, state);
                }
                catch (Exception ex)
                {
                    managerLogger.Error(ex, "Failed to apply pending uninstalls.");
                }

                if (!hasLauncherCommand && CanSaveState)
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
                var selection = RuntimeSessionSelection.Create(launchProfile?.ProfileId ?? "direct-game", launchProfile?.ProfileRevision ?? 0,
                    packages, loadOrder, validationContext);
                runtime.ActivateSessionRuntime(selection.Profile, rejectedSelections: selection.RejectedSelections);
                launchPublisher = new RuntimeLaunchPublisher(runtime.Sessions, launchStaging,
                    launchProfile?.RequestId ?? rejectedCorrelation?.RequestId,
                    error => managerLogger.Error(error, "Runtime launch publication failed; launcher acknowledgement may remain unconfirmed."));
                runtime.Load(selection.Packages);
                PublishRuntimeObservations();
                // Every selected package now has a binding result, including failed owners.
                ArmWorldLaunch();
                RecordStartupStage("mod-loading", stageStart);
                if (!hasLauncherCommand)
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
            PublishRuntimeObservations();
            overlay.Tick();
            menuButtonInjector.Update();
        }

        private void OnDestroy()
        {
            ready = false;
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

            if (runtime == null)
            {
                FinishPluginTeardown(OperationResult<bool>.Success(true));
                return;
            }
            try
            {
                _ = RuntimeShutdownCompletion.Observe(runtime.NativeDispatcher, runtime.UnloadAllAsync(),
                    FinishPluginTeardown, exception => LogCleanupFailure(exception, "Runtime shutdown completion failed."));
            }
            catch (Exception exception)
            {
                LogCleanupFailure(exception, "Runtime teardown could not be scheduled on the process host.");
            }
        }

        private void FinishPluginTeardown(OperationResult<bool> shutdown)
        {
            PublishRuntimeObservations();
            launchPublisher?.Dispose();
            if (!shutdown.Succeeded)
                LogCleanupFailure(new InvalidOperationException(shutdown.ErrorMessage), "Mod runtime teardown completed with failures.");
            if (runtime != null)
            {
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
            try
            {
                if (managerLogger != null) managerLogger.Error(exception, message);
                else Logger.LogError(message + " " + exception);
            }
            catch { /* Teardown must attempt every independent cleanup even after logging stops. */ }
        }
    }
}
