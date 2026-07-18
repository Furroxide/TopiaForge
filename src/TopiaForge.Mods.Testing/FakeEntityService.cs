using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Mutable engine-independent entity for world-service tests.</summary>
    public sealed class FakeEntity : IEntity
    {
        /// <summary>Creates a live fake entity.</summary>
        public FakeEntity(string id, string name, Vec3 position)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("An entity id is required.", nameof(id));
            }

            Id = id;
            Name = name ?? string.Empty;
            Position = position;
            IsAlive = true;
        }

        /// <inheritdoc/>
        public string Id { get; }

        /// <inheritdoc/>
        public string Name { get; set; }

        /// <inheritdoc/>
        public bool IsAlive { get; private set; }

        /// <inheritdoc/>
        public Vec3 Position { get; set; }

        /// <summary>Gets or sets deterministic world rotation.</summary>
        public Quat Rotation { get; set; } = Quat.Identity;

        /// <summary>Gets or sets deterministic local scale.</summary>
        public Vec3 Scale { get; set; } = new Vec3(1f, 1f, 1f);

        /// <summary>Marks the entity unavailable for future SDK operations.</summary>
        public void Destroy()
        {
            IsAlive = false;
        }
    }

    /// <summary>Deterministic entity registry and motion-control factory.</summary>
    public sealed class FakeEntityService : IEntityService
    {
        private readonly FakeModLifetime lifetime;
        private readonly Dictionary<string, FakeEntity> entities =
            new Dictionary<string, FakeEntity>(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeEntityMotion> motions =
            new Dictionary<string, FakeEntityMotion>(StringComparer.Ordinal);
        private int nextId;

        /// <summary>Creates a fake entity service.</summary>
        public FakeEntityService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        internal FakeModLifetime Lifetime => lifetime;

        /// <summary>Gets or sets a stable error used to reject every motion acquisition.</summary>
        public ModErrorCode AcquireMotionErrorCode { get; set; }

        /// <summary>Gets or sets the diagnostic paired with <see cref="AcquireMotionErrorCode"/>.</summary>
        public string AcquireMotionErrorMessage { get; set; } = "Entity motion is unavailable in this test.";

        /// <summary>Gets the number of live registered entities.</summary>
        public int LiveEntityCount
        {
            get
            {
                var count = 0;
                foreach (var entity in entities.Values)
                {
                    if (entity.IsAlive)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Gets the number of active exclusive motion leases.</summary>
        public int ActiveMotionCount => motions.Count;

        /// <summary>Creates and registers a live fake entity.</summary>
        public FakeEntity Create(string name, Vec3 position)
        {
            var entity = new FakeEntity("entity-" + (++nextId), name, position);
            Add(entity);
            return entity;
        }

        /// <summary>Adds an externally constructed entity to the registry.</summary>
        public void Add(FakeEntity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (entities.ContainsKey(entity.Id))
            {
                throw new InvalidOperationException("An entity is already registered as '" + entity.Id + "'.");
            }

            entities.Add(entity.Id, entity);
        }

        /// <summary>Attempts to find a registered entity by its opaque identifier.</summary>
        public bool TryGet(string id, out FakeEntity? entity) => entities.TryGetValue(id, out entity);

        /// <inheritdoc/>
        public OperationResult<IEntityMotion> AcquireMotion(IEntity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (AcquireMotionErrorCode != ModErrorCode.None)
            {
                return OperationResult<IEntityMotion>.Failure(
                    AcquireMotionErrorCode,
                    AcquireMotionErrorMessage);
            }

            if (!entity.IsAlive)
            {
                return OperationResult<IEntityMotion>.Failure(ModErrorCode.InvalidState, "The entity is no longer alive.");
            }

            if (!(entity is FakeEntity fake) || !entities.ContainsKey(fake.Id))
            {
                return OperationResult<IEntityMotion>.Failure(ModErrorCode.NotFound, "The entity is not registered in this fake context.");
            }

            if (motions.ContainsKey(fake.Id))
            {
                return OperationResult<IEntityMotion>.Failure(ModErrorCode.Conflict, "The entity already has a motion owner.");
            }

            var motion = new FakeEntityMotion(fake, value => motions.Remove(value.Entity.Id));
            motions.Add(fake.Id, motion);
            return lifetime.TrackResult<IEntityMotion>(
                motion,
                "The fake mod stopped before entity motion could be acquired.");
        }

        /// <inheritdoc/>
        public bool TryGetTransform(IEntity entity, out TransformState transform)
        {
            if (entity is FakeEntity fake && entities.ContainsKey(fake.Id) && fake.IsAlive)
            {
                transform = new TransformState(fake.Position, fake.Rotation, fake.Scale);
                return true;
            }

            transform = TransformState.Identity;
            return false;
        }

        /// <inheritdoc/>
        public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform)
        {
            if (!(entity is FakeEntity fake) || !entities.ContainsKey(fake.Id))
            {
                return OperationResult<TransformState>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The entity is not registered in this fake context.");
            }

            if (!fake.IsAlive)
            {
                return OperationResult<TransformState>.Failure(ModErrorCode.NotFound, "The entity is no longer alive.");
            }

            fake.Position = transform.Position;
            fake.Rotation = transform.Rotation;
            fake.Scale = transform.Scale;
            return OperationResult<TransformState>.Success(transform);
        }

        /// <inheritdoc/>
        public IReadOnlyList<IEntity> Query(EntityQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var found = new List<IEntity>();
            var radiusSquared = query.Radius * query.Radius;
            foreach (var entity in entities.Values)
            {
                if (!entity.IsAlive
                    || (query.NameContains.Length > 0
                        && entity.Name.IndexOf(query.NameContains, StringComparison.OrdinalIgnoreCase) < 0)
                    || (query.Center.HasValue && query.Radius > 0f
                        && (entity.Position - query.Center.Value).LengthSquared > radiusSquared))
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

        /// <inheritdoc/>
        public OperationResult<bool> Destroy(IEntity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (entity is FakeEntity fake && entities.ContainsKey(fake.Id))
            {
                if (motions.TryGetValue(fake.Id, out var motion))
                {
                    motion.Dispose();
                }

                fake.Destroy();
                return OperationResult<bool>.Success(true);
            }

            if (entity is ISpawnedEntity spawned)
            {
                spawned.Dispose();
                return OperationResult<bool>.Success(true);
            }

            return OperationResult<bool>.Failure(
                ModErrorCode.InvalidArgument,
                "Only entities owned by this fake context can be destroyed.");
        }
    }

    /// <summary>Inspectable motion lease that applies deterministic position and throw changes.</summary>
    public sealed class FakeEntityMotion : IEntityMotion
    {
        private readonly FakeEntity entity;
        private Action<FakeEntityMotion>? release;

        internal FakeEntityMotion(FakeEntity entity, Action<FakeEntityMotion> release)
        {
            this.entity = entity;
            this.release = release;
        }

        /// <inheritdoc/>
        public IEntity Entity => entity;

        /// <inheritdoc/>
        public bool IsAlive => release != null && entity.IsAlive;

        /// <summary>Gets the number of move requests received.</summary>
        public int MoveCallCount { get; private set; }

        /// <summary>Gets the most recent requested target.</summary>
        public Vec3 LastMoveTarget { get; private set; }

        /// <summary>Gets the velocity applied by the most recent throw.</summary>
        public Vec3 LastThrowVelocity { get; private set; }

        /// <inheritdoc/>
        public OperationResult<Vec3> MoveToward(
            Vec3 target,
            float responsiveness,
            float damping,
            float maximumSpeed,
            float deltaTime)
        {
            if (!IsAlive)
            {
                return OperationResult<Vec3>.Failure(ModErrorCode.InvalidState, "The motion lease is no longer active.");
            }

            if (!target.IsFinite || responsiveness < 0f || damping < 0f || maximumSpeed < 0f || deltaTime < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(target), "Motion values must be finite and non-negative.");
            }

            MoveCallCount++;
            LastMoveTarget = target;
            var displacement = target - entity.Position;
            var velocity = Vec3.ClampLength(displacement * responsiveness, maximumSpeed);
            var blend = Math.Min(1f, damping * deltaTime);
            entity.Position += velocity * (deltaTime * blend);
            return OperationResult<Vec3>.Success(entity.Position);
        }

        /// <inheritdoc/>
        public OperationResult<Vec3> Throw(Vec3 direction, float speed)
        {
            if (!IsAlive)
            {
                return OperationResult<Vec3>.Failure(ModErrorCode.InvalidState, "The motion lease is no longer active.");
            }

            if (!direction.IsFinite || direction.LengthSquared <= 0.000000000001f || speed < 0f || float.IsNaN(speed) || float.IsInfinity(speed))
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }

            LastThrowVelocity = direction.Normalized * speed;
            Dispose();
            return OperationResult<Vec3>.Success(LastThrowVelocity);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            var callback = release;
            release = null;
            callback?.Invoke(this);
        }
    }
}
