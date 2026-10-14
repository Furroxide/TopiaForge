using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>64-bit FNV-1a, used for stable keys and content fingerprints. Not a security hash.</summary>
    internal static class NetHash
    {
        public const ulong Fnv1a64OffsetBasis = 0xCBF29CE484222325UL;
        public const ulong Fnv1a64Prime = 0x00000100000001B3UL;

        /// <summary>Encodes lone surrogates as U+FFFD instead of throwing, as the session protocol does.</summary>
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        public static ulong Fnv1a64(byte[] data) => Fnv1a64(data, 0, data?.Length ?? 0);

        public static ulong Fnv1a64(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            var hash = Fnv1a64OffsetBasis;
            for (var index = offset; index < offset + count; index++)
            {
                hash ^= data[index];
                hash = unchecked(hash * Fnv1a64Prime);
            }

            return hash;
        }

        /// <summary>FNV-1a over the UTF-8 bytes of <paramref name="text"/>.</summary>
        public static ulong Fnv1a64(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            return Fnv1a64(Utf8.GetBytes(text));
        }
    }

    /// <summary>One step of a scene object's path from its root: the object's name and its index among siblings.</summary>
    internal readonly struct SceneObjectPathSegment
    {
        public SceneObjectPathSegment(string name, int siblingIndex)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            if (siblingIndex < 0) throw new ArgumentOutOfRangeException(nameof(siblingIndex));
            SiblingIndex = siblingIndex;
        }

        public string Name { get; }

        public int SiblingIndex { get; }
    }

    /// <summary>A scene object as the census sees it: scene, path from the root, and primary component type.</summary>
    internal sealed class SceneObjectDescriptor
    {
        public SceneObjectDescriptor(string scene, IReadOnlyList<SceneObjectPathSegment> path, string primaryComponentType)
        {
            Scene = scene ?? throw new ArgumentNullException(nameof(scene));
            Path = path ?? throw new ArgumentNullException(nameof(path));
            if (path.Count == 0) throw new ArgumentException("A path needs at least the object itself.", nameof(path));
            PrimaryComponentType = primaryComponentType ?? throw new ArgumentNullException(nameof(primaryComponentType));
        }

        public string Scene { get; }

        public IReadOnlyList<SceneObjectPathSegment> Path { get; }

        public string PrimaryComponentType { get; }
    }

    /// <summary>
    /// A stable key for an object every peer loads from the same scene, so host and client can name it without a
    /// spawn message.
    /// </summary>
    internal readonly struct SceneObjectKey : IEquatable<SceneObjectKey>, IComparable<SceneObjectKey>
    {
        public SceneObjectKey(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        /// <summary>
        /// FNV-1a 64 over the UTF-8 bytes of <c>scene NUL path NUL component</c>. The path joins
        /// <c>name#siblingIndex</c> segments, root first, with <c>/</c>. In every field a backslash, slash, hash
        /// or NUL is escaped as <c>\\</c>, <c>\/</c>, <c>\#</c> or <c>\0</c>, so the text is unambiguous.
        /// </summary>
        public static SceneObjectKey Compute(SceneObjectDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            return new SceneObjectKey(NetHash.Fnv1a64(FormatPreimage(descriptor)));
        }

        /// <summary>The exact text whose UTF-8 bytes are hashed, for diagnostics and other implementations.</summary>
        public static string FormatPreimage(SceneObjectDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            var builder = new StringBuilder();
            AppendEscaped(builder, descriptor.Scene);
            builder.Append('\0');
            for (var index = 0; index < descriptor.Path.Count; index++)
            {
                if (index > 0) builder.Append('/');
                var segment = descriptor.Path[index];
                AppendEscaped(builder, segment.Name);
                builder.Append('#').Append(segment.SiblingIndex.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append('\0');
            AppendEscaped(builder, descriptor.PrimaryComponentType);
            return builder.ToString();
        }

        public int CompareTo(SceneObjectKey other) => Value.CompareTo(other.Value);

        public bool Equals(SceneObjectKey other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is SceneObjectKey other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString("x16", CultureInfo.InvariantCulture);

        private static void AppendEscaped(StringBuilder builder, string text)
        {
            foreach (var character in text)
            {
                switch (character)
                {
                    case '\\': builder.Append('\\').Append('\\'); break;
                    case '/': builder.Append('\\').Append('/'); break;
                    case '#': builder.Append('\\').Append('#'); break;
                    case '\0': builder.Append('\\').Append('0'); break;
                    default: builder.Append(character); break;
                }
            }
        }
    }

    /// <summary>
    /// The keys of one scene census. A key that more than one object produces is excluded: none of those objects is
    /// bound, rather than guessing which is which. Unique keys map to the object's index in the census input.
    /// </summary>
    internal sealed class SceneObjectKeyCensus
    {
        private readonly SceneObjectKey[] keys;
        private readonly Dictionary<SceneObjectKey, int> unique;
        private readonly SceneObjectKey[] duplicates;

        private SceneObjectKeyCensus(SceneObjectKey[] keys, Dictionary<SceneObjectKey, int> unique, SceneObjectKey[] duplicates, int excluded)
        {
            this.keys = keys;
            this.unique = unique;
            this.duplicates = duplicates;
            ExcludedObjectCount = excluded;
        }

        public int ObjectCount => keys.Length;

        public int UniqueCount => unique.Count;

        public int ExcludedObjectCount { get; }

        /// <summary>The excluded keys, ascending.</summary>
        public IReadOnlyList<SceneObjectKey> DuplicateKeys => duplicates;

        public static SceneObjectKeyCensus Build(IReadOnlyList<SceneObjectDescriptor> objects)
        {
            if (objects == null) throw new ArgumentNullException(nameof(objects));
            var keys = new SceneObjectKey[objects.Count];
            var counts = new Dictionary<SceneObjectKey, int>();
            for (var index = 0; index < objects.Count; index++)
            {
                keys[index] = SceneObjectKey.Compute(objects[index] ?? throw new ArgumentException("A descriptor is null.", nameof(objects)));
                counts.TryGetValue(keys[index], out var seen);
                counts[keys[index]] = seen + 1;
            }

            var unique = new Dictionary<SceneObjectKey, int>();
            var duplicates = new List<SceneObjectKey>();
            var excluded = 0;
            for (var index = 0; index < keys.Length; index++)
            {
                var occurrences = counts[keys[index]];
                if (occurrences == 1) unique.Add(keys[index], index);
                else excluded++;
            }

            foreach (var pair in counts)
            {
                if (pair.Value > 1) duplicates.Add(pair.Key);
            }

            duplicates.Sort();
            return new SceneObjectKeyCensus(keys, unique, duplicates.ToArray(), excluded);
        }

        public SceneObjectKey KeyAt(int index)
        {
            if (index < 0 || index >= keys.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return keys[index];
        }

        /// <summary>Finds the census object with a key; excluded and unknown keys are not found.</summary>
        public bool TryGetObjectIndex(SceneObjectKey key, out int index) => unique.TryGetValue(key, out index);

        public bool IsExcluded(SceneObjectKey key) => Array.BinarySearch(duplicates, key) >= 0;
    }
}
