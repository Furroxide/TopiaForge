using System;
using System.Collections.Generic;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class UnityWorldRuntimeBackend
    {
        private sealed partial class NativeLoad
        {
            private Transform? placedPlayer;
            private TransformState? requestedSpawn;
            private NativeSpawnSettlement? settlement;
            private CharacterController? spawnController;
            public OperationResult<TransformState>? ReadDefaultSpawn()
            {
                UnityMainThreadGuard.AssertCurrent();
                try
                {
                    if (request.Kind == NativeWorldLoadKind.OpenSandbox && RequiresDispatch)
                    {
                        var type = Type.GetType("UgcImportPlayBootstrap, GameCode", false);
                        if (type == null) return Failure<TransformState>(ModErrorCode.Unavailable, "The supported sandbox bootstrap is unavailable.");
                        Component? selected = null;
                        foreach (var candidate in UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None))
                        {
                            if (!(candidate is Component component) || component == null || component.gameObject.scene.handle != captured!.Value.handle) continue;
                            if (selected != null) return Failure<TransformState>(ModErrorCode.Conflict, "The selected scene contains multiple sandbox bootstraps.");
                            selected = component;
                        }
                        return selected == null ? null : OperationResult<TransformState>.Success(ReadTransform(selected.transform));
                    }
                    var player = ResolvePlayer();
                    return player == null ? null : OperationResult<TransformState>.Success(ReadTransform(player));
                }
                catch (Exception error) { return Failure<TransformState>(ModErrorCode.External, Unwrap(error).Message); }
            }
            public OperationResult<bool> ValidateContent(IEntity? contentRoot)
            {
                var scene = ValidateScene();
                if (!scene.Succeeded) return scene;
                return contentRoot == null || (entities.TryGetGameObject(contentRoot, out var root) && root.scene.handle == captured!.Value.handle)
                    ? OperationResult<bool>.Success(true)
                    : Failure<bool>(ModErrorCode.InvalidArgument, "The world content root is not a live entity in the prepared scene.");
            }
            public OperationResult<IReadOnlyList<TransformState>> FindSpawnMarkers(IEntity? contentRoot, string markerName)
            {
                UnityMainThreadGuard.AssertCurrent();
                try
                {
                    var valid = ValidateScene();
                    if (!valid.Succeeded) return Failure<IReadOnlyList<TransformState>>(valid.ErrorCode, valid.ErrorMessage);
                    var queue = new Queue<Transform>();
                    if (contentRoot != null)
                    {
                        if (!entities.TryGetGameObject(contentRoot, out var root) || root.scene.handle != captured!.Value.handle)
                            return Failure<IReadOnlyList<TransformState>>(ModErrorCode.InvalidArgument, "The world content root is not a live entity in the prepared scene.");
                        queue.Enqueue(root.transform);
                    }
                    else foreach (var root in captured!.Value.GetRootGameObjects()) queue.Enqueue(root.transform);
                    var matches = new List<TransformState>(); var inspected = 0;
                    while (queue.Count > 0)
                    {
                        if (++inspected > 16384) return Failure<IReadOnlyList<TransformState>>(ModErrorCode.InvalidState, "World marker inspection exceeded its bounded hierarchy limit.");
                        var current = queue.Dequeue();
                        if (string.Equals(current.name, markerName, StringComparison.Ordinal)) matches.Add(ReadTransform(current));
                        if (matches.Count > 1) break;
                        for (var index = 0; index < current.childCount; index++) queue.Enqueue(current.GetChild(index));
                    }
                    return OperationResult<IReadOnlyList<TransformState>>.Success(matches.AsReadOnly());
                }
                catch (Exception error) { return Failure<IReadOnlyList<TransformState>>(ModErrorCode.External, Unwrap(error).Message); }
            }
            public OperationResult<TransformState>? ApplySpawn(TransformState spawn)
            {
                UnityMainThreadGuard.AssertCurrent();
                try
                {
                    var player = ResolvePlayer();
                    if (player == null) return null;
                    if (requestedSpawn.HasValue)
                    {
                        if (placedPlayer == null || placedPlayer != player || requestedSpawn.Value != spawn)
                            return Failure<TransformState>(ModErrorCode.Conflict, "The player or selected spawn changed during readiness.");
                        var actual = ReadTransform(player);
                        if (spawnController == null || !spawnController.enabled || player.GetComponent<CharacterController>() != spawnController || settlement == null)
                        {
                            WarnSpawnOffset(actual, spawn, "refused");
                            return Failure<TransformState>(ModErrorCode.Unavailable, "The native player collision shape is unavailable during spawn settling.");
                        }
                        var result = settlement.Probe(actual, Time.fixedTime);
                        if (result != null) WarnSpawnOffset(actual, spawn, result.Succeeded ? "accepted" : "refused");
                        return result;
                    }
                    var controller = player.GetComponent<CharacterController>();
                    var scale = player.lossyScale;
                    if (controller == null || !controller.enabled || !controller.gameObject.activeInHierarchy
                        || !NativeSpawnSettlement.Finite(scale.x) || !NativeSpawnSettlement.Finite(scale.y)
                        || !NativeSpawnSettlement.Finite(scale.z) || scale.x <= 0f || scale.x != scale.y || scale.x != scale.z
                        || !NativeSpawnSettlement.Finite(controller.height) || controller.height <= 0f
                        || !NativeSpawnSettlement.Finite(controller.height * scale.y) || !NativeSpawnSettlement.Finite(Time.fixedTime))
                        return Failure<TransformState>(ModErrorCode.Unavailable, "The native player collision height is unavailable for spawn settling.");
                    TopiaForge.Mods.GameBridge.NativePlayerTeleport.Place(player,
                        new Vector3(spawn.Position.X, spawn.Position.Y, spawn.Position.Z),
                        new Quaternion(spawn.Rotation.X, spawn.Rotation.Y, spawn.Rotation.Z, spawn.Rotation.W));
                    spawnController = controller;
                    settlement = new NativeSpawnSettlement(spawn, controller.height * scale.y, Time.fixedTime);
                    try { WarnSpawnOffset(ReadTransform(player), spawn, "applied"); }
                    catch (Exception) { }
                    placedPlayer = player; requestedSpawn = spawn;
                    return null; // Observe settling on subsequent fixed-time epochs within the existing readiness deadline.
                }
                catch (Exception error) { return Failure<TransformState>(ModErrorCode.External, Unwrap(error).Message); }
            }
            private static void WarnSpawnOffset(TransformState actual, TransformState spawn, string stage)
            {
                try { UnityEngine.Debug.LogWarning(NativeSpawnSettlement.DescribeOffset(actual, spawn, stage)); }
                catch (Exception) { } // Optional relative telemetry cannot affect placement/readiness ownership.
            }
            private static Transform? ResolvePlayer()
            {
                var type = Type.GetType("PlayerController, GameCode", false)
                    ?? throw new InvalidOperationException("The supported native player controller is unavailable.");
                var method = type.GetMethod("FindPlayer", PublicStatic, null, Type.EmptyTypes, null)
                    ?? throw new InvalidOperationException("The supported native player lookup is unavailable.");
                var value = method.Invoke(null, null);
                return value is Component player && player != null && player.gameObject.activeInHierarchy
                    && player.gameObject.scene.IsValid() ? player.transform : null;
            }
            private static TransformState ReadTransform(Transform value)
            {
                var position = value.position; var rotation = value.rotation; var scale = value.localScale;
                return new TransformState(new Vec3(position.x, position.y, position.z), new Quat(rotation.x, rotation.y, rotation.z, rotation.w),
                    new Vec3(scale.x, scale.y, scale.z));
            }
        }
    }
}
