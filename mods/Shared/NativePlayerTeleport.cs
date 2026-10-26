using System;
using System.Reflection;
using UnityEngine;

namespace TopiaForge.Mods.GameBridge
{
    /// <summary>Uses native locomotion's teleport contract, including its velocity and look state.</summary>
    internal static class NativePlayerTeleport
    {
        internal static void Place(Transform player, Vector3 position, Quaternion rotation)
        {
            var type = Type.GetType("PlayerController, GameCode", throwOnError: false)
                ?? throw new InvalidOperationException("The native player controller is unavailable. Restart the game and try again.");
            var component = player.GetComponent(type);
            if (component == null) throw new InvalidOperationException("The native player is unavailable. Wait for the level to load and try again.");
            PlaceController(component, position, rotation);
        }

        internal static void PlaceController(object player, Vector3 position, Quaternion rotation)
        {
            var property = player.GetType().GetProperty("FPSController", BindingFlags.Public | BindingFlags.Instance);
            var controller = property?.GetValue(player)
                ?? throw new InvalidOperationException("The native movement controller is unavailable. Wait for the level to load and try again.");
            var teleport = controller.GetType().GetMethod("TeleportTo", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(Vector3), typeof(Quaternion?) }, null);
            if (teleport == null || teleport.ReturnType != typeof(void))
                throw new InvalidOperationException("The native teleport contract is unsupported. Update TopiaForge for this game build.");
            teleport.Invoke(controller, new object[] { position, rotation });
        }
    }
}
