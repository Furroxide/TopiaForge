using System;
using System.Collections.Generic;
using System.Threading;

namespace TopiaForge.Mods.Testing
{
    // The fake models the runtime's flowing record and synchronous ancestors separately.
    // Captured contexts remain restricted on their originating thread; workers never observe them.
    internal static class FakeGuestOrigin
    {
        private static readonly AsyncLocal<Record?> flowing = new AsyncLocal<Record?>();
        [ThreadStatic] private static List<Record>? ancestors;

        internal static bool IsActive =>
            flowing.Value?.Thread == Environment.CurrentManagedThreadId || ancestors?.Count > 0;

        internal static void Run(bool guest, Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (!guest) { callback(); return; }

            var previous = flowing.Value;
            var record = new Record(Environment.CurrentManagedThreadId);
            var active = ancestors ??= new List<Record>();
            active.Add(record);
            flowing.Value = record;
            try { callback(); }
            finally
            {
                flowing.Value = previous;
                active.RemoveAt(active.Count - 1);
            }
        }

        private sealed class Record
        {
            internal Record(int thread) { Thread = thread; }
            internal int Thread { get; }
        }
    }
}
