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
        private readonly PluginInitializationLifetime initializationLifetime = new PluginInitializationLifetime();
        // Long enough for session, package and UI cleanup to drain over a few frames; short enough that a stuck
        // cleanup cannot keep the game open after the player quits.
        private static readonly TimeSpan QuitTeardownLimit = TimeSpan.FromSeconds(10);
        // Quit timing reads a clock that never stops. The startup stopwatch stops once startup is reported, and a
        // stopped clock freezes every deadline read from it: a stuck teardown would keep the game open for good.
        private readonly Stopwatch quitClock = Stopwatch.StartNew();
        private PluginQuitCoordinator? quitCoordinator;
        private PluginQuitHandler? quitHandler;
        private bool teardownFinished;
        private bool quitDeadlineLogged;

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
            paths = new ManagerPaths(BepInEx.Paths.BepInExRootPath);
            try
            {
                initializationLifetime.Start(AdmitStartup, () => DontDestroyOnLoad(gameObject), () =>
                {
                    paths.EnsureCreated();
                    managerLogger = new ManagerFileLogger(paths.ManagerLogFile, Logger);
                });
            }
            catch (Exception error) { Logger.LogError("TopiaForge storage was rejected: " + error); return; }

            // Unity runs no frame once a quit starts destroying objects, and cleanup begun in OnDestroy needs
            // later frames, so a quit is held until the teardown it starts has finished. The handler answers last,
            // so the game's own quit steps run while every mod, and every patch, is still in place.
            quitCoordinator = new PluginQuitCoordinator(BeginPluginTeardown, () => teardownFinished,
                () => quitClock.Elapsed, QuitTeardownLimit,
                error => LogCleanupFailure(error, "TopiaForge teardown could not start when the game quit."));
            quitHandler = new PluginQuitHandler(quitCoordinator, MoveQuitHandlerLast,
                error => LogCleanupFailure(error, "TopiaForge could not wait for the game's own quit steps, so mods unload now."));
            Application.wantsToQuit += OnWantsToQuit;

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
                WatchForLauncherQuit();
                stateStore = ManagerStateStartupStore.Open(paths.StateFile);
                state = stateStore.State;
                if (!stateStore.CanSave)
                {
                    managerLogger.Warn(stateStore.Failure);
                    if (launchProfile != null && !launchProfile.SafeMode)
                    { rejectedLauncherCommand = true; rejectionReason = stateStore.Failure; }
                }
                ApplyStartupRecovery();
                OfferLaunchSession();
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
                // The game's own recording reaches the microphone arbiter before any mod can claim the microphone.
                ObserveNativeRecording();
                // The TopiaForge service a session launch names comes first, then the manager's multiplayer configuration.
                runtime.Multiplayer.Host.ConfigureServiceOrigin(launchServiceOrigin, launchStaging.ReadPreferences()?.ServiceOrigin);
                OpenLauncherChannels();
                var selection = RuntimeSessionSelection.Create(launchProfile?.ProfileId ?? "direct-game", launchProfile?.ProfileRevision ?? 0,
                    packages, loadOrder, validationContext);
                runtime.ActivateSessionRuntime(selection.Profile, rejectedSelections: selection.RejectedSelections);
                launchPublisher = new RuntimeLaunchPublisher(runtime.Sessions, launchStaging,
                    launchProfile?.RequestId ?? rejectedCorrelation?.RequestId,
                    error => managerLogger.Error(error, "Runtime launch publication failed; launcher acknowledgement may remain unconfirmed."),
                    session: LaunchSessionIdentityForPublication(), sessionRefusal: launchSessionRefusal);
                initializationLifetime.InitializeUi(() => runtime.Load(selection.Packages));
                // The provider published its control as it loaded, and ModRuntime attached to it: the provider takes the session now.
                TakeLaunchSession();
                PublishRuntimeObservations();
                // Every selected package now has a binding result, including failed owners.
                ArmWorldLaunch();
                // A session no provider took while mods loaded is withdrawn; no one takes it later.
                WithdrawLaunchSession();
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
                WithdrawLaunchSession();
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
            if (QuitIfLauncherAsks()) return;
            if (quitHandler?.ShouldRequestQuit() == true)
            {
                LogQuitDeadline();
                Application.Quit();
                return;
            }

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
            Application.wantsToQuit -= OnWantsToQuit;
            BeginPluginTeardown();
        }

        private bool OnWantsToQuit()
        {
            var proceed = quitHandler?.Answer() ?? true;
            if (proceed) LogQuitDeadline();
            return proceed;
        }

        // Unity asks wantsToQuit handlers in the order they subscribed, so subscribing again puts this one last.
        private void MoveQuitHandlerLast()
        {
            Application.wantsToQuit -= OnWantsToQuit;
            Application.wantsToQuit += OnWantsToQuit;
            try
            {
                managerLogger?.Info("Quit requested. Mods unload once the game has finished its own quit steps.");
            }
            catch { /* The quit goes ahead even when logging has stopped. */ }
        }

        private void LogQuitDeadline()
        {
            if (quitDeadlineLogged || quitCoordinator?.DeadlinePassed != true) return;
            quitDeadlineLogged = true;
            try
            {
                managerLogger?.Warn("TopiaForge cleanup did not finish within " + QuitTeardownLimit.TotalSeconds
                    + " seconds of quitting; the game quits without a clean-exit record, so the next start opens in safe mode.");
            }
            catch { /* The quit goes ahead even when logging has stopped. */ }
        }

        private void BeginPluginTeardown()
        {
            if (!initializationLifetime.TryBeginTeardown()) return;
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
            // A launched session still running ends as the game closes, and its terminal record says how cleanly.
            launchPublisher?.Dispose(shutdown.Succeeded);
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
                initializationLifetime.ShutdownUi(() => { TopiaForgeUi.Shutdown(); });
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

            teardownFinished = true;
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
