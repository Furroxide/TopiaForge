using System;
using System.Linq;
using System.Reflection;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.RobotKit
{
    // Reflection bridge for the small amount of player access combat gamemodes need: locate the player, read its
    // position, deal damage to its Health, and suspend/resume its first-person controller.
    internal static class PlayerBridge
    {
        private const BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        public static Component? FindPlayerController()
        {
            var playerType = Type.GetType("PlayerController, GameCode", throwOnError: false);
            if (playerType != null)
            {
                var findPlayer = playerType.GetMethod("FindPlayer", StaticFlags);
                if (findPlayer != null)
                {
                    try
                    {
                        if (findPlayer.Invoke(null, Array.Empty<object>()) is Component player)
                        {
                            return player;
                        }
                    }
                    catch (Exception ex)
                    {
                        // The named API is a compatibility optimization; the component scan below remains the
                        // authoritative fallback. Report the contract failure once so build drift is visible.
                        RobotKitDiagnostics.ReportOnce("player lookup", ex);
                    }
                }
            }

            return UnityEngine.Object.FindObjectsByType<Component>(FindObjectsSortMode.None)
                .FirstOrDefault(component => GameReflection.IsNamed(component, "PlayerController"));
        }

        // The player's GameObject, suitable as a native chase target (ActionTarget resolves its prefab root).
        public static object GetPlayerObject(Component playerController)
        {
            return playerController.gameObject;
        }

        public static Component? FindHealth(Component component)
        {
            // Prefer the PlayerController's OWN Health (how the game itself resolves it), then children, then
            // ancestors — so a Health on an ancestor rig/vehicle is never picked over the player's own.
            return component.GetComponents<Component>()
                .Concat(component.GetComponentsInChildren<Component>(true))
                .Concat(component.GetComponentsInParent<Component>(true))
                .FirstOrDefault(candidate => GameReflection.IsNamed(candidate, "Health"));
        }

        // Apply damage to a resolved Health component via Health.ChangeHealth(neg amount, source).
        public static bool ChangeHealth(Component health, float amount, string source, IModLogger? logger)
        {
            return GameReflection.Invoke(health, "ChangeHealth", logger, -amount, source);
        }

        // Enable/disable the player's first-person controller (PlayerController.FPSController).
        public static void SetFpsControllerEnabled(Component playerController, bool enabled)
        {
            if (GameReflection.GetPropertyValue(playerController, "FPSController") is Behaviour fps)
            {
                fps.enabled = enabled;
            }
        }
    }
}
