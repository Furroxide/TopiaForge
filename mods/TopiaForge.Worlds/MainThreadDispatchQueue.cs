using System;
using System.Collections.Generic;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Carries completion data from arbitrary worker threads to an owner-controlled main-thread drain.
    /// Disposal is terminal: queued items are discarded and producers can no longer retain work for a
    /// service that has unloaded.
    /// </summary>
    internal sealed class MainThreadDispatchQueue<T> : IDisposable
    {
        private readonly object gate = new object();
        private readonly Queue<T> pending = new Queue<T>();
        private bool disposed;

        public bool TryEnqueue(T item)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return false;
                }

                pending.Enqueue(item);
                return true;
            }
        }

        /// <summary>
        /// Delivers pending items on the calling thread. The service owning this queue calls this only from
        /// its Unity update tick. Holding the gate while dispatching guarantees that, once Dispose returns,
        /// no callback can start or remain queued on another thread.
        /// </summary>
        public void Drain(Action<T> dispatch)
        {
            if (dispatch == null)
            {
                throw new ArgumentNullException(nameof(dispatch));
            }

            lock (gate)
            {
                while (!disposed && pending.Count > 0)
                {
                    dispatch(pending.Dequeue());
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                pending.Clear();
            }
        }
    }
}
