using System;
using System.Collections.Generic;
using System.Threading;

namespace TopiaForge.Worlds
{
    /// <summary>Retains exact native owners and attempts each independent cleanup even when another fails.</summary>
    internal sealed class LocalImportOwnership : IDisposable
    {
        private readonly Action discardContent;
        private readonly Action restoreOverrides;
        private readonly Action restoreSelection;
        private int disposed;
        internal LocalImportOwnership(Action discardContent, Action restoreOverrides, Action restoreSelection)
        {
            this.discardContent = discardContent ?? throw new ArgumentNullException(nameof(discardContent));
            this.restoreOverrides = restoreOverrides ?? throw new ArgumentNullException(nameof(restoreOverrides));
            this.restoreSelection = restoreSelection ?? throw new ArgumentNullException(nameof(restoreSelection));
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            var failures = new List<Exception>();
            Attempt(discardContent, failures);
            Attempt(restoreOverrides, failures);
            Attempt(restoreSelection, failures);
            if (failures.Count > 0) throw new AggregateException("Local import cleanup failed.", failures);
        }
        private static void Attempt(Action action, List<Exception> failures)
        { try { action(); } catch (Exception failure) { failures.Add(failure); } }
    }
}
