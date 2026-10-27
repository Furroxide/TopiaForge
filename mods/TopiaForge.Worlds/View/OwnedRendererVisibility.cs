using System;
using System.Collections.Generic;

namespace TopiaForge.Worlds
{
    // Renderer state ownership is independent of Unity, including overlapping parent/child hides.
    internal sealed class OwnedRendererVisibility<T> : IDisposable where T : class
    {
        private sealed class Entry { internal bool Original; internal int Count; }
        private readonly Dictionary<T, Entry> entries = new Dictionary<T, Entry>();
        private readonly Func<T, bool> alive;
        private readonly Func<T, bool> read;
        private readonly Action<T, bool> write;
        internal OwnedRendererVisibility(Func<T, bool> alive, Func<T, bool> read, Action<T, bool> write)
        { this.alive = alive; this.read = read; this.write = write; }
        internal IDisposable Hide(IEnumerable<T> targets)
        {
            var held = new List<T>();
            try
            {
                foreach (var target in targets)
                {
                    if (!alive(target) || held.Contains(target)) continue;
                    if (!entries.TryGetValue(target, out var entry))
                    {
                        entry = new Entry { Original = read(target) };
                        entries.Add(target, entry);
                        try { write(target, false); }
                        catch { entries.Remove(target); throw; }
                    }
                    entry.Count++; held.Add(target);
                }
                return new Hold(() => Release(held));
            }
            catch { Release(held); throw; }
        }
        private void Release(IEnumerable<T> held)
        {
            var failed = false;
            foreach (var target in held)
            {
                if (!entries.TryGetValue(target, out var entry) || --entry.Count > 0) continue;
                entries.Remove(target);
                try { Restore(target, entry); } catch { failed = true; }
            }
            if (failed) throw new InvalidOperationException("A renderer could not be restored.");
        }
        private void Restore(T target, Entry entry)
        {
            if (alive(target) && read(target) == false) write(target, entry.Original);
        }
        public void Dispose()
        {
            var failed = false;
            foreach (var entry in entries)
                try { Restore(entry.Key, entry.Value); } catch { failed = true; }
            entries.Clear();
            if (failed) throw new InvalidOperationException("A renderer could not be restored.");
        }
        private sealed class Hold : IDisposable
        {
            private Action? end;
            internal Hold(Action end) { this.end = end; }
            public void Dispose() { var action = end; end = null; action?.Invoke(); }
        }
    }
}
