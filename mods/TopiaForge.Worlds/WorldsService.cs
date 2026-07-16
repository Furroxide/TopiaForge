using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    public sealed class WorldsService : IWorldGamemodeService, IWorldTransitionState,
        IOwnerBoundExtensionFactory, IDisposable
    {
        // Aliases of the SDK's WellKnownIds so consumers that cannot reference this assembly and this
        // service always agree on the ids (SdkSurfaceTests pins the WellKnownIds values).
        public const string OpenSandboxWorldId = WellKnownWorldIds.OpenSandboxWorld;
        public const string SandboxGamemodeId = WellKnownWorldIds.SandboxGamemode;

        private readonly IModLogger logger;
        private readonly IModFiles files;
        private readonly GameLevelBridge levelBridge;
        private readonly List<WorldDefinition> worlds = new List<WorldDefinition>();
        private readonly List<GamemodeDefinition> gamemodes = new List<GamemodeDefinition>();
        private readonly List<GamemodeMenuEntry> menuEntries = new List<GamemodeMenuEntry>();
        private readonly Dictionary<string, Registration> worldRegistrations =
            new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Registration> gamemodeRegistrations =
            new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Registration> menuEntryRegistrations =
            new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> worldCheckpoints = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly ReadOnlyCollection<WorldDefinition> worldsView;
        private readonly ReadOnlyCollection<GamemodeDefinition> gamemodesView;
        private readonly ReadOnlyCollection<GamemodeMenuEntry> menuEntriesView;
        private readonly Dictionary<string, ICustomWorldContent> customWorldContent =
            new Dictionary<string, ICustomWorldContent>(StringComparer.OrdinalIgnoreCase);
        private GameObject? arenaRoot;
        private VolumeProfile? arenaProfile;
        private float lastLaunchTime = -10f;
        // Provisional scene-load lifecycle. Async loader faults can arrive off-thread; the tracker carries
        // them to UpdateTransition on the Unity thread and generation-isolates late faults from older loads.
        private const float TransitionTimeoutSeconds = 30f;
        private const int MaxCatalogBytes = 1024 * 1024;
        private readonly SceneTransitionTracker transitionTracker = new SceneTransitionTracker();
        private string sessionSceneName = string.Empty;
        // Open Sandbox arena is built once the game's clean play scene finishes loading (async); this tracks the
        // one-shot "build the arena on the next sandbox-scene load" handshake set up by LoadOpenSandbox.
        private bool sandboxArenaPending;
        private bool disposed;
        // One-shot payload armed by LoadCustomWorld and consumed on the same sandbox-scene load: the custom
        // world's pre-created content, waiting for the play scene (and its player spawn) to exist.
        private PendingCustomWorld? pendingCustomWorld;
        private IWorldContent? activeWorldContent;

        public WorldsService(IModLogger logger, IModFiles files)
        {
            this.logger = logger;
            this.files = files;
            levelBridge = new GameLevelBridge(logger);
            worldsView = new ReadOnlyCollection<WorldDefinition>(worlds);
            gamemodesView = new ReadOnlyCollection<GamemodeDefinition>(gamemodes);
            menuEntriesView = new ReadOnlyCollection<GamemodeMenuEntry>(menuEntries);

            // Persistent scene hook (removed in Dispose). Registered here — before the manager plugin's own
            // sceneLoaded dispatch to mods — so the session is already ended by the time per-session handlers
            // (e.g. a gamemode controller's own sceneLoaded hook) run in the same dispatch.
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // Live read-only views over the registries (registries are only mutated on the main thread during load).
        public IReadOnlyList<WorldDefinition> Worlds => worldsView;
        public IReadOnlyList<GamemodeDefinition> Gamemodes => gamemodesView;
        public IReadOnlyList<GamemodeMenuEntry> MenuEntries => menuEntriesView;
        public WorldSession? CurrentSession { get; private set; }

        public bool IsTransitionInFlight =>
            !disposed && transitionTracker.IsInFlight(Time.realtimeSinceStartup, TransitionTimeoutSeconds);

        public event Action<WorldSession>? SessionChanged;
        public event Action<WorldSessionEnd>? SessionEnded;

        // Config gate for the automatic end-on-menu behaviour (WorldsConfig.EndSessionOnMenuScene). Explicit
        // EndSession calls are never gated.
        public bool EndSessionOnMenuScene { get; set; } = true;

        public void DiscoverBuiltIns()
        {
            ThrowIfDisposed();
            RegisterWorld(new WorldDefinition(
                OpenSandboxWorldId,
                "Open Sandbox",
                "Generated open-world sandbox arena.",
                supportsAdditiveArena: true));
            RegisterGamemode(new GamemodeDefinition(
                SandboxGamemodeId,
                "Sandbox",
                "Freeform creator sandbox."));

            // Prefer the game's curated level entry points: these carry a checkpoint asset, so we can launch
            // them through the game's own loader and they come up in correct play state (player + HDRP).
            var levels = levelBridge.GetLevels();
            if (levels.Count > 0)
            {
                foreach (var level in levels)
                {
                    var worldId = "io.github.furroxide.topiaforge.worlds.level." + Slug(level.SceneName);
                    RegisterWorld(new WorldDefinition(
                        worldId,
                        level.DisplayName,
                        string.IsNullOrWhiteSpace(level.Description) ? "First-party Robotopia level." : level.Description,
                        level.SceneName,
                        firstParty: true,
                        supportsSceneReplacement: true,
                        supportsAdditiveArena: false));
                    worldCheckpoints[worldId] = level.CheckpointAsset;
                }
            }
            else
            {
                DiscoverBuildSettingsScenes();
            }
        }

        private void DiscoverBuildSettingsScenes()
        {
            var activeScene = SceneManager.GetActiveScene().name;
            for (var index = 0; index < SceneManager.sceneCountInBuildSettings; index++)
            {
                var scenePath = SceneUtility.GetScenePathByBuildIndex(index);
                var sceneName = Path.GetFileNameWithoutExtension(scenePath);
                if (string.IsNullOrWhiteSpace(sceneName))
                {
                    continue;
                }

                // Skip menu/boot/loader scenes so users cannot "launch" a non-gameplay scene as a world.
                if (string.Equals(sceneName, activeScene, StringComparison.OrdinalIgnoreCase) || GameScenes.IsNonGameplayScene(sceneName))
                {
                    continue;
                }

                RegisterWorld(new WorldDefinition(
                    "io.github.furroxide.topiaforge.worlds.first-party." + Slug(sceneName),
                    sceneName,
                    "First-party Robotopia scene.",
                    sceneName,
                    firstParty: true,
                    supportsSceneReplacement: true,
                    supportsAdditiveArena: true));
            }
        }

        public OperationResult<IWorldRegistration> RegisterWorld(
            WorldDefinition world,
            ICustomWorldContent? content = null)
        {
            ThrowIfDisposed();
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (worldRegistrations.ContainsKey(world.Id))
            {
                return OperationResult<IWorldRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "World '" + world.Id + "' is already registered.");
            }

            worlds.Add(world);
            // A plain re-registration means "this id is a normal world again" — drop any stale content link.
            customWorldContent.Remove(world.Id);
            if (content != null)
            {
                customWorldContent[world.Id] = content;
            }

            var registration = new Registration(this, world.Id, WorldRegistrationKind.World);
            worldRegistrations.Add(world.Id, registration);
            return OperationResult<IWorldRegistration>.Success(registration);
        }

        public bool UnregisterWorld(string worldId)
        {
            if (disposed || string.IsNullOrWhiteSpace(worldId))
            {
                return false;
            }

            return worldRegistrations.TryGetValue(worldId, out var registration)
                && ReleaseRegistration(registration);
        }

        public OperationResult<IWorldRegistration> RegisterGamemode(GamemodeDefinition gamemode)
        {
            ThrowIfDisposed();
            if (gamemode == null)
            {
                throw new ArgumentNullException(nameof(gamemode));
            }

            if (gamemodeRegistrations.ContainsKey(gamemode.Id))
            {
                return OperationResult<IWorldRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "Gamemode '" + gamemode.Id + "' is already registered.");
            }

            gamemodes.Add(gamemode);
            var registration = new Registration(this, gamemode.Id, WorldRegistrationKind.Gamemode);
            gamemodeRegistrations.Add(gamemode.Id, registration);
            return OperationResult<IWorldRegistration>.Success(registration);
        }

        public OperationResult<IWorldRegistration> RegisterMenuEntry(GamemodeMenuEntry entry)
        {
            ThrowIfDisposed();
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (menuEntryRegistrations.ContainsKey(entry.Id))
            {
                return OperationResult<IWorldRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "World menu entry '" + entry.Id + "' is already registered.");
            }

            menuEntries.Add(entry);
            var registration = new Registration(this, entry.Id, WorldRegistrationKind.MenuEntry);
            menuEntryRegistrations.Add(entry.Id, registration);
            return OperationResult<IWorldRegistration>.Success(registration);
        }

        public bool UnregisterGamemode(string gamemodeId)
        {
            if (disposed || string.IsNullOrWhiteSpace(gamemodeId))
            {
                return false;
            }

            return gamemodeRegistrations.TryGetValue(gamemodeId, out var registration)
                && ReleaseRegistration(registration);
        }

        public bool UnregisterMenuEntry(string entryId)
        {
            return !disposed && !string.IsNullOrWhiteSpace(entryId)
                && menuEntryRegistrations.TryGetValue(entryId, out var registration)
                && ReleaseRegistration(registration);
        }

        public Task<OperationResult<WorldSession>> LoadAsync(
            WorldLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<WorldSession>.Failure(
                    ModErrorCode.Cancelled,
                    "World load was cancelled."));
            }

            return Task.FromResult(ToOperation(Load(request)));
        }

        public Task<OperationResult<WorldSession>> LaunchMenuEntryAsync(
            string entryId,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<WorldSession>.Failure(
                    ModErrorCode.Cancelled,
                    "World launch was cancelled."));
            }

            return Task.FromResult(ToOperation(LaunchMenuEntry(entryId)));
        }

        internal WorldLoadResult LaunchMenuEntry(string entryId)
        {
            return LaunchMenuEntry(
                entryId,
                preferSceneReplacement: true,
                allowAdditiveFallback: true,
                WorldLoadPriority.UserInitiated);
        }

        // Overload that threads the caller's configured load mode through to Load, so the launcher's "Load mode"
        // selection is honoured on the menu-entry path instead of being structurally dropped.
        internal WorldLoadResult LaunchMenuEntry(string entryId, bool preferSceneReplacement, bool allowAdditiveFallback)
        {
            return LaunchMenuEntry(
                entryId,
                preferSceneReplacement,
                allowAdditiveFallback,
                WorldLoadPriority.UserInitiated);
        }

        internal WorldLoadResult LaunchMenuEntry(
            string entryId,
            bool preferSceneReplacement,
            bool allowAdditiveFallback,
            WorldLoadPriority priority)
        {
            if (disposed)
            {
                return WorldLoadResult.Fail("World service is disposed.");
            }

            var entry = menuEntries.FirstOrDefault(item => string.Equals(item.Id, entryId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return WorldLoadResult.Fail("Unknown gamemode menu entry: " + entryId);
            }

            var worldId = ResolveWorldId(entry.WorldId);
            if (string.IsNullOrWhiteSpace(worldId))
            {
                return WorldLoadResult.Fail("No playable world is available for " + entry.Title + ".");
            }

            return Load(new WorldLoadRequest(
                worldId,
                entry.GamemodeId,
                priority,
                preferSceneReplacement,
                allowAdditiveFallback));
        }

        internal WorldLoadResult Load(WorldLoadRequest request)
        {
            if (disposed)
            {
                return WorldLoadResult.Fail("World service is disposed.");
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var world = worlds.FirstOrDefault(item => string.Equals(item.Id, request.WorldId, StringComparison.OrdinalIgnoreCase));
            var gamemode = gamemodes.FirstOrDefault(item => string.Equals(item.Id, request.GamemodeId, StringComparison.OrdinalIgnoreCase));
            if (world == null)
            {
                return WorldLoadResult.Fail("Unknown world: " + request.WorldId);
            }

            if (gamemode == null)
            {
                return WorldLoadResult.Fail("Unknown gamemode: " + request.GamemodeId);
            }

            // Do not supersede a dispatched scene load before its sceneLoaded/failure callback arrives. The
            // callback API can only identify the dispatch that failed; sceneLoaded itself has no generation
            // token, so allowing a second load here would let a late scene from the first dispatch resolve the
            // second transition and hide its eventual failure.
            if (transitionTracker.BlocksAdmission)
            {
                return transitionTracker.IsQuarantined
                    ? WorldLoadResult.Fail(
                        "The previous world load is still being retired. Wait for its scene change before retrying; "
                        + "if it never finishes, restart the game.")
                    : WorldLoadResult.Fail("A world is already loading; please wait.");
            }

            // Debounce rapid re-launches so a second click does not race a second scene load against the
            // first one's in-flight async load (which would overwrite the static checkpoint override). Stamp
            // immediately so even a launch that ultimately fails still throttles repeated attempts.
            if (Time.realtimeSinceStartup - lastLaunchTime < 1.5f)
            {
                return WorldLoadResult.Fail("A world is already loading; please wait.");
            }

            // Resolve the route before claiming or mutating the active session. Most importantly, automatic
            // startup loads must be refused before LaunchLevel/LoadScene/LaunchOpenSandbox has any side effect.
            var hasCustomContent = customWorldContent.TryGetValue(world.Id, out var content);
            // Custom content always wins, including when paired with the Sandbox gamemode. Otherwise Sandbox is
            // a story-free creator mode regardless of which catalog world the UI happened to retain.
            var useOpenSandbox = !hasCustomContent
                && (string.Equals(world.Id, OpenSandboxWorldId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(gamemode.Id, SandboxGamemodeId, StringComparison.OrdinalIgnoreCase));
            var hasCheckpoint = worldCheckpoints.TryGetValue(world.Id, out var checkpoint);
            var useSceneReplacement = !hasCustomContent && !useOpenSandbox
                && (hasCheckpoint
                    || (world.SupportsSceneReplacement
                        && !string.IsNullOrWhiteSpace(world.SceneName)
                        && (request.PreferSceneReplacement || !world.SupportsAdditiveArena)));
            var useAdditiveArena = !hasCustomContent && !useOpenSandbox && !useSceneReplacement
                && world.SupportsAdditiveArena
                && (!request.PreferSceneReplacement || request.AllowAdditiveFallback);

            if (!hasCustomContent && !useOpenSandbox && !useSceneReplacement && !useAdditiveArena)
            {
                return WorldLoadResult.Fail("World cannot be loaded with the requested mode: " + world.Name);
            }

            var targetScene = hasCustomContent || useOpenSandbox
                ? GameLevelBridge.SandboxSceneName
                : useSceneReplacement
                    ? world.SceneName
                    : SceneManager.GetActiveScene().name;

            IDisposable? launchClaim = null;

            lastLaunchTime = Time.realtimeSinceStartup;
            try
            {
                // A new approved launch replaces any live session: end it properly (arena teardown +
                // SessionEnded) before the incoming scene dispatch and SessionChanged notification.
                EndSession(WorldSessionEndReason.Superseded);

                WorldLoadResult result;
                if (hasCustomContent)
                {
                    result = LoadCustomWorld(world, gamemode, content!, launchClaim);
                }
                else if (useOpenSandbox)
                {
                    result = LoadOpenSandbox(world, gamemode, launchClaim);
                }
                else if (hasCheckpoint)
                {
                    UnloadArena();
                    var transition = transitionTracker.Begin(
                        Time.realtimeSinceStartup,
                        world.SceneName);
                    if (levelBridge.LaunchLevel(
                            checkpoint!,
                            message => transitionTracker.ReportFailure(transition, message)))
                    {
                        result = StartSession(world, gamemode, "gameScene", world.SceneName, launchClaim);
                    }
                    else
                    {
                        transitionTracker.Cancel(transition);
                        logger.Warn("Worlds could not launch " + world.Name + " via the game loader; falling back.");
                        result = LoadSceneReplacement(world, gamemode, launchClaim);
                    }
                }
                else if (useSceneReplacement)
                {
                    result = LoadSceneReplacement(world, gamemode, launchClaim);
                }
                else
                {
                    BuildArena();
                    result = StartSession(
                        world,
                        gamemode,
                        "additiveArena",
                        SceneManager.GetActiveScene().name,
                        launchClaim);
                }

                if (result.Ok)
                {
                    // StartSession accepted the launch.
                    launchClaim = null;
                }

                return result;
            }
            finally
            {
                // Creation/dispatch failures must never leave an automatic transition permanently blocked.
                launchClaim?.Dispose();
            }
        }

        private WorldLoadResult LoadSceneReplacement(
            WorldDefinition world,
            GamemodeDefinition gamemode,
            IDisposable? launchClaim)
        {
            if (!world.SupportsSceneReplacement || string.IsNullOrWhiteSpace(world.SceneName))
            {
                return WorldLoadResult.Fail("World has no replacement scene: " + world.Name);
            }

            UnloadArena();
            var transition = transitionTracker.Begin(
                Time.realtimeSinceStartup,
                world.SceneName);
            if (!levelBridge.LoadSceneByName(
                    world.SceneName,
                    message => transitionTracker.ReportFailure(transition, message)))
            {
                try
                {
                    // Last-resort fallback. First-party scenes are often addressable/streamed and not in
                    // build settings, so this can throw; degrade gracefully instead of crashing the game.
                    SceneManager.LoadScene(world.SceneName, LoadSceneMode.Single);
                }
                catch (Exception ex)
                {
                    transitionTracker.Cancel(transition);
                    logger.Warn("Worlds could not load scene '" + world.SceneName + "': " + ex.Message);
                    return WorldLoadResult.Fail("Could not load world scene: " + world.Name);
                }
            }

            return StartSession(world, gamemode, "sceneReplacement", world.SceneName, launchClaim);
        }

        public void WriteCatalog()
        {
            ThrowIfDisposed();
            var json = CatalogJson();
            var bytes = new UTF8Encoding(false, true).GetBytes(json);
            if (bytes.Length > MaxCatalogBytes)
            {
                throw new InvalidDataException("World catalog exceeds " + MaxCatalogBytes + " bytes.");
            }

            var result = files.WriteDataTextAsync("catalog.json", json).GetAwaiter().GetResult();
            if (!result.Succeeded)
            {
                throw new IOException("Could not write world catalog: " + result.ErrorMessage);
            }
        }

        public void UnloadArena()
        {
            // Cancel any in-flight "build the arena when the sandbox scene loads" handshake: tearing the arena
            // down (or switching to a different world) must not leave a pending build that fires on a later load.
            sandboxArenaPending = false;

            pendingCustomWorld = null;
            activeWorldContent?.Dispose();
            activeWorldContent = null;

            if (arenaRoot != null)
            {
                UnityEngine.Object.Destroy(arenaRoot);
                arenaRoot = null;
            }

            // The HDRP VolumeProfile + its components are ScriptableObjects, not destroyed with the GameObject.
            HdrpEnvironment.Cleanup(arenaProfile);
            arenaProfile = null;
        }

        /// <summary>
        /// Ends the current session: clears <see cref="CurrentSession"/> first (so re-entrant calls and
        /// subscribers observing the service see no active session), tears down the sandbox arena, then fires
        /// <see cref="SessionEnded"/> exactly once.
        /// </summary>
        public OperationResult<bool> EndSession(WorldSessionEndReason reason)
        {
            if (disposed)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidState,
                    "World service is disposed.");
            }

            // The scene API exposes no cancellation handle. If teardown arrives before sceneLoaded, quarantine
            // that dispatch instead of declaring it resolved: a late arrival must retire it before another world
            // load can begin, or it could be mistaken for the retry's scene.
            transitionTracker.Abandon();
            var session = CurrentSession;
            if (session == null)
            {
                return OperationResult<bool>.Success(false);
            }

            CurrentSession = null;
            sessionSceneName = string.Empty;
            UnloadArena();
            SafeEvent.Invoke(
                SessionEnded,
                new WorldSessionEnd(session, reason),
                ex => logger.Error(ex, "A SessionEnded subscriber failed."));

            logger.Info("World session ended (" + reason + "): " + session.GamemodeId + " in " + session.WorldId + ".");
            return OperationResult<bool>.Success(true);
        }

        // Releases the scene-loaded subscription. Called when the mod unloads (C# assemblies never unload under
        // Mono, so a dangling static event handler would otherwise survive and fire against a dead service).
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            EndSession(WorldSessionEndReason.ProviderUnloading);
            disposed = true;
            levelBridge.Dispose();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnloadArena();
            transitionTracker.Abandon();
            DeactivateRegistrations(worldRegistrations);
            DeactivateRegistrations(gamemodeRegistrations);
            DeactivateRegistrations(menuEntryRegistrations);
            worlds.Clear();
            gamemodes.Clear();
            menuEntries.Clear();
            worldCheckpoints.Clear();
            customWorldContent.Clear();
            SessionChanged = null;
            SessionEnded = null;
        }

        /// <summary>
        /// Main-thread failure/timeout drain for a provisional async scene load. A failed dispatch must not leave
        /// a gamemode active over the old scene or retain its session-scoped coordinator claim indefinitely.
        /// </summary>
        internal void UpdateTransition()
        {
            if (disposed)
            {
                return;
            }

            // Reflective Task/UniTask continuations may complete on worker threads. Drain their immutable
            // results here so logging, callbacks, and transition state changes stay on Unity's main thread.
            levelBridge.DrainAsyncLoadOutcomes();

            var failure = transitionTracker.ConsumeFailure(
                Time.realtimeSinceStartup,
                TransitionTimeoutSeconds);
            if (failure == null)
            {
                return;
            }

            EndSession(WorldSessionEndReason.LoadFailed);
            logger.Warn("Worlds provisional scene load failed: " + failure);
        }

        // Launches the clean Open Sandbox arena: load the game's story-free play scene (which spawns a real
        // player), then build the arena geometry around that spawn once the async scene load completes.
        private WorldLoadResult LoadOpenSandbox(
            WorldDefinition selectedWorld,
            GamemodeDefinition gamemode,
            IDisposable? launchClaim)
        {
            UnloadArena();

            // Report the session as the Open Sandbox world (so the result message and SessionChanged reflect what
            // actually loaded), falling back to the requested world if the built-in sandbox world is missing.
            var sandboxWorld = worlds.FirstOrDefault(item =>
                string.Equals(item.Id, OpenSandboxWorldId, StringComparison.OrdinalIgnoreCase)) ?? selectedWorld;

            var transition = transitionTracker.Begin(
                Time.realtimeSinceStartup,
                GameLevelBridge.SandboxSceneName);
            if (levelBridge.LaunchOpenSandbox(
                    message => transitionTracker.ReportFailure(transition, message)))
            {
                ArmSandboxArena();
                return StartSession(
                    sandboxWorld,
                    gamemode,
                    "openSandbox",
                    GameLevelBridge.SandboxSceneName,
                    launchClaim);
            }

            transitionTracker.Cancel(transition);

            // The game's play scene could not be loaded (missing symbol). Fall back to building the arena over the
            // current scene so the launch still produces something, even if it overlays a non-gameplay scene.
            logger.Warn("Worlds could not load the game sandbox scene; building the arena over the current scene.");
            BuildArena();
            return StartSession(
                sandboxWorld,
                gamemode,
                "additiveArena",
                SceneManager.GetActiveScene().name,
                launchClaim);
        }

        // Arms a one-shot: when the sandbox play scene finishes its async load, build the arena around the player.
        // The persistent OnSceneLoaded hook (registered in the constructor) picks it up.
        private void ArmSandboxArena()
        {
            sandboxArenaPending = true;
        }

        // Launches a mod-provided custom world. The SDK content factory is invoked only after the clean play
        // scene arrives, so all assets/entities are created in their intended scene and remain opaque here.
        private WorldLoadResult LoadCustomWorld(
            WorldDefinition world,
            GamemodeDefinition gamemode,
            ICustomWorldContent content,
            IDisposable? launchClaim)
        {
            UnloadArena();

            var transition = transitionTracker.Begin(
                Time.realtimeSinceStartup,
                GameLevelBridge.SandboxSceneName);
            if (!levelBridge.LaunchOpenSandbox(
                    message => transitionTracker.ReportFailure(transition, message)))
            {
                transitionTracker.Cancel(transition);
                return WorldLoadResult.Fail("The game's sandbox play scene could not be loaded for '" + world.Name + "'.");
            }

            pendingCustomWorld = new PendingCustomWorld(world, content);
            sandboxArenaPending = false;
            return StartSession(
                world,
                gamemode,
                "customWorld",
                GameLevelBridge.SandboxSceneName,
                launchClaim);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (disposed || mode != LoadSceneMode.Single)
            {
                return;
            }

            // Admission stays serialized until the expected target arrives. An unrelated/menu single scene does
            // not retire an abandoned/timed-out dispatch, so its target still cannot resolve a newer retry.
            transitionTracker.ResolveSceneArrival(scene.name);

            // Reaching a non-gameplay scene (menu/boot/loader) under a live session means the player left the
            // world — most commonly via the game's own pause-menu exit. End the session so no gamemode stays
            // active over the menu (HUD overlays, time drivers, spawning). The mode==Single gate above keeps
            // additively streamed scenes (e.g. "...Loader" content scenes) from falsely ending a session.
            if (EndSessionOnMenuScene && CurrentSession != null && GameScenes.IsNonGameplayScene(scene.name))
            {
                EndSession(WorldSessionEndReason.MenuReached);
                return;
            }

            if (CurrentSession != null)
            {
                if (!string.Equals(scene.name, sessionSceneName, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Debug("Worlds session scene changed to '" + scene.name + "' without a coordinator claim"
                        + " (native level transition); the session continues.");
                    sessionSceneName = scene.name;
                }
            }

            OnSandboxSceneLoaded(scene, mode);
        }

        private void OnSandboxSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single
                || !string.Equals(scene.name, GameLevelBridge.SandboxSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (pendingCustomWorld != null)
            {
                var pending = pendingCustomWorld;
                pendingCustomWorld = null;
                PlaceCustomWorld(pending, scene);
                return;
            }

            if (!sandboxArenaPending)
            {
                return;
            }

            sandboxArenaPending = false;

            // The scene's native bootstrap spawns the player at its own transform; centre the arena there so the
            // ground/walls line up with the spawn, and grab the prefab in case we must spawn a fallback player.
            var spawnPosition = levelBridge.GetSandboxSpawnPosition();
            var playerPrefab = levelBridge.ResolveSandboxPlayerPrefab();
            BuildArena(spawnPosition);

            if (arenaRoot != null)
            {
                var guard = arenaRoot.AddComponent<SandboxPlayerGuard>();
                guard.Initialize(levelBridge, playerPrefab, spawnPosition, logger, 1.5f);
            }

            logger.Info("Worlds open sandbox arena ready in scene '" + scene.name + "'.");
        }

        // Materializes SDK-owned content in the freshly loaded sandbox play scene. The content owns its own
        // transform and teardown; this provider supplies environment and player safety only.
        private void PlaceCustomWorld(PendingCustomWorld pending, Scene scene)
        {
            var spawnPosition = levelBridge.GetSandboxSpawnPosition();
            try
            {
                arenaRoot = new GameObject("TopiaForge Worlds - Custom World: " + pending.World.Id);
                UnityEngine.Object.DontDestroyOnLoad(arenaRoot);

                var options = pending.Content.Options;
                var result = pending.Content.CreateAsync().GetAwaiter().GetResult();
                if (!result.TryGetValue(out var created))
                {
                    throw new InvalidOperationException(result.ErrorCode + ": " + result.ErrorMessage);
                }

                activeWorldContent = created;
                var effectiveSpawn = spawnPosition;

                if (options.ApplyDefaultEnvironment)
                {
                    arenaProfile = HdrpEnvironment.Apply(arenaRoot, logger);
                }

                var guard = arenaRoot.AddComponent<SandboxPlayerGuard>();
                guard.Initialize(levelBridge, levelBridge.ResolveSandboxPlayerPrefab(), effectiveSpawn, logger, 1.5f);
                if (options.EnableKillPlane)
                {
                    var killPlane = arenaRoot.AddComponent<CustomWorldPlayerGuard>();
                    killPlane.Initialize(levelBridge, effectiveSpawn, effectiveSpawn.y - options.KillPlaneDepth, logger);
                }

                logger.Info("Custom world '" + pending.World.Name + "' placed in scene '" + scene.name + "'.");
            }
            catch (Exception ex)
            {
                // Never strand the player on a void: tear down whatever half-placed content exists and fall
                // back to the generated arena so the session stays playable.
                logger.Error(ex, "Custom world '" + pending.World.Name + "' failed to place; falling back to the arena.");
                UnloadArena();
                BuildArena(spawnPosition);
            }
        }

        private static Transform? FindDescendant(Transform root, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Breadth-first so a top-level marker wins over an identically named nested one.
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current != root && string.Equals(current.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                for (var index = 0; index < current.childCount; index++)
                {
                    queue.Enqueue(current.GetChild(index));
                }
            }

            return null;
        }

        private static bool HasGlobalVolume(GameObject root)
        {
            foreach (var volume in root.GetComponentsInChildren<Volume>(true))
            {
                if (volume.isGlobal)
                {
                    return true;
                }
            }

            return false;
        }

        private string ResolveWorldId(string requestedWorldId)
        {
            if (!string.IsNullOrWhiteSpace(requestedWorldId) &&
                worlds.Any(item => string.Equals(item.Id, requestedWorldId, StringComparison.OrdinalIgnoreCase)))
            {
                return requestedWorldId;
            }

            // Prefer a real, checkpoint-backed level (correct HDRP state); fall back to the sandbox arena.
            var realLevel = worlds.FirstOrDefault(item => worldCheckpoints.ContainsKey(item.Id));
            if (realLevel != null)
            {
                return realLevel.Id;
            }

            return worlds.Any(item => string.Equals(item.Id, OpenSandboxWorldId, StringComparison.OrdinalIgnoreCase))
                ? OpenSandboxWorldId
                : worlds.FirstOrDefault()?.Id ?? string.Empty;
        }

        private WorldLoadResult StartSession(
            WorldDefinition world,
            GamemodeDefinition gamemode,
            string mode,
            string sceneName,
            IDisposable? launchClaim)
        {
            // The debounce timestamp is already stamped at the top of Load (covering both success and failure);
            // re-stamping here would be a redundant second source of truth for the same value.
            var session = new WorldSession(world.Id, gamemode.Id, mode, sceneName, DateTimeOffset.UtcNow);
            CurrentSession = session;
            sessionSceneName = sceneName;

            // A consumer must not turn an already-dispatched world load into a reported failure (which
            // would also cause the caller to dispose the now session-owned scene claim), nor starve later
            // subscribers that also own session-scoped state.
            SafeEvent.Invoke(
                SessionChanged,
                session,
                ex => logger.Error(ex, "A SessionChanged subscriber failed."));
            var message = "Loaded " + world.Name + " [" + world.Id + "] with " + gamemode.Name
                + " [" + gamemode.Id + "] via " + mode + " in scene '" + sceneName + "'.";
            logger.Info("World session started: " + message);
            return WorldLoadResult.Success(session, message);
        }

        private void BuildArena()
        {
            BuildArena(Vector3.zero);
        }

        // Centres the ground/boundary geometry at <paramref name="center"/> so the arena lines up with wherever
        // the sandbox player actually spawns (the play scene's spawn point), rather than always at world origin.
        private void BuildArena(Vector3 center)
        {
            UnloadArena();
            arenaRoot = new GameObject("TopiaForge Worlds - Open Sandbox");
            UnityEngine.Object.DontDestroyOnLoad(arenaRoot);

            SandboxArenaBuilder.Build(arenaRoot, center, logger);

            // HDRP has no default sky/exposure/tonemapping; without a global Volume the arena looks washed out.
            arenaProfile = HdrpEnvironment.Apply(arenaRoot, logger);
            logger.Info("Built open sandbox arena.");
        }

        private string CatalogJson()
        {
            var builder = new StringBuilder();
            builder.Append("{\"worlds\":[");
            AppendWorlds(builder);
            builder.Append("],\"gamemodes\":[");
            AppendGamemodes(builder);
            builder.Append("]}");
            return builder.ToString();
        }

        private void AppendWorlds(StringBuilder builder)
        {
            for (var index = 0; index < worlds.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var world = worlds[index];
                builder
                    .Append("{\"id\":\"").Append(Escape(world.Id))
                    .Append("\",\"name\":\"").Append(Escape(world.Name))
                    .Append("\",\"description\":\"").Append(Escape(world.Description))
                    .Append("\",\"sceneName\":\"").Append(Escape(world.SceneName))
                    .Append("\",\"firstParty\":").Append(world.FirstParty ? "true" : "false")
                    .Append(",\"supportsSceneReplacement\":").Append(world.SupportsSceneReplacement ? "true" : "false")
                    .Append(",\"supportsAdditiveArena\":").Append(world.SupportsAdditiveArena ? "true" : "false")
                    .Append('}');
            }
        }

        private void AppendGamemodes(StringBuilder builder)
        {
            for (var index = 0; index < gamemodes.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var gamemode = gamemodes[index];
                builder
                    .Append("{\"id\":\"").Append(Escape(gamemode.Id))
                    .Append("\",\"name\":\"").Append(Escape(gamemode.Name))
                    .Append("\",\"description\":\"").Append(Escape(gamemode.Description))
                    .Append("\"}");
            }
        }

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        private static string Slug(string value)
        {
            var builder = new StringBuilder();
            foreach (var character in value.ToLowerInvariant())
            {
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');
            }

            return builder.ToString().Trim('_');
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(WorldsService));
            }
        }

        private static OperationResult<WorldSession> ToOperation(WorldLoadResult result)
        {
            return result.Ok && result.Session != null
                ? OperationResult<WorldSession>.Success(result.Session)
                : OperationResult<WorldSession>.Failure(ModErrorCode.External, result.Message);
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(IWorldGamemodeService))
            {
                throw new ArgumentException("Unsupported Worlds extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, lifetime);
        }

        private bool ReleaseRegistration(Registration registration)
        {
            if (disposed || !RegistrationMap(registration.Kind).TryGetValue(registration.Id, out var current)
                || !ReferenceEquals(current, registration))
            {
                registration.Deactivate();
                return false;
            }

            RegistrationMap(registration.Kind).Remove(registration.Id);
            registration.Deactivate();
            switch (registration.Kind)
            {
                case WorldRegistrationKind.World:
                    worlds.RemoveAll(item => string.Equals(item.Id, registration.Id, StringComparison.OrdinalIgnoreCase));
                    customWorldContent.Remove(registration.Id);
                    worldCheckpoints.Remove(registration.Id);
                    if (CurrentSession != null
                        && string.Equals(CurrentSession.WorldId, registration.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        EndSession(WorldSessionEndReason.ProviderUnloading);
                    }

                    break;
                case WorldRegistrationKind.Gamemode:
                    gamemodes.RemoveAll(item => string.Equals(item.Id, registration.Id, StringComparison.OrdinalIgnoreCase));
                    if (CurrentSession != null
                        && string.Equals(CurrentSession.GamemodeId, registration.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        EndSession(WorldSessionEndReason.ProviderUnloading);
                    }

                    break;
                case WorldRegistrationKind.MenuEntry:
                    menuEntries.RemoveAll(item => string.Equals(item.Id, registration.Id, StringComparison.OrdinalIgnoreCase));
                    break;
            }

            return true;
        }

        private Dictionary<string, Registration> RegistrationMap(WorldRegistrationKind kind)
        {
            switch (kind)
            {
                case WorldRegistrationKind.World:
                    return worldRegistrations;
                case WorldRegistrationKind.Gamemode:
                    return gamemodeRegistrations;
                case WorldRegistrationKind.MenuEntry:
                    return menuEntryRegistrations;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static void DeactivateRegistrations(Dictionary<string, Registration> registrations)
        {
            foreach (var registration in registrations.Values)
            {
                registration.Deactivate();
            }

            registrations.Clear();
        }

        private sealed class Registration : IWorldRegistration
        {
            private WorldsService? owner;

            public Registration(WorldsService owner, string id, WorldRegistrationKind kind)
            {
                this.owner = owner;
                Id = id;
                Kind = kind;
            }

            public string Id { get; }
            public WorldRegistrationKind Kind { get; }
            public bool IsActive => owner != null;

            public void Dispose()
            {
                var current = Interlocked.Exchange(ref owner, null);
                current?.ReleaseRegistration(this);
            }

            public void Deactivate()
            {
                Interlocked.Exchange(ref owner, null);
            }
        }

        private sealed class OwnerFacade : IWorldGamemodeService
        {
            private readonly WorldsService service;
            private readonly IModLifetime lifetime;
            private readonly object subscriptionSync = new object();
            private readonly List<SessionChangedSubscription> changedSubscriptions =
                new List<SessionChangedSubscription>();
            private readonly List<SessionEndedSubscription> endedSubscriptions =
                new List<SessionEndedSubscription>();

            public OwnerFacade(WorldsService service, IModLifetime lifetime)
            {
                this.service = service;
                this.lifetime = lifetime;
            }

            public IReadOnlyList<WorldDefinition> Worlds => service.Worlds;
            public IReadOnlyList<GamemodeDefinition> Gamemodes => service.Gamemodes;
            public IReadOnlyList<GamemodeMenuEntry> MenuEntries => service.MenuEntries;
            public WorldSession? CurrentSession => service.CurrentSession;

            public event Action<WorldSession>? SessionChanged
            {
                add
                {
                    if (value == null || lifetime.IsStopping)
                    {
                        return;
                    }

                    var subscription = new SessionChangedSubscription(service, value);
                    lock (subscriptionSync)
                    {
                        changedSubscriptions.Add(subscription);
                    }

                    service.SessionChanged += subscription.Wrapper;
                    lifetime.Track(subscription);
                }
                remove
                {
                    if (value != null)
                    {
                        RemoveChanged(value);
                    }
                }
            }

            public event Action<WorldSessionEnd>? SessionEnded
            {
                add
                {
                    if (value == null || lifetime.IsStopping)
                    {
                        return;
                    }

                    var subscription = new SessionEndedSubscription(service, value);
                    lock (subscriptionSync)
                    {
                        endedSubscriptions.Add(subscription);
                    }

                    service.SessionEnded += subscription.Wrapper;
                    lifetime.Track(subscription);
                }
                remove
                {
                    if (value != null)
                    {
                        RemoveEnded(value);
                    }
                }
            }

            public OperationResult<IWorldRegistration> RegisterWorld(
                WorldDefinition world,
                ICustomWorldContent? content = null) =>
                Track(service.RegisterWorld(world, content));

            public OperationResult<IWorldRegistration> RegisterGamemode(GamemodeDefinition gamemode) =>
                Track(service.RegisterGamemode(gamemode));

            public OperationResult<IWorldRegistration> RegisterMenuEntry(GamemodeMenuEntry entry) =>
                Track(service.RegisterMenuEntry(entry));

            public Task<OperationResult<WorldSession>> LoadAsync(
                WorldLoadRequest request,
                CancellationToken cancellationToken = default) =>
                RunWithLifetimeCancellation(token => service.LoadAsync(request, token), cancellationToken);

            public Task<OperationResult<WorldSession>> LaunchMenuEntryAsync(
                string entryId,
                CancellationToken cancellationToken = default) =>
                RunWithLifetimeCancellation(token => service.LaunchMenuEntryAsync(entryId, token), cancellationToken);

            public OperationResult<bool> EndSession(WorldSessionEndReason reason) => service.EndSession(reason);

            private OperationResult<IWorldRegistration> Track(OperationResult<IWorldRegistration> result)
            {
                if (lifetime.IsStopping)
                {
                    if (result.TryGetValue(out var stoppingRegistration))
                    {
                        stoppingRegistration.Dispose();
                    }

                    return OperationResult<IWorldRegistration>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot register world content.");
                }

                if (!result.TryGetValue(out var registration))
                {
                    return result;
                }

                try
                {
                    var ownedRegistration = new OwnerRegistration(
                        registration,
                        lifetime.Track(registration));
                    return OperationResult<IWorldRegistration>.Success(ownedRegistration);
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IWorldRegistration>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before its world registration could be retained.");
                }
            }

            private async Task<OperationResult<WorldSession>> RunWithLifetimeCancellation(
                Func<CancellationToken, Task<OperationResult<WorldSession>>> operation,
                CancellationToken cancellationToken)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.StoppingToken))
                {
                    return await operation(linked.Token);
                }
            }

            private void RemoveChanged(Action<WorldSession> handler)
            {
                SessionChangedSubscription? subscription = null;
                lock (subscriptionSync)
                {
                    for (var index = changedSubscriptions.Count - 1; index >= 0; index--)
                    {
                        if (changedSubscriptions[index].Matches(handler))
                        {
                            subscription = changedSubscriptions[index];
                            changedSubscriptions.RemoveAt(index);
                            break;
                        }
                    }
                }

                subscription?.Dispose();
            }

            private void RemoveEnded(Action<WorldSessionEnd> handler)
            {
                SessionEndedSubscription? subscription = null;
                lock (subscriptionSync)
                {
                    for (var index = endedSubscriptions.Count - 1; index >= 0; index--)
                    {
                        if (endedSubscriptions[index].Matches(handler))
                        {
                            subscription = endedSubscriptions[index];
                            endedSubscriptions.RemoveAt(index);
                            break;
                        }
                    }
                }

                subscription?.Dispose();
            }

            private sealed class SessionChangedSubscription : IDisposable
            {
                private WorldsService? service;
                private readonly Action<WorldSession> handler;

                public SessionChangedSubscription(WorldsService service, Action<WorldSession> handler)
                {
                    this.service = service;
                    this.handler = handler;
                    Wrapper = session => this.handler(session);
                }

                public Action<WorldSession> Wrapper { get; }
                public bool Matches(Action<WorldSession> candidate) => handler == candidate;

                public void Dispose()
                {
                    var current = Interlocked.Exchange(ref service, null);
                    if (current != null)
                    {
                        current.SessionChanged -= Wrapper;
                    }
                }
            }

            private sealed class OwnerRegistration : IWorldRegistration
            {
                private readonly IWorldRegistration registration;
                private IDisposable? lifetimeLease;

                public OwnerRegistration(IWorldRegistration registration, IDisposable lifetimeLease)
                {
                    this.registration = registration;
                    this.lifetimeLease = lifetimeLease;
                }

                public string Id => registration.Id;
                public WorldRegistrationKind Kind => registration.Kind;
                public bool IsActive => lifetimeLease != null && registration.IsActive;

                public void Dispose()
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }

            private sealed class SessionEndedSubscription : IDisposable
            {
                private WorldsService? service;
                private readonly Action<WorldSessionEnd> handler;

                public SessionEndedSubscription(WorldsService service, Action<WorldSessionEnd> handler)
                {
                    this.service = service;
                    this.handler = handler;
                    Wrapper = session => this.handler(session);
                }

                public Action<WorldSessionEnd> Wrapper { get; }
                public bool Matches(Action<WorldSessionEnd> candidate) => handler == candidate;

                public void Dispose()
                {
                    var current = Interlocked.Exchange(ref service, null);
                    if (current != null)
                    {
                        current.SessionEnded -= Wrapper;
                    }
                }
            }
        }

        private sealed class PendingCustomWorld
        {
            public PendingCustomWorld(WorldDefinition world, ICustomWorldContent content)
            {
                World = world;
                Content = content;
            }

            public WorldDefinition World { get; }
            public ICustomWorldContent Content { get; }

        }

        internal sealed class WorldLoadResult
        {
            private WorldLoadResult(bool ok, WorldSession? session, string message)
            {
                Ok = ok;
                Session = session;
                Message = message;
            }

            public bool Ok { get; }
            public WorldSession? Session { get; }
            public string Message { get; }

            public static WorldLoadResult Success(WorldSession session, string message) =>
                new WorldLoadResult(true, session, message);

            public static WorldLoadResult Fail(string message) =>
                new WorldLoadResult(false, null, message);
        }
    }
}
