using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private IRuntimeSessionShutdown? sessionShutdown;
        private IHostDispatcher? sessionDispatcher;
        private TaskCompletionSource<OperationResult<bool>>? shutdownCompletion;

        internal void AttachSessionLifecycle(IRuntimeSessionShutdown sessions, IHostDispatcher dispatcher)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (shutdownCompletion != null) throw new ObjectDisposedException(nameof(ModRuntime));
            if (sessionShutdown != null) throw new InvalidOperationException("A runtime already has a session lifecycle.");
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            sessionShutdown = sessions;
            sessionDispatcher = dispatcher;
        }

        internal IReadOnlyList<Exception> ShutdownFailures { get; private set; } = Array.Empty<Exception>();

        /// <summary>Requests cancellation and cleanup without blocking the game thread.</summary>
        public void UnloadAll() => _ = UnloadAllAsync();

        /// <summary>Waits for session work to drain before invoking package unload callbacks or disposing services.</summary>
        public Task<OperationResult<bool>> UnloadAllAsync()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (shutdownCompletion != null) return shutdownCompletion.Task;
            shutdownCompletion = new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new List<Exception>();
            // Publish the shutdown request before callbacks so reentrant unload requests share this completion.
            foreach (var loaded in loadedMods)
                Attempt(() => loaded.Context.BeginStopping(), failures, "Mod cancellation failed for " + loaded.Manifest.Id + ".");

            Task<OperationResult<bool>>? drain = null;
            Attempt(() =>
            {
                if (sessionShutdown != null)
                    drain = sessionShutdown.ShutdownAsync()
                        ?? throw new InvalidOperationException("Session shutdown returned no drain task.");
            }, failures, "Session shutdown could not begin.");
            if (drain == null || drain.IsCompleted)
            {
                CompleteAfterSessionDrain(drain, failures);
            }
            else
            {
                // The host dispatcher outlives both package scopes and the runtime's gameplay services.
                _ = drain.ContinueWith(completed => sessionDispatcher!.Post(
                    () => CompleteAfterSessionDrain(completed, failures)), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return shutdownCompletion.Task;
        }

        private void CompleteAfterSessionDrain(Task<OperationResult<bool>>? drain, List<Exception> failures)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (drain != null)
            {
                Attempt(() =>
                {
                    // Already completed: observing the result here never waits on the game thread.
                    var outcome = drain.GetAwaiter().GetResult();
                    if (!outcome.Succeeded) throw new InvalidOperationException(outcome.ErrorMessage);
                }, failures, "Session cleanup reported a failure.");
            }

            // Keep the active runtime generation attached until ignored-cancellation session work has drained.
            // Native records survive detachment, so a replacement still encounters their Busy/quarantine state.
            Attempt(() => nativeHost?.DetachRuntime(runtimeOwnershipId), failures, "Native runtime access could not be revoked.");

            for (var index = loadedMods.Count - 1; index >= 0; index--)
            {
                var loaded = loadedMods[index];
                var before = failures.Count;
                Attempt(loaded.Instance.OnUnload, failures, "Mod failed during OnUnload: " + loaded.Manifest.Id);
                Attempt(loaded.Context.DisposeLifetime, failures, "Mod lifetime cleanup failed for " + loaded.Manifest.Id + ".");
                Attempt(() => CleanupOwnedFrameworkServices(loaded.Manifest.Id), failures,
                    "Framework service cleanup failed for " + loaded.Manifest.Id + ".");
                Attempt(() => serviceRegistry.UnregisterOwner(loaded.Manifest.Id), failures,
                    "Mod service cleanup failed for " + loaded.Manifest.Id + ".");
                if (failures.Count == before)
                {
                    try { logger.Info("Unloaded mod " + loaded.Manifest.Id + "."); }
                    catch { /* Diagnostic sinks cannot prevent independent cleanup. */ }
                }
            }

            loadedMods.Clear();
            loadedModIds.Clear();
            assemblyOwners.Clear();
            assemblyCatalog = null;
            loadingOwnerId = null;
            updateFailureLogged.Clear();
            sceneFailureLogged.Clear();
            failedMods.Clear();
            runtimeInfo.SetCapabilityRefresher(null);
            coreGameplayServices.FixedUpdate -= DispatchFixedUpdate;
            coreGameplayServices.LateUpdate -= DispatchLateUpdate;
            Attempt(coreGameplayServices.Dispose, failures, "Gameplay host cleanup failed.");
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
            sessionShutdown = null;
            sessionDispatcher = null;
            ShutdownFailures = failures.AsReadOnly();
            shutdownCompletion!.TrySetResult(failures.Count == 0
                ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(ModErrorCode.External,
                    "Runtime shutdown encountered " + failures.Count + " cleanup failure(s); see manager logs."));
        }

        private void Attempt(Action action, List<Exception> failures, string message)
        {
            try { action(); }
            catch (Exception exception)
            {
                failures.Add(exception);
                try { logger.Error(exception, message); }
                catch { /* Cleanup retains its outcome even if all log sinks have stopped. */ }
            }
        }
    }
}
