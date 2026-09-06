using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private readonly List<Task> failedLoadCleanupTasks = new List<Task>();
        private int pendingFailedLoadCleanups;

        private void BeginFailedLoadCleanup(string ownerId, ModContext? context, IModEntrypoint? instance,
            bool onLoadStarted, bool notifyObserver)
        {
            // Enroll before cancellation or diagnostics can reenter shutdown or native admission.
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            failedLoadCleanupTasks.Add(completion.Task);
            pendingFailedLoadCleanups++;
            sessionBindings?.SetPackageCleanupPending(true);
            var failures = new List<Exception>();
            if (context != null) TryCleanup(context.BeginStopping);
            if (notifyObserver) TryCleanup(() => loadObserver?.OnLoadCompleted(ownerId, succeeded: false));

            var work = new List<Task>();
            if (context != null)
                TryCleanup(() => work.Add(((IInternalNativeWorkLifetime)context.Lifetime).DrainNativeWorkAsync()));
            // Revoke only this owner's claim. An unrelated owner's operation is not this transaction's work.
            TryCleanup(() => work.Add(sceneCoordinator.RevokeOwnerAndDrainAsync(ownerId)));
            var drain = Task.WhenAll(work);
            if (drain.IsCompleted) Complete(drain);
            else
                _ = drain.ContinueWith(completed => nativeDispatcher.Post(() => Complete(completed)),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            void TryCleanup(Action cleanup)
            {
                try { cleanup(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            void Complete(Task completed)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (completed.Exception != null) failures.AddRange(completed.Exception.Flatten().InnerExceptions);
                else if (completed.IsCanceled) failures.Add(new TaskCanceledException("Failed package native cleanup was cancelled before drain."));
                if (onLoadStarted && instance != null) TryCleanup(instance.OnUnload);
                if (context != null) TryCleanup(context.DisposeLifetime);
                TryCleanup(() => CleanupOwnedFrameworkServices(ownerId));
                TryCleanup(() => serviceRegistry.UnregisterOwner(ownerId));
                pendingFailedLoadCleanups--;
                sessionBindings?.SetPackageCleanupPending(pendingFailedLoadCleanups != 0);
                if (failures.Count == 0) completion.TrySetResult(true);
                else
                {
                    var failure = new AggregateException("Failed to clean up partially loaded mod " + ownerId + ".", failures);
                    try { logger.Error(failure, failure.Message); } catch { }
                    completion.TrySetException(failure);
                    // Retain the failed task for the shutdown barrier while observing it even if shutdown
                    // has not been requested yet. Native completion faults must never become unobserved.
                    _ = completion.Task.Exception;
                }
            }
        }

        internal Task WaitForFailedLoadCleanupAsync()
        {
            UnityMainThreadGuard.AssertCurrent();
            return ObserveFailedLoadCleanupAsync(failedLoadCleanupTasks.ToArray());
        }

        private static async Task ObserveFailedLoadCleanupAsync(Task[] tasks)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch
            {
                var failures = tasks.Where(task => task.Exception != null)
                    .SelectMany(task => task.Exception!.Flatten().InnerExceptions).ToArray();
                throw new AggregateException("Failed package cleanup did not complete cleanly.", failures);
            }
        }
    }
}
