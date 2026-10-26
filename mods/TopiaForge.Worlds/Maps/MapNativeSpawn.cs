using System;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Worlds.Maps
{
    // The respawn factory is isolated here so native verification can adjust placement
    // without changing validation, cache identity or the world readiness contract.
    internal static class MapNativeSpawn
    {
        internal static OperationResult<TransformState> Place(Transform? root, string entityId)
        {
            try
            {
                if (root == null) return Failure("The map's owned content is no longer available.");
                var entityType = Type.GetType("UgcImportedEntity, GameCode", false);
                var spawnType = Type.GetType("UgcSceneSpawnLocation, GameCode", false);
                var bootstrapType = Type.GetType("UgcImportPlayBootstrap, GameCode", false);
                var factory = Type.GetType("UgcRuntimeSpawnPointFactory, GameCode", false);
                if (entityType == null || spawnType == null || bootstrapType == null || factory == null) return Failure();
                var id = entityType.GetProperty("EntityId", BindingFlags.Public | BindingFlags.Instance);
                if (id?.PropertyType != typeof(string)) return Failure();
                var matches = root.GetComponentsInChildren(entityType, true)
                    .Where(item => string.Equals(id.GetValue(item) as string, entityId, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1 || matches[0].GetComponent(spawnType) == null)
                    return Failure("The selected map spawn did not survive import. Ask its creator to export the map again.");
                var marker = matches[0].transform;
                var bootstraps = root.gameObject.scene.GetRootGameObjects()
                    .SelectMany(item => item.GetComponentsInChildren(bootstrapType, true)).ToArray();
                var prefab = bootstrapType.GetField("playerPrefab", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (bootstraps.Length != 1 || prefab?.FieldType != typeof(GameObject) ||
                    !(prefab.GetValue(bootstraps[0]) is GameObject playerPrefab) || playerPrefab == null) return Failure();
                var create = factory.GetMethod("CreateOrUpdateSpawnPoint", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(Transform) }, null);
                if (create?.ReturnType.FullName != "Checkpoint") return Failure();
                var position = marker.position; var rotation = marker.rotation;
                var checkpoint = create.Invoke(null, new object[] { position, rotation, playerPrefab, root });
                if (!(checkpoint is Component native) || native == null || native.gameObject.scene != root.gameObject.scene)
                    return Failure("The map's respawn point could not be placed. Update TopiaForge.");
                return OperationResult<TransformState>.Success(new TransformState(new Vec3(position.x, position.y, position.z),
                    new Quat(rotation.x, rotation.y, rotation.z, rotation.w), new Vec3(1, 1, 1)));
            }
            catch (Exception) { return Failure(); }
        }
        private static OperationResult<TransformState> Failure(string message = "This game build cannot place map spawns yet. Update TopiaForge.") =>
            OperationResult<TransformState>.Failure(ModErrorCode.Unavailable, message);
    }
}
