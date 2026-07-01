using System;
using System.Linq;
using System.Reflection;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.Zombies
{
    // The robot-spawning, navigation, and player-access reflection that used to live here has moved to the
    // Robotopia.RobotKit framework mod (consumed via IRobotAgentService). What remains is the small surface
    // Zombies still needs directly: identifying game-robot colliders (zapper + spawn placement) and loading the
    // menu scene on return-to-menu.
    internal static class ReflectionUtil
    {
        private const BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly Type? RobotBodyType = Type.GetType("RobotBody, GameCode", throwOnError: false);

        // Non-allocating "is this collider part of a game robot (including a spawned one)?" — used on hot paths
        // (per-shot zapper hits, spawn placement). Spawned zombies keep their RobotBody, so this matches them too.
        public static bool IsGameRobotInParent(Component? component)
        {
            if (component == null)
            {
                return false;
            }

            if (RobotBodyType != null)
            {
                return component.GetComponentInParent(RobotBodyType) != null;
            }

            return HasComponentInParent(component, "RobotBody");
        }

        public static void LoadScene(string sceneName, IModLogger? logger)
        {
            try
            {
                var sceneUtil = Type.GetType("SceneUtil, GameCode", throwOnError: false);
                var method = sceneUtil?.GetMethod(
                    "LoadScene",
                    StaticFlags,
                    null,
                    new[] { typeof(string), typeof(System.Threading.CancellationToken) },
                    null);
                if (method != null)
                {
                    method.Invoke(null, new object[] { sceneName, System.Threading.CancellationToken.None });
                    return;
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("Game scene loader unavailable: " + ex.Message);
            }

            try
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
            }
            catch (Exception ex)
            {
                logger?.Warn("Could not load scene '" + sceneName + "': " + ex.Message);
            }
        }

        private static bool HasComponentInParent(Component? component, params string[] typeNames)
        {
            if (component == null)
            {
                return false;
            }

            foreach (var candidate in component.GetComponentsInParent<Component>(true))
            {
                if (IsNamed(candidate, typeNames))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsNamed(Component? component, params string[] typeNames)
        {
            if (component == null)
            {
                return false;
            }

            var type = component.GetType();
            while (type != null)
            {
                if (typeNames.Any(name => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }
    }
}
