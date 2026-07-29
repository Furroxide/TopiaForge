using System;
using System.Collections.Generic;
using System.Threading;
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
        private bool disposed;

        public OwnerModLifetime()
        {
            stoppingToken = stoppingSource.Token;
        }

        public CancellationToken StoppingToken => stoppingToken;

        public bool IsStopping => stoppingToken.IsCancellationRequested;

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
                if (!disposed)
                {
                    resources.Add(tracked);
                    return tracked;
                }
            }

            tracked.DisposeFromOwner();
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

        public void Dispose()
        {
            TrackedResource[] snapshot;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                snapshot = resources.ToArray();
                resources.Clear();
            }

            List<Exception>? failures = null;
            try
            {
                stoppingSource.Cancel();
            }
            catch (Exception ex)
            {
                AddFailure(ref failures, ex);
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
