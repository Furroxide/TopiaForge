using System;
using System.Collections.Generic;
using System.Threading;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// Runs multiplayer's file writes and reads away from the frame, one at a time and in order, and hands each result back
    /// to the main thread at <see cref="Pump"/>. The settings file is a disk write, and the launcher channels the game is to
    /// write, the session handoff and the preference journal, can wait for a shared file for a few hundred milliseconds while
    /// they retry; neither may hold a frame. The interface reads <see cref="IsBusy"/> to show that a write is pending and
    /// stays responsive meanwhile.
    /// </summary>
    internal sealed class MultiplayerBackgroundQueue
    {
        private readonly IMultiplayerWorkScheduler scheduler;
        private readonly object gate = new object();
        private readonly Queue<Item> work = new Queue<Item>();
        private readonly Queue<Action> completions = new Queue<Action>();
        private bool draining;
        private int outstanding;

        internal MultiplayerBackgroundQueue(IMultiplayerWorkScheduler scheduler)
        {
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        /// <summary>Gets whether any work is queued, running, or waiting for its result to reach the main thread.</summary>
        internal bool IsBusy => Volatile.Read(ref outstanding) > 0;

        /// <summary>
        /// Queues <paramref name="background"/> to run off the main thread, after everything queued before it. Its outcome,
        /// null or the exception it threw, reaches <paramref name="done"/> on the main thread at a later <see cref="Pump"/>.
        /// </summary>
        internal void Enqueue(Action background, Action<Exception?>? done = null)
        {
            if (background == null) throw new ArgumentNullException(nameof(background));
            bool start;
            Interlocked.Increment(ref outstanding);
            lock (gate)
            {
                work.Enqueue(new Item(background, done));
                start = !draining;
                draining = true;
            }

            if (!start) return;
            try
            {
                scheduler.Schedule(Drain);
            }
            catch (Exception exception)
            {
                // The work cannot start; every queued item fails on the main thread rather than run here.
                FailQueued(exception);
            }
        }

        /// <summary>Main thread, every frame: delivers the results that finished since the last call.</summary>
        internal void Pump()
        {
            while (true)
            {
                Action? next;
                lock (gate)
                {
                    if (completions.Count == 0) return;
                    next = completions.Dequeue();
                }

                try
                {
                    next();
                }
                finally
                {
                    Interlocked.Decrement(ref outstanding);
                }
            }
        }

        private void Drain()
        {
            while (true)
            {
                Item item;
                lock (gate)
                {
                    if (work.Count == 0)
                    {
                        draining = false;
                        return;
                    }

                    item = work.Dequeue();
                }

                Exception? failure = null;
                try
                {
                    item.Background();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                var done = item.Done;
                lock (gate) completions.Enqueue(() => done?.Invoke(failure));
            }
        }

        private void FailQueued(Exception exception)
        {
            lock (gate)
            {
                draining = false;
                while (work.Count > 0)
                {
                    var done = work.Dequeue().Done;
                    completions.Enqueue(() => done?.Invoke(exception));
                }
            }
        }

        private readonly struct Item
        {
            internal Item(Action background, Action<Exception?>? done)
            {
                Background = background;
                Done = done;
            }

            internal Action Background { get; }

            internal Action<Exception?>? Done { get; }
        }
    }
}
