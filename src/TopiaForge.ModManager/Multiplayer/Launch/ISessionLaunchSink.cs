using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Where the in-game flows report a session launch, for the launcher: the provider's session block, the launch's one
    /// result, and the end of a session that started (docs/internal/MultiplayerLaunchContract.md, sections 4 and 5). The
    /// runtime's publisher takes a launch request's; a live join's go to its own records. Main thread only. The records hold
    /// no address.
    /// </summary>
    internal interface ISessionLaunchSink
    {
        /// <summary>Gets whether the launch already has its outcome, from the runtime's command or from the session.</summary>
        bool HasResult { get; }

        /// <summary>Reports the session's progress: its block, and the phase a join's state reports in place of the runtime's.</summary>
        void Progress(LaunchProgressSession block, string? phase);

        /// <summary>Reports the launch's result, once; a launch the runtime's command already failed keeps that outcome.</summary>
        void Result(SessionLaunchResult result, string? phase);

        /// <summary>Reports why a session that started has ended, once; <paramref name="clean"/> says whether it tore down cleanly.</summary>
        void Ended(LaunchOutcomeMultiplayer block, bool clean);
    }
}
