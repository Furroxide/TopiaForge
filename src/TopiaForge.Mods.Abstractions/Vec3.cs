using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// An engine-independent 3D vector (<c>x</c>, <c>y</c>, <c>z</c>) used across SDK service contracts.
    /// Runtime providers perform any native conversion behind the safe contract boundary.
    /// </summary>
    /// <remarks>
    /// This struct supersedes the older allocation-heavy <c>float[]</c> convention for new
    /// vector-carrying APIs: it is allocation-free, has a fixed element count, and is self-documenting.
    /// </remarks>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        /// <summary>Creates a vector from its components.</summary>
        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>The x component.</summary>
        public float X { get; }

        /// <summary>The y component.</summary>
        public float Y { get; }

        /// <summary>The z component.</summary>
        public float Z { get; }

        /// <summary>The zero vector.</summary>
        public static Vec3 Zero => new Vec3(0f, 0f, 0f);

        /// <summary>Gets the vector length.</summary>
        public float Length => (float)Math.Sqrt(LengthSquared);

        /// <summary>Gets the squared vector length without performing a square root.</summary>
        public float LengthSquared => X * X + Y * Y + Z * Z;

        /// <summary>Gets whether every component is neither NaN nor infinity.</summary>
        public bool IsFinite => IsFiniteValue(X) && IsFiniteValue(Y) && IsFiniteValue(Z);

        /// <summary>Gets a unit vector in the same direction, or <see cref="Zero"/> for a near-zero vector.</summary>
        public Vec3 Normalized
        {
            get
            {
                var length = Length;
                return length > 0.000001f ? this / length : Zero;
            }
        }

        /// <summary>Adds two vectors.</summary>
        public static Vec3 operator +(Vec3 left, Vec3 right)
        {
            return new Vec3(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        }

        /// <summary>Subtracts one vector from another.</summary>
        public static Vec3 operator -(Vec3 left, Vec3 right)
        {
            return new Vec3(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        }

        /// <summary>Negates a vector.</summary>
        public static Vec3 operator -(Vec3 value)
        {
            return new Vec3(-value.X, -value.Y, -value.Z);
        }

        /// <summary>Scales a vector.</summary>
        public static Vec3 operator *(Vec3 vector, float scale)
        {
            return new Vec3(vector.X * scale, vector.Y * scale, vector.Z * scale);
        }

        /// <summary>Scales a vector.</summary>
        public static Vec3 operator *(float scale, Vec3 vector)
        {
            return vector * scale;
        }

        /// <summary>Divides a vector by a scalar.</summary>
        /// <exception cref="DivideByZeroException"><paramref name="scale"/> is zero.</exception>
        public static Vec3 operator /(Vec3 vector, float scale)
        {
            if (scale == 0f)
            {
                throw new DivideByZeroException();
            }

            return new Vec3(vector.X / scale, vector.Y / scale, vector.Z / scale);
        }

        /// <summary>Returns the dot product of two vectors.</summary>
        public static float Dot(Vec3 left, Vec3 right)
        {
            return left.X * right.X + left.Y * right.Y + left.Z * right.Z;
        }

        /// <summary>Returns the cross product of two vectors.</summary>
        public static Vec3 Cross(Vec3 left, Vec3 right)
        {
            return new Vec3(
                left.Y * right.Z - left.Z * right.Y,
                left.Z * right.X - left.X * right.Z,
                left.X * right.Y - left.Y * right.X);
        }

        /// <summary>Returns the distance between two points.</summary>
        public static float Distance(Vec3 first, Vec3 second)
        {
            return (first - second).Length;
        }

        /// <summary>Compares two vectors for exact component equality.</summary>
        public static bool operator ==(Vec3 left, Vec3 right)
        {
            return left.Equals(right);
        }

        /// <summary>Compares two vectors for component inequality.</summary>
        public static bool operator !=(Vec3 left, Vec3 right)
        {
            return !left.Equals(right);
        }

        /// <summary>Limits a vector to the supplied maximum length.</summary>
        /// <param name="value">The vector to limit.</param>
        /// <param name="maximumLength">A non-negative maximum length.</param>
        /// <returns>The original vector when already within the limit; otherwise a vector with the requested length.</returns>
        public static Vec3 ClampLength(Vec3 value, float maximumLength)
        {
            if (maximumLength < 0f || float.IsNaN(maximumLength) || float.IsInfinity(maximumLength))
            {
                throw new ArgumentOutOfRangeException(nameof(maximumLength));
            }

            var squared = value.LengthSquared;
            return squared > maximumLength * maximumLength
                ? value.Normalized * maximumLength
                : value;
        }

        private static bool IsFiniteValue(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>Returns the components as a new <c>[x, y, z]</c> array (interop with the float[] convention).</summary>
        public float[] ToArray()
        {
            return new[] { X, Y, Z };
        }

        /// <summary>Builds a vector from a <c>[x, y, z]</c> array; shorter/<c>null</c> arrays yield <see cref="Zero"/>.</summary>
        public static Vec3 FromArray(float[]? values)
        {
            return values != null && values.Length >= 3 ? new Vec3(values[0], values[1], values[2]) : Zero;
        }

        /// <inheritdoc/>
        public bool Equals(Vec3 other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is Vec3 other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return hash;
            }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return "(" + X + ", " + Y + ", " + Z + ")";
        }
    }
}
