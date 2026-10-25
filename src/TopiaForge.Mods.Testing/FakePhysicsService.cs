using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic physics service that returns an explicitly configured next hit.</summary>
    public sealed class FakePhysicsService : IPhysicsService
    {
        private readonly List<FakeBlocker> blockers = new List<FakeBlocker>();
        private readonly IModLifetime? lifetime;
        private readonly FakeEntityService? entities;
        private int nextBlocker;

        /// <summary>Creates independently disposable test blockers and configurable physics queries.</summary>
        public FakePhysicsService() { }

        internal FakePhysicsService(IModLifetime lifetime, FakeEntityService entities)
        { this.lifetime = lifetime; this.entities = entities; }

        /// <summary>Gets the recorded blockers, including disposed handles, for assertions.</summary>
        [RequiresTopiaForge("0.2.0")]
        public IReadOnlyList<FakeBlocker> Blockers => blockers.AsReadOnly();

        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public OperationResult<IBlocker> AddBlocker(BlockerRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (lifetime?.IsStopping == true) return OperationResult<IBlocker>.Failure(ModErrorCode.Cancelled,
                "The part of the mod that asked for this is stopping. Place the blocker after a new scope starts.");
            var count = 0;
            foreach (var existing in blockers) if (existing.IsAlive) count++;
            if (count >= 1024) return OperationResult<IBlocker>.Failure(ModErrorCode.RateLimited,
                "A mod can keep at most 1,024 blockers. Dispose of an existing blocker before adding another.");
            var entity = entities?.Create("Blocker", request.Placement.Position) ??
                new FakeEntity("blocker-" + (++nextBlocker), "Blocker", request.Placement.Position);
            var blocker = new FakeBlocker(entity, request);
            if (lifetime != null) blocker.Track(lifetime.Track(blocker));
            blockers.Add(blocker);
            return OperationResult<IBlocker>.Success(blocker);
        }

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

            foreach (var blocker in blockers)
            {
                if (found.Count >= maximumResults) break;
                if (blocker.IsAlive && blocker.BlocksSight && blocker.Overlaps(bounds) && !found.Contains(blocker)) found.Add(blocker);
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
