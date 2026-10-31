using System;
using System.Collections.Generic;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotAgentService
    {
        // StartingMode is committed before the gamemode factory runs. A catalog observed during world load
        // must retry composition on that notification, without waiting for the ordinary two-second throttle.
        internal void InvalidateComposedDiscovery() => nextPrefabScan = 0f;

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
            if (cachedCatalog != null)
            {
                var staleComposition = false;
                foreach (var candidate in cachedCatalog)
                {
                    if (candidate.Prefab != null) continue;
                    if (candidate.IsCurrent) return cachedCatalog;
                    staleComposition = true;
                }
                if (staleComposition)
                {
                    var retained = new List<RobotPrefabCandidate>();
                    foreach (var candidate in cachedCatalog) if (candidate.Prefab != null) retained.Add(candidate);
                    cachedCatalog = retained;
                    cachedTypes = null;
                    nextPrefabScan = 0f;
                }
            }
            // ResolveAll does full Resources scans, which are expensive; throttle re-scans while nothing is
            // found (e.g. before a gameplay level has loaded any robots). Reset on scene change.
            if (Time.unscaledTime < nextPrefabScan)
            {
                return cachedCatalog;
            }

            // A map can finish loading after ordinary prefab discovery has already succeeded. Retry composed
            // discovery too, without changing the existing default-prefab order or rescanning every frame.
            var catalog = new List<RobotPrefabCandidate>();
            if (cachedCatalog != null)
                foreach (var candidate in cachedCatalog) if (candidate.Prefab != null) catalog.Add(candidate);
            if (catalog.Count == 0) catalog.AddRange(prefabResolver.ResolveAll());
            var composed = RobotComposedScene.Discover(world);
            if (composed != null && !catalog.Exists(candidate => string.Equals(candidate.Id, composed.Id, StringComparison.OrdinalIgnoreCase)))
                catalog.Add(composed);
            var changed = cachedCatalog == null || catalog.Count != cachedCatalog.Count;
            if (changed) cachedCatalog = catalog;
            nextPrefabScan = Time.unscaledTime + 2f;
            if (cachedCatalog == null || cachedCatalog.Count == 0)
            {
                cachedCatalog = null;
                nextPrefabScan = Time.unscaledTime + 2f;
            }
            else if (changed)
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
