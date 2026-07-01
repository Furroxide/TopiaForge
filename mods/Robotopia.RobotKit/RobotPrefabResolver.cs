using System;
using System.Linq;
using System.Reflection;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.RobotKit
{
    // Resolves a spawnable robot prefab asset (not a live scene instance) from the loaded game, scoring candidates
    // by the robot components they carry. Previously lived inside the Zombies mod; promoted here so every
    // robot-spawning mod shares one authoritative resolver.
    internal sealed class RobotPrefabResolver
    {
        private static readonly string[] RobotComponentNames =
        {
            "RobotBody",
            "LLMAgent",
            "AgentHead",
            "LocomotionController",
            "SegmentedRobotBodyController",
            "SegmentedGenericBodyController"
        };

        private readonly IModLogger logger;
        private bool loggedSource;

        public RobotPrefabResolver(IModLogger logger)
        {
            this.logger = logger;
        }

        public GameObject? Resolve()
        {
            var fromSpawner = ResolveFromPooledSpawner();
            if (fromSpawner != null)
            {
                LogSource("using robot prefab from PooledSpawner: " + fromSpawner.name);
                return fromSpawner;
            }

            var fromLoadedAssets = ResolveFromLoadedAssets();
            if (fromLoadedAssets != null)
            {
                LogSource("using loaded robot object: " + fromLoadedAssets.name);
                return fromLoadedAssets;
            }

            return null;
        }

        private GameObject? ResolveFromPooledSpawner()
        {
            var bestScore = 0;
            GameObject? best = null;
            foreach (var component in UnityEngine.Object.FindObjectsByType<Component>(FindObjectsSortMode.None))
            {
                if (!GameReflection.IsNamed(component, "PooledSpawner"))
                {
                    continue;
                }

                var prefabs = GetPrefabs(component);
                foreach (var prefab in prefabs)
                {
                    var score = Score(prefab);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = prefab;
                    }
                }
            }

            return bestScore > 0 ? best : null;
        }

        private GameObject? ResolveFromLoadedAssets()
        {
            var bestScore = 0;
            GameObject? best = null;
            foreach (var component in Resources.FindObjectsOfTypeAll<Component>())
            {
                if (!GameReflection.IsNamed(component, RobotComponentNames))
                {
                    continue;
                }

                var root = GameReflection.GetRobotBodyRoot(component);
                var score = Score(root);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = root;
                }
            }

            return bestScore > 0 ? best : null;
        }

        private static GameObject[] GetPrefabs(Component spawner)
        {
            try
            {
                var field = spawner.GetType().GetField(
                    "prefabs",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return field?.GetValue(spawner) as GameObject[] ?? Array.Empty<GameObject>();
            }
            catch
            {
                return Array.Empty<GameObject>();
            }
        }

        private static int Score(GameObject? candidate)
        {
            if (candidate == null || GameReflection.HasComponent(candidate, "PlayerController"))
            {
                return 0;
            }

            // Only clone genuine prefab assets. A loaded prefab asset has an invalid/zero scene handle, while a
            // live (or already-spawned) scene instance belongs to a valid loaded scene. Cloning a live instance
            // would snapshot its mutated runtime state and momentarily duplicate it.
            if (candidate.scene.IsValid())
            {
                return 0;
            }

            var score = 0;
            if (GameReflection.HasComponent(candidate, "RobotBody"))
            {
                score += 100;
            }

            if (GameReflection.HasComponent(candidate, "LLMAgent"))
            {
                score += 40;
            }

            if (GameReflection.HasComponent(candidate, "AgentHead"))
            {
                score += 30;
            }

            if (GameReflection.HasComponent(candidate, "LocomotionController"))
            {
                score += 25;
            }

            if (GameReflection.HasComponent(candidate, "Health"))
            {
                score += 5;
            }

            return score;
        }

        private void LogSource(string message)
        {
            if (loggedSource)
            {
                return;
            }

            loggedSource = true;
            logger.Info("RobotKit " + message + ".");
        }
    }
}
