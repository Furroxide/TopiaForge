using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed class UnityPhysicsBackend : IPhysicsService
    {
        private readonly UnityEntityRegistry entities;

        public UnityPhysicsBackend(UnityEntityRegistry entities)
        {
            this.entities = entities;
        }

        public bool TryRaycast(TopiaForge.Mods.Ray ray, float maximumDistance, out PhysicsHit? hit)
        {
            UnityMainThreadGuard.AssertCurrent();
            hit = null;
            if (maximumDistance <= 0f || float.IsNaN(maximumDistance) || float.IsInfinity(maximumDistance))
            {
                return false;
            }

            var unityRay = new UnityEngine.Ray(ToUnity(ray.Origin), ToUnity(ray.Direction));
            if (!UnityEngine.Physics.Raycast(
                    unityRay,
                    out var unityHit,
                    maximumDistance,
                    UnityEngine.Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            var entity = entities.GetOrCreate(unityHit.collider, unityHit.rigidbody);
            hit = new PhysicsHit(
                entity,
                FromUnity(unityHit.point),
                FromUnity(unityHit.normal),
                unityHit.distance);
            return true;
        }

        public bool TrySphereCast(
            TopiaForge.Mods.Ray ray,
            float radius,
            float maximumDistance,
            out PhysicsHit? hit)
        {
            UnityMainThreadGuard.AssertCurrent();
            hit = null;
            if (radius <= 0f || float.IsNaN(radius) || float.IsInfinity(radius)
                || maximumDistance <= 0f || float.IsNaN(maximumDistance) || float.IsInfinity(maximumDistance))
            {
                return false;
            }

            if (!UnityEngine.Physics.SphereCast(
                    ToUnity(ray.Origin),
                    radius,
                    ToUnity(ray.Direction),
                    out var unityHit,
                    maximumDistance,
                    UnityEngine.Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            hit = new PhysicsHit(
                entities.GetOrCreate(unityHit.collider, unityHit.rigidbody),
                FromUnity(unityHit.point),
                FromUnity(unityHit.normal),
                unityHit.distance);
            return true;
        }

        public IReadOnlyList<IEntity> Overlap(TopiaForge.Mods.Bounds bounds, int maximumResults = 64)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (maximumResults < 1 || maximumResults > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumResults));
            }

            var colliders = UnityEngine.Physics.OverlapBox(
                ToUnity(bounds.Center),
                ToUnity(bounds.Extents),
                Quaternion.identity,
                UnityEngine.Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            var found = new List<IEntity>(Math.Min(maximumResults, colliders.Length));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < colliders.Length && found.Count < maximumResults; index++)
            {
                var collider = colliders[index];
                if (collider == null)
                {
                    continue;
                }

                var entity = entities.GetOrCreate(collider, collider.attachedRigidbody);
                if (entity.IsAlive && ids.Add(entity.Id))
                {
                    found.Add(entity);
                }
            }

            found.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
            return found.AsReadOnly();
        }

        internal static Vec3 FromUnity(Vector3 value)
        {
            return new Vec3(value.x, value.y, value.z);
        }

        internal static Vector3 ToUnity(Vec3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }
    }

    internal sealed class OwnerEntityService : IEntityService
    {
        private readonly IModLifetime lifetime;
        private readonly UnityEntityRegistry registry;

        public OwnerEntityService(IModLifetime lifetime, UnityEntityRegistry registry)
        {
            this.lifetime = lifetime;
            this.registry = registry;
        }

        public OperationResult<IEntityMotion> AcquireMotion(IEntity entity)
        {
            UnityMainThreadGuard.AssertCurrent();
            var result = registry.AcquireMotion(entity);
            if (result.TryGetValue(out var motion))
            {
                lifetime.Track(motion);
            }

            return result;
        }

        public bool TryGetTransform(IEntity entity, out TransformState transform)
        {
            UnityMainThreadGuard.AssertCurrent();
            return registry.TryGetTransform(entity, out transform);
        }

        public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform)
        {
            UnityMainThreadGuard.AssertCurrent();
            return registry.SetTransform(entity, transform);
        }

        public IReadOnlyList<IEntity> Query(EntityQuery query)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (query == null) throw new ArgumentNullException(nameof(query));
            return registry.Query(query);
        }

        public OperationResult<bool> Destroy(IEntity entity)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (!(entity is IUnityOwnedEntity owned))
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Only entities spawned by this mod can be destroyed through the safe SDK.");
            }

            owned.Dispose();
            return OperationResult<bool>.Success(true);
        }
    }

    internal interface IUnityOwnedEntity : IEntity, IDisposable
    {
        IEntity InnerEntity { get; }
    }
}
