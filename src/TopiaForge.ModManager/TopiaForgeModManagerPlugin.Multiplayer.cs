using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        private StagingLauncherChannels? launcherChannels;

        /// <summary>Gets the runtime's multiplayer composition root, for the in-game sessions interface; null before the runtime.</summary>
        internal MultiplayerRuntime? Multiplayer => runtime?.Multiplayer;

        /// <summary>
        /// Gets the files this game shares with the TopiaForge launcher: its preferences, the session handoff and live joins,
        /// keyed by the launch request a launcher started this process with; null before startup made them.
        /// </summary>
        internal ILauncherChannels? LauncherChannels => launcherChannels;

        /// <summary>Gets the local session's phase as the realm rules read it; idle before the runtime exists.</summary>
        internal SessionPhase LocalPhase => runtime?.Multiplayer.SessionPhaseSource() ?? SessionPhase.Idle;

        /// <summary>Gets where following the host's world stands on a joined player (WS3.8).</summary>
        internal SessionFollowStatus FollowStatus => runtime?.SessionFollow?.Follower.Status ?? SessionFollowStatus.None;

        /// <summary>Gets the multiplayer provider's session as mods see it, for the participant list; null without the provider.</summary>
        internal IMultiplayerSession? GetMultiplayerSession() => runtime?.GetService<IMultiplayerSession>();

        /// <summary>Takes the session this game was launched to host or join, once the provider planned it; once, then null.</summary>
        internal ILaunchedSession? TakeLaunchedSession()
        {
            var launched = launchedSession;
            launchedSession = null;
            return launched;
        }

        /// <summary>Gets the names players know the worlds by, by world id, so HOST and a hosted session name worlds as players do.</summary>
        internal IReadOnlyDictionary<string, string> GetWorldNames()
        {
            if (runtime == null) return HostTargets.WorldNames(null, null);
            var snapshot = runtime.CaptureSessionRuntime();
            return HostTargets.WorldNames(snapshot.Profile, snapshot.Observation);
        }

        /// <summary>
        /// Makes the launcher's channels once the runtime knows the service it reaches. Live joins and handoffs need the launch
        /// request a launcher started this process with; an acceptance run, a rejected request and a direct start have none.
        /// </summary>
        private void OpenLauncherChannels()
        {
            var owned = acceptanceAdmitted || rejectedLauncherCommand ? null : launchProfile;
            launcherChannels = new StagingLauncherChannels(launchStaging, owned?.RequestId, owned?.Digest, runtime.Multiplayer.Host.ServiceOrigin);
        }
    }
}
