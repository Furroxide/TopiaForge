using System.Globalization;
using System.Threading;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class UnityEntityRegistry
    {
        internal sealed class UnityEntity : IEntity
        {
            private readonly GameObject gameObject;

            public UnityEntity(
                UnityEntityRegistry owner,
                int instanceId,
                GameObject gameObject,
                Rigidbody? body,
                string? runtimeEntityId = null)
            {
                Owner = owner;
                InstanceId = instanceId;
                this.gameObject = gameObject;
                Body = body;
                Id = runtimeEntityId ?? instanceId.ToString(CultureInfo.InvariantCulture);
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
}
