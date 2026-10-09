using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal interface IAssetNativeRequest<T> : IDisposable where T : class, IDisposable
    {
        void Start();
        bool HasStarted { get; }
        bool IsDone { get; }
        OperationResult<T> ReadResult();
    }
    internal interface IAssetNativePoll { bool Poll(); }

    // Process-owned polling survives caller cancellation and package unload. There is no event
    // subscription window after allocation, and stopping a package cannot remove its native observer.
    internal static class AssetNativeRequestPump
    {
        private static readonly List<IAssetNativePoll> pending = new List<IAssetNativePoll>();
        private static bool polling;
        internal static void Add(IAssetNativePoll operation) => pending.Add(operation);
        internal static void Poll()
        {
            if (polling) return;
            polling = true;
            try
            {
                for (var index = pending.Count - 1; index >= 0; index--)
                    if (pending[index].Poll()) pending.RemoveAt(index);
            }
            finally { polling = false; }
        }
    }

    internal static class AssetNativeOperation<T> where T : class, IDisposable
    {
        internal static Task<OperationResult<T>> Start(IModLifetime lifetime, string description,
            Func<IAssetNativeRequest<T>> create, CancellationToken token)
        {
            if (lifetime.IsStopping || token.IsCancellationRequested) return Task.FromResult(Cancelled());
            if (!(lifetime is IInternalNativeWorkLifetime nativeLifetime))
                return Task.FromResult(OperationResult<T>.Failure(ModErrorCode.Unavailable, "The owner cannot retain native asset work."));
            AssetNativeWorkTicket ticket;
            try { ticket = nativeLifetime.RegisterNativeWork(description); }
            catch (ObjectDisposedException) { return Task.FromResult(Cancelled()); }
            var state = new State(lifetime, ticket);
            state.Begin(create, token);
            return state.Completion;
        }
        private static OperationResult<T> Cancelled() => OperationResult<T>.Failure(ModErrorCode.Cancelled, "The native asset load was cancelled.");

        private sealed class State : IAssetNativePoll
        {
            private readonly IModLifetime lifetime;
            private readonly AssetNativeWorkTicket ticket;
            private readonly TaskCompletionSource<OperationResult<T>> completion =
                new TaskCompletionSource<OperationResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly List<Exception> failures = new List<Exception>();
            private IAssetNativeRequest<T>? request;
            private CancellationTokenRegistration stoppingRegistration;
            private CancellationTokenRegistration callerRegistration;
            private bool terminal;
            private bool beginFinished;
            private bool pollFailureRecorded;
            private int cancelled;
            internal State(IModLifetime lifetime, AssetNativeWorkTicket ticket) { this.lifetime = lifetime; this.ticket = ticket; }
            internal Task<OperationResult<T>> Completion => completion.Task;
            internal void Begin(Func<IAssetNativeRequest<T>> create, CancellationToken token)
            {
                try
                {
                    stoppingRegistration = lifetime.StoppingToken.Register(Cancel);
                    callerRegistration = token.Register(Cancel);
                    if (Volatile.Read(ref cancelled) != 0) { Finish(false); return; }
                    request = create();
                    // Retain the adapter before calling the engine, including indeterminate Start failures.
                    AssetNativeRequestPump.Add(this);
                    if (Volatile.Read(ref cancelled) == 0 && !lifetime.IsStopping) request.Start();
                }
                catch (Exception error)
                {
                    failures.Add(error);
                    completion.TrySetResult(OperationResult<T>.Failure(ModErrorCode.External, "The native asset adapter failed: " + error.Message));
                }
                finally { beginFinished = true; }
                Poll();
            }
            private void Cancel()
            {
                Interlocked.Exchange(ref cancelled, 1);
                completion.TrySetResult(Cancelled());
            }
            public bool Poll()
            {
                if (terminal) return true;
                if (!beginFinished) return false;
                bool started;
                bool done;
                try { started = request != null && request.HasStarted; done = !started || request!.IsDone; }
                catch (Exception error)
                {
                    // A failed observation cannot prove retirement. Keep polling the retained request.
                    if (!pollFailureRecorded) { failures.Add(error); pollFailureRecorded = true; }
                    completion.TrySetResult(OperationResult<T>.Failure(ModErrorCode.External, "Native asset completion could not be observed: " + error.Message));
                    return false;
                }
                if (done) Finish(started);
                return terminal;
            }
            private void Finish(bool readResult)
            {
                if (terminal) return;
                terminal = true;
                OnceResource? owned = null;
                OperationResult<T>? result = null;
                Exception? nativeFailure = null;
                try
                {
                    if (readResult)
                    {
                        result = request!.ReadResult();
                        if (result.Succeeded) owned = new OnceResource(result.Value!);
                        else nativeFailure = new InvalidOperationException(result.ErrorCode + ": " + result.ErrorMessage);
                    }
                }
                catch (Exception error) { failures.Add(error); }
                // Release request pins before publication, so pin-release failures cannot return success.
                Try(() => request?.Dispose());
                if (owned != null)
                {
                    if (failures.Count == 0 && Volatile.Read(ref cancelled) == 0 && !lifetime.IsStopping)
                    {
                        try
                        {
                            lifetime.Track(owned);
                            if (lifetime.IsStopping || Volatile.Read(ref cancelled) != 0 || !completion.TrySetResult(result!))
                                Try(owned.Dispose);
                        }
                        catch (ObjectDisposedException) when (lifetime.IsStopping && owned.DisposalFailure == null)
                        { Try(owned.Dispose); }
                        catch (Exception error) { failures.Add(error); Try(owned.Dispose); }
                    }
                    else Try(owned.Dispose);
                }
                Try(stoppingRegistration.Dispose);
                Try(callerRegistration.Dispose);
                var nativeFailureDelivered = false;
                if (Volatile.Read(ref cancelled) != 0 || lifetime.IsStopping) completion.TrySetResult(Cancelled());
                else if (result != null && !result.Succeeded) nativeFailureDelivered = completion.TrySetResult(result);
                else if (failures.Count != 0) completion.TrySetResult(OperationResult<T>.Failure(ModErrorCode.External,
                    "Native asset work failed: " + new AggregateException(failures).Message));
                else if (result != null) completion.TrySetResult(result);
                else completion.TrySetResult(Cancelled());
                // Expected operation failures delivered to the caller are already actionable. Preserve
                // a later native failure only when cancellation/adapter failure hid that native result.
                if (nativeFailure != null && !nativeFailureDelivered) failures.Add(nativeFailure);
                ticket.Complete(failures.Count == 0 ? null : new AggregateException("Native asset completion or cleanup failed.", failures));
            }
            private void Try(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
        }
        private sealed class OnceResource : IDisposable
        {
            private T? resource;
            internal OnceResource(T resource) { this.resource = resource; }
            internal Exception? DisposalFailure { get; private set; }
            public void Dispose()
            {
                var current = Interlocked.Exchange(ref resource, null);
                if (current == null) return;
                try { current.Dispose(); }
                catch (Exception error) { DisposalFailure = error; throw; }
            }
        }
    }
}
