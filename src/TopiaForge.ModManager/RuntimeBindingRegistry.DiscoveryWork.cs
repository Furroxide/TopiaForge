using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    internal interface IRuntimeDiscoveryWork : IDisposable
    {
        void RecordCleanupFailure(Exception failure);
    }

    internal sealed partial class RuntimeBindingRegistry
    {
        private readonly HashSet<DiscoveryWorkLease> discoveryWork = new HashSet<DiscoveryWorkLease>();
        private readonly List<DiscoveryCleanupFailure> discoveryCleanupFailures = new List<DiscoveryCleanupFailure>();
        private readonly List<DiscoveryDrain> discoveryDrains = new List<DiscoveryDrain>();

        /// <summary>Acquired before scope construction; released only after callbacks and scope cleanup drain.</summary>
        internal IRuntimeDiscoveryWork? RegisterDiscoveryWork(RuntimeDiscoveryAttempt attempt)
        {
            lock (gate)
            {
                if (!CurrentDiscovery(attempt, out _) || attempt.CancellationToken.IsCancellationRequested) return null;
                var lease = new DiscoveryWorkLease(this, attempt.Package);
                discoveryWork.Add(lease);
                return lease;
            }
        }

        internal Task<bool> WaitForDiscoveryIdleAsync(PackageIdentity? package = null)
        {
            lock (gate)
            {
                if (!HasDiscoveryWork(package))
                {
                    var failure = CleanupFailure(package);
                    return failure == null ? Task.FromResult(true) : Task.FromException<bool>(failure);
                }
                var waiting = discoveryDrains.FirstOrDefault(value => Equals(value.Package, package));
                if (waiting == null) { waiting = new DiscoveryDrain(package); discoveryDrains.Add(waiting); }
                return waiting.Completion.Task;
            }
        }

        private bool HasDiscoveryWork(PackageIdentity? package) => discoveryWork.Any(work => package == null || work.Package.Equals(package));
        private void CompleteDiscoveryWork(DiscoveryWorkLease lease)
        {
            lock (gate)
            {
                if (!discoveryWork.Remove(lease)) return;
                if (lease.Failures.Count != 0)
                    discoveryCleanupFailures.Add(new DiscoveryCleanupFailure(lease.Package,
                        new AggregateException("Discovery cleanup failed for " + lease.Package.Id + ".", lease.Failures)));
                foreach (var drain in discoveryDrains.Where(value => !HasDiscoveryWork(value.Package)).ToArray())
                {
                    discoveryDrains.Remove(drain);
                    var failure = CleanupFailure(drain.Package);
                    if (failure == null) drain.Completion.TrySetResult(true);
                    else drain.Completion.TrySetException(failure);
                }
            }
        }
        private Exception? CleanupFailure(PackageIdentity? package)
        {
            var failures = discoveryCleanupFailures.Where(value => package == null || value.Package.Equals(package))
                .Select(value => value.Failure).ToArray();
            return failures.Length == 0 ? null : new AggregateException("Discovery work failed to clean up.", failures);
        }
        private void RecordCleanupFailure(DiscoveryWorkLease lease, Exception failure)
        {
            lock (gate) if (discoveryWork.Contains(lease)) lease.Failures.Add(failure);
        }
        private sealed class DiscoveryCleanupFailure
        {
            internal DiscoveryCleanupFailure(PackageIdentity package, Exception failure) { Package = package; Failure = failure; }
            internal PackageIdentity Package { get; }
            internal Exception Failure { get; }
        }
        private sealed class DiscoveryWorkLease : IRuntimeDiscoveryWork
        {
            private RuntimeBindingRegistry? registry;
            internal DiscoveryWorkLease(RuntimeBindingRegistry registry, PackageIdentity package) { this.registry = registry; Package = package; }
            internal PackageIdentity Package { get; }
            internal List<Exception> Failures { get; } = new List<Exception>();
            public void RecordCleanupFailure(Exception failure)
            {
                if (failure == null) throw new ArgumentNullException(nameof(failure));
                registry?.RecordCleanupFailure(this, failure);
            }
            public void Dispose() => Interlocked.Exchange(ref registry, null)?.CompleteDiscoveryWork(this);
        }
        private sealed class DiscoveryDrain
        {
            internal DiscoveryDrain(PackageIdentity? package) { Package = package; }
            internal readonly PackageIdentity? Package;
            internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
