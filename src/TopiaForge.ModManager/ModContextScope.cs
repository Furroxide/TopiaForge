using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Host-owned resources for one package participating in one session.</summary>
    internal sealed class ModContextScope : IDisposable
    {
        private readonly object sync = new object();
        private readonly ModContext parent;
        private readonly IHostDispatcher dispatcher;
        private readonly Action requestSessionStop;
        private readonly List<IDisposable> rejected = new List<IDisposable>();
        private readonly List<Task> pending = new List<Task>();
        private readonly CancellationTokenRegistration sessionStopping;
        private ModContext? context;
        private CleanupPhase cleanupPhase;
        private int activeDrains;
        private enum CleanupPhase { Active, Disposing, Draining, Sealed }
        private int stopRequested;

        internal ModContextScope(ModContext parent, string sessionId, CancellationToken stoppingToken,
            Action requestSessionStop, IHostDispatcher dispatcher)
        {
            this.parent = parent;
            SessionId = sessionId;
            this.requestSessionStop = requestSessionStop;
            this.dispatcher = dispatcher;
            OwnerLifetime = new OwnerModLifetime(Reject);
            Lifetime = new ScopedModLifetime(this);
            sessionStopping = stoppingToken.Register(() => Schedule(BeginStop));
        }

        internal string SessionId { get; }
        internal OwnerModLifetime OwnerLifetime { get; }
        internal ModContext Context => context ?? throw new InvalidOperationException("The scope context is not initialized.");
        internal IModLifetime Lifetime { get; }
        internal void Initialize(ModContext value) { context = value; }

        internal void BeginStop()
        {
            AssertHost();
            OwnerLifetime.BeginStop();
        }

        internal void RequestSessionStop()
        {
            if (Interlocked.Exchange(ref stopRequested, 1) != 0) return;
            Schedule(requestSessionStop);
        }

        internal IDisposable Track(IDisposable resource)
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            var tracking = OwnerLifetime.Track(new DispatchedResource(this, resource));
            return new DispatchedResource(this, tracking);
        }

        internal void DisposeResource(IDisposable resource) => Schedule(resource.Dispose);

        private void Reject(IDisposable resource)
        {
            lock (sync)
            {
                if (cleanupPhase != CleanupPhase.Sealed)
                {
                    rejected.Add(resource);
                    return;
                }
            }
            // Once terminal cleanup has passed, no future drain call is guaranteed.
            // Still hand ownership to the host, and report any failure there.
            dispatcher.Post(() =>
            {
                try { resource.Dispose(); }
                catch (Exception exception) { ReportLateFailure(exception); }
            });
        }

        private void Schedule(Action action)
        {
            if (dispatcher.IsCurrent)
            {
                action();
                return;
            }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync)
            {
                if (cleanupPhase == CleanupPhase.Sealed)
                {
                    dispatcher.Post(() =>
                    {
                        try { action(); }
                        catch (Exception exception) { ReportLateFailure(exception); }
                    });
                    return;
                }
                // Enrol before releasing the phase lock, so sealing cannot miss a worker submission.
                pending.Add(completion.Task);
            }
            try
            {
                dispatcher.Post(() =>
                {
                    try { action(); completion.TrySetResult(true); }
                    catch (Exception exception) { completion.TrySetException(exception); }
                });
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }

        /// <summary>Call after callback/native drain, then again after scope disposal.</summary>
        internal async Task DrainRejectedResourcesAsync()
        {
            AssertHost();
            var failures = new List<Exception>();
            lock (sync) activeDrains++;
            try
            {
                while (true)
                {
                    IDisposable[] resources;
                    Task[] tasks;
                    var releaseParent = false;
                    lock (sync)
                    {
                        resources = rejected.ToArray();
                        rejected.Clear();
                        tasks = pending.ToArray();
                        pending.Clear();
                        if (resources.Length == 0 && tasks.Length == 0
                            && cleanupPhase == CleanupPhase.Draining && activeDrains == 1)
                        {
                            // The empty check and seal share the submission lock. No worker can enqueue
                            // an unobserved resource between the last drain and terminal ownership release.
                            cleanupPhase = CleanupPhase.Sealed;
                            releaseParent = true;
                        }
                    }
                    if (releaseParent) parent.ReleaseChildScope(this);
                    if (resources.Length == 0 && tasks.Length == 0) break;
                    var current = new List<Task>(tasks);
                    foreach (var resource in resources) current.Add(dispatcher.InvokeAsync(resource.Dispose));
                    try { await Task.WhenAll(current).ConfigureAwait(false); }
                    catch
                    {
                        foreach (var task in current)
                            if (task.Exception != null) failures.AddRange(task.Exception.Flatten().InnerExceptions);
                    }
                }
            }
            finally
            {
                lock (sync) activeDrains--;
                SealIfDrained();
            }
            if (failures.Count > 0) throw new AggregateException("Scoped resource cleanup failed.", failures);
        }

        // Failed initialization has no returned owner. Keep the parent registration and host callback
        // alive until queued/rejected resources have drained, then propagate every cleanup failure.
        internal Task CleanupFailedConstructionAsync() => dispatcher.InvokeCallbackAsync(async () =>
        {
            var failures = new List<Exception>();
            Try(BeginStop, failures);
            try { await DrainRejectedResourcesAsync(); } catch (Exception error) { failures.Add(error); }
            Try(Dispose, failures);
            try { await DrainRejectedResourcesAsync(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException("Failed scope initialization cleanup failed.", failures);
            return true;
        });

        public void Dispose()
        {
            AssertHost();
            lock (sync)
            {
                if (cleanupPhase != CleanupPhase.Active) return;
                cleanupPhase = CleanupPhase.Disposing;
            }
            var failures = new List<Exception>();
            Try(() => sessionStopping.Dispose(), failures);
            Try(OwnerLifetime.Dispose, failures);
            // Disposers may submit more resources even though Track rejects them. They still belong to
            // this cleanup transaction; only submissions after the complete drain use terminal posting.
            while (true)
            {
                IDisposable[] remainder;
                lock (sync) { remainder = rejected.ToArray(); rejected.Clear(); }
                if (remainder.Length == 0) break;
                foreach (var resource in remainder) Try(resource.Dispose, failures);
            }
            lock (sync) cleanupPhase = CleanupPhase.Draining;
            SealIfDrained();
            if (failures.Count > 0) throw new AggregateException("Scoped resource cleanup failed.", failures);
        }

        private void SealIfDrained()
        {
            lock (sync)
            {
                if (cleanupPhase != CleanupPhase.Draining || activeDrains != 0
                    || rejected.Count != 0 || pending.Count != 0) return;
                cleanupPhase = CleanupPhase.Sealed;
            }
            parent.ReleaseChildScope(this);
        }

        private void AssertHost()
        {
            if (!dispatcher.IsCurrent) throw new InvalidOperationException("Scope lifecycle operations require the host thread.");
        }
        private void ReportLateFailure(Exception exception)
        {
            try { parent.Logger.Error(exception, "A resource submitted after session cleanup failed to dispose."); }
            catch { }
        }
        private static void Try(Action action, List<Exception> failures)
        {
            try { action(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        private sealed class DispatchedResource : IDisposable
        {
            private readonly ModContextScope scope;
            private IDisposable? resource;
            internal DispatchedResource(ModContextScope scope, IDisposable resource)
            { this.scope = scope; this.resource = resource; }
            public void Dispose()
            {
                var current = Interlocked.Exchange(ref resource, null);
                if (current != null) scope.Schedule(current.Dispose);
            }
        }
    }
}
