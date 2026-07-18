using System;

namespace TopiaForge.Mods
{
    /// <summary>Represents a Unity-free two-dimensional vector.</summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        /// <summary>Creates a vector from its components.</summary>
        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        /// <summary>Gets the horizontal component.</summary>
        public float X { get; }

        /// <summary>Gets the vertical component.</summary>
        public float Y { get; }

        /// <summary>Gets the zero vector.</summary>
        public static Vec2 Zero => new Vec2(0f, 0f);

        /// <summary>Gets the vector length.</summary>
        public float Length => (float)Math.Sqrt(LengthSquared);

        /// <summary>Gets the squared vector length.</summary>
        public float LengthSquared => X * X + Y * Y;

        /// <summary>Gets whether both components are finite.</summary>
        public bool IsFinite => IsFiniteValue(X) && IsFiniteValue(Y);

        /// <summary>Gets the unit vector in the same direction, or zero for a near-zero vector.</summary>
        public Vec2 Normalized => Length > 0.000001f ? this / Length : Zero;

        /// <summary>Adds two vectors.</summary>
        public static Vec2 operator +(Vec2 left, Vec2 right) => new Vec2(left.X + right.X, left.Y + right.Y);

        /// <summary>Subtracts one vector from another.</summary>
        public static Vec2 operator -(Vec2 left, Vec2 right) => new Vec2(left.X - right.X, left.Y - right.Y);

        /// <summary>Negates a vector.</summary>
        public static Vec2 operator -(Vec2 value) => new Vec2(-value.X, -value.Y);

        /// <summary>Scales a vector.</summary>
        public static Vec2 operator *(Vec2 value, float scale) => new Vec2(value.X * scale, value.Y * scale);

        /// <summary>Scales a vector.</summary>
        public static Vec2 operator *(float scale, Vec2 value) => value * scale;

        /// <summary>Divides a vector by a non-zero scalar.</summary>
        public static Vec2 operator /(Vec2 value, float scale)
        {
            if (scale == 0f)
            {
                throw new DivideByZeroException();
            }

            return new Vec2(value.X / scale, value.Y / scale);
        }

        /// <summary>Compares two vectors for exact component equality.</summary>
        public static bool operator ==(Vec2 left, Vec2 right) => left.Equals(right);

        /// <summary>Compares two vectors for component inequality.</summary>
        public static bool operator !=(Vec2 left, Vec2 right) => !left.Equals(right);

        /// <summary>Returns the dot product of two vectors.</summary>
        public static float Dot(Vec2 left, Vec2 right) => left.X * right.X + left.Y * right.Y;

        /// <inheritdoc/>
        public bool Equals(Vec2 other) => X.Equals(other.X) && Y.Equals(other.Y);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Vec2 other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Y.GetHashCode();
            }
        }

        /// <inheritdoc/>
        public override string ToString() => "(" + X + ", " + Y + ")";

        private static bool IsFiniteValue(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>Represents a Unity-free quaternion rotation.</summary>
    public readonly struct Quat : IEquatable<Quat>
    {
        /// <summary>Creates a quaternion from its four components.</summary>
        public Quat(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        /// <summary>Gets the x component.</summary>
        public float X { get; }

        /// <summary>Gets the y component.</summary>
        public float Y { get; }

        /// <summary>Gets the z component.</summary>
        public float Z { get; }

        /// <summary>Gets the scalar component.</summary>
        public float W { get; }

        /// <summary>Gets the identity rotation.</summary>
        public static Quat Identity => new Quat(0f, 0f, 0f, 1f);

        /// <summary>Gets the squared quaternion magnitude.</summary>
        public float LengthSquared => X * X + Y * Y + Z * Z + W * W;

        /// <summary>Gets whether every component is finite.</summary>
        public bool IsFinite => IsFiniteValue(X) && IsFiniteValue(Y) && IsFiniteValue(Z) && IsFiniteValue(W);

        /// <summary>Gets the normalized rotation, or identity for a near-zero quaternion.</summary>
        public Quat Normalized
        {
            get
            {
                var length = (float)Math.Sqrt(LengthSquared);
                return length > 0.000001f
                    ? new Quat(X / length, Y / length, Z / length, W / length)
                    : Identity;
            }
        }

        /// <summary>Composes two rotations.</summary>
        public static Quat operator *(Quat left, Quat right)
        {
            return new Quat(
                left.W * right.X + left.X * right.W + left.Y * right.Z - left.Z * right.Y,
                left.W * right.Y - left.X * right.Z + left.Y * right.W + left.Z * right.X,
                left.W * right.Z + left.X * right.Y - left.Y * right.X + left.Z * right.W,
                left.W * right.W - left.X * right.X - left.Y * right.Y - left.Z * right.Z);
        }

        /// <summary>Rotates a vector.</summary>
        public static Vec3 operator *(Quat rotation, Vec3 point)
        {
            var normalized = rotation.Normalized;
            var vector = new Vec3(normalized.X, normalized.Y, normalized.Z);
            var twiceCross = 2f * Vec3.Cross(vector, point);
            return point + normalized.W * twiceCross + Vec3.Cross(vector, twiceCross);
        }

        /// <summary>Compares two rotations for exact component equality.</summary>
        public static bool operator ==(Quat left, Quat right) => left.Equals(right);

        /// <summary>Compares two rotations for component inequality.</summary>
        public static bool operator !=(Quat left, Quat right) => !left.Equals(right);

        /// <inheritdoc/>
        public bool Equals(Quat other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Quat other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return (hash * 397) ^ W.GetHashCode();
            }
        }

        /// <inheritdoc/>
        public override string ToString() => "(" + X + ", " + Y + ", " + Z + ", " + W + ")";

        private static bool IsFiniteValue(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>Represents a normalized world-space ray.</summary>
    public readonly struct Ray : IEquatable<Ray>
    {
        /// <summary>Creates a ray from an origin and non-zero direction.</summary>
        public Ray(Vec3 origin, Vec3 direction)
        {
            if (!origin.IsFinite || !direction.IsFinite || direction.LengthSquared <= 0.000000000001f)
            {
                throw new ArgumentException("A ray direction must be finite and non-zero.", nameof(direction));
            }

            Origin = origin;
            Direction = direction.Normalized;
        }

        /// <summary>Gets the ray origin.</summary>
        public Vec3 Origin { get; }

        /// <summary>Gets the normalized ray direction.</summary>
        public Vec3 Direction { get; }

        /// <summary>Returns a point at a non-negative distance along the ray.</summary>
        public Vec3 GetPoint(float distance)
        {
            if (distance < 0f || float.IsNaN(distance) || float.IsInfinity(distance))
            {
                throw new ArgumentOutOfRangeException(nameof(distance));
            }

            return Origin + Direction * distance;
        }

        /// <summary>Compares two rays for exact equality.</summary>
        public static bool operator ==(Ray left, Ray right) => left.Equals(right);

        /// <summary>Compares two rays for inequality.</summary>
        public static bool operator !=(Ray left, Ray right) => !left.Equals(right);

        /// <inheritdoc/>
        public bool Equals(Ray other) => Origin.Equals(other.Origin) && Direction.Equals(other.Direction);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Ray other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                return (Origin.GetHashCode() * 397) ^ Direction.GetHashCode();
            }
        }
    }

    /// <summary>Represents axis-aligned world bounds using a center and non-negative size.</summary>
    public readonly struct Bounds : IEquatable<Bounds>
    {
        /// <summary>Creates bounds from a center and non-negative size.</summary>
        public Bounds(Vec3 center, Vec3 size)
        {
            if (!center.IsFinite || !size.IsFinite || size.X < 0f || size.Y < 0f || size.Z < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "Bounds size must be finite and non-negative.");
            }

            Center = center;
            Size = size;
        }

        /// <summary>Gets the center.</summary>
        public Vec3 Center { get; }

        /// <summary>Gets the full size.</summary>
        public Vec3 Size { get; }

        /// <summary>Gets the half size.</summary>
        public Vec3 Extents => Size * 0.5f;

        /// <summary>Gets the minimum corner.</summary>
        public Vec3 Min => Center - Extents;

        /// <summary>Gets the maximum corner.</summary>
        public Vec3 Max => Center + Extents;

        /// <summary>Determines whether a point lies inside or on the bounds.</summary>
        public bool Contains(Vec3 point)
        {
            var min = Min;
            var max = Max;
            return point.X >= min.X && point.X <= max.X
                && point.Y >= min.Y && point.Y <= max.Y
                && point.Z >= min.Z && point.Z <= max.Z;
        }

        /// <summary>Determines whether two bounds overlap.</summary>
        public bool Intersects(Bounds other)
        {
            var min = Min;
            var max = Max;
            var otherMin = other.Min;
            var otherMax = other.Max;
            return min.X <= otherMax.X && max.X >= otherMin.X
                && min.Y <= otherMax.Y && max.Y >= otherMin.Y
                && min.Z <= otherMax.Z && max.Z >= otherMin.Z;
        }

        /// <summary>Compares two bounds for exact equality.</summary>
        public static bool operator ==(Bounds left, Bounds right) => left.Equals(right);

        /// <summary>Compares two bounds for inequality.</summary>
        public static bool operator !=(Bounds left, Bounds right) => !left.Equals(right);

        /// <inheritdoc/>
        public bool Equals(Bounds other) => Center.Equals(other.Center) && Size.Equals(other.Size);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Bounds other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                return (Center.GetHashCode() * 397) ^ Size.GetHashCode();
            }
        }
    }

    /// <summary>Represents a Unity-free linear RGBA color.</summary>
    public readonly struct RgbaColor : IEquatable<RgbaColor>
    {
        /// <summary>Creates a color with components conventionally in the zero-to-one range.</summary>
        public RgbaColor(float red, float green, float blue, float alpha = 1f)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
        }

        /// <summary>Gets the red component.</summary>
        public float Red { get; }

        /// <summary>Gets the green component.</summary>
        public float Green { get; }

        /// <summary>Gets the blue component.</summary>
        public float Blue { get; }

        /// <summary>Gets the alpha component.</summary>
        public float Alpha { get; }

        /// <summary>Gets opaque white.</summary>
        public static RgbaColor White => new RgbaColor(1f, 1f, 1f, 1f);

        /// <summary>Gets transparent black.</summary>
        public static RgbaColor Clear => new RgbaColor(0f, 0f, 0f, 0f);

        /// <summary>Returns a color with every component clamped to zero through one.</summary>
        public RgbaColor Clamped => new RgbaColor(Clamp01(Red), Clamp01(Green), Clamp01(Blue), Clamp01(Alpha));

        /// <summary>Linearly interpolates between two colors.</summary>
        public static RgbaColor Lerp(RgbaColor from, RgbaColor to, float amount)
        {
            var t = Clamp01(amount);
            return new RgbaColor(
                from.Red + (to.Red - from.Red) * t,
                from.Green + (to.Green - from.Green) * t,
                from.Blue + (to.Blue - from.Blue) * t,
                from.Alpha + (to.Alpha - from.Alpha) * t);
        }

        /// <summary>Compares two colors for exact component equality.</summary>
        public static bool operator ==(RgbaColor left, RgbaColor right) => left.Equals(right);

        /// <summary>Compares two colors for component inequality.</summary>
        public static bool operator !=(RgbaColor left, RgbaColor right) => !left.Equals(right);

        /// <inheritdoc/>
        public bool Equals(RgbaColor other)
        {
            return Red.Equals(other.Red) && Green.Equals(other.Green)
                && Blue.Equals(other.Blue) && Alpha.Equals(other.Alpha);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is RgbaColor other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Red.GetHashCode();
                hash = (hash * 397) ^ Green.GetHashCode();
                hash = (hash * 397) ^ Blue.GetHashCode();
                return (hash * 397) ^ Alpha.GetHashCode();
            }
        }

        /// <inheritdoc/>
        public override string ToString() => "(" + Red + ", " + Green + ", " + Blue + ", " + Alpha + ")";

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
    }
}
