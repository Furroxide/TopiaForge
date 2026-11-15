using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    // One teleport, then bounded observation only. The world readiness clock still owns the overall deadline.
    internal sealed class NativeSpawnSettlement
    {
        // A body falling from rest drops about 11 cm in 0.15 s of fixed time, well past the 1 cm guards.
        // The window is longer than 0.1 s so a small upward pop (about 0.4 m/s) that turns into a fall
        // inside the 1 cm band at 0.1 s is still observed falling before it can be accepted.
        internal const float SteadySeconds = 0.15f;
        // Bounds an unsteady player: refuse after 8 fixed samples once 0.5 s of fixed time has passed.
        internal const float SettleSeconds = 0.5f;
        private const float ClockTolerance = 0.0005f;
        private readonly TransformState requested;
        private readonly float height;
        private readonly float placedAt;
        private float fixedTime;
        private Vec3 anchor;
        private float steadySince;
        private int stable;
        private int samples;
        internal NativeSpawnSettlement(TransformState requested, float height, float fixedTime)
        {
            this.requested = requested;
            this.height = height;
            this.fixedTime = fixedTime;
            placedAt = fixedTime;
            steadySince = fixedTime;
            anchor = requested.Position;
        }
        // Only fixed steps count. Paused or extra rendered frames observe the same physics state,
        // so they validate the pose but cannot add a sample.
        internal OperationResult<TransformState>? Probe(TransformState actual, float currentFixedTime)
        {
            if (!Finite(height) || height <= 0f || !Finite(fixedTime) || !Finite(currentFixedTime))
                return OperationResult<TransformState>.Failure(ModErrorCode.Unavailable, "The native player collision height or fixed clock is unavailable for spawn settling.");
            if (!actual.Position.IsFinite || !actual.Rotation.IsFinite)
                return Refused();
            var delta = actual.Position - requested.Position;
            var rotation = actual.Rotation;
            var expected = requested.Rotation;
            var dot = Math.Abs(rotation.X * expected.X + rotation.Y * expected.Y + rotation.Z * expected.Z + rotation.W * expected.W);
            if (!delta.IsFinite || delta.X * delta.X + delta.Z * delta.Z > 0.0001f || dot < 0.9999f
                || (delta.Y < 0f && delta.Y * delta.Y > 0.0001f) || delta.Y > height || currentFixedTime < fixedTime)
                return Refused();
            if (currentFixedTime == fixedTime) return null;
            fixedTime = currentFixedTime;
            samples++;
            // Every sample of a steady run stays within 1 cm of the run's first position.
            if ((actual.Position - anchor).LengthSquared <= 0.0001f) stable++;
            else { stable = 0; anchor = actual.Position; steadySince = currentFixedTime; }
            if (stable >= 2 && currentFixedTime - steadySince + ClockTolerance >= SteadySeconds)
                return OperationResult<TransformState>.Success(actual);
            return samples >= 8 && currentFixedTime - placedAt >= SettleSeconds ? Refused() : null;
        }
        private static OperationResult<TransformState> Refused() => OperationResult<TransformState>.Failure(
            ModErrorCode.External, TopiaForge.Mods.Internal.WorldSpawnReadinessText.SettleRefused);
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
