using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Robotopia.GravityGun
{
    internal sealed class HeldRigidbodyGroup : IGravityGunTarget
    {
        private readonly List<HeldRigidbody> bodies;
        private readonly List<Vector3> offsets;
        private readonly string name;

        private HeldRigidbodyGroup(List<HeldRigidbody> bodies, List<Vector3> offsets, string name)
        {
            this.bodies = bodies;
            this.offsets = offsets;
            this.name = name;
        }

        public string Name => name;

        public Vector3 Position => GetCenter();

        public bool IsAlive
        {
            get
            {
                for (var i = 0; i < bodies.Count; i++)
                {
                    if (bodies[i].IsAlive)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public static HeldRigidbodyGroup? Capture(IEnumerable<Rigidbody> sourceBodies, GravityGunConfig config, string name)
        {
            var rigidbodies = sourceBodies
                .Where(body => body != null && body.gameObject != null && !body.isKinematic)
                .Distinct()
                .ToList();
            if (rigidbodies.Count == 0)
            {
                return null;
            }

            var center = AverageCenter(rigidbodies);
            var heldBodies = rigidbodies.Select(body => HeldRigidbody.Capture(body, config)).ToList();
            var offsets = rigidbodies.Select(body => body.worldCenterOfMass - center).ToList();
            return new HeldRigidbodyGroup(heldBodies, offsets, name);
        }

        public void UpdateHold(Camera camera, float holdDistance, GravityGunConfig config, float deltaTime)
        {
            var targetCenter = camera.transform.position + camera.transform.forward * holdDistance;
            for (var i = 0; i < bodies.Count; i++)
            {
                if (bodies[i].IsAlive)
                {
                    bodies[i].UpdateHoldAt(targetCenter + offsets[i], config, deltaTime);
                }
            }
        }

        public void Throw(Vector3 direction, GravityGunConfig config)
        {
            foreach (var body in bodies)
            {
                body.Throw(direction, config);
            }
        }

        public void Release()
        {
            foreach (var body in bodies)
            {
                body.Release();
            }
        }

        private Vector3 GetCenter()
        {
            var total = Vector3.zero;
            var count = 0;
            for (var i = 0; i < bodies.Count; i++)
            {
                if (!bodies[i].IsAlive)
                {
                    continue;
                }

                total += bodies[i].Body.worldCenterOfMass;
                count++;
            }

            return count == 0 ? Vector3.zero : total / count;
        }

        private static Vector3 AverageCenter(IEnumerable<Rigidbody> rigidbodies)
        {
            var total = Vector3.zero;
            var count = 0;
            foreach (var body in rigidbodies)
            {
                total += body.worldCenterOfMass;
                count++;
            }

            return count == 0 ? Vector3.zero : total / count;
        }
    }
}
