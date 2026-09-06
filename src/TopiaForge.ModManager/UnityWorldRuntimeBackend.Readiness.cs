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
                        var rotation = actual.Rotation; var expected = spawn.Rotation;
                        var dot = Math.Abs(rotation.X * expected.X + rotation.Y * expected.Y + rotation.Z * expected.Z + rotation.W * expected.W);
                        if ((actual.Position - spawn.Position).LengthSquared > 0.0001f || dot < 0.9999f)
                            return Failure<TransformState>(ModErrorCode.External, "The native player did not retain the applied spawn position and rotation.");
                        return OperationResult<TransformState>.Success(actual);
                    }
                    var controller = player.GetComponent<CharacterController>();
                    var wasEnabled = controller != null && controller.enabled;
                    try
                    {
                        if (controller != null) controller.enabled = false;
                        player.SetPositionAndRotation(new Vector3(spawn.Position.X, spawn.Position.Y, spawn.Position.Z),
                            new Quaternion(spawn.Rotation.X, spawn.Rotation.Y, spawn.Rotation.Z, spawn.Rotation.W));
                    }
                    finally { if (controller != null) controller.enabled = wasEnabled; }
                    placedPlayer = player; requestedSpawn = spawn;
                    return null; // Read back on a later frame after native player code has run.
                }
                catch (Exception error) { return Failure<TransformState>(ModErrorCode.External, Unwrap(error).Message); }
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
