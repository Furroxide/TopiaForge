using System;
using System.Globalization;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// A world-space vector in metres, or a velocity in metres or radians per second, stored as Unity stores it.
    /// Core stays free of Unity, so the runtime converts at its boundary.
    /// </summary>
    internal readonly struct NetVector3 : IEquatable<NetVector3>
    {
        public NetVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static NetVector3 Zero => default;

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);

        /// <summary>The squared length, computed in double precision.</summary>
        public double LengthSquared => (double)X * X + (double)Y * Y + (double)Z * Z;

        public bool Equals(NetVector3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object? obj) => obj is NetVector3 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        public override string ToString() =>
            "(" + Format(X) + ", " + Format(Y) + ", " + Format(Z) + ")";

        internal static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A rotation quaternion in Unity's component order and handedness.</summary>
    internal readonly struct NetQuaternion : IEquatable<NetQuaternion>
    {
        public NetQuaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static NetQuaternion Identity => new NetQuaternion(0f, 0f, 0f, 1f);

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float W { get; }

        public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && float.IsFinite(W);

        public bool Equals(NetQuaternion other) =>
            X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);

        public override bool Equals(object? obj) => obj is NetQuaternion other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);

        public override string ToString() =>
            "(" + NetVector3.Format(X) + ", " + NetVector3.Format(Y) + ", " + NetVector3.Format(Z) + ", " +
            NetVector3.Format(W) + ")";
    }

    /// <summary>Exact integer helpers, so validation gives the same answer on every platform.</summary>
    internal static class NetIntegerMath
    {
        /// <summary>The largest integer whose square does not exceed <paramref name="value"/>.</summary>
        public static long FloorSqrt(long value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (value < 2) return value;

            // The double estimate is within one of the answer for every long; the loops make it exact.
            var root = (long)Math.Sqrt(value);
            while (root > 0 && root > value / root) root--;
            while (root + 1 <= value / (root + 1)) root++;
            return root;
        }

        /// <summary>The smallest integer whose square is at least <paramref name="value"/>.</summary>
        public static long CeilingSqrt(long value)
        {
            var root = FloorSqrt(value);
            return root * root == value ? root : root + 1;
        }

        /// <summary>Adds without wrapping past <see cref="long.MaxValue"/>; both operands are non-negative.</summary>
        public static long SaturatingAdd(long value, long increment) =>
            value > long.MaxValue - increment ? long.MaxValue : value + increment;
    }
}
