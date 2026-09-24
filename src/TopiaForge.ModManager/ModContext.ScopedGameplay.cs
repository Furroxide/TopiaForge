using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext
    {
        private sealed class LifetimePlayerService : ILocalPlayerService
        {
            private readonly ILocalPlayerService inner;
            private readonly IModLifetime lifetime;
            internal LifetimePlayerService(ILocalPlayerService inner, IModLifetime lifetime)
            { this.inner = inner; this.lifetime = lifetime; }
            public bool TryGetSnapshot(out PlayerSnapshot? snapshot) => inner.TryGetSnapshot(out snapshot);
            public bool TryGetHealth(out PlayerHealthSnapshot? health) => inner.TryGetHealth(out health);
            public OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request) => lifetime.IsStopping
                ? Stopped<PlayerHealthSnapshot>() : inner.Damage(request);
            public OperationResult<PlayerHealthSnapshot> Heal(float amount, string source) => lifetime.IsStopping
                ? Stopped<PlayerHealthSnapshot>() : inner.Heal(amount, source);
            public OperationResult<IPlayerControlLease> AcquireControl(string reason) => lifetime.IsStopping
                ? Stopped<IPlayerControlLease>() : inner.AcquireControl(reason);
        }
        private sealed class LifetimeEntityService : IEntityService
        {
            private readonly IEntityService inner;
            private readonly IModLifetime lifetime;
            internal LifetimeEntityService(IEntityService inner, IModLifetime lifetime)
            { this.inner = inner; this.lifetime = lifetime; }
            public bool TryGetTransform(IEntity entity, out TransformState transform) => inner.TryGetTransform(entity, out transform);
            public IReadOnlyList<IEntity> Query(EntityQuery query) => inner.Query(query);
            public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform) => lifetime.IsStopping
                ? Stopped<TransformState>() : inner.SetTransform(entity, transform);
            public OperationResult<bool> Destroy(IEntity entity) => lifetime.IsStopping ? Stopped<bool>() : inner.Destroy(entity);
            public OperationResult<IEntityMotion> AcquireMotion(IEntity entity)
            {
                if (lifetime.IsStopping) return Stopped<IEntityMotion>();
                var result = inner.AcquireMotion(entity);
                return result.TryGetValue(out var motion)
                    ? OperationResult<IEntityMotion>.Success(new LifetimeMotion(motion, lifetime)) : result;
            }
        }
        private sealed class LifetimeMotion : IEntityMotion
        {
            private readonly IEntityMotion inner;
            private readonly IModLifetime lifetime;
            internal LifetimeMotion(IEntityMotion inner, IModLifetime lifetime) { this.inner = inner; this.lifetime = lifetime; }
            public IEntity Entity => inner.Entity;
            public bool IsAlive => !lifetime.IsStopping && inner.IsAlive;
            public OperationResult<Vec3> MoveToward(Vec3 target, float responsiveness, float damping, float maximumSpeed, float deltaTime) =>
                lifetime.IsStopping ? Stopped<Vec3>() : inner.MoveToward(target, responsiveness, damping, maximumSpeed, deltaTime);
            public OperationResult<Vec3> Throw(Vec3 direction, float speed) => lifetime.IsStopping
                ? Stopped<Vec3>() : inner.Throw(direction, speed);
            public void Dispose()
            {
                if (lifetime is ScopedModLifetime scoped) scoped.DisposeResource(inner);
                else inner.Dispose();
            }
        }
        private static OperationResult<T> Stopped<T>() where T : notnull => OperationResult<T>.Failure(ModErrorCode.Cancelled, "The context is stopping.");
    }
}
