using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// The toast stack's bookkeeping, Unity-free so it is tested: a bounded queue of waiting toasts, at most a few shown at
    /// once in pooled views, each shown toast's time left, and where each rests, top to bottom, below the taller or shorter
    /// toasts above it. Views are reused, and a view's last exit left it faded out and slid aside, so every toast a view
    /// takes is presented fresh: placed where it rests and brought in, whatever that view showed before.
    /// </summary>
    /// <typeparam name="T">What a toast carries, such as its text and tone.</typeparam>
    internal sealed class TopiaForgeToastStack<T>
    {
        private readonly List<ViewState> views = new List<ViewState>();
        private readonly Queue<Entry> waiting = new Queue<Entry>();
        private readonly int maxShown;
        private readonly int maxWaiting;
        private readonly float gap;

        /// <param name="maxShown">The most toasts shown at once.</param>
        /// <param name="maxWaiting">The most toasts waiting to show; past it, the oldest waiting one is dropped.</param>
        /// <param name="gap">The space between two toasts that rest one above the other.</param>
        internal TopiaForgeToastStack(int maxShown, int maxWaiting, float gap)
        {
            if (maxShown < 1) throw new ArgumentOutOfRangeException(nameof(maxShown));
            if (maxWaiting < 1) throw new ArgumentOutOfRangeException(nameof(maxWaiting));
            if (gap < 0f || float.IsNaN(gap)) throw new ArgumentOutOfRangeException(nameof(gap));
            this.maxShown = maxShown;
            this.maxWaiting = maxWaiting;
            this.gap = gap;
        }

        /// <summary>Gets how many views the stack has made; a view's index never changes.</summary>
        internal int ViewCount => views.Count;

        /// <summary>Gets how many toasts wait for a view.</summary>
        internal int WaitingCount => waiting.Count;

        /// <summary>Queues a toast to show for <paramref name="seconds"/>. Returns true when the queue was full and its oldest toast was dropped.</summary>
        internal bool Enqueue(T toast, float seconds)
        {
            var dropped = false;
            if (waiting.Count >= maxWaiting)
            {
                waiting.Dequeue();
                dropped = true;
            }

            waiting.Enqueue(new Entry(toast, seconds));
            return dropped;
        }

        /// <summary>
        /// Gives the next waiting toast a view while fewer than the most toasts show: a view no toast holds, or a new one. The
        /// view is fresh until it is next placed.
        /// </summary>
        internal bool TryShowNext(out int view, out T toast)
        {
            view = -1;
            toast = default!;
            if (waiting.Count == 0 || HeldCount() >= maxShown)
            {
                return false;
            }

            var entry = waiting.Dequeue();
            view = FreeView();
            var state = views[view];
            state.Held = true;
            state.Leaving = false;
            state.Fresh = true;
            state.Remaining = entry.Seconds;
            state.Height = 0f;
            toast = entry.Toast;
            return true;
        }

        /// <summary>Sets how tall a view's toast is, as its view measured the text; the toasts below rest that much lower.</summary>
        internal void SetHeight(int view, float height)
        {
            if (view < 0 || view >= views.Count) return;
            views[view].Height = height > 0f && !float.IsInfinity(height) ? height : 0f;
        }

        /// <summary>
        /// Counts <paramref name="seconds"/> off every shown toast that is not leaving, and fills <paramref name="expired"/>
        /// with the views whose time ran out; those are leaving now, and keep their view until <see cref="Release"/>.
        /// </summary>
        internal void Expire(float seconds, List<int> expired)
        {
            expired.Clear();
            for (var index = 0; index < views.Count; index++)
            {
                var state = views[index];
                if (!state.Held || state.Leaving)
                {
                    continue;
                }

                state.Remaining -= seconds;
                if (state.Remaining <= 0f)
                {
                    state.Leaving = true;
                    expired.Add(index);
                }
            }
        }

        /// <summary>
        /// Gets where a view's toast rests: false for a view that holds no toast or is leaving; otherwise how far below the
        /// first toast's top it rests, past each toast above it and a gap, and whether it is fresh, to be placed and brought
        /// in rather than moved. A view is fresh for one placement only.
        /// </summary>
        internal bool TryPlace(int view, out float top, out bool fresh)
        {
            top = 0f;
            fresh = false;
            if (view < 0 || view >= views.Count || !Rests(views[view]))
            {
                return false;
            }

            for (var index = 0; index < view; index++)
            {
                if (Rests(views[index])) top += views[index].Height + gap;
            }

            fresh = views[view].Fresh;
            views[view].Fresh = false;
            return true;
        }

        /// <summary>Frees a view whose exit has finished, so it takes the next toast. A view the stack does not hold is ignored.</summary>
        internal void Release(int view)
        {
            if (view < 0 || view >= views.Count)
            {
                return;
            }

            var state = views[view];
            state.Held = false;
            state.Leaving = false;
            state.Fresh = false;
            state.Remaining = 0f;
        }

        /// <summary>Forgets every view and waiting toast.</summary>
        internal void Clear()
        {
            views.Clear();
            waiting.Clear();
        }

        private static bool Rests(ViewState state) => state.Held && !state.Leaving;

        private int HeldCount()
        {
            var count = 0;
            for (var index = 0; index < views.Count; index++)
            {
                if (views[index].Held) count++;
            }

            return count;
        }

        private int FreeView()
        {
            for (var index = 0; index < views.Count; index++)
            {
                if (!views[index].Held) return index;
            }

            views.Add(new ViewState());
            return views.Count - 1;
        }

        private readonly struct Entry
        {
            internal Entry(T toast, float seconds)
            {
                Toast = toast;
                Seconds = seconds;
            }

            internal T Toast { get; }

            internal float Seconds { get; }
        }

        private sealed class ViewState
        {
            internal bool Held;
            internal bool Leaving;
            internal bool Fresh;
            internal float Remaining;
            internal float Height;
        }
    }
}
