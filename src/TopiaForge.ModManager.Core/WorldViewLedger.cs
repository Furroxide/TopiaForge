using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core
{
    // Primitive, Unity-free derivation. Also linked into the testing kit so fakes obey the same rules.
    internal sealed class WorldViewLedger<T> where T : class
    {
        internal const int MaximumLeases = 4096;
        internal const int MaximumHiddenTargets = 1024;
        internal readonly struct Entry
        {
            internal Entry(string? environment, float distance, int tint, T? hidden)
            { Environment = environment; Distance = distance; Tint = tint; Hidden = hidden; }
            internal string? Environment { get; }
            internal float Distance { get; }
            internal int Tint { get; }
            internal T? Hidden { get; }
        }
        private readonly SortedDictionary<long, Entry> entries = new SortedDictionary<long, Entry>();
        private readonly Dictionary<T, int> hidden;
        private long next;
        internal WorldViewLedger(IEqualityComparer<T> comparer) { hidden = new Dictionary<T, int>(comparer); }
        internal int Count => entries.Count;
        internal int HiddenCount => hidden.Count;
        internal bool IsHidden(T target) => hidden.ContainsKey(target);
        internal long Add(Entry entry)
        {
            if (Count >= MaximumLeases || next == long.MaxValue) return 0;
            if (entry.Hidden != null && !hidden.ContainsKey(entry.Hidden) && hidden.Count >= MaximumHiddenTargets) return 0;
            var id = ++next;
            entries.Add(id, entry);
            if (entry.Hidden != null) hidden[entry.Hidden] = hidden.TryGetValue(entry.Hidden, out var count) ? count + 1 : 1;
            return id;
        }
        internal bool Remove(long id)
        {
            if (!entries.TryGetValue(id, out var entry)) return false;
            entries.Remove(id);
            if (entry.Hidden != null)
            {
                var count = hidden[entry.Hidden] - 1;
                if (count == 0) hidden.Remove(entry.Hidden); else hidden[entry.Hidden] = count;
            }
            return true;
        }
        internal bool Contains(long id) => entries.ContainsKey(id);
        internal string? Environment
        {
            get { string? result = null; foreach (var entry in entries.Values) if (entry.Environment != null) result = entry.Environment; return result; }
        }
        internal (float Distance, int Tint) Fog
        {
            get
            {
                var distance = 0f; var tint = 0;
                foreach (var entry in entries.Values)
                    if (entry.Distance > 0 && (distance == 0 || entry.Distance < distance || entry.Distance == distance && entry.Tint < tint))
                    { distance = entry.Distance; tint = entry.Tint; }
                return (distance, tint);
            }
        }
        internal void Clear() { entries.Clear(); hidden.Clear(); }
        internal static bool ValidPreset(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value!.Length > 64) return false;
            foreach (var c in value) if (char.IsControl(c)) return false;
            return true;
        }
    }
}
