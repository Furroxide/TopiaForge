using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;

namespace TopiaForge.ModManager.Tests
{
    internal static class HostDispatcherTests
    {
        internal static void Run()
        {
            var failures = new List<string>();
            var errors = new List<Exception>();
            var host = new HostDispatcher(errors.Add);
            var thread = Thread.CurrentThread.ManagedThreadId;
            var original = SynchronizationContext.Current;
            SynchronizationContext? callbackContext = null;
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callback = host.InvokeCallbackAsync(async () =>
            {
                callbackContext = SynchronizationContext.Current;
                await signal.Task;
                return Thread.CurrentThread.ManagedThreadId;
            });
            if (!ReferenceEquals(original, SynchronizationContext.Current)) failures.Add("Callback context was not restored");
            try { host.Dispose(); failures.Add("Dispatcher discarded a pending callback"); } catch (InvalidOperationException) { }
            Task.Run(() => signal.SetResult(true)).GetAwaiter().GetResult();
            Pump(host, callback);
            if (callback.Result != thread || callbackContext == null) failures.Add("Worker completion resumed outside host context");
            var send = Task.Run(() =>
            {
                try { callbackContext!.Send(_ => { }, null); return false; } catch (InvalidOperationException) { return true; }
            });
            if (!send.GetAwaiter().GetResult()) failures.Add("Cross-thread Send was allowed");
            host.Post(() => throw new InvalidOperationException("subscriber failure"));
            var delivered = false;
            host.Post(() => delivered = true);
            host.Drain();
            if (!delivered || errors.Count != 1) failures.Add("Post failure stopped later dispatch");
            SynchronizationContext? constructorContext = null;
            host.InvokeAsync(() => constructorContext = SynchronizationContext.Current).GetAwaiter().GetResult();
            if (constructorContext == null || ReferenceEquals(constructorContext, original)) failures.Add("Constructor callback had no host synchronization context");
            host.Dispose();
            var activatedAfterDispose = false;
            try { host.InvokeCallbackAsync(() => { activatedAfterDispose = true; return Task.FromResult(true); }); }
            catch (ObjectDisposedException) { }
            if (activatedAfterDispose) failures.Add("Disposed dispatcher invoked author callback");
            if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
        }
        internal static void Pump(HostDispatcher dispatcher, Task task)
        {
            var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < end) { dispatcher.Drain(); Thread.Sleep(1); }
            dispatcher.Drain();
            if (!task.IsCompleted) throw new InvalidOperationException("Controlled host operation did not finish.");
            task.GetAwaiter().GetResult();
        }
    }
}
