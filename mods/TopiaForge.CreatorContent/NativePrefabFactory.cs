using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;
using UnityEngine;

namespace TopiaForge.CreatorContent
{
    /// <summary>
    /// Narrow native adapter for a prefab reached through a versioned Robotopia registry. It never searches
    /// Resources or clones live scene instances: the owning catalog must supply the exact registry prefab.
    /// </summary>
    internal sealed class NativePrefabFactory : ICreatorContentFactory
    {
        private readonly GameObject prefab;
        private readonly IUnityInteropService interop;

        public NativePrefabFactory(GameObject prefab, IUnityInteropService interop)
        {
            this.prefab = prefab != null ? prefab : throw new ArgumentNullException(nameof(prefab));
            this.interop = interop ?? throw new ArgumentNullException(nameof(interop));
        }

        public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
        {
            GameObject? instance = null;
            try
            {
                instance = UnityEngine.Object.Instantiate(prefab);
                instance.name = prefab.name;
                Apply(instance.transform, transform);

                var wrapped = interop.Wrap(instance);
                if (!wrapped.TryGetValue(out var entity))
                {
                    UnityEngine.Object.Destroy(instance);
                    return OperationResult<ICreatorSourceInstance>.Failure(wrapped.ErrorCode, wrapped.ErrorMessage);
                }

                return OperationResult<ICreatorSourceInstance>.Success(new NativePrefabInstance(instance, entity));
            }
            catch (Exception exception)
            {
                if (instance != null)
                {
                    UnityEngine.Object.Destroy(instance);
                }

                return OperationResult<ICreatorSourceInstance>.Failure(
                    ModErrorCode.External,
                    "Robotopia could not instantiate the curated prefab: " + exception.Message);
            }
        }

        private static void Apply(Transform transform, TransformState state)
        {
            transform.SetPositionAndRotation(
                new Vector3(state.Position.X, state.Position.Y, state.Position.Z),
                new Quaternion(state.Rotation.X, state.Rotation.Y, state.Rotation.Z, state.Rotation.W));
            transform.localScale = new Vector3(state.Scale.X, state.Scale.Y, state.Scale.Z);
        }

        private sealed class NativePrefabInstance : ICreatorSourceInstance
        {
            private GameObject? instance;
            private IEntity? entity;

            public NativePrefabInstance(GameObject instance, IEntity entity)
            {
                this.instance = instance;
                this.entity = entity;
            }

            public IEntity Entity => entity ?? throw new ObjectDisposedException(nameof(NativePrefabInstance));

            public bool IsAlive
            {
                get
                {
                    try
                    {
                        return instance != null && entity?.IsAlive == true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            public bool TryGetTransform(out TransformState transform)
            {
                var current = instance;
                if (current == null)
                {
                    transform = TransformState.Identity;
                    return false;
                }

                try
                {
                    var native = current.transform;
                    var position = native.position;
                    var rotation = native.rotation;
                    var scale = native.localScale;
                    transform = new TransformState(
                        new Vec3(position.x, position.y, position.z),
                        new Quat(rotation.x, rotation.y, rotation.z, rotation.w),
                        new Vec3(scale.x, scale.y, scale.z));
                    return true;
                }
                catch
                {
                    transform = TransformState.Identity;
                    return false;
                }
            }

            public OperationResult<TransformState> SetTransform(TransformState transform)
            {
                var current = instance;
                if (current == null)
                {
                    return OperationResult<TransformState>.Failure(
                        ModErrorCode.InvalidState,
                        "The curated native instance is no longer alive.");
                }

                try
                {
                    Apply(current.transform, transform);
                    return OperationResult<TransformState>.Success(transform);
                }
                catch (Exception exception)
                {
                    return OperationResult<TransformState>.Failure(ModErrorCode.External, exception.Message);
                }
            }

            public void Dispose()
            {
                var current = instance;
                instance = null;
                entity = null;
                if (current != null)
                {
                    UnityEngine.Object.Destroy(current);
                }
            }
        }
    }
}
