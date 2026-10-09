using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    // One owner for allocations made before a provider can return an IWorldInstance.
    internal sealed class WorldResourceScope : IDisposable
    {
        private readonly object gate = new object();
        private readonly List<IDisposable> resources = new List<IDisposable>();
        private readonly CancellationTokenSource cancellation;
        private IDisposable? lifetimeLease;
        private bool disposed;

        public WorldResourceScope() : this(CancellationToken.None, CancellationToken.None) { }
        private WorldResourceScope(CancellationToken caller, CancellationToken lifetime)
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
            StoppingToken = cancellation.Token;
        }
        public CancellationToken StoppingToken { get; }
        public static WorldResourceScope Create(IModLifetime lifetime, CancellationToken cancellationToken = default)
        {
            if (lifetime == null) throw new ArgumentNullException(nameof(lifetime));
            var scope = new WorldResourceScope(cancellationToken, lifetime.StoppingToken);
            // Track transfers ownership even if a stopped lifetime rejects registration.
            var lease = lifetime.Track(scope);
            lock (scope.gate)
            {
                if (!scope.disposed) { scope.lifetimeLease = lease; return scope; }
            }
            lease.Dispose();
            throw new ObjectDisposedException(nameof(WorldResourceScope));
        }
        public T Add<T>(T resource) where T : IDisposable
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            bool wasDisposed;
            lock (gate)
            {
                wasDisposed = disposed;
                if (!wasDisposed && !StoppingToken.IsCancellationRequested)
                {
                    resources.Add(resource);
                    return resource;
                }
            }
            var failure = wasDisposed ? (Exception)new ObjectDisposedException(nameof(WorldResourceScope))
                : new OperationCanceledException(StoppingToken);
            try { resource.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw failure;
        }
        public void ThrowIfStopping()
        {
            lock (gate) if (disposed) throw new ObjectDisposedException(nameof(WorldResourceScope));
            StoppingToken.ThrowIfCancellationRequested();
        }
        public void Dispose()
        {
            IDisposable[] owned;
            IDisposable? lease;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                owned = resources.ToArray();
                resources.Clear();
                lease = lifetimeLease;
                lifetimeLease = null;
            }
            var failures = new List<Exception>();
            Try(cancellation.Cancel, failures);
            for (var index = owned.Length - 1; index >= 0; index--) Try(owned[index].Dispose, failures);
            if (lease != null) Try(lease.Dispose, failures);
            Try(cancellation.Dispose, failures);
            if (failures.Count != 0) throw new AggregateException("World cleanup failed.", failures);
        }
        private static void Try(Action action, ICollection<Exception> failures)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); }
        }
    }
}
