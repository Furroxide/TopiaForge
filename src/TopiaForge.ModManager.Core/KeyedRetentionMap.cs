using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Plans a keyed retained-mode refresh without mutating the previous snapshot.
    /// A key can be claimed exactly once, and only claimed keys can enter the next snapshot.
    /// </summary>
    internal sealed class KeyedRetentionMap<T> where T : class
    {
        private readonly IReadOnlyDictionary<string, T> previous;
        private readonly Dictionary<string, T> next = new Dictionary<string, T>(StringComparer.Ordinal);
        private readonly HashSet<string> claimed = new HashSet<string>(StringComparer.Ordinal);

        public KeyedRetentionMap(IReadOnlyDictionary<string, T> previous)
        {
            this.previous = previous ?? throw new ArgumentNullException(nameof(previous));
        }

        public bool TryClaimPrevious(string key, out T? value)
        {
            ValidateKey(key);
            if (!claimed.Add(key))
            {
                throw new InvalidOperationException("Retention key '" + key + "' was claimed more than once.");
            }

            return previous.TryGetValue(key, out value);
        }

        public void SetClaimed(string key, T value)
        {
            ValidateKey(key);
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (!claimed.Contains(key))
            {
                throw new InvalidOperationException("Retention key '" + key + "' has not been claimed.");
            }

            if (!next.TryAdd(key, value))
            {
                throw new InvalidOperationException("Retention key '" + key + "' was assigned more than once.");
            }
        }

        public IReadOnlyDictionary<string, T> Next => next;

        public IEnumerable<string> StaleKeys
        {
            get
            {
                foreach (var key in previous.Keys)
                {
                    if (!next.ContainsKey(key)) yield return key;
                }
            }
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A retention key is required.", nameof(key));
        }
    }
}
