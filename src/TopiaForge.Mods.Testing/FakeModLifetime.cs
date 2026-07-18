using System;
using System.Collections.Generic;
using System.Threading;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic test implementation of a mod-owned resource lifetime.</summary>
    public sealed class FakeModLifetime : IModLifetime
    {
        private readonly object gate = new object();
        private readonly List<TrackedEntry> entries = new List<TrackedEntry>();
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private bool disposed;

        /// <inheritdoc/>
        public CancellationToken StoppingToken => stopping.Token;

        /// <inheritdoc/>
        public bool IsStopping
        {
            get
            {
                lock (gate)
                {
                    return disposed;
                }
            }
        }

        /// <summary>Gets the number of resources still awaiting cleanup.</summary>
        public int TrackedResourceCount
        {
            get
            {
                lock (gate)
                {
                    var count = 0;
                    foreach (var entry in entries)
                    {
                        if (!entry.IsReleased)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        /// <inheritdoc/>
        public IDisposable Track(IDisposable resource)
        {
            if (resource == null)
            {
                throw new ArgumentNullException(nameof(resource));
            }

            TrackedEntry? entry = null;
            lock (gate)
            {
                if (!disposed)
                {
                    entry = new TrackedEntry(resource);
                    entries.Add(entry);
                }
            }

            if (entry == null)
            {
                resource.Dispose();
                throw new ObjectDisposedException(nameof(FakeModLifetime));
            }

            return new ReleaseLease(this, entry);
        }

        internal OperationResult<T> TrackResult<T>(T resource, string cancelledMessage)
            where T : class, IDisposable
        {
            try
            {
                Track(resource);
                return OperationResult<T>.Success(resource);
            }
            catch (ObjectDisposedException)
            {
                return OperationResult<T>.Failure(
                    ModErrorCode.Cancelled,
                    cancelledMessage ?? string.Empty);
            }
        }

        /// <inheritdoc/>
        public IDisposable Defer(Action cleanup)
        {
            if (cleanup == null)
            {
                throw new ArgumentNullException(nameof(cleanup));
            }

            return Track(new DelegateDisposable(cleanup));
        }

        /// <summary>Throws when one or more resources remain registered with this lifetime.</summary>
        public void AssertNoTrackedResources()
        {
            var count = TrackedResourceCount;
            if (count != 0)
            {
                throw new ModTestAssertionException(
                    "Expected the mod lifetime to have no tracked resources, but found " + count + ".");
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            List<TrackedEntry> snapshot;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                snapshot = new List<TrackedEntry>(entries);
            }

            stopping.Cancel();
            List<Exception>? failures = null;
            for (var index = snapshot.Count - 1; index >= 0; index--)
            {
                try
                {
                    Release(snapshot[index]);
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }

            if (failures != null)
            {
                throw new AggregateException("One or more fake mod resources failed during cleanup.", failures);
            }
        }

        private void Release(TrackedEntry entry)
        {
            IDisposable? resource;
            lock (gate)
            {
                resource = entry.Take();
            }

            resource?.Dispose();
        }

        private sealed class TrackedEntry
        {
            private IDisposable? resource;

            public TrackedEntry(IDisposable resource)
            {
                this.resource = resource;
            }

            public bool IsReleased => resource == null;

            public IDisposable? Take()
            {
                var value = resource;
                resource = null;
                return value;
            }
        }

        private sealed class ReleaseLease : IDisposable
        {
            private FakeModLifetime? owner;
            private TrackedEntry? entry;

            public ReleaseLease(FakeModLifetime owner, TrackedEntry entry)
            {
                this.owner = owner;
                this.entry = entry;
            }

            public void Dispose()
            {
                var capturedOwner = Interlocked.Exchange(ref owner, null);
                var capturedEntry = Interlocked.Exchange(ref entry, null);
                if (capturedOwner != null && capturedEntry != null)
                {
                    capturedOwner.Release(capturedEntry);
                }
            }
        }

        private sealed class DelegateDisposable : IDisposable
        {
            private Action? action;

            public DelegateDisposable(Action action)
            {
                this.action = action;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref action, null)?.Invoke();
            }
        }
    }

    /// <summary>Signals an assertion failure produced by the runner-neutral testing kit.</summary>
    public sealed class ModTestAssertionException : Exception
    {
        /// <summary>Creates an assertion failure with a plain-language description.</summary>
        /// <param name="message">The failed expectation.</param>
        public ModTestAssertionException(string message)
            : base(message)
        {
        }
    }
}
