using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    // Owns partial content and its cancellation link before asynchronous preparation starts.
    internal sealed class OwnedWorldContent : IDisposable
    {
        private readonly object gate = new object();
        private readonly List<IDisposable> resources = new List<IDisposable>();
        private readonly CancellationTokenSource stopping;
        private IDisposable? lifetimeLease;
        private bool disposed;

        private OwnedWorldContent(CancellationToken ownerToken, CancellationToken callerToken)
        {
            stopping = CancellationTokenSource.CreateLinkedTokenSource(ownerToken, callerToken);
            StoppingToken = stopping.Token;
        }
        internal CancellationToken StoppingToken { get; }

        internal static OwnedWorldContent Create(IModLifetime lifetime, CancellationToken callerToken)
        {
            var owned = new OwnedWorldContent(lifetime.StoppingToken, callerToken);
            // Track transfers ownership even if a stopped lifetime rejects registration.
            var lease = lifetime.Track(owned);
            lock (owned.gate)
            {
                if (!owned.disposed) { owned.lifetimeLease = lease; return owned; }
            }
            var cancellation = new OperationCanceledException("World ownership ended during registration.");
            try { lease.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(cancellation, cleanup); }
            throw cancellation;
        }

        internal T Add<T>(T resource) where T : class, IDisposable
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            lock (gate)
            {
                if (!disposed && !StoppingToken.IsCancellationRequested) { resources.Add(resource); return resource; }
            }
            var cancellation = new OperationCanceledException("World ownership ended before content acquisition completed.");
            try { resource.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(cancellation, cleanup); }
            throw cancellation;
        }

        public void Dispose()
        {
            IDisposable[] release;
            IDisposable? lease;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                release = resources.ToArray();
                resources.Clear();
                lease = lifetimeLease;
                lifetimeLease = null;
            }
            var errors = new List<Exception>();
            Try(stopping.Cancel, errors);
            for (var index = release.Length - 1; index >= 0; index--) Try(release[index].Dispose, errors);
            if (lease != null) Try(lease.Dispose, errors);
            Try(stopping.Dispose, errors);
            if (errors.Count != 0) throw new AggregateException("World content cleanup failed.", errors);
        }
        private static void Try(Action action, ICollection<Exception> errors)
        {
            try { action(); }
            catch (Exception error) { errors.Add(error); }
        }
    }
}
