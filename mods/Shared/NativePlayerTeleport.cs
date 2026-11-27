using System;
using System.Reflection;
using UnityEngine;

namespace TopiaForge.Mods.GameBridge
{
    /// <summary>Uses native locomotion's teleport contract, including its velocity and look state.</summary>
    internal static class NativePlayerTeleport
    {
        // A game build without the contract stays without it, so waiting or retrying cannot help.
        internal const string Unsupported = "TopiaForge cannot move the player in this game version. Update TopiaForge, then try again.";

        internal static void Place(Transform player, Vector3 position, Quaternion rotation)
        {
            var type = Type.GetType("PlayerController, GameCode", throwOnError: false)
                ?? throw new InvalidOperationException(Unsupported);
            var component = player.GetComponent(type);
            if (component == null) throw new InvalidOperationException("The native player is unavailable. Wait for the level to load and try again.");
            PlaceController(component, position, rotation);
        }

        internal static void PlaceController(object player, Vector3 position, Quaternion rotation)
        {
            var property = player.GetType().GetProperty("FPSController", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(Unsupported);
            var controller = property.GetValue(player)
                ?? throw new InvalidOperationException("The native movement controller is unavailable. Wait for the level to load and try again.");
            var teleport = controller.GetType().GetMethod("TeleportTo", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(Vector3), typeof(Quaternion?) }, null);
            if (teleport == null || teleport.ReturnType != typeof(void)) throw new InvalidOperationException(Unsupported);
            teleport.Invoke(controller, new object[] { position, rotation });
        }
    }
}
