using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// A Unity-free 3D vector (<c>x</c>, <c>y</c>, <c>z</c>) used across SDK service contracts so the
    /// abstractions assembly never references <c>UnityEngine</c>. Framework mods convert to and from
    /// <c>UnityEngine.Vector3</c> on the implementation side.
    /// </summary>
    /// <remarks>
    /// This struct supersedes the older <c>float[]</c> convention (e.g. <see cref="UgcAssetOverride"/>) for new
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
