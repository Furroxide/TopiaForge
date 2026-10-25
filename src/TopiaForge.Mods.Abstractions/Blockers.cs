using System;

namespace TopiaForge.Mods
{
    /// <summary>An invisible box collider, replicated to every player and owned by the calling context.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class BlockerRequest
    {
        internal const string InvalidGeometry = "A blocker's sides are 0.05 to 1,000 m and finite. Use finite world coordinates and a finite rotation.";

        private BlockerRequest(TransformState placement, Vec3 size, bool blocksSight)
        {
            if (!ValidSide(size.X) || !ValidSide(size.Y) || !ValidSide(size.Z) ||
                !placement.Position.IsFinite || !placement.Rotation.IsFinite)
                throw new ArgumentException(InvalidGeometry);
            Placement = new TransformState(placement.Position, placement.Rotation, new Vec3(1f, 1f, 1f));
            Size = size;
            BlocksSight = blocksSight;
        }

        /// <summary>Creates a box blocker turned <paramref name="yawDegrees"/> about the vertical axis.</summary>
        /// <exception cref="ArgumentException">Geometry is not finite, or a side is outside 0.05 to 1,000 metres.</exception>
        public static BlockerRequest Box(Vec3 center, Vec3 size, float yawDegrees = 0f, bool blocksSight = true)
        {
            if (!center.IsFinite || float.IsNaN(yawDegrees) || float.IsInfinity(yawDegrees))
                throw new ArgumentException(InvalidGeometry);
            var halfAngle = (yawDegrees % 360f) * (Math.PI / 360d);
            return Box(new TransformState(center, new Quat(0f, (float)Math.Sin(halfAngle), 0f,
                (float)Math.Cos(halfAngle)), new Vec3(1f, 1f, 1f)), size, blocksSight);
        }

        /// <summary>Creates a box at a world transform's position and rotation; its scale is ignored.</summary>
        /// <exception cref="ArgumentException">Geometry is not finite, or a side is outside 0.05 to 1,000 metres.</exception>
        public static BlockerRequest Box(TransformState placement, Vec3 size, bool blocksSight = true) =>
            new BlockerRequest(placement, size, blocksSight);

        /// <summary>Gets whether ordinary physics queries and sight lines stop at the blocker; true by default.</summary>
        public bool BlocksSight { get; }

        internal TransformState Placement { get; }
        internal Vec3 Size { get; }
        internal static bool ValidSide(float side) => side >= 0.05f && side <= 1_000f;
    }

    /// <summary>
    /// A blocker this context owns. Disposing it removes it for every player. Move it with
    /// <c>Context.Entities.SetTransform</c>, which keeps its size; no tool can hold or throw it.
    /// </summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IBlocker : ISpawnedEntity
    {
        /// <summary>Gets the box's size in metres, which a move never changes.</summary>
        Vec3 Size { get; }
    }
}
