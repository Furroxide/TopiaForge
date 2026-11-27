using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiThemeScope
    {
        private static readonly Task<OperationResult<bool>> Preloaded = Task.FromResult(Success);
        private static readonly Task<OperationResult<bool>> Cancelled = Task.FromResult(Stopped);
        private static readonly Task<OperationResult<bool>> NotFound = Task.FromResult(Missing);
        private static readonly Task<OperationResult<bool>> Unavailable = Task.FromResult(UiThemeAcquisitions.Unavailable);
        private Dictionary<string, Preload>? preloads;

        public Task<OperationResult<bool>> PreloadAsync(UiThemeRef theme, CancellationToken cancellationToken = default)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            if (IsStopping || cancellationToken.IsCancellationRequested) return Cancelled;
            if (catalogue == null || !catalogue.TryGet(theme.Id, out var descriptor)) return NotFound;
            if (!UiThemeAcquisitions.HasNativeResources(descriptor)) return Preloaded;
            if (acquisitions == null) return Unavailable;

            preloads ??= new Dictionary<string, Preload>(StringComparer.Ordinal);
            if (!preloads.TryGetValue(theme.Id, out var preload))
            {
                preload = new Preload(theme.Id);
                preload.Demand = acquisitions.Demand(descriptor, result => SettlePreload(preload, result));
                if (preload.Demand == null) return Cancelled;
                preloads.Add(theme.Id, preload);
            }
            if (preload.Demand!.Outcome is { } settled)
            {
                if (settled.Succeeded && cancellationToken.IsCancellationRequested)
                {
                    if (!preload.Retained) ReleasePreload(preload);
                    return Cancelled;
                }
                if (settled.Succeeded)
                {
                    preload.Retained = true;
                    return Preloaded;
                }
                ReleasePreload(preload);
                return Task.FromResult(settled);
            }

            var waiter = new Waiter(cancellationToken);
            preload.Waiters.Add(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Cancellation = cancellationToken.Register(() =>
                    acquisitions.OnMain(() => CancelPreload(preload, waiter)));
                // Register can run the callback inline before returning its registration.
                if (waiter.Task.IsCompleted) waiter.Cancellation.Dispose();
            }
            return waiter.Task;
        }

        private void SettlePreload(Preload preload, OperationResult<bool> result)
        {
            foreach (var waiter in preload.Waiters)
            {
                var outcome = IsStopping || waiter.Token.IsCancellationRequested ? Stopped : result;
                preload.Retained |= outcome.Succeeded;
                waiter.Complete(outcome);
            }

            preload.Waiters.Clear();
            if (!preload.Retained || IsStopping || result.ErrorCode == ModErrorCode.Cancelled)
                ReleasePreload(preload);
        }

        private void CancelPreload(Preload preload, Waiter waiter)
        {
            if (!preload.Waiters.Remove(waiter)) return;
            waiter.Complete(Stopped);
            if (!preload.Retained && preload.Waiters.Count == 0)
                ReleasePreload(preload);
        }

        private void ReleasePreload(Preload preload)
        {
            preloads!.Remove(preload.Id);
            preload.Demand?.Dispose();
        }

        private void StopPreloads()
        {
            if (preloads == null) return;
            foreach (var preload in preloads.Values)
            {
                foreach (var waiter in preload.Waiters) waiter.Complete(Stopped);
                preload.Waiters.Clear();
                preload.Demand?.Dispose();
            }
            preloads.Clear();
        }

        private sealed class Preload
        {
            internal Preload(string id) { Id = id; }
            internal readonly string Id;
            internal readonly List<Waiter> Waiters = new List<Waiter>();
            internal UiThemeDemand? Demand;
            internal bool Retained;
        }

        private sealed class Waiter
        {
            private readonly TaskCompletionSource<OperationResult<bool>> completion =
                new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Waiter(CancellationToken token) { Token = token; }
            internal readonly CancellationToken Token;
            internal CancellationTokenRegistration Cancellation;
            internal Task<OperationResult<bool>> Task => completion.Task;

            internal void Complete(OperationResult<bool> outcome)
            {
                completion.TrySetResult(outcome);
                Cancellation.Dispose();
            }
        }
    }
}
