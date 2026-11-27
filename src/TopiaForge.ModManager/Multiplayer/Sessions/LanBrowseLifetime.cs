using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    [Flags]
    internal enum LanBrowseUse
    {
        None = 0,
        Browse = 1,
        Join = 2,
        Launch = 4
    }

    /// <summary>
    /// Shares one LAN browser between BROWSE, the connecting overlay and a launched join. A user releases only its own
    /// share; the browser stops once the last share ends, and only if this lifetime started it. Main thread, action-time
    /// only: the status tracker's cached IsBrowsing can predate another screen's search, so it cannot decide ownership.
    /// </summary>
    internal sealed class LanBrowseLifetime
    {
        private readonly IMultiplayerSessionEnvironment environment;
        private LanBrowseUse users;
        private IInternalMultiplayerControl? started;

        internal LanBrowseLifetime(IMultiplayerSessionEnvironment environment) => this.environment = environment;

        internal bool Start(LanBrowseUse use)
        {
            if ((users & use) != 0) return true;
            if (users == LanBrowseUse.None)
            {
                var control = environment.Control;
                if (control == null) return false;
                if (!control.GetStatus().IsBrowsing)
                {
                    control.StartBrowsing();
                    started = control;
                }
            }

            users |= use;
            return true;
        }

        internal void Stop(LanBrowseUse use)
        {
            users &= ~use;
            if (users == LanBrowseUse.None) Stop();
        }

        /// <summary>Releases every share when the screens stop; also used after the last individual release.</summary>
        internal void Stop()
        {
            users = LanBrowseUse.None;
            var control = started;
            started = null;
            try
            {
                control?.StopBrowsing();
            }
            catch (Exception)
            {
                // Stopping is best effort; the provider closes its browser when it stops.
            }
        }
    }
}
