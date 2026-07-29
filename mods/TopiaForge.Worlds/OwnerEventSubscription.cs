using System;
using System.Threading;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Owner-bound event subscription that can be published before its mod-lifetime lease is returned. Disposal
    /// always detaches the handler and releases the tracked lifetime node; a concurrent stop before either attach
    /// finishes is repaired as soon as that attach completes.
    /// </summary>
    internal sealed class OwnerEventSubscription<T> : IDisposable
    {
        private readonly Action<T> handler;
        private readonly Action<Action<T>> subscribe;
        private readonly Action<Action<T>> unsubscribe;
        private readonly Action onDisposed;
        private IDisposable? lifetimeLease;
        private int publisherAttached;
        private int lifetimeAttached;
        private int disposed;

        public OwnerEventSubscription(
            Action<T> handler,
            Action<Action<T>> subscribe,
            Action<Action<T>> unsubscribe,
            Action onDisposed)
        {
            this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
            this.subscribe = subscribe ?? throw new ArgumentNullException(nameof(subscribe));
            this.unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
            this.onDisposed = onDisposed ?? throw new ArgumentNullException(nameof(onDisposed));
            Wrapper = value =>
            {
                if (Volatile.Read(ref disposed) == 0)
                {
                    this.handler(value);
                }
            };
        }

        public Action<T> Wrapper { get; }
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public bool Matches(Action<T> candidate) => handler == candidate;

        public void AttachPublisher()
        {
            if (Interlocked.Exchange(ref publisherAttached, 1) != 0)
            {
                throw new InvalidOperationException("The event subscription is already attached to its publisher.");
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            subscribe(Wrapper);
            if (Volatile.Read(ref disposed) != 0)
            {
                unsubscribe(Wrapper);
            }
        }

        public void AttachLifetimeLease(IDisposable lease)
        {
            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            if (Interlocked.Exchange(ref lifetimeAttached, 1) != 0)
            {
                throw new InvalidOperationException("The event subscription already has a lifetime lease.");
            }

            Interlocked.Exchange(ref lifetimeLease, lease);
            if (Volatile.Read(ref disposed) != 0)
            {
                Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref publisherAttached) != 0)
                {
                    unsubscribe(Wrapper);
                }
            }
            finally
            {
                try
                {
                    onDisposed();
                }
                finally
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }
}
