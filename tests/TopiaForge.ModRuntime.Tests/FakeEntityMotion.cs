using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>The engine lease under the real network decorator; deterministic physics for loopback tests.</summary>
    internal sealed class FakeEntityMotion : IEntityMotion
    {
        private readonly FakeWorldObject body;
        private readonly bool kinematic;
        private bool ended;

        private FakeEntityMotion(FakeWorldObject body)
        {
            this.body = body;
            kinematic = body.Kinematic;
            body.Kinematic = false;
        }

        internal static OperationResult<IEntityMotion> Acquire(IEntity entity)
        {
            var body = entity is FakeWorldEntity world ? world.Object : (entity as FakeSpawnedEntity)?.Object;
            return body?.HasRigidbody == true && body.IsAlive
                ? OperationResult<IEntityMotion>.Success(new FakeEntityMotion(body))
                : OperationResult<IEntityMotion>.Failure(ModErrorCode.Unavailable, "The object has no body.");
        }

        public IEntity Entity => body.Entity;
        public bool IsAlive => !ended && body.IsAlive;
        public OperationResult<Vec3> MoveToward(Vec3 target, float responsiveness, float damping, float maximumSpeed, float deltaTime)
        {
            var delta = new NetVector3(target.X - body.Position.X, target.Y - body.Position.Y, target.Z - body.Position.Z);
            var length = Math.Sqrt(delta.LengthSquared);
            var scale = length > 0 ? Math.Min(length, maximumSpeed * deltaTime) / length : 0;
            var step = new NetVector3((float)(delta.X * scale), (float)(delta.Y * scale), (float)(delta.Z * scale));
            body.Position = new NetVector3(body.Position.X + step.X, body.Position.Y + step.Y, body.Position.Z + step.Z);
            body.Velocity = new NetVector3(step.X / deltaTime, step.Y / deltaTime, step.Z / deltaTime);
            return OperationResult<Vec3>.Success(target);
        }
        public OperationResult<Vec3> Throw(Vec3 direction, float speed)
        {
            ended = true;
            body.Velocity = new NetVector3(direction.X * speed, direction.Y * speed, direction.Z * speed);
            return OperationResult<Vec3>.Success(new Vec3(body.Velocity.Value.X, body.Velocity.Value.Y, body.Velocity.Value.Z));
        }
        public void Dispose()
        {
            if (ended) return;
            ended = true;
            body.Kinematic = kinematic;
        }
    }
}
