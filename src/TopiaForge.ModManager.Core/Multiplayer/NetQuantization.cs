using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>A position on the 1/512 m grid: three signed 22-bit axis values.</summary>
    internal readonly struct NetQuantizedPosition : IEquatable<NetQuantizedPosition>
    {
        public NetQuantizedPosition(int x, int y, int z)
        {
            NetQuantization.RequireRange(x, NetQuantization.PositionMinimumUnits, NetQuantization.PositionMaximumUnits, nameof(x));
            NetQuantization.RequireRange(y, NetQuantization.PositionMinimumUnits, NetQuantization.PositionMaximumUnits, nameof(y));
            NetQuantization.RequireRange(z, NetQuantization.PositionMinimumUnits, NetQuantization.PositionMaximumUnits, nameof(z));
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }

        public int Y { get; }

        public int Z { get; }

        /// <summary>The exact squared distance in grid units. It fits a long: at most 3 * 2^44.</summary>
        public long DistanceSquaredTo(NetQuantizedPosition other)
        {
            var dx = (long)X - other.X;
            var dy = (long)Y - other.Y;
            var dz = (long)Z - other.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        public bool Equals(NetQuantizedPosition other) => X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object? obj) => obj is NetQuantizedPosition other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        public override string ToString() => "[" + X + ", " + Y + ", " + Z + "]";
    }

    /// <summary>A linear or angular velocity on the 1/16 unit-per-second grid: three signed 11-bit axis values.</summary>
    internal readonly struct NetQuantizedVelocity : IEquatable<NetQuantizedVelocity>
    {
        public NetQuantizedVelocity(int x, int y, int z)
        {
            NetQuantization.RequireRange(x, NetQuantization.VelocityMinimumUnits, NetQuantization.VelocityMaximumUnits, nameof(x));
            NetQuantization.RequireRange(y, NetQuantization.VelocityMinimumUnits, NetQuantization.VelocityMaximumUnits, nameof(y));
            NetQuantization.RequireRange(z, NetQuantization.VelocityMinimumUnits, NetQuantization.VelocityMaximumUnits, nameof(z));
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }

        public int Y { get; }

        public int Z { get; }

        public bool Equals(NetQuantizedVelocity other) => X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object? obj) => obj is NetQuantizedVelocity other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        public override string ToString() => "[" + X + ", " + Y + ", " + Z + "]";
    }

    /// <summary>
    /// Transform quantization and its wire format. Every operation is exact or a fixed sequence of IEEE-754
    /// double operations, so any platform, and any language, produces the same bits.
    /// </summary>
    internal static class NetQuantization
    {
        public const int PositionAxisBits = 22;
        public const int PositionUnitsPerMeter = 512;
        public const int PositionMinimumUnits = -(1 << 21);
        public const int PositionMaximumUnits = (1 << 21) - 1;

        /// <summary>The largest error of an in-range axis: half a grid step.</summary>
        public const double PositionMaximumErrorMeters = 1.0 / 1024.0;

        public const int RotationComponentBits = 10;
        public const int RotationBits = 32;

        /// <summary>Each smallest-three component uses codes 0 to 1022, centred on 511 so zero is exact.</summary>
        public const int RotationHalfRange = 511;

        /// <summary>The tested bound on the angle between a rotation and its round trip.</summary>
        public const double RotationMaximumErrorDegrees = 0.28;

        /// <summary>The identity rotation's bits: the largest component is w, and the others are code 511.</summary>
        public const uint IdentityRotationBits = 0x7FDFF7FFu;

        public const int VelocityAxisBits = 11;
        public const int VelocityUnitsPerUnitPerSecond = 16;
        public const int VelocityMinimumUnits = -1024;
        public const int VelocityMaximumUnits = 1023;

        /// <summary>The largest error of an in-range velocity axis: half a grid step.</summary>
        public const double VelocityMaximumErrorPerSecond = 1.0 / 32.0;

        public const int TransformBitsWithoutVelocity = 3 * PositionAxisBits + RotationBits + 1;
        public const int TransformBitsWithVelocity = TransformBitsWithoutVelocity + 3 * VelocityAxisBits;

        public static float PositionMinimumMeters => DequantizePositionAxis(PositionMinimumUnits);

        public static float PositionMaximumMeters => DequantizePositionAxis(PositionMaximumUnits);

        public static float VelocityMinimumPerSecond => DequantizeVelocityAxis(VelocityMinimumUnits);

        public static float VelocityMaximumPerSecond => DequantizeVelocityAxis(VelocityMaximumUnits);

        /// <summary>Rounds to the nearest 1/512 m and clamps to the range. NaN maps to zero.</summary>
        public static int QuantizePositionAxis(float meters) =>
            QuantizeAxis(meters, PositionUnitsPerMeter, PositionMinimumUnits, PositionMaximumUnits);

        /// <summary>Every grid value is exact in a float: at most 13 integer and 9 fraction bits.</summary>
        public static float DequantizePositionAxis(int units)
        {
            RequireRange(units, PositionMinimumUnits, PositionMaximumUnits, nameof(units));
            return (float)(units / (double)PositionUnitsPerMeter);
        }

        public static NetQuantizedPosition QuantizePosition(NetVector3 position) =>
            new NetQuantizedPosition(
                QuantizePositionAxis(position.X),
                QuantizePositionAxis(position.Y),
                QuantizePositionAxis(position.Z));

        public static NetVector3 DequantizePosition(NetQuantizedPosition position) =>
            new NetVector3(
                DequantizePositionAxis(position.X),
                DequantizePositionAxis(position.Y),
                DequantizePositionAxis(position.Z));

        /// <summary>Rounds to the nearest 1/16 unit per second and clamps to the range. NaN maps to zero.</summary>
        public static int QuantizeVelocityAxis(float perSecond) =>
            QuantizeAxis(perSecond, VelocityUnitsPerUnitPerSecond, VelocityMinimumUnits, VelocityMaximumUnits);

        public static float DequantizeVelocityAxis(int units)
        {
            RequireRange(units, VelocityMinimumUnits, VelocityMaximumUnits, nameof(units));
            return (float)(units / (double)VelocityUnitsPerUnitPerSecond);
        }

        public static NetQuantizedVelocity QuantizeVelocity(NetVector3 velocity) =>
            new NetQuantizedVelocity(
                QuantizeVelocityAxis(velocity.X),
                QuantizeVelocityAxis(velocity.Y),
                QuantizeVelocityAxis(velocity.Z));

        public static NetVector3 DequantizeVelocity(NetQuantizedVelocity velocity) =>
            new NetVector3(
                DequantizeVelocityAxis(velocity.X),
                DequantizeVelocityAxis(velocity.Y),
                DequantizeVelocityAxis(velocity.Z));

        /// <summary>
        /// Smallest-three: two bits name the largest component, made non-negative by negating the whole quaternion,
        /// and the other three follow in index order as 10-bit codes. A zero-length or non-finite input encodes the
        /// identity.
        /// </summary>
        public static uint QuantizeRotation(NetQuaternion rotation)
        {
            if (!rotation.IsFinite) return IdentityRotationBits;
            double x = rotation.X, y = rotation.Y, z = rotation.Z, w = rotation.W;
            var normSquared = ((x * x + y * y) + z * z) + w * w;
            if (!(normSquared > 0.0)) return IdentityRotationBits;
            var norm = Math.Sqrt(normSquared);
            x /= norm;
            y /= norm;
            z /= norm;
            w /= norm;

            // Ties go to the lowest index.
            var largest = 0;
            var magnitude = Math.Abs(x);
            if (Math.Abs(y) > magnitude) { largest = 1; magnitude = Math.Abs(y); }
            if (Math.Abs(z) > magnitude) { largest = 2; magnitude = Math.Abs(z); }
            if (Math.Abs(w) > magnitude) largest = 3;
            var largestValue = largest == 0 ? x : largest == 1 ? y : largest == 2 ? z : w;
            var sign = largestValue < 0.0 ? -1.0 : 1.0;

            var packed = (uint)largest;
            var shift = 2;
            for (var index = 0; index < 4; index++)
            {
                if (index == largest) continue;
                var component = index == 0 ? x : index == 1 ? y : index == 2 ? z : w;
                packed |= EncodeComponent(sign * component) << shift;
                shift += RotationComponentBits;
            }

            return packed;
        }

        /// <summary>Decodes smallest-three bits. The never-written code 1023 decodes as 1022.</summary>
        public static NetQuaternion DequantizeRotation(uint packed)
        {
            var largest = (int)(packed & 3u);
            var a = DecodeComponent((packed >> 2) & 1023u);
            var b = DecodeComponent((packed >> 12) & 1023u);
            var c = DecodeComponent((packed >> 22) & 1023u);
            var big = Math.Sqrt(Math.Max(0.0, 1.0 - ((a * a + b * b) + c * c)));
            switch (largest)
            {
                case 0: return new NetQuaternion((float)big, (float)a, (float)b, (float)c);
                case 1: return new NetQuaternion((float)a, (float)big, (float)b, (float)c);
                case 2: return new NetQuaternion((float)a, (float)b, (float)big, (float)c);
                default: return new NetQuaternion((float)a, (float)b, (float)c, (float)big);
            }
        }

        public static void WritePosition(NetBitWriter writer, NetQuantizedPosition position)
        {
            Require(writer, 3 * PositionAxisBits);
            writer.WriteBits((uint)(position.X - PositionMinimumUnits), PositionAxisBits);
            writer.WriteBits((uint)(position.Y - PositionMinimumUnits), PositionAxisBits);
            writer.WriteBits((uint)(position.Z - PositionMinimumUnits), PositionAxisBits);
        }

        public static bool TryReadPosition(NetBitReader reader, out NetQuantizedPosition position)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            position = default;
            if (reader.RemainingBits < 3 * PositionAxisBits) return false;
            reader.TryReadBits(PositionAxisBits, out var x);
            reader.TryReadBits(PositionAxisBits, out var y);
            reader.TryReadBits(PositionAxisBits, out var z);
            position = new NetQuantizedPosition(
                (int)x + PositionMinimumUnits,
                (int)y + PositionMinimumUnits,
                (int)z + PositionMinimumUnits);
            return true;
        }

        public static void WriteVelocity(NetBitWriter writer, NetQuantizedVelocity velocity)
        {
            Require(writer, 3 * VelocityAxisBits);
            writer.WriteBits((uint)(velocity.X - VelocityMinimumUnits), VelocityAxisBits);
            writer.WriteBits((uint)(velocity.Y - VelocityMinimumUnits), VelocityAxisBits);
            writer.WriteBits((uint)(velocity.Z - VelocityMinimumUnits), VelocityAxisBits);
        }

        public static bool TryReadVelocity(NetBitReader reader, out NetQuantizedVelocity velocity)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            velocity = default;
            if (reader.RemainingBits < 3 * VelocityAxisBits) return false;
            reader.TryReadBits(VelocityAxisBits, out var x);
            reader.TryReadBits(VelocityAxisBits, out var y);
            reader.TryReadBits(VelocityAxisBits, out var z);
            velocity = new NetQuantizedVelocity(
                (int)x + VelocityMinimumUnits,
                (int)y + VelocityMinimumUnits,
                (int)z + VelocityMinimumUnits);
            return true;
        }

        /// <summary>
        /// Writes position (66 bits), rotation (32), a velocity flag (1) and, when present, velocity (33): 99 or 132
        /// bits. Nothing is written when it does not fit.
        /// </summary>
        public static void WriteTransform(
            NetBitWriter writer,
            NetQuantizedPosition position,
            uint rotation,
            NetQuantizedVelocity? velocity)
        {
            Require(writer, velocity.HasValue ? TransformBitsWithVelocity : TransformBitsWithoutVelocity);
            WritePosition(writer, position);
            writer.WriteBits(rotation, RotationBits);
            writer.WriteBool(velocity.HasValue);
            if (velocity.HasValue) WriteVelocity(writer, velocity.Value);
        }

        /// <summary>Reads a transform record; on failure the reader is left where it started.</summary>
        public static bool TryReadTransform(
            NetBitReader reader,
            out NetQuantizedPosition position,
            out uint rotation,
            out NetQuantizedVelocity? velocity)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            var start = reader.Position;
            rotation = 0;
            velocity = null;
            if (!TryReadPosition(reader, out position) ||
                !reader.TryReadBits(RotationBits, out rotation) ||
                !reader.TryReadBool(out var hasVelocity))
            {
                reader.Seek(start);
                position = default;
                rotation = 0;
                return false;
            }

            if (!hasVelocity) return true;
            if (!TryReadVelocity(reader, out var read))
            {
                reader.Seek(start);
                position = default;
                rotation = 0;
                return false;
            }

            velocity = read;
            return true;
        }

        /// <summary>
        /// Rounds half away from zero exactly. Math.Round is avoided because older runtimes misround values just
        /// below one half.
        /// </summary>
        public static double RoundHalfAwayFromZero(double value)
        {
            var floor = Math.Floor(value);
            var fraction = value - floor;
            if (fraction > 0.5) return floor + 1.0;
            if (fraction < 0.5) return floor;
            return value > 0.0 ? floor + 1.0 : floor;
        }

        internal static void RequireRange(int value, int minimum, int maximum, string parameterName)
        {
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(parameterName);
        }

        private const double Sqrt2 = 1.4142135623730951;

        private static int QuantizeAxis(float value, int unitsPerWhole, int minimum, int maximum)
        {
            if (float.IsNaN(value)) return 0;
            if (float.IsPositiveInfinity(value)) return maximum;
            if (float.IsNegativeInfinity(value)) return minimum;
            var rounded = RoundHalfAwayFromZero((double)value * unitsPerWhole);
            if (rounded < minimum) return minimum;
            if (rounded > maximum) return maximum;
            return (int)rounded;
        }

        private static uint EncodeComponent(double component)
        {
            var scaled = component * Sqrt2;
            if (scaled < -1.0) scaled = -1.0;
            else if (scaled > 1.0) scaled = 1.0;
            return (uint)(RoundHalfAwayFromZero(scaled * RotationHalfRange) + RotationHalfRange);
        }

        private static double DecodeComponent(uint code)
        {
            var clamped = Math.Min(code, 2u * RotationHalfRange);
            return ((double)clamped - RotationHalfRange) / RotationHalfRange / Sqrt2;
        }

        private static void Require(NetBitWriter writer, int bits)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (writer.RemainingBits < bits) throw new InvalidOperationException("The bit writer is full.");
        }
    }
}
