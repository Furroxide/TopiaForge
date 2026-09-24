using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Owns bounded family callbacks and atomically publishes one current package observation.</summary>
    internal sealed class RuntimeWorldDiscovery
    {
        private const int PackageLimit = 4096;
        private readonly RuntimeBindingRegistry registry;
        private readonly IHostDispatcher dispatcher;
        private readonly TimeSpan timeout;

        internal RuntimeWorldDiscovery(RuntimeBindingRegistry registry, IHostDispatcher dispatcher, TimeSpan? timeout = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.timeout = timeout ?? TimeSpan.FromSeconds(30);
            if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException(nameof(timeout), "Discovery requires a positive deadline of at most five minutes.");
        }

        internal async Task<OperationResult<bool>> DiscoverAsync(PackageIdentity package,
            int maximumResultsPerFamily = 256, CancellationToken cancellationToken = default)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (maximumResultsPerFamily < 1 || maximumResultsPerFamily > PackageLimit)
                return Failure(ModErrorCode.InvalidArgument, "Discovery requires a family result budget between 1 and 4096.");
            if (cancellationToken.IsCancellationRequested) return Failure(ModErrorCode.Cancelled, "World discovery was cancelled.");
            var attempt = registry.BeginDiscovery(package, cancellationToken);
            if (attempt == null) return Failure(ModErrorCode.Unavailable, "The selected package has no live discovery binding.");
            // Enrol synchronously before any queued constructor. Shutdown revokes new work, then awaits this lease.
            using var work = registry.RegisterDiscoveryWork(attempt);
            if (work == null) return OperationResult<bool>.Success(false);
            try
            {
                return await dispatcher.InvokeCallbackAsync(() => DiscoverCoreAsync(attempt, maximumResultsPerFamily, work)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                registry.AbandonDiscovery(attempt);
                return Failure(error is OperationCanceledException ? ModErrorCode.Cancelled : ModErrorCode.External,
                    "World discovery could not complete: " + error.Message);
            }
        }

        private async Task<OperationResult<bool>> DiscoverCoreAsync(RuntimeDiscoveryAttempt attempt, int requestedLimit, IRuntimeDiscoveryWork work)
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(attempt.OwnerContext.Lifetime.StoppingToken);
            using var deadline = new DiscoveryDeadline(stopping, dispatcher, attempt.OwnerContext.Logger, timeout,
                attempt.CancellationToken, work);
            var cleanupFailed = false;
            var worlds = new List<DiscoveredWorldObservation>();
            var availability = new List<DeclarationAvailability>();
            var families = attempt.Families.OrderBy(family => family.DeclarationId, StringComparer.Ordinal).ToArray();
            // Every family gets an equal bounded opportunity; input order cannot let one consume the package budget.
            var limit = Math.Min(requestedLimit, PackageLimit / families.Length);
            if (limit == 0)
            {
                registry.AbandonDiscovery(attempt);
                return Failure(ModErrorCode.InvalidState, "The package declares too many discovery families.");
            }
            foreach (var family in families)
            {
                if (stopping.IsCancellationRequested) break;
                var result = await DiscoverFamilyAsync(attempt, family, limit, stopping, deadline.RequestStop, work);
                cleanupFailed |= result.CleanupFailed;
                if (result.Worlds != null) worlds.AddRange(result.Worlds);
                else availability.Add(new DeclarationAvailability("world", family.DeclarationId,
                    new[] { new LaunchBlock(LaunchBlockCode.WorldUnavailable, family.DeclarationId, attempt.Package.Version) }));
            }
            if (attempt.CancellationToken.IsCancellationRequested) deadline.RequestStop();
            if (stopping.IsCancellationRequested)
            {
                registry.AbandonDiscovery(attempt);
                var reason = deadline.TimedOut ? "World discovery timed out" : "World discovery was cancelled";
                if (cleanupFailed || deadline.CancellationFailed)
                    return Failure(ModErrorCode.External, reason + " and scoped cleanup failed; see owner diagnostics.");
                return Failure(deadline.TimedOut ? ModErrorCode.TimedOut : ModErrorCode.Cancelled,
                    reason + "; all started callbacks and scopes have drained.");
            }
            return OperationResult<bool>.Success(registry.PublishDiscovery(attempt, worlds, availability));
        }

        private async Task<FamilyResult> DiscoverFamilyAsync(RuntimeDiscoveryAttempt attempt,
            ISessionImplementation<IWorldDiscoverySource> family, int limit, CancellationTokenSource stopping,
            Action requestStop, IRuntimeDiscoveryWork work)
        {
            ModContextScope? scope = null;
            IReadOnlyList<DiscoveredWorldObservation>? worlds = null;
            var failures = new List<Exception>();
            var cleanupFailed = false;
            try
            {
                stopping.Token.ThrowIfCancellationRequested();
                var scopeId = "discovery-" + Guid.NewGuid().ToString("N");
                // Discovery may inspect the world inventory, but cannot reserve a native scene transition.
                var access = new NativeTransitionAccessSlot(scopeId + ":" + attempt.Package.Id, scopeId, () => false);
                scope = await attempt.OwnerContext.CreateChildScopeAsync(scopeId, stopping.Token, requestStop, access, dispatcher);
                stopping.Token.ThrowIfCancellationRequested();
                var source = family.Create() ?? throw new InvalidOperationException("The discovery constructor returned no source.");
                if (source is IDisposable resource) scope.Lifetime.Track(resource);
                stopping.Token.ThrowIfCancellationRequested();
                var task = source.DiscoverAsync(new DiscoveryContext(family.DeclarationId, limit, scope.Context), stopping.Token)
                    ?? throw new InvalidOperationException("The discovery source returned no task.");
                // Cancellation does not abandon a provider that ignores its token. Its scope and package stay alive.
                var discovered = await task;
                stopping.Token.ThrowIfCancellationRequested();
                if (discovered == null || !discovered.TryGetValue(out var descriptors))
                    throw new InvalidOperationException("Discovery failed: " + discovered?.ErrorCode + ": " + discovered?.ErrorMessage);
                worlds = ValidateResults(attempt, family.DeclarationId, descriptors, limit);
            }
            catch (Exception error)
            {
                failures.Add(error);
                if (error is ScopedContextConstructionException construction)
                {
                    cleanupFailed = true;
                    work.RecordCleanupFailure(construction.CleanupFailure);
                }
            }
            finally
            {
                if (scope != null)
                {
                    try { await scope.CloseAsync(); }
                    catch (Exception cleanup) { cleanupFailed = true; failures.Add(cleanup); work.RecordCleanupFailure(cleanup); }
                }
            }
            if (failures.Count != 0)
            {
                // Availability is structured and bounded; detailed primary/cleanup errors remain in owner diagnostics.
                try { attempt.OwnerContext.Logger.Error(new AggregateException(failures), "Discovery failed for " + family.DeclarationId + "."); }
                catch { /* A diagnostic sink cannot prevent independent family cleanup or publication. */ }
                return new FamilyResult(null, cleanupFailed);
            }
            return new FamilyResult(worlds);
        }

        private IReadOnlyList<DiscoveredWorldObservation> ValidateResults(RuntimeDiscoveryAttempt attempt, string family,
            IReadOnlyList<DiscoveredWorldDescriptor> descriptors, int limit)
        {
            if (descriptors == null) throw new InvalidOperationException("The discovery source returned no descriptors.");
            var count = descriptors.Count;
            if (count < 0 || count > limit) throw new InvalidOperationException("The discovery source exceeded its result budget.");
            var values = new List<DiscoveredWorldObservation>(count);
            // Index the bounded snapshot rather than trusting a custom collection's unrelated unbounded enumerator.
            for (var index = 0; index < count; index++)
            {
                var item = descriptors[index];
                if (item == null || !string.Equals(item.FamilyId, family, StringComparison.Ordinal))
                    throw new InvalidOperationException("A discovered instance must name its declared family exactly.");
                values.Add(new DiscoveredWorldObservation(item.Id, family, item.Name, item.Description));
            }
            var envelope = new RuntimeObservationEnvelope(attempt.ProfileId, attempt.ProfileRevision, attempt.Package,
                attempt.PackageSetDigest, 1, values, Array.Empty<DeclarationAvailability>());
            var accepted = RuntimeObservation.FromEnvelopes(registry.Capture().Profile, new[] { envelope });
            if (accepted.DiscoveredWorlds.Count != values.Count)
                throw new InvalidOperationException("Discovered instances conflict with static declarations or a longer owner/family.");
            return accepted.DiscoveredWorlds;
        }

        private static OperationResult<bool> Failure(ModErrorCode code, string message) => OperationResult<bool>.Failure(code, message);
        private sealed class FamilyResult
        {
            internal FamilyResult(IReadOnlyList<DiscoveredWorldObservation>? worlds, bool cleanupFailed = false)
            { Worlds = worlds; CleanupFailed = cleanupFailed; }
            internal bool CleanupFailed { get; }
            internal IReadOnlyList<DiscoveredWorldObservation>? Worlds { get; }
        }
        private sealed class DiscoveryDeadline : IDisposable
        {
            private readonly CancellationTokenSource stopping;
            private readonly IHostDispatcher dispatcher;
            private readonly IModLogger logger;
            private readonly IRuntimeDiscoveryWork work;
            private readonly Timer timer;
            private readonly CancellationTokenRegistration callerCancellation;
            private int disposed;
            internal DiscoveryDeadline(CancellationTokenSource stopping, IHostDispatcher dispatcher,
                IModLogger logger, TimeSpan timeout, CancellationToken callerToken, IRuntimeDiscoveryWork work)
            {
                this.stopping = stopping; this.dispatcher = dispatcher; this.logger = logger; this.work = work;
                timer = new Timer(_ => QueueCancellation(true), null, timeout, Timeout.InfiniteTimeSpan);
                callerCancellation = callerToken.Register(RequestStop);
            }
            internal bool TimedOut { get; private set; }
            internal bool CancellationFailed { get; private set; }
            internal void RequestStop() => QueueCancellation(false);
            private void QueueCancellation(bool timedOut)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                try
                {
                    if (dispatcher.IsCurrent) RequestCancellation(timedOut);
                    else dispatcher.Post(() => RequestCancellation(timedOut));
                }
                catch (Exception error) { Report(error); }
            }
            private void RequestCancellation(bool timedOut)
            {
                if (Volatile.Read(ref disposed) != 0 || stopping.IsCancellationRequested) return;
                TimedOut = timedOut;
                // Caller/timeout callbacks can originate on workers. Provider token callbacks always run
                // on the host, and their failures belong to discovery and package cleanup evidence.
                try { stopping.Cancel(); }
                catch (Exception error) { Report(error); }
            }
            private void Report(Exception error)
            {
                CancellationFailed = true;
                work.RecordCleanupFailure(error);
                try { logger.Error(error, "Discovery cancellation failed."); }
                catch { /* Diagnostic failure cannot escape a cancellation callback. */ }
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    callerCancellation.Dispose();
                    timer.Dispose();
                }
            }
        }
        private sealed class DiscoveryContext : IWorldDiscoveryContext
        {
            internal DiscoveryContext(string familyId, int maximumResults, IModContext context)
            { FamilyId = familyId; MaximumResults = maximumResults; Context = context; }
            public string FamilyId { get; }
            public int MaximumResults { get; }
            public IModContext Context { get; }
        }
    }
}
