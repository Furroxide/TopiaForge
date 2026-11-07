using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    // Keep the affine basis through the whole hierarchy. Decomposing intermediate nodes to TRS
    // would discard shear from rotated, nonuniformly scaled parents and misplace descendants.
    internal readonly struct AuthoredAffine
    {
        private readonly bool collapsed;
        internal AuthoredAffine(Vec3 position, Vec3 x, Vec3 y, Vec3 z, Quat rotation, bool collapsed = false)
        {
            Position = position; X = x; Y = y; Z = z; Rotation = NormalizeRotation(rotation);
            this.collapsed = collapsed || Determinant(x, y, z) == 0;
        }
        internal Vec3 Position { get; }
        internal Vec3 X { get; }
        internal Vec3 Y { get; }
        internal Vec3 Z { get; }
        internal Quat Rotation { get; }
        internal static AuthoredAffine Identity => From(TransformState.Identity);
        internal static AuthoredAffine From(TransformState value) => From(value.Position, value.Rotation, value.Scale);
        internal static AuthoredAffine From(Vec3 position, Quat rotation, Vec3 scale)
        {
            var unit = NormalizeRotation(rotation);
            return new AuthoredAffine(position, Rotate(unit, new Vec3(scale.X, 0, 0)), Rotate(unit, new Vec3(0, scale.Y, 0)),
                Rotate(unit, new Vec3(0, 0, scale.Z)), unit);
        }
        internal static Quat NormalizeRotation(Quat rotation)
        {
            // A finite float quaternion can overflow float LengthSquared. Double covers the
            // square and sum of every finite float component without inventing an identity pose.
            var length = Math.Sqrt((double)rotation.X * rotation.X + (double)rotation.Y * rotation.Y
                + (double)rotation.Z * rotation.Z + (double)rotation.W * rotation.W);
            if (!rotation.IsFinite || length == 0)
                throw new InvalidOperationException("This authored rotation is not a finite nonzero quaternion. Inspect the authored transform and coordinate conversion.");
            return new Quat((float)(rotation.X / length), (float)(rotation.Y / length), (float)(rotation.Z / length), (float)(rotation.W / length));
        }
        private static Vec3 Rotate(Quat unit, Vec3 value)
        {
            double x = unit.X, y = unit.Y, z = unit.Z, w = unit.W;
            return new Vec3(
                (float)((1 - 2 * (y * y + z * z)) * value.X + 2 * (x * y - w * z) * value.Y + 2 * (x * z + w * y) * value.Z),
                (float)(2 * (x * y + w * z) * value.X + (1 - 2 * (x * x + z * z)) * value.Y + 2 * (y * z - w * x) * value.Z),
                (float)(2 * (x * z - w * y) * value.X + 2 * (y * z + w * x) * value.Y + (1 - 2 * (x * x + y * y)) * value.Z));
        }
        internal Vec3 Vector(Vec3 value) => Apply(value, Vec3.Zero);
        private Vec3 Apply(Vec3 value, Vec3 translation) => new Vec3(
            (float)((double)X.X * value.X + (double)Y.X * value.Y + (double)Z.X * value.Z + translation.X),
            (float)((double)X.Y * value.X + (double)Y.Y * value.Y + (double)Z.Y * value.Z + translation.Y),
            (float)((double)X.Z * value.X + (double)Y.Z * value.Y + (double)Z.Z * value.Z + translation.Z));
        internal AuthoredAffine Append(AuthoredAffine child)
        {
            // Both factors are already unit quaternions, so their product cannot overflow;
            // the constructor renormalizes every generation rather than accumulating drift.
            return new AuthoredAffine(Apply(child.Position, Position), Vector(child.X), Vector(child.Y), Vector(child.Z),
                Rotation * child.Rotation, collapsed || child.collapsed);
        }
        internal Vec3 LossyScale
        {
            get
            {
                // Unity-style lossy scale: basis lengths, with odd reflection assigned to X.
                // The quaternion remains the product of local quaternions, independent of scale.
                var sign = Determinant(X, Y, Z) < 0 ? -1 : 1;
                return new Vec3(sign * Length(X), Length(Y), Length(Z));
            }
        }
        internal TransformState Pose
        {
            get
            {
                if (!TryGetPose(out var pose))
                    throw new InvalidOperationException("This authored world pose cannot be represented as TransformState: an authored ancestor or import root has a collapsed scale or the composed values overflow. Inspect EditorTransform and the parent chain; use a non-collapsed authored marker for placement.");
                return pose;
            }
        }
        internal bool TryGetPose(out TransformState pose)
        {
            pose = TransformState.Identity;
            var scale = LossyScale;
            if (collapsed || !Position.IsFinite || !Rotation.IsFinite || !scale.IsFinite || scale.X == 0 || scale.Y == 0 || scale.Z == 0)
                return false;
            pose = new TransformState(Position, Rotation, scale);
            return true;
        }
        private static double Determinant(Vec3 x, Vec3 y, Vec3 z) =>
            (double)x.X * ((double)y.Y * z.Z - (double)y.Z * z.Y)
            - (double)x.Y * ((double)y.X * z.Z - (double)y.Z * z.X)
            + (double)x.Z * ((double)y.X * z.Y - (double)y.Y * z.X);
        private static float Length(Vec3 value) => (float)Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
        internal static IReadOnlyDictionary<string, AuthoredAffine> Compose(IReadOnlyList<AuthoredRecord> records,
            Func<MapTransform, AuthoredAffine> local, AuthoredAffine root)
        {
            var bases = new Dictionary<string, AuthoredAffine>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                var parent = record.ParentId == null ? root : bases[record.ParentId];
                var world = parent.Append(local(record.Transform));
                bases.Add(record.Id, world);
            }
            return bases;
        }
    }
}
