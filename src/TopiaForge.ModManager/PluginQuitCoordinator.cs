using System;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Holds a quit until the plugin's teardown has finished. Unity runs no further frame once it starts
    /// destroying objects, so cleanup that starts in OnDestroy and needs later frames never completes: the
    /// mods never unload and the startup journal never records a clean exit, so the next start opens in safe
    /// mode with every mod disabled. While a quit is held, frames keep running and the host dispatcher drains
    /// the cleanup; the quit is then requested again. A deadline keeps a stuck teardown from holding the game
    /// open: past it the quit goes ahead without a clean-exit record, and the next start opens in safe mode,
    /// as it should after unfinished cleanup.
    /// </summary>
    internal sealed class PluginQuitCoordinator
    {
        private readonly Action beginTeardown;
        private readonly Func<bool> teardownFinished;
        private readonly Func<TimeSpan> clock;
        private readonly TimeSpan limit;
        private readonly Action<Exception> report;
        private TimeSpan deadline;
        private bool started;
        private bool failed;
        private bool held;
        private bool resumed;

        internal PluginQuitCoordinator(
            Action beginTeardown,
            Func<bool> teardownFinished,
            Func<TimeSpan> clock,
            TimeSpan limit,
            Action<Exception> report)
        {
            this.beginTeardown = beginTeardown ?? throw new ArgumentNullException(nameof(beginTeardown));
            this.teardownFinished = teardownFinished ?? throw new ArgumentNullException(nameof(teardownFinished));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.report = report ?? throw new ArgumentNullException(nameof(report));
            if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit), "The teardown limit must be positive.");
            this.limit = limit;
        }

        /// <summary>Gets whether a quit went ahead because the teardown missed its deadline.</summary>
        internal bool DeadlinePassed { get; private set; }

        /// <summary>
        /// Answers Unity's wantsToQuit. Returns true when the quit may go ahead now; otherwise the teardown has
        /// started (once) and <see cref="ShouldResumeQuit"/> asks for the quit again when it finishes.
        /// </summary>
        internal bool RequestQuit()
        {
            if (failed || teardownFinished()) return true;
            if (!started)
            {
                started = true;
                deadline = clock() + limit;
                try
                {
                    beginTeardown();
                }
                catch (Exception error)
                {
                    // A teardown that cannot start will not finish; holding the quit would only delay it.
                    failed = true;
                    Report(error);
                    return true;
                }

                if (teardownFinished()) return true;
            }

            if (clock() >= deadline)
            {
                DeadlinePassed = true;
                return true;
            }

            held = true;
            return false;
        }

        /// <summary>
        /// Polled once a frame. Returns true exactly once, after a held quit's teardown has finished or its
        /// deadline has passed, when the quit should be requested again. A teardown that runs without a quit
        /// request never starts one.
        /// </summary>
        internal bool ShouldResumeQuit()
        {
            if (!held || resumed) return false;
            if (!teardownFinished())
            {
                if (clock() < deadline) return false;
                DeadlinePassed = true;
            }

            resumed = true;
            return true;
        }

        private void Report(Exception error)
        {
            try { report(error); }
            catch { /* A stopped diagnostic sink must not keep the game open. */ }
        }
    }
}
