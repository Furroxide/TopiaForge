using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
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

    internal sealed class UnityEntityRegistry : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<int, UnityEntity> entities = new Dictionary<int, UnityEntity>();
        private readonly Dictionary<int, UnityEntityMotion> motions = new Dictionary<int, UnityEntityMotion>();
        private bool disposed;

        public IEntity GetOrCreate(Collider? collider, Rigidbody? body)
        {
            UnityMainThreadGuard.AssertCurrent();
            var gameObject = body != null ? body.gameObject : collider != null ? collider.gameObject : null;
            if (gameObject == null)
            {
                throw new InvalidOperationException("A physics hit did not contain a live world entity.");
            }

            var id = body != null ? body.GetInstanceID() : gameObject.GetInstanceID();
            lock (sync)
            {
                ThrowIfDisposed();
                if (!entities.TryGetValue(id, out var entity) || !entity.IsAlive)
                {
                    entity = new UnityEntity(this, id, gameObject, body);
                    entities[id] = entity;
                }

                return entity;
            }
        }

        public IEntity GetOrCreate(GameObject gameObject)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (gameObject == null)
            {
                throw new ArgumentNullException(nameof(gameObject));
            }

            return GetOrCreate(gameObject.GetComponent<Collider>(), gameObject.GetComponent<Rigidbody>(), gameObject);
        }

        private IEntity GetOrCreate(Collider? collider, Rigidbody? body, GameObject fallback)
        {
            var resolved = body != null ? body.gameObject : collider != null ? collider.gameObject : fallback;
            var id = body != null ? body.GetInstanceID() : resolved.GetInstanceID();
            lock (sync)
            {
                ThrowIfDisposed();
                if (!entities.TryGetValue(id, out var entity) || !entity.IsAlive)
                {
                    entity = new UnityEntity(this, id, resolved, body);
                    entities[id] = entity;
                }

                return entity;
            }
        }

        public OperationResult<IEntityMotion> AcquireMotion(IEntity entity)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!(entity is UnityEntity unityEntity) || !ReferenceEquals(unityEntity.Owner, this))
            {
                return OperationResult<IEntityMotion>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The entity was not created by this TopiaForge runtime.");
            }

            var body = unityEntity.Body;
            if (!unityEntity.IsAlive || body == null)
            {
                return OperationResult<IEntityMotion>.Failure(
                    ModErrorCode.NotFound,
                    "The entity is no longer alive or has no physics body.");
            }

            if (body.isKinematic)
            {
                return OperationResult<IEntityMotion>.Failure(
                    ModErrorCode.InvalidState,
                    "The entity has a kinematic physics body and cannot be moved by velocity control.");
            }

            lock (sync)
            {
                ThrowIfDisposed();
                if (motions.ContainsKey(unityEntity.InstanceId))
                {
                    return OperationResult<IEntityMotion>.Failure(
                        ModErrorCode.Conflict,
                        "Another mod already owns motion control of this entity.");
                }

                var motion = new UnityEntityMotion(this, unityEntity, body);
                motions.Add(unityEntity.InstanceId, motion);
                return OperationResult<IEntityMotion>.Success(motion);
            }
        }

        public bool TryGetTransform(IEntity entity, out TransformState transform)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!TryResolve(entity, out var unityEntity) || !unityEntity.IsAlive)
            {
                transform = TransformState.Identity;
                return false;
            }

            var native = unityEntity.GameObject.transform;
            transform = new TransformState(
                UnityPhysicsBackend.FromUnity(native.position),
                FromUnity(native.rotation),
                UnityPhysicsBackend.FromUnity(native.localScale));
            return true;
        }

        public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!TryResolve(entity, out var unityEntity))
            {
                return OperationResult<TransformState>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The entity was not created by this TopiaForge runtime.");
            }

            if (!unityEntity.IsAlive)
            {
                return OperationResult<TransformState>.Failure(ModErrorCode.NotFound, "The entity is no longer alive.");
            }

            try
            {
                var native = unityEntity.GameObject.transform;
                native.SetPositionAndRotation(
                    UnityPhysicsBackend.ToUnity(transform.Position),
                    ToUnity(transform.Rotation));
                native.localScale = UnityPhysicsBackend.ToUnity(transform.Scale);
                return OperationResult<TransformState>.Success(transform);
            }
            catch (MissingReferenceException)
            {
                return OperationResult<TransformState>.Failure(ModErrorCode.NotFound, "The entity was destroyed.");
            }
        }

        public IReadOnlyList<IEntity> Query(EntityQuery query)
        {
            UnityMainThreadGuard.AssertCurrent();
            var candidates = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            Array.Sort(candidates, (left, right) =>
            {
                var name = StringComparer.OrdinalIgnoreCase.Compare(left?.name, right?.name);
                return name != 0 ? name : (left?.GetInstanceID() ?? 0).CompareTo(right?.GetInstanceID() ?? 0);
            });
            var found = new List<IEntity>(Math.Min(query.MaximumResults, candidates.Length));
            var radiusSquared = query.Radius * query.Radius;
            foreach (var candidate in candidates)
            {
                if (candidate == null || !candidate.scene.IsValid())
                {
                    continue;
                }

                if (query.NameContains.Length > 0
                    && candidate.name.IndexOf(query.NameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var position = UnityPhysicsBackend.FromUnity(candidate.transform.position);
                if (query.Center.HasValue && query.Radius > 0f
                    && (position - query.Center.Value).LengthSquared > radiusSquared)
                {
                    continue;
                }

                found.Add(GetOrCreate(candidate));
                if (found.Count >= query.MaximumResults)
                {
                    break;
                }
            }

            return found.AsReadOnly();
        }

        internal bool TryGetGameObject(IEntity entity, out GameObject gameObject)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (TryResolve(entity, out var unityEntity) && unityEntity.IsAlive)
            {
                gameObject = unityEntity.GameObject;
                return true;
            }

            gameObject = null!;
            return false;
        }

        private bool TryResolve(IEntity entity, out UnityEntity unityEntity)
        {
            var candidate = entity;
            if (candidate is IUnityOwnedEntity owned)
            {
                candidate = owned.InnerEntity;
            }

            if (candidate is UnityEntity native && ReferenceEquals(native.Owner, this))
            {
                unityEntity = native;
                return true;
            }

            unityEntity = null!;
            return false;
        }

        private static Quat FromUnity(Quaternion value)
        {
            return new Quat(value.x, value.y, value.z, value.w).Normalized;
        }

        private static Quaternion ToUnity(Quat value)
        {
            var normalized = value.Normalized;
            return new Quaternion(normalized.X, normalized.Y, normalized.Z, normalized.W);
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            UnityEntityMotion[] snapshot;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                snapshot = new UnityEntityMotion[motions.Count];
                motions.Values.CopyTo(snapshot, 0);
                motions.Clear();
                entities.Clear();
            }

            foreach (var motion in snapshot)
            {
                motion.DisposeFromRegistry();
            }
        }

        private void RemoveMotion(int id, UnityEntityMotion motion)
        {
            lock (sync)
            {
                if (motions.TryGetValue(id, out var registered) && ReferenceEquals(registered, motion))
                {
                    motions.Remove(id);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(UnityEntityRegistry));
            }
        }

        internal sealed class UnityEntity : IEntity
        {
            private readonly GameObject gameObject;

            public UnityEntity(UnityEntityRegistry owner, int instanceId, GameObject gameObject, Rigidbody? body)
            {
                Owner = owner;
                InstanceId = instanceId;
                this.gameObject = gameObject;
                Body = body;
                Id = instanceId.ToString(CultureInfo.InvariantCulture);
            }

            public UnityEntityRegistry Owner { get; }
            public int InstanceId { get; }
            public Rigidbody? Body { get; }
            internal GameObject GameObject => gameObject;
            public string Id { get; }
            public string Name
            {
                get
                {
                    UnityMainThreadGuard.AssertCurrent();
                    return IsAlive ? gameObject.name : "Destroyed entity";
                }
            }

            public bool IsAlive
            {
                get
                {
                    UnityMainThreadGuard.AssertCurrent();
                    return gameObject != null;
                }
            }

            public Vec3 Position
            {
                get
                {
                    UnityMainThreadGuard.AssertCurrent();
                    return !IsAlive
                        ? Vec3.Zero
                        : UnityPhysicsBackend.FromUnity(
                            Body != null ? Body.worldCenterOfMass : gameObject.transform.position);
                }
            }
        }

        internal sealed class UnityEntityMotion : IEntityMotion
        {
            private UnityEntityRegistry? owner;
            private readonly UnityEntity entity;
            private readonly Rigidbody body;
            private readonly bool originalUseGravity;
            private readonly float originalLinearDamping;
            private readonly float originalAngularDamping;
            private readonly RigidbodyInterpolation originalInterpolation;
            private readonly CollisionDetectionMode originalCollisionDetection;
            private int released;

            public UnityEntityMotion(UnityEntityRegistry owner, UnityEntity entity, Rigidbody body)
            {
                this.owner = owner;
                this.entity = entity;
                this.body = body;
                originalUseGravity = body.useGravity;
                originalLinearDamping = body.linearDamping;
                originalAngularDamping = body.angularDamping;
                originalInterpolation = body.interpolation;
                originalCollisionDetection = body.collisionDetectionMode;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }

            public IEntity Entity => entity;
            public bool IsAlive => Volatile.Read(ref released) == 0 && entity.IsAlive && body != null;

            public OperationResult<Vec3> MoveToward(
                Vec3 target,
                float responsiveness,
                float damping,
                float maximumSpeed,
                float deltaTime)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (!IsAlive)
                {
                    return OperationResult<Vec3>.Failure(ModErrorCode.NotFound, "The controlled entity is no longer alive.");
                }

                if (!IsFinitePositive(responsiveness) || !IsFiniteNonNegative(damping)
                    || !IsFinitePositive(maximumSpeed) || !IsFinitePositive(deltaTime))
                {
                    return OperationResult<Vec3>.Failure(ModErrorCode.InvalidArgument, "Motion values must be finite and within their documented positive ranges.");
                }

                try
                {
                    var targetPosition = UnityPhysicsBackend.ToUnity(target);
                    var desired = Vector3.ClampMagnitude(
                        (targetPosition - body.worldCenterOfMass) * responsiveness,
                        maximumSpeed);
                    var blend = 1f - Mathf.Exp(-Mathf.Max(0.1f, damping) * Mathf.Clamp(deltaTime, 0.001f, 0.1f));
                    body.linearVelocity = Vector3.ClampMagnitude(
                        Vector3.Lerp(body.linearVelocity, desired, blend),
                        maximumSpeed);
                    body.angularVelocity = Vector3.Lerp(body.angularVelocity, Vector3.zero, Mathf.Clamp01(damping * deltaTime));
                    return OperationResult<Vec3>.Success(entity.Position);
                }
                catch (MissingReferenceException)
                {
                    return OperationResult<Vec3>.Failure(ModErrorCode.NotFound, "The controlled entity was destroyed.");
                }
            }

            public OperationResult<Vec3> Throw(Vec3 direction, float speed)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (!IsAlive)
                {
                    return OperationResult<Vec3>.Failure(ModErrorCode.NotFound, "The controlled entity is no longer alive.");
                }

                if (direction.LengthSquared <= 0.000000000001f || speed <= 0f || float.IsNaN(speed))
                {
                    return OperationResult<Vec3>.Failure(ModErrorCode.InvalidArgument, "Throw direction and speed must be valid and non-zero.");
                }

                var velocity = direction.Normalized * speed;
                Release();
                if (body != null)
                {
                    body.linearVelocity = UnityPhysicsBackend.ToUnity(velocity);
                }

                return OperationResult<Vec3>.Success(velocity);
            }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                Release();
            }

            public void DisposeFromRegistry()
            {
                Release(removeFromOwner: false);
            }

            private void Release(bool removeFromOwner = true)
            {
                if (Interlocked.Exchange(ref released, 1) != 0)
                {
                    return;
                }

                var currentOwner = Interlocked.Exchange(ref owner, null);
                if (removeFromOwner)
                {
                    currentOwner?.RemoveMotion(entity.InstanceId, this);
                }

                if (body == null)
                {
                    return;
                }

                body.useGravity = originalUseGravity;
                body.linearDamping = originalLinearDamping;
                body.angularDamping = originalAngularDamping;
                body.interpolation = originalInterpolation;
                body.collisionDetectionMode = originalCollisionDetection;
            }

            private static bool IsFinitePositive(float value)
            {
                return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
            }

            private static bool IsFiniteNonNegative(float value)
            {
                return value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value);
            }
        }
    }

    internal interface IUnityOwnedEntity : IEntity, IDisposable
    {
        IEntity InnerEntity { get; }
    }
}
