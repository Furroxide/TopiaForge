using System;
using System.Collections.Generic;
using System.Threading;

namespace TopiaForge.Mods.Testing
{
    // The fake models the runtime's flowing record and synchronous ancestors separately.
    // Captured contexts remain restricted on their originating thread. Guest origin is only meaningful on the
    // thread that owns the fake robot service, so reading it anywhere else fails instead of answering as the host.
    internal static class FakeGuestOrigin
    {
        internal const string OwnerThreadRule =
            "The fake robot service models guest origin only on the thread that created it. Call RunAsGuest, " +
            "simulate player UI actions and make the robot calls they trigger on that thread; an async test must " +
            "resume on that thread before acting for a guest. An await that resumes on a pool thread, such as one " +
            "after DeterministicModScheduler.DelayAsync, cannot act for a guest: schedule the follow-up with " +
            "Scheduler.NextFrame or After instead, which run on the advancing thread and keep the guest's origin.";

        private static readonly AsyncLocal<Record?> flowing = new AsyncLocal<Record?>();
        [ThreadStatic] private static List<Record>? ancestors;

        internal static bool IsActiveFor(int ownerThread)
        {
            var record = flowing.Value;
            if (record == null && !(ancestors?.Count > 0)) return false;
            if (Environment.CurrentManagedThreadId != ownerThread || (record != null && record.Thread != ownerThread))
                throw new InvalidOperationException(OwnerThreadRule);
            return true;
        }

        // Work scheduled inside a guest scope keeps that guest's origin, as the runtime's scheduler does.
        internal static Action Capture(Action action)
        {
            var record = flowing.Value;
            if (record == null && ancestors?.Count > 0) record = ancestors[ancestors.Count - 1];
            return record == null ? action : () => RunCaptured(record, action);
        }

        private static void RunCaptured(Record record, Action callback)
        {
            var previous = flowing.Value;
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
