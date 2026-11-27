using System;
using System.Threading;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Runs multiplayer's file work away from the frame: hashing packages that have no install receipt, and writing the
    /// multiplayer settings. In the game it is a background thread; tests run the work inline or by hand, so they stay
    /// deterministic.
    /// </summary>
    internal interface IMultiplayerWorkScheduler
    {
        /// <summary>Runs <paramref name="work"/> once, soon. The work records its own failures; one that escapes is dropped.</summary>
        void Schedule(Action work);
    }

    /// <summary>The schedulers the runtime and its tests use.</summary>
    internal static class MultiplayerWorkSchedulers
    {
        /// <summary>A background thread at below-normal priority, so the work never holds a frame.</summary>
        internal static readonly IMultiplayerWorkScheduler Background = new BackgroundScheduler();

        /// <summary>Runs the work on the calling thread, for tests that need the old synchronous order.</summary>
        internal static readonly IMultiplayerWorkScheduler Inline = new InlineScheduler();

        /// <summary>Runs <paramref name="work"/>, never letting a failure escape onto a thread that cannot take one.</summary>
        internal static void RunGuarded(Action work)
        {
            try
            {
                work();
            }
            catch (Exception)
            {
                // The work records its own failures; anything else must not take the game down from a worker thread.
            }
        }

        private sealed class BackgroundScheduler : IMultiplayerWorkScheduler
        {
            public void Schedule(Action work)
            {
                if (work == null) throw new ArgumentNullException(nameof(work));
                var thread = new Thread(() => RunGuarded(work))
                {
                    IsBackground = true,
                    Name = "TopiaForge multiplayer files",
                    Priority = ThreadPriority.BelowNormal
                };
                thread.Start();
            }
        }

        private sealed class InlineScheduler : IMultiplayerWorkScheduler
        {
            public void Schedule(Action work)
            {
                if (work == null) throw new ArgumentNullException(nameof(work));
                RunGuarded(work);
            }
        }
    }
}
