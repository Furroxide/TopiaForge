using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Runtime-owned, thread-safe resource lifetime for a single mod instance.</summary>
    internal sealed class OwnerModLifetime : IModLifetime
    {
        private readonly object sync = new object();
        private readonly CancellationTokenSource stoppingSource = new CancellationTokenSource();
        private readonly CancellationToken stoppingToken;
        private readonly List<TrackedResource> resources = new List<TrackedResource>();
        private readonly Action<IDisposable>? rejectResource;
        private readonly TaskCompletionSource<bool> cancellationFinished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancellingThread;
        private bool cancellationComplete;
        private bool stopping;
        private bool disposed;

        public OwnerModLifetime(Action<IDisposable>? rejectResource = null)
        {
            this.rejectResource = rejectResource;
            stoppingToken = stoppingSource.Token;
        }

        public CancellationToken StoppingToken => stoppingToken;

        public bool IsStopping => Volatile.Read(ref stopping);

        public IDisposable Track(IDisposable resource)
        {
            if (resource == null)
            {
                throw new ArgumentNullException(nameof(resource));
            }

            if (ReferenceEquals(resource, this))
            {
                throw new ArgumentException("A mod lifetime cannot track itself.", nameof(resource));
            }

            var tracked = new TrackedResource(this, resource);
            lock (sync)
            {
                if (!stopping && !disposed)
                {
                    resources.Add(tracked);
                    return tracked;
                }
            }

            if (rejectResource == null) tracked.DisposeFromOwner();
            else rejectResource(tracked);
            throw new ObjectDisposedException(nameof(OwnerModLifetime),
                "Resources cannot be tracked after mod shutdown has begun.");
        }

        public IDisposable Defer(Action cleanup)
        {
            if (cleanup == null)
            {
                throw new ArgumentNullException(nameof(cleanup));
            }

            return Track(new DeferredAction(cleanup));
        }

        internal void BeginStop()
        {
            Task? waitForCancellation = null;
            lock (sync)
            {
                if (stopping)
                {
                    if (cancellationComplete || cancellingThread == Thread.CurrentThread.ManagedThreadId) return;
                    waitForCancellation = cancellationFinished.Task;
                }
                else
                {
                    stopping = true;
                    cancellingThread = Thread.CurrentThread.ManagedThreadId;
                }
            }
            if (waitForCancellation != null)
            {
                waitForCancellation.GetAwaiter().GetResult();
                return;
            }
            try { stoppingSource.Cancel(); }
            finally
            {
                lock (sync) cancellationComplete = true;
                cancellationFinished.TrySetResult(true);
            }
        }

        public void Dispose()
        {
            List<Exception>? failures = null;
            try { BeginStop(); }
            catch (Exception exception) { AddFailure(ref failures, exception); }
            TrackedResource[] snapshot;
            lock (sync)
            {
                // A reentrant Dispose from a cancellation callback cannot run cleanup ahead of
                // the remaining callbacks. The outer lifecycle operation retains disposal ownership.
                if (!cancellationComplete) return;
                if (disposed) return;
                disposed = true;
                snapshot = resources.ToArray();
                resources.Clear();
            }

            for (var index = snapshot.Length - 1; index >= 0; index--)
            {
                try
                {
                    snapshot[index].DisposeFromOwner();
                }
                catch (Exception ex)
                {
                    AddFailure(ref failures, ex);
                }
            }

            stoppingSource.Dispose();
            if (failures != null)
            {
                throw new AggregateException("One or more mod lifetime resources failed to stop.", failures);
            }
        }

        private void Remove(TrackedResource resource)
        {
            lock (sync)
            {
                if (!disposed)
                {
                    resources.Remove(resource);
                }
            }
        }

        private static void AddFailure(ref List<Exception>? failures, Exception exception)
        {
            failures ??= new List<Exception>();
            failures.Add(exception);
        }

        private sealed class TrackedResource : IDisposable
        {
            private OwnerModLifetime? owner;
            private IDisposable? resource;

            public TrackedResource(OwnerModLifetime owner, IDisposable resource)
            {
                this.owner = owner;
                this.resource = resource;
            }

            public void Dispose()
            {
                var currentOwner = Interlocked.Exchange(ref owner, null);
                currentOwner?.Remove(this);
                Interlocked.Exchange(ref resource, null)?.Dispose();
            }

            public void DisposeFromOwner()
            {
                Interlocked.Exchange(ref owner, null);
                Interlocked.Exchange(ref resource, null)?.Dispose();
            }
        }

        private sealed class DeferredAction : IDisposable
        {
            private Action? cleanup;

            public DeferredAction(Action cleanup)
            {
                this.cleanup = cleanup;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref cleanup, null)?.Invoke();
            }
        }
    }
}
