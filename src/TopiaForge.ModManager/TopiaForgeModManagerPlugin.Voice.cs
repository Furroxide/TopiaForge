using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        /// <summary>Gets the runtime's microphone arbiter, which voice chat yields through; null before the runtime exists.</summary>
        internal MultiplayerMicrophoneArbiter? MicrophoneArbiter => runtime?.Replication.Microphone;

        /// <summary>Gets manager staging, where the launcher's preferences arrive and this game's changes to them go; null before startup read it.</summary>
        internal LaunchStagingStore? LaunchStaging => launchStaging;
    }
}
