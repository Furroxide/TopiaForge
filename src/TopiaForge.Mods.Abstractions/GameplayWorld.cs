using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>An opaque, engine-independent handle to a live world entity.</summary>
    public interface IEntity
    {
        /// <summary>
        /// Gets a process-local opaque identifier stable for this entity's current lifetime. It is not a network
        /// identity and must not be compared across processes.
        /// </summary>
        string Id { get; }

        /// <summary>Gets the best available user-facing or diagnostic name.</summary>
        string Name { get; }

        /// <summary>Gets whether the underlying entity still exists.</summary>
        bool IsAlive { get; }

        /// <summary>Gets the current world position.</summary>
        Vec3 Position { get; }
    }

    /// <summary>
    /// Temporarily owns motion of a physics entity. Disposing the handle restores gravity, damping, interpolation,
    /// and collision settings captured when ownership began.
    /// </summary>
    public interface IEntityMotion : IDisposable
    {
        /// <summary>Gets the controlled entity.</summary>
        IEntity Entity { get; }

        /// <summary>Gets whether the entity and motion lease remain usable.</summary>
        bool IsAlive { get; }

        /// <summary>Moves the entity toward a world-space target using bounded velocity control.</summary>
        /// <param name="target">The desired world position.</param>
        /// <param name="responsiveness">How quickly distance becomes desired velocity.</param>
        /// <param name="damping">How quickly current velocity blends toward desired velocity.</param>
        /// <param name="maximumSpeed">The velocity limit.</param>
        /// <param name="deltaTime">The fixed-step duration in seconds.</param>
        /// <returns>The resulting entity position or a stable failure.</returns>
        OperationResult<Vec3> MoveToward(
            Vec3 target,
            float responsiveness,
            float damping,
            float maximumSpeed,
            float deltaTime);

        /// <summary>Releases motion ownership and sets a throw velocity in the supplied direction.</summary>
        /// <param name="direction">The non-zero throw direction.</param>
        /// <param name="speed">The throw speed.</param>
        /// <returns>The velocity applied to the entity or a stable failure.</returns>
        OperationResult<Vec3> Throw(Vec3 direction, float speed);
    }

    /// <summary>Provides safe operations over opaque world-entity handles.</summary>
    public interface IEntityService
    {
        /// <summary>Tries to read a complete transform for an entity created by this runtime.</summary>
        bool TryGetTransform(IEntity entity, out TransformState transform);

        /// <summary>Updates a live entity transform through the game main thread.</summary>
        OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform);

        /// <summary>Queries live scene entities using bounded engine-independent filters.</summary>
        IReadOnlyList<IEntity> Query(EntityQuery query);

        /// <summary>Destroys an entity spawned and owned by the current mod.</summary>
        OperationResult<bool> Destroy(IEntity entity);

        /// <summary>Attempts to acquire lifetime-tracked exclusive motion control of a dynamic physics entity.</summary>
        /// <param name="entity">The entity returned by another SDK service.</param>
        /// <returns>A motion handle, or a stable reason the entity cannot be controlled.</returns>
        OperationResult<IEntityMotion> AcquireMotion(IEntity entity);
    }

    /// <summary>Defines a bounded scene-entity query without engine layers or component types.</summary>
    public sealed class EntityQuery
    {
        /// <summary>Creates an entity query.</summary>
        public EntityQuery(
            Vec3? center = null,
            float radius = 0f,
            string nameContains = "",
            int maximumResults = 64)
        {
            if (center.HasValue && !center.Value.IsFinite)
            {
                throw new ArgumentException("The query center must be finite.", nameof(center));
            }

            if (radius < 0f || float.IsNaN(radius) || float.IsInfinity(radius))
            {
                throw new ArgumentOutOfRangeException(nameof(radius));
            }

            if (!center.HasValue && radius > 0f)
            {
                throw new ArgumentException("A radius requires a query center.", nameof(radius));
            }

            if (maximumResults < 1 || maximumResults > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumResults));
            }

            Center = center;
            Radius = radius;
            NameContains = nameContains ?? string.Empty;
            MaximumResults = maximumResults;
        }

        /// <summary>Gets the optional radius origin.</summary>
        public Vec3? Center { get; }

        /// <summary>Gets the inclusive radius, or zero for no distance filter.</summary>
        public float Radius { get; }

        /// <summary>Gets the optional case-insensitive name fragment.</summary>
        public string NameContains { get; }

        /// <summary>Gets the hard result bound.</summary>
        public int MaximumResults { get; }
    }

    /// <summary>Contains the result of a world physics query.</summary>
    public sealed class PhysicsHit
    {
        /// <summary>Creates a physics hit.</summary>
        public PhysicsHit(IEntity entity, Vec3 point, Vec3 normal, float distance)
        {
            Entity = entity ?? throw new ArgumentNullException(nameof(entity));
            if (distance < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(distance));
            }

            Point = point;
            Normal = normal;
            Distance = distance;
        }

        /// <summary>Gets the opaque entity hit by the query.</summary>
        public IEntity Entity { get; }

        /// <summary>Gets the world-space contact point.</summary>
        public Vec3 Point { get; }

        /// <summary>Gets the world-space surface normal.</summary>
        public Vec3 Normal { get; }

        /// <summary>Gets the distance from the query origin.</summary>
        public float Distance { get; }
    }

    /// <summary>Performs engine-independent world physics queries.</summary>
    public interface IPhysicsService
    {
        /// <summary>Raycasts against ordinary collision layers while ignoring trigger volumes.</summary>
        /// <param name="ray">The normalized world ray.</param>
        /// <param name="maximumDistance">The positive query distance.</param>
        /// <param name="hit">Receives the nearest hit when one exists.</param>
        /// <returns><see langword="true"/> when the ray hit a world entity.</returns>
        bool TryRaycast(Ray ray, float maximumDistance, out PhysicsHit? hit);

        /// <summary>Casts a sphere along a ray and returns the nearest ordinary-collider hit.</summary>
        bool TrySphereCast(
            Ray ray,
            float radius,
            float maximumDistance,
            out PhysicsHit? hit);

        /// <summary>Returns live entities whose ordinary colliders overlap the supplied bounds.</summary>
        IReadOnlyList<IEntity> Overlap(Bounds bounds, int maximumResults = 64);
    }

    /// <summary>Provides the process-local player and view state without exposing game components.</summary>
    public interface ILocalPlayerService
    {
        /// <summary>Attempts to read the process-local player and active gameplay camera.</summary>
        bool TryGetSnapshot(out PlayerSnapshot? snapshot);

        /// <summary>Attempts to read the process-local player health state.</summary>
        bool TryGetHealth(out PlayerHealthSnapshot? health);

        /// <summary>Applies positive damage through the game's supported player-health adapter.</summary>
        OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request);

        /// <summary>Restores positive health through the game's supported player-health adapter.</summary>
        OperationResult<PlayerHealthSnapshot> Heal(float amount, string source);

        /// <summary>
        /// Acquires a lifetime-tracked lease that disables normal player movement and look controls until every
        /// active lease has been released.
        /// </summary>
        /// <param name="reason">A short diagnostic reason for taking control.</param>
        /// <returns>A control lease, or a stable reason controls are unavailable.</returns>
        OperationResult<IPlayerControlLease> AcquireControl(string reason);
    }

    /// <summary>Immutable current and maximum player health.</summary>
    public sealed class PlayerHealthSnapshot
    {
        /// <summary>Creates a health snapshot.</summary>
        public PlayerHealthSnapshot(float current, float maximum)
        {
            if (float.IsNaN(current) || float.IsInfinity(current)
                || maximum <= 0f || float.IsNaN(maximum) || float.IsInfinity(maximum))
            {
                throw new ArgumentOutOfRangeException(nameof(maximum), "Health values must be finite and maximum must be positive.");
            }

            Current = Math.Max(0f, Math.Min(current, maximum));
            Maximum = maximum;
        }

        /// <summary>Gets current health clamped to the inclusive zero-to-maximum range.</summary>
        public float Current { get; }

        /// <summary>Gets maximum health.</summary>
        public float Maximum { get; }

        /// <summary>Gets current health as a zero-to-one fraction.</summary>
        public float Fraction => Current / Maximum;

        /// <summary>Gets whether current health is zero.</summary>
        public bool IsDepleted => Current <= 0f;
    }

    /// <summary>Describes framework-mediated damage to the process-local player.</summary>
    public sealed class PlayerDamageRequest
    {
        /// <summary>Creates a player damage request.</summary>
        public PlayerDamageRequest(float amount, string source)
        {
            if (amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount))
            {
                throw new ArgumentOutOfRangeException(nameof(amount));
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException("A diagnostic damage source is required.", nameof(source));
            }

            Amount = amount;
            Source = source;
        }

        /// <summary>Gets positive damage amount.</summary>
        public float Amount { get; }

        /// <summary>Gets the stable source label attributed by the game.</summary>
        public string Source { get; }
    }

    /// <summary>Immutable process-local player and view state sampled from the current game frame.</summary>
    public sealed class PlayerSnapshot
    {
        /// <summary>Creates a player snapshot.</summary>
        public PlayerSnapshot(Vec3 position, Ray aimRay)
        {
            Position = position;
            AimRay = aimRay;
        }

        /// <summary>Gets the player's current world position.</summary>
        public Vec3 Position { get; }

        /// <summary>Gets the active camera's center-screen aim ray.</summary>
        public Ray AimRay { get; }
    }

    /// <summary>A reversible lease over normal player movement and look controls.</summary>
    public interface IPlayerControlLease : IGameplayLease
    {
        /// <summary>Gets the diagnostic reason supplied when the lease was acquired.</summary>
        string Reason { get; }
    }
}
