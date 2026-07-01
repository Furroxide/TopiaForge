using UnityEngine;

namespace Robotopia.GravityGun
{
    internal sealed class HeldTransform : IGravityGunTarget
    {
        private readonly Transform transform;
        private Vector3 velocity;

        public HeldTransform(Transform transform)
        {
            this.transform = transform;
        }

        public string Name => IsAlive ? transform.name : "Missing transform";

        public Vector3 Position => IsAlive ? transform.position : Vector3.zero;

        public bool IsAlive => transform != null && transform.gameObject != null;

        public void UpdateHold(Camera camera, float holdDistance, GravityGunConfig config, float deltaTime)
        {
            if (!IsAlive)
            {
                return;
            }

            var targetPosition = camera.transform.position + camera.transform.forward * holdDistance;
            var frameTime = Mathf.Clamp(deltaTime > 0f ? deltaTime : Time.deltaTime, 0.001f, 0.05f);
            var response = Mathf.Clamp(config.PullStrength * 0.08f, 2f, 30f);
            var smoothTime = Mathf.Clamp(1f / response, 0.04f, 0.35f);
            transform.position = Vector3.SmoothDamp(
                transform.position,
                targetPosition,
                ref velocity,
                smoothTime,
                config.MaxVelocity,
                frameTime);
        }

        public void Throw(Vector3 direction, GravityGunConfig config)
        {
            if (!IsAlive)
            {
                return;
            }

            transform.position += direction.normalized * Mathf.Min(config.ThrowVelocity * 0.12f, 4f);
            velocity = Vector3.zero;
        }

        public void Release()
        {
            velocity = Vector3.zero;
        }
    }
}
