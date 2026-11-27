using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>What voice chat reads each frame, guarded so a provider that cannot answer reads as none, and what it says it does.</summary>
    internal sealed partial class VoiceChatRuntime
    {
        private VoiceTalkStatus StatusOf(InternalVoiceTransmitState state, InternalSessionRole role, bool want, bool busy, bool cannotCheck, bool transmit)
        {
            if (!ConsentCurrent) return VoiceTalkStatus.ConsentNeeded;
            if (!VoiceEnabled) return VoiceTalkStatus.Off;
            if (state == InternalVoiceTransmitState.Failed) return VoiceTalkStatus.Failed;
            if (cannotCheck && want) return VoiceTalkStatus.CannotCheckGame;
            if (busy && want) return VoiceTalkStatus.MicBusy;
            if (want && openFailed && !capture.IsCapturing)
            {
                return capture.LastOpen == VoiceMicrophoneOpen.NoDevice ? VoiceTalkStatus.NoMicrophone : VoiceTalkStatus.MicrophoneFailed;
            }

            if (Testing) return VoiceTalkStatus.Testing;
            if (state == InternalVoiceTransmitState.NotOffered) return VoiceTalkStatus.NotOffered;
            if (role == InternalSessionRole.Solo) return VoiceTalkStatus.NotInSession;
            if (state == InternalVoiceTransmitState.NotConnected) return VoiceTalkStatus.Connecting;
            if (state == InternalVoiceTransmitState.MutedByHost) return VoiceTalkStatus.MutedByHost;
            if (state == InternalVoiceTransmitState.NoListeners) return VoiceTalkStatus.NoListeners;
            if (busy) return VoiceTalkStatus.MicBusy;
            if (cannotCheck) return VoiceTalkStatus.CannotCheckGame;
            return transmit && state == InternalVoiceTransmitState.Transmitting ? VoiceTalkStatus.Talking : VoiceTalkStatus.Ready;
        }

        private IInternalMultiplayerControl? ReadControl()
        {
            try
            {
                return control();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IInternalVoiceService? ReadVoice(IInternalMultiplayerControl? source)
        {
            if (source == null) return null;
            try
            {
                return source.Voice;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static InternalSessionRole ReadRole(IInternalMultiplayerControl? source)
        {
            if (source == null) return InternalSessionRole.Solo;
            try
            {
                return source.Role;
            }
            catch (Exception)
            {
                return InternalSessionRole.Solo;
            }
        }

        private MultiplayerSessionSnapshot? ReadSnapshot()
        {
            try
            {
                return snapshot();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
