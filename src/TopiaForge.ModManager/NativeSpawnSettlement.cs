using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    // One teleport, then bounded observation only. The world readiness clock still owns the overall deadline.
    internal sealed class NativeSpawnSettlement
    {
        private readonly TransformState requested;
        private readonly float height;
        private float fixedTime;
        private Vec3 previous;
        private int stable;
        private int samples;
        internal NativeSpawnSettlement(TransformState requested, float height, float fixedTime)
        { this.requested = requested; this.height = height; this.fixedTime = fixedTime; previous = requested.Position; }
        internal OperationResult<TransformState>? Probe(TransformState actual, float currentFixedTime)
        {
            if (!Finite(height) || height <= 0f || !Finite(fixedTime) || !Finite(currentFixedTime))
                return OperationResult<TransformState>.Failure(ModErrorCode.Unavailable, "The native player collision height or fixed clock is unavailable for spawn settling.");
            if (!actual.Position.IsFinite || !actual.Rotation.IsFinite)
                return Refused();
            var delta = actual.Position - requested.Position;
            var rotation = actual.Rotation; var expected = requested.Rotation;
            var dot = Math.Abs(rotation.X * expected.X + rotation.Y * expected.Y + rotation.Z * expected.Z + rotation.W * expected.W);
            if (!delta.IsFinite || delta.X * delta.X + delta.Z * delta.Z > 0.0001f || dot < 0.9999f
                || (delta.Y < 0f && delta.Y * delta.Y > 0.0001f) || delta.Y > height || currentFixedTime < fixedTime)
                return Refused();
            if (currentFixedTime == fixedTime) return null;
            fixedTime = currentFixedTime; samples++;
            stable = (actual.Position - previous).LengthSquared <= 0.0001f ? stable + 1 : 0;
            previous = actual.Position;
            if (stable >= 2) return OperationResult<TransformState>.Success(actual);
            return samples >= 8 ? Refused() : null;
        }
        private static OperationResult<TransformState> Refused() => OperationResult<TransformState>.Failure(
            ModErrorCode.External, "The native player did not retain the applied spawn position and rotation.");
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static string DescribeOffset(TransformState actual, TransformState requested, string stage)
        {
            var delta = actual.Position - requested.Position;
            if (!delta.IsFinite) return "TopiaForge spawn settle: stage=" + stage + "; offset=unavailable.";
            return string.Format(CultureInfo.InvariantCulture,
                "TopiaForge spawn settle: stage={0}; dx={1:R}; dy={2:R}; dz={3:R}.", stage, delta.X, delta.Y, delta.Z);
        }
    }
}
