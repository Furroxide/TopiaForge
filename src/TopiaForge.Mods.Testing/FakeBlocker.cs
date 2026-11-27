using System;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Inspectable box blocker created by the fake physics service.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class FakeBlocker : IBlocker
    {
        private readonly FakeEntity entity;
        private IDisposable? tracking;
        internal FakeBlocker(FakeEntity entity, BlockerRequest request)
        {
            this.entity = entity;
            entity.Rotation = request.Placement.Rotation;
            InitialTransform = request.Placement;
            Size = request.Size;
            BlocksSight = request.BlocksSight;
        }

        /// <inheritdoc/>
        public string Id => entity.Id;
        /// <inheritdoc/>
        public string Name => entity.Name;
        /// <inheritdoc/>
        public bool IsAlive => entity.IsAlive;
        /// <inheritdoc/>
        public Vec3 Position => entity.Position;
        /// <inheritdoc/>
        public TransformState InitialTransform { get; }
        /// <inheritdoc/>
        public Vec3 Size { get; }
        /// <summary>Gets whether ordinary physics queries see this blocker.</summary>
        public bool BlocksSight { get; }
        internal FakeEntity Entity => entity;
        internal void Track(IDisposable owned) { tracking = owned; }
        /// <inheritdoc/>
        public void Dispose()
        {
            entity.Destroy();
            var owned = tracking;
            tracking = null;
            owned?.Dispose();
        }

        internal bool Overlaps(Bounds bounds)
        {
            // Separating-axis test of this oriented box against the query's world-aligned box.
            var rotation = entity.Rotation;
            var axes = new[] { rotation * new Vec3(1, 0, 0), rotation * new Vec3(0, 1, 0), rotation * new Vec3(0, 0, 1) };
            var world = new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
            var extents = Size * 0.5f;
            var delta = Position - bounds.Center;
            foreach (var axis in axes) if (Separated(axis)) return false;
            foreach (var axis in world) if (Separated(axis)) return false;
            foreach (var axis in axes)
                foreach (var other in world)
                    if (Separated(Vec3.Cross(axis, other))) return false;
            return true;

            bool Separated(Vec3 axis)
            {
                var radius = Math.Abs(Vec3.Dot(axis, axes[0])) * extents.X +
                    Math.Abs(Vec3.Dot(axis, axes[1])) * extents.Y + Math.Abs(Vec3.Dot(axis, axes[2])) * extents.Z;
                var queryRadius = Math.Abs(axis.X) * bounds.Extents.X + Math.Abs(axis.Y) * bounds.Extents.Y + Math.Abs(axis.Z) * bounds.Extents.Z;
                return Math.Abs(Vec3.Dot(delta, axis)) > radius + queryRadius + 0.00001f;
            }
        }
    }
}
