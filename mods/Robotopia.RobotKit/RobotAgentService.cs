using System;
using System.Collections.Generic;
using System.Linq;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.RobotKit
{
    // Implementation of the public IRobotAgentService. Owns a DontDestroyOnLoad root with an always-inactive
    // incubator (so a clone's native Awake/OnEnable fire only after the brain has been configured), the live
    // agent handles, and the per-frame tick that drives each agent's native walk.
    internal sealed class RobotAgentService : IRobotAgentService, IDisposable
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

        public bool IsRobotPrefab(object gameObject)
        {
            return gameObject is GameObject go && GameReflection.HasRobotBody(go);
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
        internal IRobotAgent? FindAgentByGameObject(object gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            for (var index = 0; index < agents.Count; index++)
            {
                if (ReferenceEquals(agents[index].GameObject, gameObject))
                {
                    return agents[index];
                }
            }

            return null;
        }

        public IRobotAgent? Spawn(RobotAgentSpawnRequest request)
        {
            if (disposed || request == null)
            {
                return null;
            }

            var prefab = ResolvePrefabForType(request.RobotTypeId);
            if (prefab == null)
            {
                return null;
            }

            EnsureRoots();
            if (incubator == null || root == null)
            {
                return null;
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
            return agent;
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

        public bool TryGetPlayerObject(out object gameObject)
        {
            var player = ResolvePlayer();
            if (player != null)
            {
                gameObject = PlayerBridge.GetPlayerObject(player);
                return true;
            }

            gameObject = null!;
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

        public IReachableSpawn BeginFindReachableSpawn(ReachableSpawnRequest request)
        {
            // The search self-completes in its constructor when the service is gone or there is no navigation, so a
            // caller can always poll the returned handle without special-casing those paths.
            var search = new ReachableSpawnSearch(
                request ?? new ReachableSpawnRequest(Vec3.Zero),
                ResolvePathFindSettings(),
                random,
                logger);
            if (!disposed && !search.IsComplete)
            {
                searches.Add(search);
            }

            return search;
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
