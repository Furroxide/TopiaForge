using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>
    /// A main thread's synchronization context, as Unity's is: work posted to it waits, in order, until the thread that
    /// installed it pumps. Awaits on that thread therefore resume on it, at a point the test chooses. The ModRuntime and
    /// ModManager harnesses both compile it.
    /// </summary>
    internal sealed class MainThreadPump : SynchronizationContext, IDisposable
    {
        private readonly object sync = new object();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new Queue<(SendOrPostCallback, object?)>();
        private readonly SynchronizationContext? previous;
        private bool disposed;

        /// <summary>Installs the pump as the calling thread's synchronization context until it is disposed.</summary>
        internal MainThreadPump()
        {
            previous = Current;
            SetSynchronizationContext(this);
        }

        /// <summary>Gets how much posted work waits to run.</summary>
        internal int Pending
        {
            get
            {
                lock (sync) return queue.Count;
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            lock (sync)
            {
                queue.Enqueue((d, state));
                Monitor.PulseAll(sync);
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("The pump only takes posted work.");

        public override SynchronizationContext CreateCopy() => this;

        /// <summary>Runs what was posted, in order, and what that work posts in turn, until nothing waits. Returns how much ran.</summary>
        internal int RunPending()
        {
            var ran = 0;
            while (TryTake(out var work))
            {
                work.Callback(work.State);
                ran++;
            }

            return ran;
        }

        /// <summary>
        /// Runs posted work as it arrives until <paramref name="done"/> holds, for work another thread finishes. It waits on
        /// the work arriving, never on a clock, and gives up after <paramref name="timeout"/> as a failed test.
        /// </summary>
        internal void RunUntil(Func<bool> done, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (TryTake(out var work))
                {
                    work.Callback(work.State);
                    continue;
                }

                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("The pump waited " + timeout + " for work that never came.");
                lock (sync)
                {
                    if (queue.Count == 0) Monitor.Wait(sync, remaining);
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            SetSynchronizationContext(previous);
        }

        private bool TryTake(out (SendOrPostCallback Callback, object? State) work)
        {
            lock (sync)
            {
                if (queue.Count == 0)
                {
                    work = default;
                    return false;
                }

                work = queue.Dequeue();
                return true;
            }
        }
    }
}
