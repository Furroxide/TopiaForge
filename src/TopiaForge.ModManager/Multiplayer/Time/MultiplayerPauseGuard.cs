using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Keeps the shared world running behind a player's own pause menu in a multiplayer session (WS3.13). Robotopia's pause
    /// menu freezes the world: it sets the clock's scale to zero as it opens, and puts back the scale it found as it closes.
    /// Online that menu is this player's alone, so while it is open the guard puts the session's scale back whenever the
    /// clock reads zero, and when it closes the guard corrects the scale the menu put back, which is stale once the session's
    /// time changed meanwhile. Leaving a session behind the menu hands the clock back as single player expects: the menu
    /// freezes the world again, and once it closes the world runs at this game's own time. Offline it does nothing, at the
    /// cost of one field read. Unity-free; main thread. See docs/internal/MultiplayerRuntimeIntegration.md, section 17.
    /// </summary>
    internal sealed class MultiplayerPauseGuard
    {
        private bool online;
        private bool armed;
        private bool guarding;
        private bool wasPaused;
        private bool handingBack;

        /// <summary>Gets whether the guard runs next frame: online, and after leaving until it has handed the clock back.</summary>
        internal bool IsArmed => armed;

        /// <summary>
        /// Gets whether a session ended behind the open pause menu, so the scale the menu puts back as it closes still has
        /// to be corrected to this game's own.
        /// </summary>
        internal bool IsHandingBack => handingBack;

        /// <summary>Follows the realm, as the last pump showed it.</summary>
        internal void RealmChanged(NetRealm next)
        {
            online = NetRealms.IsOnline(next);
            if (online) armed = true;
        }

        /// <summary>
        /// Runs once a frame, after the game's Update has opened or closed its menu and before the next frame's clock
        /// advances. <paramref name="time"/> is the session's time as this process runs it: online the session's, offline
        /// this game's own. Without a clock there is nothing to keep.
        /// </summary>
        internal void Run(IMultiplayerGameClock? clock, InternalSessionTime time)
        {
            if (!armed) return;
            if (clock == null)
            {
                armed = online;
                return;
            }

            var paused = clock.IsPaused;
            var scale = time.Scale;
            if (online)
            {
                if (paused)
                {
                    // The menu froze the world. A scale other than zero is someone's own write, such as a host's
                    // single-frame step, and stays; so does zero while the session itself is frozen.
                    if (scale != 0f && clock.TimeScale == 0f) clock.TimeScale = scale;
                }
                else if (wasPaused && clock.TimeScale != scale)
                {
                    // The menu closed, putting back the scale it found as it opened: stale if the session's time changed.
                    clock.TimeScale = scale;
                }

                guarding = true;
                handingBack = false;
                wasPaused = paused;
                return;
            }

            if (guarding)
            {
                // The session ended since the last frame. Behind a menu that is still open, single player freezes the world.
                guarding = false;
                handingBack = paused;
                if (paused) clock.TimeScale = 0f;
            }
            else if (handingBack && !paused)
            {
                // The menu closed after the session ended behind it, putting back a scale from the session: this game now
                // runs at its own.
                handingBack = false;
                if (clock.TimeScale != scale) clock.TimeScale = scale;
            }

            wasPaused = paused;
            if (!handingBack) armed = false;
        }
    }
}
