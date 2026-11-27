using System;
using System.Reflection;
using TopiaForge.Mods.GameBridge;
using UnityEngine;

namespace TopiaForge.Worlds
{
    // Reads the actual PlayerController; the SDK's aim snapshot may represent only a camera.
    internal static class NativeWorldPlayer
    {
        public static Transform? GetTransform()
        {
            var type = Type.GetType("PlayerController, GameCode", throwOnError: false);
            var find = type?.GetMethod("FindPlayer", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return find?.Invoke(null, null) is Component player && player != null && player.gameObject.activeInHierarchy
                && player.gameObject.scene.IsValid() ? player.transform : null;
        }
        public static void Place(Transform player, Vector3 position, Quaternion rotation)
        {
            NativePlayerTeleport.Place(player, position, rotation);
        }
    }
}
