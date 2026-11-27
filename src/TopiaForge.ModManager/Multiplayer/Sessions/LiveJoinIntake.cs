using System;
using System.Threading;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// Takes the live joins the launcher stages for this running game (<c>session-join-&lt;request key&gt;.json</c>) off the main
    /// thread: a timer looks once a second, on a pool thread, and a join it takes waits in a slot the frame reads without
    /// allocating. The store's take already refuses a join for another request, a malformed one, and a stale claim; while a
    /// join waits in the slot the timer takes no other, so a newer join the launcher writes meanwhile replaces the waiting
    /// file and is taken next. Only a game a launcher started has a request to take joins for; any other looks for none.
    /// </summary>
    internal sealed class LiveJoinIntake : IDisposable
    {
        internal const int PollMilliseconds = 1000;

        private readonly ILauncherChannels channels;
        private readonly TimerCallback poll;
        private Timer? timer;
        private SessionJoinRequest? arrived;
        private int polling;

        internal LiveJoinIntake(ILauncherChannels channels)
        {
            this.channels = channels ?? throw new ArgumentNullException(nameof(channels));
            poll = _ => Poll();
        }

        /// <summary>Gets whether this game takes live joins at all: a launcher started it.</summary>
        internal bool IsListening => channels.RequestId != null;

        /// <summary>Starts looking once a second, when a launcher started this game; once.</summary>
        internal void Start()
        {
            if (!IsListening || timer != null) return;
            timer = new Timer(poll, null, PollMilliseconds, PollMilliseconds);
        }

        /// <summary>
        /// One look for a staged join, off the main thread. A look still running, or a join the frame has not taken yet, skips
        /// it. A channel that cannot be read now is looked at again next time; nothing here throws.
        /// </summary>
        internal void Poll()
        {
            if (Volatile.Read(ref arrived) != null || Interlocked.Exchange(ref polling, 1) == 1) return;
            try
            {
                var join = channels.TakeJoin();
                if (join != null) Interlocked.CompareExchange(ref arrived, join, null);
            }
            catch (Exception)
            {
                // The take refuses what it cannot read; anything else is looked at again on the next tick.
            }
            finally
            {
                Volatile.Write(ref polling, 0);
            }
        }

        /// <summary>Main thread: the join taken since the last frame, once; null when none was. It allocates nothing.</summary>
        internal SessionJoinRequest? TakeArrived() => Volatile.Read(ref arrived) == null ? null : Interlocked.Exchange(ref arrived, null);

        public void Dispose()
        {
            var running = timer;
            timer = null;
            running?.Dispose();
        }
    }
}
