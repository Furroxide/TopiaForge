using System;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Watches for the launcher that started this game asking it to quit (<see cref="LauncherQuitRequest"/>), so that a restart
    /// closes the game as its own Quit button does: the plugin's quit handler holds the quit until every mod has unloaded and
    /// the clean exit is recorded, and the restarted game does not open in safe mode. Polled once a frame, it looks at most once
    /// an interval, and a look is one file check until a request is there.
    /// </summary>
    internal sealed class LauncherQuitWatcher
    {
        private readonly Func<bool> pending;
        private readonly Func<LauncherQuitRequest?> take;
        private readonly Func<TimeSpan> clock;
        private readonly TimeSpan interval;
        private readonly Action<Exception> report;
        private TimeSpan nextLook;
        private bool asked;
        private bool reported;

        /// <param name="pending">Whether a request may be waiting: a cheap check, made each look.</param>
        /// <param name="take">Takes the request for this process, once; null when none is there or it is not for this process.</param>
        /// <param name="clock">Elapsed time since the game started.</param>
        /// <param name="interval">How often to look.</param>
        /// <param name="report">Reports the first failure to look; the watcher keeps looking.</param>
        internal LauncherQuitWatcher(
            Func<bool> pending,
            Func<LauncherQuitRequest?> take,
            Func<TimeSpan> clock,
            TimeSpan interval,
            Action<Exception> report)
        {
            this.pending = pending ?? throw new ArgumentNullException(nameof(pending));
            this.take = take ?? throw new ArgumentNullException(nameof(take));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.report = report ?? throw new ArgumentNullException(nameof(report));
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
            this.interval = interval;
        }

        /// <summary>Returns true once, on the look that takes the launcher's request; false on every other poll.</summary>
        internal bool Poll()
        {
            if (asked) return false;
            var now = clock();
            if (now < nextLook) return false;
            nextLook = now + interval;
            try
            {
                if (!pending() || take() == null) return false;
            }
            catch (Exception error)
            {
                if (!reported)
                {
                    reported = true;
                    try { report(error); }
                    catch { /* A stopped diagnostic sink must not stop the game from watching. */ }
                }

                return false;
            }

            asked = true;
            return true;
        }
    }
}
