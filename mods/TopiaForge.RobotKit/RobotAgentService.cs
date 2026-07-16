using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Implementation of the public IRobotAgentService. Owns a DontDestroyOnLoad root with an always-inactive
    // incubator (so a clone's native Awake/OnEnable fire only after the brain has been configured), the live
    // agent handles, and the per-frame tick that drives each agent's native walk.
    internal sealed class RobotAgentService : IRobotAgentService, IOwnerBoundExtensionFactory, IDisposable
    {
        private readonly IModLogger logger;
        private readonly RobotPrefabResolver prefabResolver;
        private readonly List<RobotAgent> agents = new List<RobotAgent>();
        private readonly List<ReachableSpawnSearch> searches = new List<ReachableSpawnSearch>();
        private readonly System.Random random = new System.Random();

        private GameObject? root;
        private GameObject? incubator;
        private IReadOnlyList<RobotPrefabCandidate>? cachedCatalog;
        private RobotTypeDescriptor[]? cachedTypes;
        private object? cachedPathFindSettings;
        private float nextPrefabScan;
        private Component? playerController;
        private Component? playerHealth;
        private IRobotAgent[]? activeSnapshot;
        private bool activeDirty = true;
        private int spawnCounter;
        private bool loggedSpawnMode;
        private bool disposed;

        public RobotAgentService(IModLogger logger)
        {
            this.logger = logger;
            RobotKitDiagnostics.Configure(logger);
            prefabResolver = new RobotPrefabResolver(logger);
        }

        public bool IsAvailable => !disposed && LocomotionBridge.LocomotionAvailable() && ResolveCachedPrefab() != null;

        public bool IsNavigationAvailable => LocomotionBridge.NavAvailable();

        public IReadOnlyList<RobotTypeDescriptor> RobotTypes
        {
            get
            {
                var catalog = ResolveCachedCatalog();
                if (catalog == null || catalog.Count == 0)
                {
                    return Array.Empty<RobotTypeDescriptor>();
                }

                if (cachedTypes == null || cachedTypes.Length != catalog.Count)
                {
                    cachedTypes = new RobotTypeDescriptor[catalog.Count];
                    for (var index = 0; index < catalog.Count; index++)
                    {
                        cachedTypes[index] = new RobotTypeDescriptor(catalog[index].Id, catalog[index].DisplayName);
                    }
                }

                return cachedTypes;
            }
        }

        public bool TryGetRobot(IEntity entity, out IRobotAgent? agent)
        {
            agent = FindAgentByEntity(entity);
            return agent != null;
        }

        public IReadOnlyList<IRobotAgent> ActiveAgents
        {
            get
            {
                // Rebuilt only when the set changes (spawn/despawn/clear), so a per-frame poll does not allocate.
                if (activeDirty || activeSnapshot == null)
                {
                    activeSnapshot = agents.ToArray();
                    activeDirty = false;
                }

                return activeSnapshot;
            }
        }

        // Maps a spawned robot's GameObject (as object) back to its agent handle, by CLR reference — identity
        // holds regardless of Unity fake-null, and consumers hand out agent.GameObject itself (target snapshots).
        // Used by the objective service to resolve a Reprogram courier's recipient. Null for foreign objects.
        internal IRobotAgent? FindAgentByEntity(IEntity entity)
        {
            if (entity == null)
            {
                return null;
            }

            for (var index = 0; index < agents.Count; index++)
            {
                if (ReferenceEquals(agents[index], entity))
                {
                    return agents[index];
                }
            }

            return null;
        }

        public OperationResult<IRobotAgent> Spawn(RobotAgentSpawnRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (disposed)
            {
                return OperationResult<IRobotAgent>.Failure(
                    ModErrorCode.InvalidState,
                    "RobotKit has been disposed.");
            }

            var prefab = ResolvePrefabForType(request.RobotTypeId);
            if (prefab == null)
            {
                return OperationResult<IRobotAgent>.Failure(
                    ModErrorCode.Unavailable,
                    "No spawnable robot prefab is available in this scene.");
            }

            EnsureRoots();
            if (incubator == null || root == null)
            {
                return OperationResult<IRobotAgent>.Failure(
                    ModErrorCode.Unavailable,
                    "RobotKit could not create its scene-owned spawn root.");
            }

            var position = new Vector3(request.Position.X, request.Position.Y, request.Position.Z);
            var rotation = Quaternion.identity;
            if (request.Facing is { } facing)
            {
                var flat = new Vector3(facing.X, 0f, facing.Z);
                if (flat.sqrMagnitude > 0.0001f)
                {
                    rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
                }
            }

            // Instantiate under the inactive incubator so no native Awake/OnEnable fires before the brain is
            // configured, set the requested brain mode while inactive, then reparent to the live root and activate
            // as a fully native (but mod-driven) robot.
            var clone = UnityEngine.Object.Instantiate(prefab, position, rotation, incubator.transform);
            clone.SetActive(false);
            clone.name = request.Name ?? "RobotKit Agent";

            // Capture the brain's pristine state before any dormant writes, so a later SetBrainMode(Autonomous)
            // can restore what the prefab shipped with.
            var brainSnapshot = GameReflection.CaptureBrainState(clone);
            GameReflection.ConfigureBrain(clone, request.BrainMode, logger);
            EnsureKinematicRoot(clone);
            var agent = new RobotAgent(NextId(), clone, request, logger, brainSnapshot);

            clone.transform.SetParent(root.transform, true);
            clone.SetActive(true);
            agent.OnActivated();
            if (request.Scale != 1f)
            {
                agent.SetScale(request.Scale);
            }

            if (request.Tint is { } tint)
            {
                agent.SetTint(tint);
            }

            agents.Add(agent);
            activeDirty = true;

            LogSpawnModeOnce();
            return OperationResult<IRobotAgent>.Success(agent);
        }

        public bool TryGetPlayerPosition(out Vec3 position)
        {
            var player = ResolvePlayer();
            if (player != null)
            {
                var p = player.transform.position;
                position = new Vec3(p.x, p.y, p.z);
                return true;
            }

            position = Vec3.Zero;
            return false;
        }

        public bool TryGetPlayerEntity(out IEntity? entity)
        {
            var player = ResolvePlayer();
            if (player != null)
            {
                if (PlayerBridge.GetPlayerObject(player) is GameObject gameObject)
                {
                    entity = new NativeEntityAdapter("robotkit:player", gameObject);
                    return true;
                }
            }

            entity = null;
            return false;
        }

        public bool DamagePlayer(float amount, string source)
        {
            var player = ResolvePlayer();
            if (player == null)
            {
                return false;
            }

            playerHealth ??= PlayerBridge.FindHealth(player);
            return playerHealth != null && PlayerBridge.ChangeHealth(playerHealth, amount, source, logger);
        }

        public void SetPlayerControlsEnabled(bool enabled)
        {
            var player = ResolvePlayer();
            if (player != null)
            {
                PlayerBridge.SetFpsControllerEnabled(player, enabled);
            }
        }

        public Task<OperationResult<ReachableSpawnResult>> FindReachableSpawnAsync(
            ReachableSpawnRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (disposed)
            {
                return Task.FromResult(OperationResult<ReachableSpawnResult>.Failure(
                    ModErrorCode.InvalidState,
                    "RobotKit has been disposed."));
            }

            // The search self-completes in its constructor when the service is gone or there is no navigation, so a
            // caller can always poll the returned handle without special-casing those paths.
            var search = new ReachableSpawnSearch(
                request,
                ResolvePathFindSettings(),
                random,
                logger);
            if (!disposed && !search.IsComplete)
            {
                searches.Add(search);
            }

            search.AttachCancellation(cancellationToken);
            return search.Completion;
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(IRobotAgentService))
            {
                throw new ArgumentException("Unsupported RobotKit agent extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, lifetime);
        }

        // The designer-tuned PathFindSettings read off the spawnable robot prefab's LocomotionController (cached for
        // the scene), so reachability uses the same agent footprint the spawned robots have. Null is a valid result;
        // the search then falls back to a built-in default footprint.
        private object? ResolvePathFindSettings()
        {
            if (cachedPathFindSettings != null)
            {
                return cachedPathFindSettings;
            }

            var prefab = ResolveCachedPrefab();
            if (prefab != null)
            {
                cachedPathFindSettings = LocomotionBridge.GetPathFindSettings(prefab);
            }

            return cachedPathFindSettings;
        }

        // Per-frame tick (driven by the framework mod from context.Update): prune dead/despawned robots, then
        // drive each live agent's native walk.
        public void Tick(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            for (var index = agents.Count - 1; index >= 0; index--)
            {
                if (!agents[index].IsAlive)
                {
                    agents.RemoveAt(index);
                    activeDirty = true;
                }
            }

            for (var index = 0; index < agents.Count; index++)
            {
                // Defense-in-depth: one agent's unexpected throw must not starve the rest this frame.
                try
                {
                    agents[index].Step();
                }
                catch (Exception ex)
                {
                    logger.Debug("RobotKit agent step failed: " + ex.Message);
                }
            }

            // Advance in-flight reachable-spawn searches; a completed search is dropped from the tick list (the
            // caller keeps its own handle to read the result).
            for (var index = searches.Count - 1; index >= 0; index--)
            {
                var search = searches[index];
                try
                {
                    search.Step();
                }
                catch (Exception ex)
                {
                    logger.Debug("RobotKit spawn search step failed: " + ex.Message);
                    search.Cancel();
                }

                if (search.IsComplete)
                {
                    searches.RemoveAt(index);
                }
            }
        }

        // The root is DontDestroyOnLoad, so leftover robots and stale player handles would otherwise bleed into
        // the next scene. Clear and re-resolve everything for the new scene.
        public void OnSceneChanged()
        {
            ClearAgents();
            CancelSearches();
            LocomotionBridge.ResetSceneCache();
            cachedCatalog = null;
            cachedTypes = null;
            cachedPathFindSettings = null;
            nextPrefabScan = 0f;
            playerController = null;
            playerHealth = null;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ClearAgents();
            CancelSearches();
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }

            incubator = null;
            RobotKitDiagnostics.Clear(logger);
        }

        private void CancelSearches()
        {
            foreach (var search in searches)
            {
                search.Cancel();
            }

            searches.Clear();
        }

        private sealed class OwnerFacade : IRobotAgentService
        {
            private readonly RobotAgentService service;
            private readonly IModLifetime lifetime;
            private readonly List<IRobotAgent> ownedAgents = new List<IRobotAgent>();

            public OwnerFacade(RobotAgentService service, IModLifetime lifetime)
            {
                this.service = service;
                this.lifetime = lifetime;
            }

            public bool IsAvailable => !lifetime.IsStopping && service.IsAvailable;
            public bool IsNavigationAvailable => !lifetime.IsStopping && service.IsNavigationAvailable;
            public IReadOnlyList<RobotTypeDescriptor> RobotTypes => service.RobotTypes;

            public IReadOnlyList<IRobotAgent> ActiveAgents
            {
                get
                {
                    ownedAgents.RemoveAll(agent => !agent.IsAlive);
                    return ownedAgents.ToArray();
                }
            }

            public bool TryGetRobot(IEntity entity, out IRobotAgent? agent)
            {
                foreach (var owned in ownedAgents)
                {
                    if (ReferenceEquals(owned, entity)
                        || owned is OwnerRobotAgent wrapper && wrapper.Wraps(entity))
                    {
                        agent = owned;
                        return true;
                    }
                }

                agent = null;
                return false;
            }

            public OperationResult<IRobotAgent> Spawn(RobotAgentSpawnRequest request)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<IRobotAgent>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot spawn robots.");
                }

                var result = service.Spawn(request);
                if (!result.TryGetValue(out var agent))
                {
                    return result;
                }

                try
                {
                    var wrapper = new OwnerRobotAgent(agent, lifetime.Track(agent));
                    ownedAgents.Add(wrapper);
                    return OperationResult<IRobotAgent>.Success(wrapper);
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IRobotAgent>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before its spawned robot could be retained.");
                }
            }

            public async Task<OperationResult<ReachableSpawnResult>> FindReachableSpawnAsync(
                ReachableSpawnRequest request,
                CancellationToken cancellationToken = default)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.StoppingToken))
                {
                    return await service.FindReachableSpawnAsync(request, linked.Token);
                }
            }

            private sealed class OwnerRobotAgent : IRobotAgent
            {
                private readonly IRobotAgent agent;
                private IDisposable? lifetimeLease;

                public OwnerRobotAgent(IRobotAgent agent, IDisposable lifetimeLease)
                {
                    this.agent = agent;
                    this.lifetimeLease = lifetimeLease;
                }

                public string Id => agent.Id;
                public string Name => agent.Name;
                public bool IsAlive => lifetimeLease != null && agent.IsAlive;
                public Vec3 Position => agent.Position;
                public Vec3 HeadPosition => agent.HeadPosition;
                public RobotBrainMode BrainMode => agent.BrainMode;
                public bool IsMoving => agent.IsMoving;
                public bool HasReachedTarget => agent.HasReachedTarget;
                public float MoveSpeed => agent.MoveSpeed;
                public float TurnSpeed => agent.TurnSpeed;
                public float StopDistance => agent.StopDistance;
                public RobotGait Gait => agent.Gait;

                public bool Wraps(IEntity entity) => ReferenceEquals(agent, entity);
                public OperationResult<bool> SetBrainMode(RobotBrainMode mode) => agent.SetBrainMode(mode);
                public OperationResult<bool> ConfigureMovement(RobotMovementSettings settings) =>
                    agent.ConfigureMovement(settings);
                public OperationResult<bool> MoveTo(Vec3 position) => agent.MoveTo(position);
                public OperationResult<bool> Chase(IEntity target) =>
                    agent.Chase(target is OwnerRobotAgent wrapper ? wrapper.agent : target);
                public OperationResult<bool> Stop() => agent.Stop();
                public OperationResult<bool> SetTint(RobotColor color) => agent.SetTint(color);
                public OperationResult<bool> SetEmote(string emojiShortcode) => agent.SetEmote(emojiShortcode);
                public OperationResult<bool> SetName(string name) => agent.SetName(name);
                public OperationResult<bool> SetScale(float scale) => agent.SetScale(scale);
                public OperationResult<bool> SetInteraction(RobotInteractionOptions options) =>
                    agent.SetInteraction(options);
                public OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source) =>
                    agent.ApplyDamage(amount, type, source);
                public OperationResult<bool> Kill(RobotDamageType type, string source) => agent.Kill(type, source);
                public OperationResult<bool> Ragdoll() => agent.Ragdoll();
                public OperationResult<bool> Knockback(Vec3 impulse) => agent.Knockback(impulse);

                public OperationResult<bool> Despawn()
                {
                    var result = agent.Despawn();
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                    return result;
                }

                public void Dispose()
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }

        private void ClearAgents()
        {
            foreach (var agent in agents)
            {
                agent.Despawn();
            }

            agents.Clear();
            activeDirty = true;
        }

        private void EnsureRoots()
        {
            if (root != null)
            {
                return;
            }

            root = new GameObject("RobotKit Agents");
            UnityEngine.Object.DontDestroyOnLoad(root);
            incubator = new GameObject("RobotKit Incubator");
            incubator.transform.SetParent(root.transform, false);
            incubator.SetActive(false);
        }

        // Native locomotion drives the transform directly and requires a kinematic root rigidbody (WalkSession
        // throws otherwise). The native robot prefab is already kinematic; this only fixes a stray non-kinematic
        // root, and never touches the ragdoll bone bodies (the LocomotionController owns those on death).
        private static void EnsureKinematicRoot(GameObject clone)
        {
            if (clone.TryGetComponent<Rigidbody>(out var body) && !body.isKinematic)
            {
                body.isKinematic = true;
            }
        }

        private GameObject? ResolveCachedPrefab()
        {
            var catalog = ResolveCachedCatalog();
            return catalog != null && catalog.Count > 0 ? catalog[0].Prefab : null;
        }

        // The requested robot type's prefab; an unknown/stale id logs once and falls back to the default type
        // (index 0) rather than failing the spawn.
        private GameObject? ResolvePrefabForType(string? robotTypeId)
        {
            var catalog = ResolveCachedCatalog();
            if (catalog == null || catalog.Count == 0)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(robotTypeId))
            {
                return catalog[0].Prefab;
            }

            foreach (var candidate in catalog)
            {
                if (string.Equals(candidate.Id, robotTypeId, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.Prefab;
                }
            }

            logger.Warn("RobotKit: unknown robot type '" + robotTypeId + "' — spawning the default type instead.");
            return catalog[0].Prefab;
        }

        private IReadOnlyList<RobotPrefabCandidate>? ResolveCachedCatalog()
        {
            if (cachedCatalog != null && cachedCatalog.Count > 0)
            {
                return cachedCatalog;
            }

            // ResolveAll does full Resources scans, which are expensive; throttle re-scans while nothing is
            // found (e.g. before a gameplay level has loaded any robots). Reset on scene change.
            if (Time.unscaledTime < nextPrefabScan)
            {
                return null;
            }

            cachedCatalog = prefabResolver.ResolveAll();
            if (cachedCatalog.Count == 0)
            {
                cachedCatalog = null;
                nextPrefabScan = Time.unscaledTime + 2f;
            }
            else
            {
                cachedTypes = null;
            }

            return cachedCatalog;
        }

        private Component? ResolvePlayer()
        {
            if (playerController != null)
            {
                return playerController;
            }

            playerController = PlayerBridge.FindPlayerController();
            playerHealth = null;
            return playerController;
        }

        private string NextId()
        {
            spawnCounter++;
            return "robot-" + spawnCounter;
        }

        private void LogSpawnModeOnce()
        {
            if (loggedSpawnMode)
            {
                return;
            }

            loggedSpawnMode = true;
            logger.Info("RobotKit: spawning standard agents — native locomotion via WalkSession.");
            if (IsNavigationAvailable)
            {
                logger.Info("RobotKit navigation: native pathfinder available.");
            }
            else
            {
                logger.Warn("RobotKit navigation: native pathfinder unavailable; robots can stand and animate but cannot path until one exists.");
            }
        }
    }
}
