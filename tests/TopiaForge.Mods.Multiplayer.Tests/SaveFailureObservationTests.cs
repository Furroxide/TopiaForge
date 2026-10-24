extern alias provider;
using System.Runtime.CompilerServices;
using provider::TopiaForge.Multiplayer.Network;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A failed save is observed when it is superseded or retried, and even if its final retry fails after the caller
/// stops waiting. Its file-service text must never reach global unobserved-exception subscribers.
/// </summary>
internal static class SaveFailureObservationTests
{
    internal static void RetriedAndLateSaveFailuresAreObserved()
    {
        foreach (var known in new[] { false, true })
            foreach (var mode in new[] { "close", "changed-close", "pending-close", "changed-frame", "late-close" })
            {
                var raised = 0;
                EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
                {
                    if (args.Exception.Flatten().InnerExceptions.Any(error => error is SaveFailure))
                    {
                        Interlocked.Increment(ref raised);
                        args.SetObserved();
                    }
                };
                TaskScheduler.UnobservedTaskException += handler;
                try
                {
                    var weak = Exercise(known, mode);
                    for (var count = 0; count < 10; count++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    }
                    var label = (known ? "known-hosts" : "bans") + " " + mode;
                    TransportAssert.That(weak.All(item => !item.IsAlive), label + ": the failed save tasks were collected");
                    TransportAssert.That(raised == 0, label + ": no failed save reaches the unobserved-exception event (" + raised + ")");
                }
                finally { TaskScheduler.UnobservedTaskException -= handler; }
            }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Exercise(bool known, string mode)
    {
        using var files = new Files(mode == "late-close");
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var weak = new WeakReference(source.Task);
        if (mode != "pending-close") source.SetException(new SaveFailure());
        Task saved;
        if (known)
        {
            var store = new KnownHostsStore(files);
            store.EnsureReadAsync().GetAwaiter().GetResult();
            if (mode.StartsWith("changed", StringComparison.Ordinal)) store.MarkChanged();
            saved = mode == "changed-frame" ? store.SaveAfter(source.Task) : store.SaveOnClose(source.Task);
        }
        else
        {
            var store = new BanStore(files);
            store.EnsureReadAsync().GetAwaiter().GetResult();
            if (mode.StartsWith("changed", StringComparison.Ordinal)) store.MarkChanged();
            saved = mode == "changed-frame" ? store.SaveAfter(source.Task) : store.SaveOnClose(source.Task);
        }
        if (mode == "pending-close") source.SetException(new SaveFailure());
        if (mode == "late-close")
        {
            if (!files.Started.Wait(TimeSpan.FromSeconds(10))) throw new Exception("The final retry did not start.");
            files.Pending.SetException(new SaveFailure());
            if (!SpinWait.SpinUntil(() => saved.IsCompleted, TimeSpan.FromSeconds(10))) throw new Exception("The late retry did not finish.");
            TransportAssert.That(saved.IsFaulted, "observing a late failure preserves the failed task result");
        }
        else saved.GetAwaiter().GetResult();
        return new[] { weak, new WeakReference(saved) };
    }

    private sealed class SaveFailure : Exception { }
    private sealed class Files(bool late) : INetworkDataFiles, IDisposable
    {
        internal readonly ManualResetEventSlim Started = new();
        internal readonly TaskCompletionSource Pending = new();
        public void Dispose() => Started.Dispose();
        public Task<byte[]?> ReadAsync(string path, CancellationToken token) => Task.FromResult<byte[]?>(null);
        public Task WriteAsync(string path, byte[] content, CancellationToken token)
        {
            Started.Set();
            return late ? Pending.Task : Task.CompletedTask;
        }
    }
}
