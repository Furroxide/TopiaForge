using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Assertions shared by the Unity-free replication model tests.</summary>
    internal static class MultiplayerModelAssert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + ": expected " + expected + ", got " + actual + ".");
            }
        }

        public static void Near(double expected, double actual, double tolerance, string message)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException(
                    message + ": expected " + expected.ToString("R", CultureInfo.InvariantCulture) + " within " +
                    tolerance.ToString("R", CultureInfo.InvariantCulture) + ", got " +
                    actual.ToString("R", CultureInfo.InvariantCulture) + ".");
            }
        }

        public static void Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message + ": expected " + typeof(TException).Name + ".");
        }

        public static void Sequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
        {
            var same = expected.Count == actual.Count;
            for (var index = 0; same && index < expected.Count; index++)
            {
                same = EqualityComparer<T>.Default.Equals(expected[index], actual[index]);
            }

            if (!same)
            {
                throw new InvalidOperationException(
                    message + ": expected [" + string.Join(", ", expected) + "], got [" + string.Join(", ", actual) + "].");
            }
        }

        /// <summary>Loads tests/fixtures/multiplayer/replication/<paramref name="name"/>.</summary>
        public static JsonObject LoadFixture(string name)
        {
            var path = Path.Combine(Program.FindRepoRoot(), "tests", "fixtures", "multiplayer", "replication", name);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            True(document["schema"]!.GetValue<int>() == 1, name + " has an unknown schema");
            return document;
        }

        public static float FloatFromHex(string bits) =>
            BitConverter.Int32BitsToSingle(unchecked((int)uint.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

        public static string HexOf(float value) =>
            unchecked((uint)BitConverter.SingleToInt32Bits(value)).ToString("x8", CultureInfo.InvariantCulture);

        public static string HexOf(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>A seeded SplitMix64 stream, so randomized sweeps repeat exactly.</summary>
    internal sealed class SeededRandom
    {
        private ulong state;

        public SeededRandom(ulong seed)
        {
            state = seed;
        }

        public ulong NextUInt64()
        {
            state = unchecked(state + 0x9E3779B97F4A7C15UL);
            var z = state;
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            return z ^ (z >> 31);
        }

        /// <summary>A double in [0, 1).</summary>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

        /// <summary>A double in [minimum, maximum).</summary>
        public double NextDouble(double minimum, double maximum) => minimum + (maximum - minimum) * NextDouble();

        /// <summary>An integer in [minimum, maximum).</summary>
        public int NextInt(int minimum, int maximum) => minimum + (int)(NextUInt64() % (ulong)(maximum - minimum));
    }
}
