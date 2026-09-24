using System;
using System.Collections;
using System.Collections.Generic;
namespace TopiaForge.Worlds
{
    internal sealed class NativeImportOverrides
    {
        private readonly IDictionary table;
        private readonly List<DictionaryEntry> previous = new List<DictionaryEntry>();
        internal NativeImportOverrides(IDictionary table)
        {
            this.table = table ?? throw new ArgumentNullException(nameof(table));
            var entries = table.GetEnumerator();
            while (entries.MoveNext()) previous.Add(entries.Entry);
        }
        internal void Restore()
        {
            var failures = new List<Exception>();
            try { table.Clear(); } catch (Exception error) { failures.Add(error); }
            foreach (var entry in previous)
                try { table[entry.Key] = entry.Value; } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("Native asset overrides could not be restored.", failures);
        }
    }
}
