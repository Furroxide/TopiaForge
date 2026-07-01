using UnityEngine;

namespace Robotopia.GravityGun
{
    internal sealed class HeldRigidbody : IGravityGunTarget
    {
        private readonly Rigidbody body;
        private readonly bool originalIsKinematic;
        private readonly bool originalUseGravity;
        private readonly float originalLinearDamping;
        private readonly float originalAngularDamping;
        private readonly RigidbodyInterpolation originalInterpolation;
        private readonly CollisionDetectionMode originalCollisionDetectionMode;
        private bool restored;

        private HeldRigidbody(Rigidbody body)
        {
            this.body = body;
            originalIsKinematic = body.isKinematic;
            originalUseGravity = body.useGravity;
            originalLinearDamping = body.linearDamping;
            originalAngularDamping = body.angularDamping;
            originalInterpolation = body.interpolation;
            originalCollisionDetectionMode = body.collisionDetectionMode;
        }

        public Rigidbody Body => body;

        public string Name => IsAlive ? body.name : "Missing Rigidbody";

        public Vector3 Position => IsAlive ? body.worldCenterOfMass : Vector3.zero;

        public bool IsAlive => body != null && body.gameObject != null;

        public static HeldRigidbody Capture(Rigidbody body, GravityGunConfig config)
        {
            var held = new HeldRigidbody(body);
            body.isKinematic = false;
            body.useGravity = false;
            body.linearDamping = Mathf.Max(body.linearDamping, Mathf.Max(1f, config.Damping * 0.2f));
            body.angularDamping = Mathf.Max(body.angularDamping, Mathf.Max(4f, config.Damping * 0.5f));
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            return held;
        }

        public void UpdateHold(Camera camera, float holdDistance, GravityGunConfig config, float deltaTime)
        {
            var targetPosition = camera.transform.position + camera.transform.forward * holdDistance;
            UpdateHoldAt(targetPosition, config, deltaTime);
        }

        public void UpdateHoldAt(Vector3 targetPosition, GravityGunConfig config, float deltaTime)
        {
            if (!IsAlive)
            {
                return;
            }

            if (body.isKinematic)
            {
                body.isKinematic = false;
            }

            var toTarget = targetPosition - body.worldCenterOfMass;
            var response = Mathf.Clamp(config.PullStrength * 0.08f, 2f, 30f);
            var desiredVelocity = Vector3.ClampMagnitude(toTarget * response, config.MaxVelocity);
            var frameTime = Mathf.Clamp(deltaTime > 0f ? deltaTime : Time.deltaTime, 0.001f, 0.05f);
            var velocityBlend = 1f - Mathf.Exp(-Mathf.Max(0.1f, config.Damping) * frameTime);

            body.linearVelocity = Vector3.ClampMagnitude(
                Vector3.Lerp(body.linearVelocity, desiredVelocity, velocityBlend),
                config.MaxVelocity);
            body.angularVelocity = Vector3.Lerp(
                body.angularVelocity,
                Vector3.zero,
                Mathf.Clamp01(Mathf.Max(0f, config.Damping) * frameTime));
        }

        public void Throw(Vector3 direction, GravityGunConfig config)
        {
            if (!IsAlive)
            {
                return;
            }

            Restore();
            body.linearVelocity = direction.normalized * config.ThrowVelocity;
        }

        public void Release()
        {
            Restore();
        }

        private void Restore()
        {
            if (restored || !IsAlive)
            {
                return;
            }

            body.isKinematic = originalIsKinematic;
            body.useGravity = originalUseGravity;
            body.linearDamping = originalLinearDamping;
            body.angularDamping = originalAngularDamping;
            body.interpolation = originalInterpolation;
            body.collisionDetectionMode = originalCollisionDetectionMode;
            restored = true;
        }
    }
}
