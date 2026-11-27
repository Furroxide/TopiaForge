using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using TopiaForge.ModManager.Core.Multiplayer;

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

    /// <summary>
    /// Renders a buffer frame by frame and checks each frame's render advance against the written rule, not the code:
    /// within 10% of the elapsed time while the delay before the frame lay between 60 ms, the 80 ms floor less its dead
    /// band, and 400 ms, the cap plus its dead band; beyond 400 ms, one percent more per millisecond of excess, up to
    /// 100%; below 60 ms, one percent more per millisecond short of it, up to 50%. So playback never runs faster than
    /// twice or slower than half real time.
    /// </summary>
    internal sealed class PlaybackRate
    {
        private long lastNow = -1;
        private long lastRender;

        public int Frames { get; private set; }

        /// <summary>Frames that used more than the normal 10% to correct the delay.</summary>
        public int Corrections { get; private set; }

        /// <summary>Frames that ran faster than 1.1 times real time.</summary>
        public int FastFrames { get; private set; }

        /// <summary>Frames that ran slower than 0.9 times real time.</summary>
        public int SlowFrames { get; private set; }

        public long SlowestAdvance { get; private set; } = long.MaxValue;

        public double SlowestRatio { get; private set; } = double.MaxValue;

        public double FastestRatio { get; private set; }

        public long LargestDelay { get; private set; } = long.MinValue;

        public long SmallestDelay { get; private set; } = long.MaxValue;

        public bool Sample(SnapshotInterpolationBuffer buffer, long now, out InterpolatedTransform result)
        {
            var delayBefore = buffer.CurrentDelayMicros;
            if (!buffer.TrySample(now, out result)) return false;
            Frames++;
            if (lastNow >= 0 && now > lastNow)
            {
                var elapsed = now - lastNow;
                var advance = result.RenderTimeMicros - lastRender;
                var percent = AllowedPercent(delayBefore);
                var allowed = Math.Min(elapsed, SnapshotInterpolationBuffer.DiscontinuityMicros) * percent / 100;
                var ratio = advance / (double)elapsed;
                MultiplayerModelAssert.True(
                    Math.Abs(advance - elapsed) <= allowed,
                    "playback ran at " + ratio.ToString("F3", CultureInfo.InvariantCulture) + " times real time with a delay of " +
                    delayBefore + " us, where " + percent + "% is allowed");
                if (percent > SnapshotInterpolationBuffer.PlaybackSlewPercent) Corrections++;
                if (advance * 10 > elapsed * 11) FastFrames++;
                if (advance * 10 < elapsed * 9) SlowFrames++;
                SlowestAdvance = Math.Min(SlowestAdvance, advance);
                SlowestRatio = Math.Min(SlowestRatio, ratio);
                FastestRatio = Math.Max(FastestRatio, ratio);
            }

            LargestDelay = Math.Max(LargestDelay, buffer.CurrentDelayMicros);
            SmallestDelay = Math.Min(SmallestDelay, buffer.CurrentDelayMicros);
            lastNow = now;
            lastRender = result.RenderTimeMicros;
            return true;
        }

        private static long AllowedPercent(long delay)
        {
            if (delay > 400_000) return Math.Min(100, 10 + (delay - 400_000) / 1_000);
            return delay < 60_000 ? Math.Min(50, 10 + (60_000 - delay) / 1_000) : 10;
        }
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
