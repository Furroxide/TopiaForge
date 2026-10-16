using System;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>The bit stream and transform quantization, with their tested error bounds (WS3.5).</summary>
    internal static class MultiplayerQuantizationTests
    {
        public static void Run()
        {
            BitStreamPacksLeastSignificantBitFirst();
            BitStreamRefusesBadWritesAndReads();
            RoundingIsExactAndPortable();
            PositionsStayWithinHalfAStep();
            PositionsClampAtTheRange();
            VelocitiesStayWithinHalfAStep();
            RotationsStayWithinTheStatedBound();
            RotationEdgeCasesAreDefined();
            TransformsRoundTripAtomically();
            Console.WriteLine("MultiplayerQuantizationTests passed.");
        }

        private static void BitStreamPacksLeastSignificantBitFirst()
        {
            var writer = new NetBitWriter(8);
            writer.WriteBits(0b101, 3);
            writer.WriteBits(0b11111, 5);
            writer.WriteBits(0x3, 2);
            Equal(10, writer.BitLength, "bit length");
            Equal(2, writer.ByteLength, "byte length");
            Equal("fd03", HexOf(writer.ToArray()), "three bits then five fill byte 0 low-first; the rest starts byte 1");

            writer.Reset();
            Equal(0, writer.BitLength, "reset");
            writer.WriteBits(0xDEADBEEF, 32);
            writer.WriteBool(true);
            Equal("efbeadde01", HexOf(writer.ToArray()), "a 32-bit value is little-endian");

            var reader = new NetBitReader(writer.AsSegment());
            True(reader.TryReadBits(32, out var word) && word == 0xDEADBEEF, "read back 32 bits");
            True(reader.TryReadBool(out var flag) && flag, "read back a flag");
            True(reader.IsAtCanonicalEnd, "seven zero padding bits are a canonical end");

            var random = new SeededRandom(7);
            var widths = new int[400];
            var values = new uint[400];
            var stream = new NetBitWriter(4096);
            for (var index = 0; index < widths.Length; index++)
            {
                widths[index] = random.NextInt(1, 33);
                values[index] = widths[index] == 32 ? (uint)random.NextUInt64() : (uint)(random.NextUInt64() & ((1UL << widths[index]) - 1));
                stream.WriteBits(values[index], widths[index]);
            }

            var back = new NetBitReader(stream.AsSegment());
            for (var index = 0; index < widths.Length; index++)
            {
                True(back.TryReadBits(widths[index], out var value) && value == values[index], "random width round trip " + index);
            }

            True(back.IsAtCanonicalEnd, "the random stream ends canonically");
        }

        private static void BitStreamRefusesBadWritesAndReads()
        {
            var writer = new NetBitWriter(1);
            Throws<ArgumentOutOfRangeException>(() => writer.WriteBits(4, 2), "a value wider than its count");
            Throws<ArgumentOutOfRangeException>(() => writer.WriteBits(0, 0), "zero bits");
            Throws<ArgumentOutOfRangeException>(() => writer.WriteBits(0, 33), "33 bits");
            writer.WriteBits(0x7F, 7);
            Throws<InvalidOperationException>(() => writer.WriteBits(0, 2), "past capacity");
            Equal(7, writer.BitLength, "a refused write writes nothing");
            Throws<ArgumentOutOfRangeException>(() => new NetBitWriter(0), "zero capacity");

            var reader = new NetBitReader(new ArraySegment<byte>(new byte[] { 0xFF }));
            True(!reader.TryReadBits(9, out _), "an overrun fails");
            Equal(0, reader.Position, "a failed read consumes nothing");
            True(reader.TryReadBits(3, out var three) && three == 7, "read three ones");
            True(!reader.IsAtCanonicalEnd, "non-zero padding is not a canonical end");
            reader.Seek(0);
            True(reader.TryReadBits(8, out var all) && all == 0xFF && reader.IsAtCanonicalEnd, "seek back and read all");
            Throws<ArgumentOutOfRangeException>(() => reader.Seek(9), "seek past the end");
            Throws<ArgumentOutOfRangeException>(() => reader.TryReadBits(33, out _), "read 33 bits");
            True(new NetBitReader(new ArraySegment<byte>(new byte[] { 0x00, 0x00 })).IsAtCanonicalEnd == false, "a whole spare byte is not an end");
        }

        private static void RoundingIsExactAndPortable()
        {
            Equal(0.0, NetQuantization.RoundHalfAwayFromZero(0.49999999999999994), "just below a half rounds down");
            Equal(0.0, NetQuantization.RoundHalfAwayFromZero(-0.49999999999999994), "just above minus a half rounds to zero");
            Equal(-1.0, NetQuantization.RoundHalfAwayFromZero(-0.5000000000000001), "just below minus a half");
            Equal(-1.0, NetQuantization.RoundHalfAwayFromZero(-0.9999999999999999), "just above minus one");
            Equal(-1.0, NetQuantization.RoundHalfAwayFromZero(-1.4999999999999998), "just above minus one and a half");
            Equal(-4503599627370496.0, NetQuantization.RoundHalfAwayFromZero(-4503599627370495.5), "a half below -2^52");
            Equal(1.0, NetQuantization.RoundHalfAwayFromZero(0.5), "a half rounds up");
            Equal(-1.0, NetQuantization.RoundHalfAwayFromZero(-0.5), "a negative half rounds away");
            Equal(3.0, NetQuantization.RoundHalfAwayFromZero(2.5), "2.5");
            Equal(-3.0, NetQuantization.RoundHalfAwayFromZero(-2.5), "-2.5");
            Equal(-2.0, NetQuantization.RoundHalfAwayFromZero(-2.4999999999999996), "just above -2.5");
            Equal(1e300, NetQuantization.RoundHalfAwayFromZero(1e300), "large values are integers already");
            Equal(4503599627370497.0, NetQuantization.RoundHalfAwayFromZero(4503599627370497.0), "2^52 + 1");
        }

        private static void PositionsStayWithinHalfAStep()
        {
            var random = new SeededRandom(11);
            var worst = 0.0;
            for (var index = 0; index < 200_000; index++)
            {
                var meters = (float)random.NextDouble(-4095.9, 4095.9);
                var units = NetQuantization.QuantizePositionAxis(meters);
                var error = Math.Abs(NetQuantization.DequantizePositionAxis(units) - (double)meters);
                worst = Math.Max(worst, error);
            }

            True(worst <= NetQuantization.PositionMaximumErrorMeters, "position error " + worst + " exceeds 1/1024 m");
            Equal(1, NetQuantization.QuantizePositionAxis(1f / 1024f), "half a step rounds away from zero");
            Equal(-1, NetQuantization.QuantizePositionAxis(-1f / 1024f), "negative half a step rounds away from zero");
            Equal(0, NetQuantization.QuantizePositionAxis(0.00097656f), "just below half a step rounds to zero");
            for (var units = -5000; units <= 5000; units += 7)
            {
                var meters = NetQuantization.DequantizePositionAxis(units);
                Equal(units, NetQuantization.QuantizePositionAxis(meters), "grid values round trip exactly");
            }

            var position = new NetVector3(12.345f, -0.5f, 1000f);
            var quantized = NetQuantization.QuantizePosition(position);
            Equal(new NetQuantizedPosition(6321, -256, 512000), quantized, "vector quantization");
            Equal(new NetVector3(12.3457031f, -0.5f, 1000f), NetQuantization.DequantizePosition(quantized), "vector dequantization");
        }

        private static void PositionsClampAtTheRange()
        {
            Equal(-4096f, NetQuantization.PositionMinimumMeters, "documented minimum");
            Equal(4095.998046875f, NetQuantization.PositionMaximumMeters, "documented maximum");
            Equal(NetQuantization.PositionMaximumUnits, NetQuantization.QuantizePositionAxis(4095.999f), "rounds into the clamp");
            Equal(NetQuantization.PositionMaximumUnits, NetQuantization.QuantizePositionAxis(5000f), "clamps high");
            Equal(NetQuantization.PositionMinimumUnits, NetQuantization.QuantizePositionAxis(-4096.4f), "clamps low");
            Equal(NetQuantization.PositionMaximumUnits, NetQuantization.QuantizePositionAxis(float.MaxValue), "float max clamps");
            Equal(NetQuantization.PositionMaximumUnits, NetQuantization.QuantizePositionAxis(float.PositiveInfinity), "+infinity clamps");
            Equal(NetQuantization.PositionMinimumUnits, NetQuantization.QuantizePositionAxis(float.NegativeInfinity), "-infinity clamps");
            Equal(0, NetQuantization.QuantizePositionAxis(float.NaN), "NaN encodes as zero");
            Throws<ArgumentOutOfRangeException>(() => new NetQuantizedPosition(NetQuantization.PositionMaximumUnits + 1, 0, 0), "units range");
            Throws<ArgumentOutOfRangeException>(() => NetQuantization.DequantizePositionAxis(int.MinValue), "dequantize range");
        }

        private static void VelocitiesStayWithinHalfAStep()
        {
            var random = new SeededRandom(13);
            var worst = 0.0;
            for (var index = 0; index < 100_000; index++)
            {
                var speed = (float)random.NextDouble(-63.9, 63.9);
                var error = Math.Abs(NetQuantization.DequantizeVelocityAxis(NetQuantization.QuantizeVelocityAxis(speed)) - (double)speed);
                worst = Math.Max(worst, error);
            }

            True(worst <= NetQuantization.VelocityMaximumErrorPerSecond, "velocity error " + worst + " exceeds 1/32");
            Equal(-64f, NetQuantization.VelocityMinimumPerSecond, "velocity minimum");
            Equal(63.9375f, NetQuantization.VelocityMaximumPerSecond, "velocity maximum");
            Equal(1023, NetQuantization.QuantizeVelocityAxis(250f), "throws faster than the range clamp");
            Equal(-1024, NetQuantization.QuantizeVelocityAxis(-64.03f), "rounds into the low clamp");
            Equal(0, NetQuantization.QuantizeVelocityAxis(float.NaN), "NaN velocity");
            var velocity = NetQuantization.QuantizeVelocity(new NetVector3(22f, -9.81f, 0.03125f));
            Equal(new NetQuantizedVelocity(352, -157, 1), velocity, "vector velocity");
        }

        private static void RotationsStayWithinTheStatedBound()
        {
            var random = new SeededRandom(17);
            var worst = 0.0;
            for (var index = 0; index < 200_000; index++)
            {
                var rotation = RandomRotation(random);
                var decoded = NetQuantization.DequantizeRotation(NetQuantization.QuantizeRotation(rotation));
                worst = Math.Max(worst, AngleDegrees(rotation, decoded));
                Near(1.0, Norm(decoded), 1e-6, "decoded rotations are unit length");
            }

            True(worst <= NetQuantization.RotationMaximumErrorDegrees, "rotation error " + worst + " exceeds the stated bound");

            // Three components exactly between two codes, all rounding the same way, is close to the true worst case,
            // so the bound is tight rather than loose.
            var half = (360.5 / NetQuantization.RotationHalfRange) / Math.Sqrt(2.0);
            var adversarial = new NetQuaternion((float)half, (float)half, (float)half, (float)Math.Sqrt(1.0 - 3.0 * half * half));
            var adversarialError = AngleDegrees(adversarial, NetQuantization.DequantizeRotation(NetQuantization.QuantizeRotation(adversarial)));
            True(adversarialError > 0.27 && adversarialError <= NetQuantization.RotationMaximumErrorDegrees, "adversarial error " + adversarialError);
        }

        private static void RotationEdgeCasesAreDefined()
        {
            Equal(NetQuantization.IdentityRotationBits, NetQuantization.QuantizeRotation(NetQuaternion.Identity), "identity bits");
            Equal(NetQuaternion.Identity, NetQuantization.DequantizeRotation(NetQuantization.IdentityRotationBits), "identity is exact");
            Equal(NetQuantization.IdentityRotationBits, NetQuantization.QuantizeRotation(new NetQuaternion(0f, 0f, 0f, 0f)), "zero length");
            Equal(NetQuantization.IdentityRotationBits, NetQuantization.QuantizeRotation(new NetQuaternion(float.NaN, 0f, 0f, 1f)), "NaN");
            Equal(NetQuantization.IdentityRotationBits, NetQuantization.QuantizeRotation(new NetQuaternion(0f, 0f, 0f, -1f)), "-identity");

            var rotation = new NetQuaternion(0.1f, -0.7f, 0.3f, 0.6403124f);
            var negated = new NetQuaternion(-0.1f, 0.7f, -0.3f, -0.6403124f);
            Equal(NetQuantization.QuantizeRotation(rotation), NetQuantization.QuantizeRotation(negated), "q and -q are one rotation");
            var scaled = new NetQuaternion(0.2f, -1.4f, 0.6f, 1.2806248f);
            Equal(NetQuantization.QuantizeRotation(rotation), NetQuantization.QuantizeRotation(scaled), "inputs are normalised");

            var tied = NetQuantization.QuantizeRotation(new NetQuaternion(0.5f, 0.5f, 0.5f, 0.5f));
            Equal(0u, tied & 3u, "ties pick the lowest index");
            var withCode1023 = (3u) | (1023u << 2) | (511u << 12) | (511u << 22);
            var withCode1022 = (3u) | (1022u << 2) | (511u << 12) | (511u << 22);
            Equal(NetQuantization.DequantizeRotation(withCode1022), NetQuantization.DequantizeRotation(withCode1023), "1023 decodes as 1022");
        }

        private static void TransformsRoundTripAtomically()
        {
            var position = NetQuantization.QuantizePosition(new NetVector3(-12.5f, 3.25f, 900.001f));
            var rotation = NetQuantization.QuantizeRotation(new NetQuaternion(0f, 0.38268343f, 0f, 0.9238795f));
            var velocity = NetQuantization.QuantizeVelocity(new NetVector3(1.5f, -2f, 30f));
            var writer = new NetBitWriter(64);
            NetQuantization.WriteTransform(writer, position, rotation, velocity);
            Equal(NetQuantization.TransformBitsWithVelocity, writer.BitLength, "132 bits with velocity");
            Equal(17, writer.ByteLength, "17 bytes with velocity");
            NetQuantization.WriteTransform(writer, position, rotation, null);
            Equal(NetQuantization.TransformBitsWithVelocity + NetQuantization.TransformBitsWithoutVelocity, writer.BitLength, "99 more bits");

            var reader = new NetBitReader(writer.AsSegment());
            True(NetQuantization.TryReadTransform(reader, out var readPosition, out var readRotation, out var readVelocity), "first record");
            Equal(position, readPosition, "position");
            Equal(rotation, readRotation, "rotation");
            True(readVelocity.HasValue && readVelocity.Value.Equals(velocity), "velocity");
            True(NetQuantization.TryReadTransform(reader, out _, out _, out var noVelocity) && !noVelocity.HasValue, "second record");
            True(reader.IsAtCanonicalEnd, "two records end canonically");

            var truncated = new NetBitWriter(64);
            NetQuantization.WriteTransform(truncated, position, rotation, velocity);
            var bytes = truncated.ToArray();
            var shortReader = new NetBitReader(new ArraySegment<byte>(bytes, 0, 15));
            True(!NetQuantization.TryReadTransform(shortReader, out _, out _, out _), "a truncated velocity fails");
            Equal(0, shortReader.Position, "the reader is left at the start");

            var full = new NetBitWriter(16);
            Throws<InvalidOperationException>(() => NetQuantization.WriteTransform(full, position, rotation, velocity), "does not fit");
            Equal(0, full.BitLength, "nothing was written");
        }

        private static NetQuaternion RandomRotation(SeededRandom random)
        {
            // Shoemake's uniform rotation; trigonometry is fine here because only the encoder's output is compared.
            var u1 = random.NextDouble();
            var u2 = random.NextDouble() * 2.0 * Math.PI;
            var u3 = random.NextDouble() * 2.0 * Math.PI;
            var a = Math.Sqrt(1.0 - u1);
            var b = Math.Sqrt(u1);
            return new NetQuaternion((float)(a * Math.Sin(u2)), (float)(a * Math.Cos(u2)), (float)(b * Math.Sin(u3)), (float)(b * Math.Cos(u3)));
        }

        private static double Norm(NetQuaternion value) =>
            Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z + (double)value.W * value.W);

        internal static double AngleDegrees(NetQuaternion left, NetQuaternion right)
        {
            var dot = Math.Abs((double)left.X * right.X + (double)left.Y * right.Y + (double)left.Z * right.Z + (double)left.W * right.W);
            dot /= Norm(left) * Norm(right);
            return 2.0 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI;
        }
    }
}
