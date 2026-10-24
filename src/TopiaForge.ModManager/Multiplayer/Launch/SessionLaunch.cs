using System;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// One session launch (launch request V5, or a live join of this running game) from its consumed request to the
    /// provider's plan. The runtime offers the session before mods load; the provider takes it once, through the internal
    /// control ModRuntime attaches to at the end of the load, and plans it. Once mods have loaded, a session no provider took
    /// is withdrawn and the launch fails at once as unavailable, so neither the session nor a direct target's address stays
    /// in memory for the life of the process. Starting the plan is the in-game flows' (the launch presenter's). Nothing here
    /// logs or shows the target. Main thread only.
    /// </summary>
    internal sealed class SessionLaunch
    {
        internal const string ProviderMissing = "The multiplayer provider is not loaded.";

        private InternalLaunchSession? offered;

        /// <param name="startsGame">Whether the launch starts a game before its session opens, as a host's launch target does.</param>
        internal SessionLaunch(LaunchSessionIdentity identity, InternalLaunchSession session, bool startsGame)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            offered = session ?? throw new ArgumentNullException(nameof(session));
            if ((identity.Role == "host") != (session.Host != null)) throw new ArgumentException("The session plays the launch's role.", nameof(session));
            if (!string.Equals(identity.IntentId, session.IntentId, StringComparison.Ordinal)) throw new ArgumentException("The session carries the launch's intent.", nameof(session));
            StartsGame = startsGame;
            HostPort = session.Host != null ? session.Port : 0;
        }

        /// <summary>Gets the game port a launched host was to use, so a port another program holds is named; zero for any free one.</summary>
        internal int HostPort { get; }

        /// <summary>Gets the identity every record of this launch is reported under.</summary>
        internal LaunchSessionIdentity Identity { get; }

        internal bool IsHost => Identity.Role == "host";

        /// <summary>Gets whether the launch starts a game before its session opens; otherwise it opens at the main menu.</summary>
        internal bool StartsGame { get; }

        /// <summary>Gets whether the session still waits for a provider to take it.</summary>
        internal bool IsOffered => offered != null;

        /// <summary>Gets the provider's plan, once it took the session.</summary>
        internal IInternalLaunchPlan? Plan { get; private set; }

        /// <summary>Gets why the session cannot start, when it cannot: the code a failed outcome reports.</summary>
        internal ModErrorCode RefusalCode { get; private set; }

        /// <summary>Gets why the session cannot start, in this game's words; empty while it can.</summary>
        internal string Refusal { get; private set; } = string.Empty;

        internal bool IsRefused => Refusal.Length != 0;

        /// <summary>
        /// Hands the offered session to <paramref name="control"/>, once: the provider plans it, or says why it cannot. True
        /// when this call took the session and there is a plan; any later call is false. The session itself is let go of
        /// either way.
        /// </summary>
        internal bool Take(IInternalMultiplayerControl? control)
        {
            var session = offered;
            if (session == null) return false;
            offered = null;
            if (control == null)
            {
                Refuse(ModErrorCode.Unavailable, ProviderMissing);
                return false;
            }

            OperationResult<IInternalLaunchPlan> planned;
            try
            {
                planned = control.PlanLaunchSession(session);
            }
            catch (Exception exception)
            {
                Refuse(ModErrorCode.External, "The multiplayer provider could not plan the session (" + exception.GetType().Name + ").");
                return false;
            }

            if (planned.TryGetValue(out var plan) && plan != null)
            {
                Plan = plan;
                return true;
            }

            Refuse(planned.Succeeded ? ModErrorCode.External : planned.ErrorCode,
                planned.Succeeded || string.IsNullOrEmpty(planned.ErrorMessage) ? "The multiplayer provider did not plan the session." : planned.ErrorMessage);
            return false;
        }

        /// <summary>Withdraws a session no provider took while mods loaded: the launch fails as unavailable. True when it was withdrawn.</summary>
        internal bool Withdraw()
        {
            if (offered == null) return false;
            offered = null;
            Refuse(ModErrorCode.Unavailable, ProviderMissing);
            return true;
        }

        /// <summary>Never names the target.</summary>
        public override string ToString() => "session launch (" + Identity.Role + ")";

        private void Refuse(ModErrorCode code, string message)
        {
            RefusalCode = code;
            Refusal = MultiplayerDiagnosticRedactor.Redact(message);
        }
    }
}
