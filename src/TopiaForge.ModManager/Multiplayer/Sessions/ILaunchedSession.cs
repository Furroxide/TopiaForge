using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Where the game a launch started stands, before its session opens.</summary>
    internal enum LaunchedGame
    {
        /// <summary>The launch's own target is still starting, or the main menu it asked for has not loaded.</summary>
        Starting = 0,

        /// <summary>The launch's target runs, or its main menu has loaded: the session may open.</summary>
        Ready = 1,

        /// <summary>The launch's own command failed or was cancelled; that outcome is the launch's, and no session opens.</summary>
        Failed = 2
    }

    /// <summary>
    /// A session the launcher asked this game to host or join, planned by the provider: the session it was launched to host
    /// or join (launch request V5), or a live join of the running game. The screens start it through the realm, as their own
    /// flows start sessions, and report how it goes for the launcher. Main thread only; nothing here holds an address.
    /// </summary>
    internal interface ILaunchedSession
    {
        bool IsHost { get; }

        /// <summary>
        /// Gets the request this launch's records go under: the launch request's, or a live join's own id. Session follow
        /// reports the host's world under it while the launch it carries out still waits for its result (WS3.8).
        /// </summary>
        string RequestId { get; }

        /// <summary>Gets whether this is a live join of the running game, which waits for no game of its own.</summary>
        bool IsLiveJoin { get; }

        /// <summary>Gets whether the launch started a game its host session opens in place; otherwise the session opens at the menu.</summary>
        bool StartsGame { get; }

        /// <summary>Gets what the failure view's Open launcher hands the launcher for this join; null when nothing can.</summary>
        LauncherHandoff? Handoff { get; }

        /// <summary>Gets the provider's plan: host settings, or the pinned target with the launch's own name and privacy.</summary>
        IInternalLaunchPlan Plan { get; }

        /// <summary>Gets where the launch's own game stands; a live join's is always ready.</summary>
        LaunchedGame Game { get; }

        /// <summary>Gets whether the session began: hosting is opening, or the provider has the join.</summary>
        bool HasStarted { get; }

        /// <summary>Gets whether the launch's result is in, so nothing more starts for it.</summary>
        bool IsSettled { get; }

        /// <summary>Gets whether nothing more is reported: the launch failed, or the session it started has ended.</summary>
        bool IsFinished { get; }

        /// <summary>
        /// Says the session began: the provider took the hosting or the join. <paramref name="reads"/> is the status tracker's
        /// count of reads then (<see cref="MultiplayerStatusTracker.Reads"/>): a status read no later than that still shows
        /// what came before the session, such as an earlier join's end, so only a later read can settle it.
        /// </summary>
        void Started(int reads);

        /// <summary>
        /// Follows the session in the provider's status into the launcher's records; every frame, allocating only on change.
        /// <paramref name="reads"/> is the status tracker's count of reads, which says whether <paramref name="status"/> was
        /// read after the session began.
        /// </summary>
        void Follow(InternalLiveStatus status, int version, int reads);

        /// <summary>
        /// The game closes while this session runs. A live join's own records end it as the player leaving it, since nothing
        /// else writes them; the session this game was launched with is ended by the runtime's publisher, which knows whether
        /// the runtime stopped cleanly.
        /// </summary>
        void Closing();

        /// <summary>Reports that the session did not start, for a reason of this game's own, unless its result is in already.</summary>
        void Refuse(ModErrorCode code, string message);

        /// <summary>Reports a failure the launcher's join flow names, as when a LAN session was never heard, unless its result is in.</summary>
        void Fail(string failure, string detail);
    }
}
