using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class UnityEntityRegistry : IDisposable
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

            return GetOrCreate(collider, body, gameObject);
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
            string? runtimeEntityId = null;
            if (TryFindRuntimeIdentityAnchor(collider, body, fallback, out var anchoredRoot, out var anchoredId))
            {
                resolved = anchoredRoot;
                body = anchoredRoot.GetComponent<Rigidbody>();
                runtimeEntityId = anchoredId;
            }

            // Anchored entities are keyed by their canonical root rather than a Rigidbody that can change when a
            // robot enters/leaves ragdoll. Unanchored entities retain the existing body-first identity behavior.
            var id = runtimeEntityId != null
                ? resolved.GetInstanceID()
                : body != null ? body.GetInstanceID() : resolved.GetInstanceID();
            lock (sync)
            {
                ThrowIfDisposed();
                if (!entities.TryGetValue(id, out var entity) || !entity.IsAlive)
                {
                    entity = new UnityEntity(this, id, resolved, body, runtimeEntityId);
                    entities[id] = entity;
                }

                return entity;
            }
        }

        private static bool TryFindRuntimeIdentityAnchor(
            Collider? collider,
            Rigidbody? body,
            GameObject fallback,
            out GameObject anchoredRoot,
            out string runtimeEntityId)
        {
            if (TryFindRuntimeIdentityAnchor(collider != null ? collider.transform : null, out anchoredRoot, out runtimeEntityId)
                || TryFindRuntimeIdentityAnchor(body != null ? body.transform : null, out anchoredRoot, out runtimeEntityId)
                || TryFindRuntimeIdentityAnchor(fallback.transform, out anchoredRoot, out runtimeEntityId))
            {
                return true;
            }

            anchoredRoot = null!;
            runtimeEntityId = string.Empty;
            return false;
        }

        private static bool TryFindRuntimeIdentityAnchor(
            Transform? candidate,
            out GameObject anchoredRoot,
            out string runtimeEntityId)
        {
            for (var current = candidate; current != null; current = current.parent)
            {
                var component = current.gameObject.GetComponent(typeof(IRuntimeEntityIdentityAnchor));
                if (component is IRuntimeEntityIdentityAnchor anchor
                    && !string.IsNullOrWhiteSpace(anchor.RuntimeEntityId))
                {
                    anchoredRoot = current.gameObject;
                    runtimeEntityId = anchor.RuntimeEntityId;
                    return true;
                }
            }

            anchoredRoot = null!;
            runtimeEntityId = string.Empty;
            return false;
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
            var foundIds = new HashSet<string>(StringComparer.Ordinal);
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

                var entity = GetOrCreate(candidate);
                if (!foundIds.Add(entity.Id))
                {
                    continue;
                }

                found.Add(entity);
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
    }
}
