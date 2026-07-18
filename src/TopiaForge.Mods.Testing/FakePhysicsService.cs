using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic physics service that returns an explicitly configured next hit.</summary>
    public sealed class FakePhysicsService : IPhysicsService
    {
        /// <summary>Gets or sets the hit returned by subsequent raycasts; <see langword="null"/> means no hit.</summary>
        public PhysicsHit? RaycastHit { get; set; }

        /// <summary>Gets or sets the hit returned by subsequent sphere casts.</summary>
        public PhysicsHit? SphereCastHit { get; set; }

        /// <summary>Gets mutable candidates returned by bounded overlap queries.</summary>
        public IList<IEntity> OverlapEntities { get; } = new List<IEntity>();

        /// <summary>Gets the number of raycast queries received.</summary>
        public int RaycastCount { get; private set; }

        /// <summary>Gets the number of sphere-cast queries received.</summary>
        public int SphereCastCount { get; private set; }

        /// <summary>Gets the number of overlap queries received.</summary>
        public int OverlapCount { get; private set; }

        /// <summary>Gets the most recent ray.</summary>
        public Ray LastRay { get; private set; }

        /// <summary>Gets the most recent maximum distance.</summary>
        public float LastMaximumDistance { get; private set; }

        /// <summary>Gets the most recent sphere radius.</summary>
        public float LastSphereRadius { get; private set; }

        /// <inheritdoc/>
        public bool TryRaycast(Ray ray, float maximumDistance, out PhysicsHit? hit)
        {
            if (maximumDistance <= 0f || float.IsNaN(maximumDistance) || float.IsInfinity(maximumDistance))
            {
                throw new ArgumentOutOfRangeException(nameof(maximumDistance));
            }

            RaycastCount++;
            LastRay = ray;
            LastMaximumDistance = maximumDistance;
            hit = RaycastHit;
            if (hit != null && hit.Distance <= maximumDistance && hit.Entity.IsAlive)
            {
                return true;
            }

            hit = null;
            return false;
        }

        /// <inheritdoc/>
        public bool TrySphereCast(
            Ray ray,
            float radius,
            float maximumDistance,
            out PhysicsHit? hit)
        {
            if (radius <= 0f || float.IsNaN(radius) || float.IsInfinity(radius))
            {
                throw new ArgumentOutOfRangeException(nameof(radius));
            }

            if (maximumDistance <= 0f || float.IsNaN(maximumDistance) || float.IsInfinity(maximumDistance))
            {
                throw new ArgumentOutOfRangeException(nameof(maximumDistance));
            }

            SphereCastCount++;
            LastRay = ray;
            LastSphereRadius = radius;
            LastMaximumDistance = maximumDistance;
            hit = SphereCastHit;
            if (hit != null && hit.Distance <= maximumDistance && hit.Entity.IsAlive)
            {
                return true;
            }

            hit = null;
            return false;
        }

        /// <inheritdoc/>
        public IReadOnlyList<IEntity> Overlap(Bounds bounds, int maximumResults = 64)
        {
            if (maximumResults < 1 || maximumResults > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumResults));
            }

            OverlapCount++;
            var found = new List<IEntity>();
            foreach (var entity in OverlapEntities)
            {
                if (entity.IsAlive && bounds.Contains(entity.Position) && !found.Contains(entity))
                {
                    found.Add(entity);
                    if (found.Count >= maximumResults)
                    {
                        break;
                    }
                }
            }

            return found.AsReadOnly();
        }

        /// <summary>Resets the query history without changing the configured hit.</summary>
        public void ResetHistory()
        {
            RaycastCount = 0;
            SphereCastCount = 0;
            OverlapCount = 0;
            LastRay = default;
            LastMaximumDistance = 0f;
            LastSphereRadius = 0f;
        }
    }
}
