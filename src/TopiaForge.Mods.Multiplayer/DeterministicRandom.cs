using System;
using System.Text;

namespace TopiaForge.Mods
{
    /// <summary>
    /// A deterministic pseudo-random generator for multiplayer simulation code: xoshiro256** seeded through
    /// SplitMix64. Every peer that seeds it identically draws identical values, so a predicted command handler
    /// agrees with the server's canonical run. Inside a command handler, create one with
    /// <see cref="MultiplayerCommandContext.CreateRandom"/>; never seed it from the tick or the wall clock, which
    /// differ between prediction and confirmation. The output sequence is frozen for multiplayer protocol major
    /// version 1. Instances are not thread-safe.
    /// </summary>
    public sealed class DeterministicRandom
    {
        private ulong s0;
        private ulong s1;
        private ulong s2;
        private ulong s3;

        /// <summary>Creates a generator whose state is expanded from <paramref name="seed"/> with SplitMix64.</summary>
        public DeterministicRandom(ulong seed)
        {
            var state = seed;
            s0 = SplitMix64(ref state);
            s1 = SplitMix64(ref state);
            s2 = SplitMix64(ref state);
            s3 = SplitMix64(ref state);
        }

        /// <summary>
        /// Creates the generator for one command. It mixes the session seed, the authenticated sender, the
        /// sender-scoped command sequence, and a caller-chosen <paramref name="salt"/> that separates independent
        /// draws inside one handler, so two players' simultaneous commands never share a stream.
        /// </summary>
        public static DeterministicRandom ForCommand(
            SessionSeed seed,
            ParticipantId sender,
            ulong sequence,
            ulong salt = 0)
        {
            if (!sender.IsValid)
            {
                throw new ArgumentException("A command random stream requires a valid sender identity.", nameof(sender));
            }

            var mixed = Mix(seed.Value);
            mixed = Mix(mixed ^ Fnv1a64(sender.Value));
            mixed = Mix(mixed ^ sequence);
            mixed = Mix(mixed ^ salt);
            return new DeterministicRandom(mixed);
        }

        /// <summary>Returns the next 64 random bits.</summary>
        public ulong NextUInt64()
        {
            unchecked
            {
                var result = RotateLeft(s1 * 5, 7) * 9;
                var t = s1 << 17;
                s2 ^= s0;
                s3 ^= s1;
                s1 ^= s2;
                s0 ^= s3;
                s2 ^= t;
                s3 = RotateLeft(s3, 45);
                return result;
            }
        }

        /// <summary>Returns the next 32 random bits, taken from the high half of <see cref="NextUInt64"/>.</summary>
        public uint NextUInt32() => (uint)(NextUInt64() >> 32);

        /// <summary>Returns an unbiased integer in [0, <paramref name="maxExclusive"/>).</summary>
        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            return (int)NextBounded((ulong)maxExclusive);
        }

        /// <summary>Returns an unbiased integer in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
        public int Next(int minInclusive, int maxExclusive)
        {
            if (minInclusive >= maxExclusive) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            var range = (ulong)((long)maxExclusive - minInclusive);
            return (int)(minInclusive + (long)NextBounded(range));
        }

        /// <summary>Returns a double in [0, 1) built from the top 53 random bits.</summary>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>Returns a float in [0, 1) built from the top 24 random bits.</summary>
        public float NextSingle() => (NextUInt64() >> 40) * (1.0f / 16777216.0f);

        /// <summary>Returns true or false with equal probability, from the top random bit.</summary>
        public bool NextBoolean() => (NextUInt64() >> 63) != 0;

        private ulong NextBounded(ulong bound)
        {
            // Reject the low remainder so every residue is equally likely.
            var threshold = unchecked(0UL - bound) % bound;
            while (true)
            {
                var value = NextUInt64();
                if (value >= threshold) return value % bound;
            }
        }

        private static ulong SplitMix64(ref ulong state)
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                var z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        private static ulong Mix(ulong value) => SplitMix64(ref value);

        private static ulong Fnv1a64(string value)
        {
            var hash = 0xCBF29CE484222325UL;
            foreach (var octet in Encoding.UTF8.GetBytes(value))
            {
                hash ^= octet;
                hash = unchecked(hash * 0x100000001B3UL);
            }

            return hash;
        }

        private static ulong RotateLeft(ulong value, int count) => (value << count) | (value >> (64 - count));
    }
}
