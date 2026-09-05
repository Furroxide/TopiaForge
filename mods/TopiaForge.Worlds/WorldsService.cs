using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    public sealed partial class WorldsService : IWorldGamemodeService, IWorldTransitionState,
        IOwnerBoundExtensionFactory, IDisposable
    {
        // Aliases of the SDK's WellKnownIds so consumers that cannot reference this assembly and this
        // service always agree on the ids (SdkSurfaceTests pins the WellKnownIds values).
        public const string OpenSandboxWorldId = WellKnownWorldIds.OpenSandboxWorld;
        public const string SandboxGamemodeId = WellKnownWorldIds.SandboxGamemode;

        private readonly IModLogger logger;
        private readonly IModFiles files;
        private readonly GameLevelBridge levelBridge;
        private readonly IInternalSceneTransitionService sceneTransitions;
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
        private IInternalSceneTransitionLease? pendingSceneClaim;
        // In-flight ICustomWorldContent.CreateAsync for the world currently being placed. SDK asset tasks only
        // complete on Unity's main thread, so this is started on the scene-loaded callback and drained from
        // UpdateTransition; it is never waited on.
        private readonly PendingOperation<IWorldContent> contentLoad = new PendingOperation<IWorldContent>();
        private PendingCustomWorld? placingCustomWorld;
        private Vector3 placingSpawnPosition;
        // Best-effort diagnostic catalog write. A catalog failure must never take down the provider that
        // Zombies, Sandbox, UiGallery, and Creator Tools all depend on, so the result is drained and logged.
        private Task<OperationResult<bool>>? catalogWrite;
        // Set whenever the registry changes. The catalog is written from the frame loop rather than at the
        // point of registration, so a mod that registers a gamemode after Worlds has loaded (every gamemode
        // does -- they all declare loadAfter: worlds) still reaches the file.
        private bool catalogDirty;
        private readonly CancellationToken lifetimeToken;

        internal WorldsService(
            IModLogger logger,
            IModFiles files,
            IInternalSceneTransitionService sceneTransitions,
            CancellationToken lifetimeToken = default)
        {
            this.logger = logger;
            this.files = files;
            this.sceneTransitions = sceneTransitions ?? throw new ArgumentNullException(nameof(sceneTransitions));
            this.lifetimeToken = lifetimeToken;
            levelBridge = new GameLevelBridge(logger, sceneTransitions);
            worldsView = new ReadOnlyCollection<WorldDefinition>(worlds);
            gamemodesView = new ReadOnlyCollection<GamemodeDefinition>(gamemodes);
            menuEntriesView = new ReadOnlyCollection<GamemodeMenuEntry>(menuEntries);

            // Persistent scene hook (removed in Dispose). Registered here — before the manager plugin's own
            // sceneLoaded dispatch to mods — so the session is already ended by the time per-session handlers
            // (e.g. a gamemode controller's own sceneLoaded hook) run in the same dispatch.
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        // Live read-only views over the registries (registries are only mutated on the main thread during load).
        public IReadOnlyList<WorldDefinition> Worlds => worldsView;
        public IReadOnlyList<GamemodeDefinition> Gamemodes => gamemodesView;
        public IReadOnlyList<GamemodeMenuEntry> MenuEntries => menuEntriesView;
        public WorldSession? CurrentSession { get; private set; }

        public bool IsTransitionInFlight =>
            !disposed && (sceneTransitions.IsBusy || transitionTracker.IsInFlight(Time.realtimeSinceStartup, TransitionTimeoutSeconds));

        public event Action<WorldSession>? SessionChanged;
        public event Action<WorldSessionEnd>? SessionEnded;

        // Config gate for the automatic end-on-menu behaviour (WorldsConfig.EndSessionOnMenuScene). Explicit
        // EndSession calls are never gated.
        public bool EndSessionOnMenuScene { get; set; } = true;

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
                : OperationResult<WorldSession>.Failure(result.ErrorCode, result.Message);
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
            private WorldLoadResult(
                bool ok,
                WorldSession? session,
                ModErrorCode errorCode,
                string message)
            {
                Ok = ok;
                Session = session;
                ErrorCode = errorCode;
                Message = message;
            }

            public bool Ok { get; }
            public WorldSession? Session { get; }
            public ModErrorCode ErrorCode { get; }
            public string Message { get; }

            public static WorldLoadResult Success(WorldSession session, string message) =>
                new WorldLoadResult(true, session, ModErrorCode.None, message);

            public static WorldLoadResult Fail(
                string message,
                ModErrorCode errorCode = ModErrorCode.External) =>
                new WorldLoadResult(false, null, errorCode, message);
        }
    }
}
