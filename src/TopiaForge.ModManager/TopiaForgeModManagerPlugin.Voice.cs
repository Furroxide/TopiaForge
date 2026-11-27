using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Voice;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        /// <summary>Gets the runtime's microphone arbiter, which voice chat yields through; null before the runtime exists.</summary>
        internal MultiplayerMicrophoneArbiter? MicrophoneArbiter => runtime?.Replication.Microphone;

        /// <summary>Gets manager staging, where the launcher's preferences arrive and this game's changes to them go; null before startup read it.</summary>
        internal LaunchStagingStore? LaunchStaging => launchStaging;

        /// <summary>
        /// Points the runtime's microphone arbiter at the game's own robot recording, as soon as the runtime exists and before
        /// any mod loads: a robot conversation and voice chat then ask the game itself whenever they claim or use the
        /// microphone, from the first frame, and whether or not voice chat starts.
        /// </summary>
        private void ObserveNativeRecording()
        {
            runtime.Replication.Microphone.ObserveNativeRecording(UnityVoiceNativeRecording.Read);
        }
    }
}
