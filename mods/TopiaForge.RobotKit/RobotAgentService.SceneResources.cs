using System;
using System.Collections.Generic;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotAgentService
    {
        private void ClearAgents()
        {
            foreach (var agent in agents)
            {
                agent.Despawn();
            }

            agents.Clear();
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
