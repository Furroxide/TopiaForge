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
    internal sealed partial class RobotAgentService :
        IRobotAgentService,
        IRobotPlayerEntitySource,
        IOwnerBoundExtensionFactory,
        IDisposable
    {
        private readonly IModLogger logger;
        private readonly RobotPrefabResolver prefabResolver;
        private readonly List<RobotAgent> agents = new List<RobotAgent>();
        private readonly Dictionary<string, RobotAgent> agentsById =
            new Dictionary<string, RobotAgent>(StringComparer.Ordinal);
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
        private NativeEntityAdapter? playerEntity;
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

        // A PhysicsHit represents the canonical safe entity created by the host registry, not this assembly's
        // RobotAgent instance. Both carry the stable id installed on the spawned root's identity anchor, so hits
        // from root, child, and ragdoll colliders all resolve through the same allocation-free index.
        internal IRobotAgent? FindAgentByEntity(IEntity entity)
        {
            if (entity == null || !entity.IsAlive)
            {
                return null;
            }

            if (agentsById.TryGetValue(entity.Id, out var agent) && agent.IsAlive)
            {
                return agent;
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
            var agentId = NextId();
            // Reuse a marker if a resolver ever hands us an already-marked clone; two anchors on one object make
            // interface-based GetComponent selection ambiguous. The new clone begins a new entity lifetime/id.
            var identityAnchor = clone.GetComponent<RobotAgentEntityIdentityAnchor>()
                ?? clone.AddComponent<RobotAgentEntityIdentityAnchor>();
            identityAnchor.Initialize(agentId);
            var agent = new RobotAgent(agentId, clone, request, logger, brainSnapshot);

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
            agentsById.Add(agent.Id, agent);
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
            if (disposed)
            {
                entity = null;
                return false;
            }

            var player = ResolvePlayer();
            if (player != null)
            {
                if (PlayerBridge.GetPlayerObject(player) is GameObject gameObject)
                {
                    if (playerEntity == null || playerEntity.NativeGameObject != gameObject)
                    {
                        playerEntity = new NativeEntityAdapter("robotkit:player", gameObject);
                    }

                    entity = playerEntity;
                    return true;
                }
            }

            playerEntity = null;
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
                    agentsById.Remove(agents[index].Id);
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
            playerEntity = null;
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
    }
}
