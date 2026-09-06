using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.ModManager
{
    internal interface IAssetNativeWorkDrain { Task DrainAsync(); }
    internal interface IInternalNativeWorkLifetime
    {
        AssetNativeWorkTicket RegisterNativeWork(string description);
        Task DrainNativeWorkAsync();
    }
    internal sealed class AssetNativeWorkTicket
    {
        private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<AssetNativeWorkTicket>? completed;
        internal AssetNativeWorkTicket(string description, Action<AssetNativeWorkTicket> completed)
        { Description = description; this.completed = completed; }
        public string Description { get; }
        public Task Completion => completion.Task;
        public void Complete(Exception? failure = null)
        {
            if (failure == null)
            {
                if (completion.TrySetResult(true)) Interlocked.Exchange(ref completed, null)?.Invoke(this);
            }
            else if (completion.TrySetException(failure)) Interlocked.Exchange(ref completed, null);
        }
    }
    internal sealed class AssetNativeWorkTracker : IAssetNativeWorkDrain
    {
        private readonly object sync = new object();
        private readonly List<AssetNativeWorkTicket> tickets = new List<AssetNativeWorkTicket>();
        private bool stopped;
        private Task? draining;
        public AssetNativeWorkTicket Begin(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("A native work description is required.", nameof(description));
            lock (sync)
            {
                if (stopped) throw new ObjectDisposedException(nameof(AssetNativeWorkTracker));
                var ticket = new AssetNativeWorkTicket(description, RemoveSuccessfulTicket); tickets.Add(ticket); return ticket;
            }
        }
        private void RemoveSuccessfulTicket(AssetNativeWorkTicket ticket)
        { lock (sync) tickets.Remove(ticket); }
        public bool HasPendingWork { get { lock (sync) return tickets.Exists(ticket => !ticket.Completion.IsCompleted); } }
        public void Stop() { lock (sync) stopped = true; }
        public Task DrainAsync()
        {
            lock (sync)
            {
                if (draining != null) return draining;
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                draining = completion.Task;
                _ = DrainCoreAsync(completion);
                return completion.Task;
            }
        }
        private async Task DrainCoreAsync(TaskCompletionSource<bool> completion)
        {
            var errors = new List<Exception>();
            while (true)
            {
                AssetNativeWorkTicket[] snapshot;
                lock (sync)
                {
                    snapshot = tickets.ToArray();
                    if (snapshot.Length == 0) { draining = null; break; }
                }
                var tasks = new List<Task>();
                foreach (var ticket in snapshot) tasks.Add(ticket.Completion);
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch
                {
                    foreach (var ticket in snapshot)
                        if (ticket.Completion.Exception != null)
                            errors.Add(new InvalidOperationException(ticket.Description + " failed during native completion.", ticket.Completion.Exception.Flatten()));
                }
                lock (sync) foreach (var ticket in snapshot) tickets.Remove(ticket);
            }
            if (errors.Count == 0) completion.TrySetResult(true);
            else completion.TrySetException(new AggregateException("Native asset work failed to drain cleanly.", errors));
        }
    }
}
