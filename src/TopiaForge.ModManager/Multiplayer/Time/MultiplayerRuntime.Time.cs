using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerRuntime
    {
        private bool timeReceiveFailureLogged;
        private bool timeSendFailureLogged;
        private bool pauseGuardFailureLogged;

        /// <summary>
        /// Gets the session's time (WS3.13): what this process's time authority publishes, the host's on a joined player, the
        /// <c>Time</c> channel between them, and the pause guard. It hears of the control and the realm as the runtime's first
        /// observer.
        /// </summary>
        internal MultiplayerSessionClock SessionTime { get; }

        /// <summary>
        /// Gets or sets the game's clock: Unity's in the game, given by the gameplay host. Chronos drives time through it,
        /// and the pause guard keeps the shared world running through it. Without one there is nothing to guard.
        /// </summary>
        internal IMultiplayerGameClock? GameClock { get; set; }

        /// <summary>Step 1, a joined player: the host's time arrives before the mods' Update, so Chronos follows it that frame.</summary>
        private void ReceiveSessionTime()
        {
            if (role != InternalSessionRole.Client) return;
            try
            {
                SessionTime.Receive();
                timeReceiveFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!timeReceiveFailureLogged) log.Error("Multiplayer: the host's time could not be read: " + Describe(exception));
                timeReceiveFailureLogged = true;
            }
        }

        /// <summary>Step 4, a listen host: the session's time goes to the players before the flush.</summary>
        private void SendSessionTime()
        {
            if (role != InternalSessionRole.ListenHost) return;
            try
            {
                SessionTime.Send();
                timeSendFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!timeSendFailureLogged) log.Error("Multiplayer: the session's time could not be sent: " + Describe(exception));
                timeSendFailureLogged = true;
            }
        }

        /// <summary>LateUpdate, before the mods: keeps the shared world running behind this player's own pause menu.</summary>
        private void GuardNativePause()
        {
            try
            {
                SessionTime.PauseGuard.Run(GameClock, SessionTime.Current);
                pauseGuardFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!pauseGuardFailureLogged) log.Error("Multiplayer: the pause guard failed: " + Describe(exception));
                pauseGuardFailureLogged = true;
            }
        }
    }
}
