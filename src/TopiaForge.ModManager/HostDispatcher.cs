using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.ModManager
{
    /// <summary>Host-owned dispatch survives participant scopes and never discards pending cleanup.</summary>
    internal sealed class HostDispatcher : IHostDispatcher, IDisposable
    {
        private readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        private readonly object gate = new object();
        private readonly Queue<Action> pending = new Queue<Action>();
        private readonly Action<Exception> report;
        private readonly SynchronizationContext context;
        private int callbacks;
        private bool disposed;

        internal HostDispatcher(Action<Exception>? report = null)
        {
            this.report = report ?? (_ => { });
            context = new HostSynchronizationContext(this);
        }

        public bool IsCurrent => Thread.CurrentThread.ManagedThreadId == threadId;
        internal bool HasPendingWork { get { lock (gate) return pending.Count != 0 || callbacks != 0; } }

        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HostDispatcher));
                pending.Enqueue(action);
            }
        }

        public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var completion = NewCompletion<T>();
            void Run()
            {
                try { completion.TrySetResult(WithContext(action)); }
                catch (Exception error) { completion.TrySetException(error); }
            }
            if (IsCurrent)
            {
                lock (gate) if (disposed) throw new ObjectDisposedException(nameof(HostDispatcher));
                Run();
            }
            else Post(Run);
            return completion.Task;
        }

        public Task<T> InvokeCallbackAsync<T>(Func<Task<T>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var completion = NewCompletion<T>();
            void Begin()
            {
                lock (gate)
                {
                    if (disposed) throw new ObjectDisposedException(nameof(HostDispatcher));
                    callbacks++;
                }
                Task<T> task;
                try { task = WithContext(callback) ?? throw new InvalidOperationException("An activation callback returned no task."); }
                catch (Exception error)
                {
                    lock (gate) callbacks--;
                    completion.TrySetException(error);
                    return;
                }
                task.ContinueWith(finished => Post(() =>
                {
                    lock (gate) callbacks--;
                    if (finished.IsCanceled) completion.TrySetCanceled();
                    else if (finished.IsFaulted) completion.TrySetException(finished.Exception!.InnerExceptions);
                    else completion.TrySetResult(finished.Result);
                }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (IsCurrent) Begin(); else Post(Begin);
            return completion.Task;
        }

        internal void Drain(int maximumCallbacks = 1024)
        {
            if (!IsCurrent) throw new InvalidOperationException("Only the owning host thread may drain callbacks.");
            if (maximumCallbacks < 1) throw new ArgumentOutOfRangeException(nameof(maximumCallbacks));
            for (var count = 0; count < maximumCallbacks; count++)
            {
                Action action;
                lock (gate)
                {
                    if (pending.Count == 0) return;
                    action = pending.Dequeue();
                }
                try { action(); }
                catch (Exception error) { try { report(error); } catch { } }
            }
        }

        public void Dispose()
        {
            if (!IsCurrent) throw new InvalidOperationException("Dispatcher teardown belongs to its host thread.");
            lock (gate)
            {
                if (pending.Count != 0 || callbacks != 0)
                    throw new InvalidOperationException("The dispatcher must drain every callback before teardown.");
                disposed = true;
            }
        }

        private T WithContext<T>(Func<T> action)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try { return action(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        private static TaskCompletionSource<T> NewCompletion<T>() =>
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class HostSynchronizationContext : SynchronizationContext
        {
            private readonly HostDispatcher host;
            internal HostSynchronizationContext(HostDispatcher host) { this.host = host; }
            public override void Post(SendOrPostCallback callback, object? state) =>
                host.Post(() => host.WithContext(() => { callback(state); return true; }));
            public override void Send(SendOrPostCallback callback, object? state)
            {
                if (!host.IsCurrent) throw new InvalidOperationException("Cross-thread synchronous dispatch would block the host.");
                host.WithContext(() => { callback(state); return true; });
            }
            public override SynchronizationContext CreateCopy() => this;
        }
    }
}
