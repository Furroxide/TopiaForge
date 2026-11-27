using System;

namespace TopiaForge.Mods
{
    /// <summary>Host-only persistent cosmetic bodies owned by the consuming gamemode server's scope.
    /// Obtain from that context's extensions. Replication and cleanup are automatic. Main thread only.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IRobotBodyService
    {
        /// <summary>Captures a separate powered-down body. No player reference is retained and no damage or native
        /// death occurs. Failure creates no lease; there is no placeholder fallback. At most 64 bodies per scope.</summary>
        OperationResult<IRobotBody> Spawn(RobotBodyRequest request);
    }

    /// <summary>Immutable appearance and world anchor for a powered-down cosmetic body.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class RobotBodyRequest
    {
        /// <summary>Captures appearance and anchor. Position components are finite within ±100,000 metres;
        /// rotation is a unit quaternion (identity when omitted). The prone visual is offset above this anchor.</summary>
        public RobotBodyRequest(AvatarAppearance appearance, Vec3 position, Quat? rotation = null)
        {
            Appearance = appearance ?? throw new ArgumentNullException(nameof(appearance));
            if (!Coordinate(position.X) || !Coordinate(position.Y) || !Coordinate(position.Z))
                throw new ArgumentOutOfRangeException(nameof(position));
            var q = rotation ?? Quat.Identity;
            var length = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            if (float.IsNaN(length) || Math.Abs(length - 1f) > 0.001f)
                throw new ArgumentOutOfRangeException(nameof(rotation));
            Position = position;
            Rotation = q;
        }

        /// <summary>Gets the captured appearance, including the selected catalog accessory slot.</summary>
        public AvatarAppearance Appearance { get; }
        /// <summary>Gets the fixed world anchor.</summary>
        public Vec3 Position { get; }
        /// <summary>Gets the anchor rotation; the powered-down visual pose is applied beneath it.</summary>
        public Quat Rotation { get; }
        private static bool Coordinate(float value) => !float.IsNaN(value) && value >= -100000 && value <= 100000;
    }

    /// <summary>A separately spawned, non-colliding powered-down body. It survives the source participant's movement,
    /// departure and avatar teardown. Dispose releases it on every peer; scope/world/provider termination also releases it.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IRobotBody : IEntity, IDisposable
    {
        /// <summary>Gets the immutable captured appearance, independent of subsequent participant edits.</summary>
        AvatarAppearance Appearance { get; }
        /// <summary>Gets the fixed world anchor rotation.</summary>
        Quat Rotation { get; }
    }
}
