using System;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The plugin's answer to Unity's quit request. Unity asks every wantsToQuit handler in the order it subscribed
    /// and stops at the first one that refuses. The plugin subscribes while the game starts, ahead of the game's own
    /// handler, whose quit steps include the shutdown feedback page. Unloading mods at the first request would remove
    /// their patches before the game's own steps ran, and a mod such as NoFeedbackUrl patches exactly those steps. So
    /// the first request is refused once: the handler moves behind every other handler and asks for the quit again on
    /// the next frame. The other handlers then answer first, with every mod still loaded and every patch in place.
    /// The teardown cannot be undone, so it starts only when every other handler has let the quit go ahead. The
    /// coordinator then holds the quit until the teardown has finished, and no patch outlives its mod.
    /// </summary>
    internal sealed class PluginQuitHandler
    {
        private readonly PluginQuitCoordinator coordinator;
        private readonly Action moveBehindOtherHandlers;
        private readonly Action<Exception> report;
        private bool deferred;
        private bool askAgain;

        internal PluginQuitHandler(PluginQuitCoordinator coordinator, Action moveBehindOtherHandlers, Action<Exception> report)
        {
            this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            this.moveBehindOtherHandlers = moveBehindOtherHandlers ?? throw new ArgumentNullException(nameof(moveBehindOtherHandlers));
            this.report = report ?? throw new ArgumentNullException(nameof(report));
        }

        /// <summary>
        /// Answers Unity's wantsToQuit. The first request is refused once, after the handler has moved behind every
        /// other handler; <see cref="ShouldRequestQuit"/> then asks for the quit again. Unity asks this handler again
        /// only once every handler ahead of it has let the quit go ahead, and from then on the coordinator decides:
        /// it starts the teardown and holds the quit until the teardown has finished.
        /// </summary>
        internal bool Answer()
        {
            if (!deferred)
            {
                deferred = true;
                if (TryMoveBehindOtherHandlers())
                {
                    askAgain = true;
                    return false;
                }
            }
            else
            {
                // The request reached this handler again, so asking for the quit once more would only repeat it.
                askAgain = false;
            }

            return coordinator.RequestQuit();
        }

        /// <summary>
        /// Polled once a frame. Returns true when the quit should be requested again: once on the frame after the
        /// first request was refused, and once more when a held quit's teardown has finished or missed its deadline.
        /// A teardown that runs without a quit request never starts a quit.
        /// </summary>
        internal bool ShouldRequestQuit()
        {
            if (askAgain)
            {
                askAgain = false;
                return true;
            }

            return coordinator.ShouldResumeQuit();
        }

        private bool TryMoveBehindOtherHandlers()
        {
            try
            {
                moveBehindOtherHandlers();
                return true;
            }
            catch (Exception error)
            {
                // Without the move the quit would only come back to this handler first again; holding it now keeps the
                // clean exit, as before this ordering existed.
                try { report(error); }
                catch { /* A stopped diagnostic sink must not keep the game open. */ }
                return false;
            }
        }
    }
}
