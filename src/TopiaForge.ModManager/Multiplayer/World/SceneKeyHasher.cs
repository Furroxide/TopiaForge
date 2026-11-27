using System;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Computes scene object keys exactly as <see cref="SceneObjectKey.Compute"/> does, without building each object's
    /// preimage. FNV-1a runs over the same escaped UTF-8 bytes in the same order, so the state after a path's prefix
    /// serves every object below it: objects that share ancestors hash only what differs, and nothing is allocated once
    /// the buffers have grown. Each part is encoded on its own, which matches encoding the whole preimage because every
    /// part is followed by an ASCII separator, so no surrogate pair spans two parts. Main thread only.
    /// </summary>
    internal sealed class SceneKeyHasher
    {
        private char[] chars = new char[128];
        private byte[] bytes = new byte[512];

        /// <summary>Gets the state after a scene's name and the separator that follows it.</summary>
        internal ulong Scene(string sceneName)
        {
            if (sceneName == null) throw new ArgumentNullException(nameof(sceneName));
            return Mix(Append(NetHash.Fnv1a64OffsetBasis, sceneName), 0);
        }

        /// <summary>
        /// Gets the state after one more path segment, <c>name#index</c>, which a slash joins to the path before it unless
        /// it is the path's first.
        /// </summary>
        internal ulong Segment(ulong state, string name, int siblingIndex, bool first)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (siblingIndex < 0) throw new ArgumentOutOfRangeException(nameof(siblingIndex));
            if (!first) state = Mix(state, (byte)'/');
            state = Mix(Append(state, name), (byte)'#');
            return AppendDecimal(state, siblingIndex);
        }

        /// <summary>Gets the key of the object whose path ends in <paramref name="pathState"/>, keyed by <paramref name="component"/>.</summary>
        internal SceneObjectKey Finish(ulong pathState, string component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            return new SceneObjectKey(Append(Mix(pathState, 0), component));
        }

        /// <summary>Hashes <paramref name="text"/> escaped as the key's preimage escapes it, in UTF-8 with lone surrogates as U+FFFD.</summary>
        private ulong Append(ulong state, string text)
        {
            if (chars.Length < 2 * text.Length) chars = new char[Math.Max(2 * text.Length, 2 * chars.Length)];
            var count = 0;
            foreach (var character in text)
            {
                switch (character)
                {
                    case '\\':
                    case '/':
                    case '#':
                        chars[count++] = '\\';
                        chars[count++] = character;
                        break;
                    case '\0':
                        chars[count++] = '\\';
                        chars[count++] = '0';
                        break;
                    default:
                        chars[count++] = character;
                        break;
                }
            }

            var needed = NetHash.Utf8.GetMaxByteCount(count);
            if (bytes.Length < needed) bytes = new byte[Math.Max(needed, 2 * bytes.Length)];
            var length = NetHash.Utf8.GetBytes(chars, 0, count, bytes, 0);
            for (var index = 0; index < length; index++) state = Mix(state, bytes[index]);
            return state;
        }

        /// <summary>Hashes the invariant decimal digits of a non-negative number, most significant first.</summary>
        private static ulong AppendDecimal(ulong state, int value)
        {
            var divisor = 1L;
            while (value / divisor >= 10) divisor *= 10;
            for (; divisor > 0; divisor /= 10) state = Mix(state, (byte)('0' + value / divisor % 10));
            return state;
        }

        private static ulong Mix(ulong hash, byte value) => unchecked((hash ^ value) * NetHash.Fnv1a64Prime);
    }
}
