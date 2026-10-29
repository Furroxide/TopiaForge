using System;
using System.Collections.Generic;

namespace TopiaForge.Worlds
{
    // Restoring a preset must not replace an environment installed by the game or another provider.
    internal sealed class OwnedEnvironment<T> where T : class
    {
        private readonly Func<IReadOnlyList<T>> read;
        private readonly Action<string> apply;
        private readonly string original;
        private IReadOnlyList<T>? written;
        private IReadOnlyList<T> retiring = Array.Empty<T>();
        private bool yielded;
        internal OwnedEnvironment(string original, Func<IReadOnlyList<T>> read, Action<string> apply)
        { this.original = original; this.read = read; this.apply = apply; }
        internal void Set(string? key)
        {
            if (written != null && !StillOwned(read())) { written = null; yielded = true; }
            if (yielded) return;
            if (key == null && written == null) return;
            var before = read();
            try
            {
                apply(key ?? original);
                Capture(before, read());
                if (key == null) { written = null; retiring = Array.Empty<T>(); }
            }
            catch
            {
                // A native applier may throw after replacing its instance; permit the caller's compensation.
                Capture(before, read());
                throw;
            }
        }
        private void Capture(IReadOnlyList<T> before, IReadOnlyList<T> after)
        {
            var added = new List<T>();
            foreach (var value in after) if (!Contains(before, value)) added.Add(value);
            // Unity destruction may complete at frame end. Old instances may disappear after Apply returns.
            written = added.Count == 0 ? after : added;
            retiring = before;
        }
        private bool StillOwned(IReadOnlyList<T> current)
        {
            foreach (var value in written!) if (!Contains(current, value)) return false;
            foreach (var value in current) if (!Contains(written!, value) && !Contains(retiring, value)) return false;
            return true;
        }
        private static bool Contains(IReadOnlyList<T> values, T target)
        {
            foreach (var value in values) if (ReferenceEquals(value, target)) return true;
            return false;
        }
    }
}
