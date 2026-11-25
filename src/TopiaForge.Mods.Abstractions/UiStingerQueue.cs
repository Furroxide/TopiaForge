using System;

namespace TopiaForge.Mods
{
    internal enum UiStingerQueueResult
    {
        Started,
        Queued,
        Coalesced,
        RateLimited,
        Cancelled,
    }

    /// <summary>One accepted announcement and its scope-owned presentation state.</summary>
    internal readonly struct UiStingerQueueEntry<T> where T : notnull
    {
        internal UiStingerQueueEntry(UiStingerRequest request, T presentation)
        {
            Request = request;
            Presentation = presentation;
        }

        internal UiStingerRequest Request { get; }
        internal T Presentation { get; }
    }

    /// <summary>One scope's timed announcement queue. The caller owns the presentation state.</summary>
    internal sealed class UiStingerQueue<T> where T : notnull
    {
        private readonly UiStingerQueueEntry<T>[] waiting = new UiStingerQueueEntry<T>[3];
        private bool stopping;

        internal UiStingerQueueEntry<T>? Active { get; private set; }
        internal double ShownAt { get; private set; }
        internal int WaitingCount { get; private set; }

        /// <summary>Offers a validated request and returns any active presentation which expired before the offer.</summary>
        internal UiStingerQueueResult Enqueue(UiStingerRequest request, T presentation, double now, out UiStingerQueueEntry<T>? expired)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (presentation is null) throw new ArgumentNullException(nameof(presentation));
            expired = null;
            if (stopping) return UiStingerQueueResult.Cancelled;

            Advance(now, out expired);
            return EnqueueRetained(request, presentation, now);
        }

        /// <summary>Reports what admission would do without changing the active announcement or accepting resources.</summary>
        internal UiStingerQueueResult Classify(UiStingerRequest request)
        {
            if (stopping) return UiStingerQueueResult.Cancelled;
            if (Active is not { } current) return UiStingerQueueResult.Started;
            if (SameAnnouncement(current.Request, request)) return UiStingerQueueResult.Coalesced;
            for (var index = 0; index < WaitingCount; index++)
                if (SameAnnouncement(waiting[index].Request, request)) return UiStingerQueueResult.Coalesced;

            return WaitingCount == waiting.Length ? UiStingerQueueResult.RateLimited : UiStingerQueueResult.Queued;
        }

        /// <summary>Offers an announcement while the view owns the exact exit and promotion boundary.</summary>
        internal UiStingerQueueResult EnqueueRetained(UiStingerRequest request, T presentation, double now)
        {
            var result = Classify(request);
            if (result == UiStingerQueueResult.Started)
            {
                Active = new UiStingerQueueEntry<T>(request, presentation);
                ShownAt = now;
            }
            else if (result == UiStingerQueueResult.Queued)
                waiting[WaitingCount++] = new UiStingerQueueEntry<T>(request, presentation);

            return result;
        }

        internal bool IsExpired(double now) => !stopping && Active is { } current &&
            now - ShownAt >= current.Request.DurationSeconds;

        /// <summary>Expires the active presentation and starts the next for its complete display duration.</summary>
        internal bool Advance(double now, out UiStingerQueueEntry<T>? expired)
        {
            if (!double.IsFinite(now) || now < 0d) throw new ArgumentOutOfRangeException(nameof(now));
            expired = null;
            if (!IsExpired(now)) return false;

            expired = Active;
            Active = TakeWaiting();
            ShownAt = Active.HasValue ? now : 0d;
            return true;
        }

        /// <summary>Closes admission and yields one presentation for release. Repeat until the scope is drained.</summary>
        internal bool TryDrain(out UiStingerQueueEntry<T> removed)
        {
            stopping = true;
            var next = Active ?? TakeWaiting();
            Active = null;
            ShownAt = 0d;
            removed = next.GetValueOrDefault();
            return next.HasValue;
        }

        private UiStingerQueueEntry<T>? TakeWaiting()
        {
            if (WaitingCount == 0) return null;
            var first = waiting[0];
            Array.Copy(waiting, 1, waiting, 0, --WaitingCount);
            waiting[WaitingCount] = default;
            return first;
        }

        private static bool SameAnnouncement(UiStingerRequest first, UiStingerRequest second) =>
            string.Equals(first.Title, second.Title, StringComparison.Ordinal) &&
            string.Equals(first.Subtitle, second.Subtitle, StringComparison.Ordinal) &&
            first.Glyph == second.Glyph && first.Role == second.Role;
    }
}
