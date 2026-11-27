using System;
using System.Collections.Generic;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotAgentService
    {
        // StartingMode is committed before the gamemode factory runs. A catalog observed during world load
        // must retry composition on that notification, without waiting for the ordinary two-second throttle.
        private float nextComposedScan;
        private float composedRetryDelay = 2f;

        internal void InvalidateComposedDiscovery()
        {
            nextPrefabScan = 0f;
            nextComposedScan = 0f;
            composedRetryDelay = 2f;
        }

        private void ClearAgents()
        {
            foreach (var agent in agents)
            {
                agent.Despawn();
            }

            agents.Clear();
            // Native death can remove an agent handle before its corpse is destroyed. Its fresh builder parent
            // still belongs to this scene lifetime and must not survive a provider reset.
            if (root != null)
                foreach (var composition in root.GetComponentsInChildren<RobotComposedLifetime>(true)) composition.Dispose();
            agentsById.Clear();
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
            if (catalog != null)
                foreach (var candidate in catalog) if (candidate.Prefab != null) return candidate.Prefab;
            return null;
        }

        private IReadOnlyList<RobotPrefabCandidate>? ResolveCachedCatalog()
        {
            // OnSceneChanged also resets nextPrefabScan. A real publication or scene boundary
            // must not retain a negative discovery delay from the previous world.
            if (nextPrefabScan == 0f)
            {
                nextComposedScan = 0f;
                composedRetryDelay = 2f;
            }
            RobotPrefabCandidate? composition = null;
            var hasPrefab = false;
            if (cachedCatalog != null)
            {
                var staleComposition = false;
                foreach (var candidate in cachedCatalog)
                {
                    if (candidate.Prefab != null) hasPrefab = true;
                    else if (candidate.IsCurrent) composition = candidate;
                    else staleComposition = true;
                }
                if (staleComposition)
                {
                    var retained = new List<RobotPrefabCandidate>();
                    foreach (var candidate in cachedCatalog)
                        if (candidate.Prefab != null || ReferenceEquals(candidate, composition)) retained.Add(candidate);
                    cachedCatalog = retained;
                    cachedTypes = null;
                    InvalidateComposedDiscovery();
                }
                // A current composed actor cannot make an empty prefab scan permanent.
                if (composition != null && hasPrefab) return cachedCatalog;
            }
            var now = Time.unscaledTime;
            if (now < nextPrefabScan) return cachedCatalog;

            var catalog = new List<RobotPrefabCandidate>();
            if (cachedCatalog != null)
                foreach (var candidate in cachedCatalog) if (candidate.Prefab != null) catalog.Add(candidate);
            if (catalog.Count == 0) catalog.AddRange(prefabResolver.ResolveAll());
            if (composition != null)
            {
                if (!catalog.Exists(candidate => string.Equals(candidate.Id, composition.Id, StringComparison.OrdinalIgnoreCase)))
                    catalog.Add(composition);
            }
            else if (now >= nextComposedScan)
            {
                var composed = RobotComposedScene.Discover(world, out var notPresent);
                if (composed != null && !catalog.Exists(candidate => string.Equals(candidate.Id, composed.Id, StringComparison.OrdinalIgnoreCase)))
                    catalog.Add(composed);
                nextComposedScan = now + (notPresent ? composedRetryDelay : 2f);
                composedRetryDelay = notPresent ? Math.Min(composedRetryDelay * 2f, 30f) : 2f;
            }
            var changed = cachedCatalog == null || catalog.Count != cachedCatalog.Count
                || (composition != null && !catalog.Contains(composition));
            if (changed) cachedCatalog = catalog;
            nextPrefabScan = now + 2f;
            if (cachedCatalog == null || cachedCatalog.Count == 0) cachedCatalog = null;
            else if (changed) cachedTypes = null;

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
