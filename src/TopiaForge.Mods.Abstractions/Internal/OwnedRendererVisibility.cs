using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    // Renderer state ownership is independent of Unity, including overlapping parent/child hides.
    internal sealed class OwnedRendererVisibility<T> : IDisposable where T : class
    {
        private sealed class Entry { internal bool Original; internal int Count; }
        // First-party renderers are owned on the frame thread. Overlapping view instances
        // share the original value until the last hold on that renderer is released.
        private static readonly Dictionary<T, Entry> entries = new Dictionary<T, Entry>();
        private readonly HashSet<Hold> holds = new HashSet<Hold>();

        internal static bool TryGetOriginal(T target, out bool enabled)
        {
            if (entries.TryGetValue(target, out var entry))
            {
                enabled = entry.Original;
                return true;
            }
            enabled = false;
            return false;
        }
        private readonly Func<T, bool> alive;
        private readonly Func<T, bool> read;
        private readonly Action<T, bool> write;
        internal OwnedRendererVisibility(Func<T, bool> alive, Func<T, bool> read, Action<T, bool> write)
        { this.alive = alive; this.read = read; this.write = write; }
        private readonly List<DynamicHold> dynamic = new List<DynamicHold>();
        internal IDisposable Hide(Func<IReadOnlyList<T>> select, Func<bool> admitted)
        {
            var hold = new DynamicHold(this, select, admitted);
            dynamic.Add(hold);
            try { hold.Refresh(); return hold; }
            catch { hold.Dispose(); throw; }
        }
        internal void Refresh()
        {
            for (var i = dynamic.Count - 1; i >= 0; i--) dynamic[i].Refresh();
        }
        private sealed class DynamicHold : IDisposable
        {
            private readonly OwnedRendererVisibility<T> owner;
            private readonly Func<IReadOnlyList<T>> select;
            private readonly Func<bool> admitted;
            private readonly Dictionary<T, IDisposable> held = new Dictionary<T, IDisposable>();
            private readonly List<T> removed = new List<T>();
            private bool disposed;
            internal DynamicHold(OwnedRendererVisibility<T> owner, Func<IReadOnlyList<T>> select, Func<bool> admitted)
            { this.owner = owner; this.select = select; this.admitted = admitted; }
            internal void Refresh()
            {
                if (disposed) return;
                // A grace-period disconnect can recover before the outer owner sees a different entity.
                // Release native writes while admission fails, but retain this hold's refresh lifetime.
                if (!admitted()) { ReleaseHeld(); return; }
                var current = select();
                removed.Clear();
                foreach (var pair in held)
                {
                    var found = false;
                    for (var i = 0; i < current.Count; i++)
                        if (ReferenceEquals(pair.Key, current[i])) { found = true; break; }
                    if (!found) removed.Add(pair.Key);
                }
                foreach (var target in removed)
                {
                    var hold = held[target]; held.Remove(target); hold.Dispose();
                }
                for (var i = 0; i < current.Count; i++)
                {
                    var target = current[i];
                    if (owner.alive(target) && !held.ContainsKey(target))
                        held.Add(target, owner.Hide(new[] { target }));
                }
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true; owner.dynamic.Remove(this);
                ReleaseHeld();
            }
            private void ReleaseHeld()
            {
                var failed = false;
                foreach (var hold in held.Values)
                    try { hold.Dispose(); } catch { failed = true; }
                held.Clear();
                if (failed) throw new InvalidOperationException("A renderer subtree could not be restored.");
            }
        }
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
                var hold = new Hold(this, held);
                holds.Add(hold);
                return hold;
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
            while (dynamic.Count > 0)
                try { dynamic[dynamic.Count - 1].Dispose(); } catch { failed = true; }
            foreach (var hold in new List<Hold>(holds))
                try { hold.Dispose(); } catch { failed = true; }
            if (failed) throw new InvalidOperationException("A renderer could not be restored.");
        }
        private sealed class Hold : IDisposable
        {
            private readonly OwnedRendererVisibility<T> owner;
            private readonly List<T> targets;
            internal Hold(OwnedRendererVisibility<T> owner, List<T> targets)
            { this.owner = owner; this.targets = targets; }
            public void Dispose()
            {
                if (owner.holds.Remove(this)) owner.Release(targets);
            }
        }
    }
}
