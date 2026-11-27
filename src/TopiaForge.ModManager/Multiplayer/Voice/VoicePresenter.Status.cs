using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>VOICE's transmit state: the runtime's status as text, tone and the talk glyph's state, cached until it moves.</summary>
    internal sealed partial class VoicePresenter
    {
        /// <summary>Brings the transmit state's text, tone and glyph up to the runtime's last frame; it allocates only when the state moved.</summary>
        private void UpdateStatus(VoiceSettings settings)
        {
            var status = runtime.Status;
            if (status == VoiceTalkStatus.Ready && settings.Mode == InternalVoiceTransmitMode.PushToTalk && readyVersion != bindingsVersion)
            {
                readyVersion = bindingsVersion;
                readyText = ReadyText();
                shownStatus = (VoiceTalkStatus)(-1);
            }

            if (status == shownStatus && (status != VoiceTalkStatus.Ready || readyMode == settings.Mode)) return;
            shownStatus = status;
            readyMode = settings.Mode;
            statusText = TextOf(status, settings.Mode);
            statusTone = ToneOf(status);
            indicator = IndicatorOf(status);
        }

        private string TextOf(VoiceTalkStatus status, InternalVoiceTransmitMode mode)
        {
            switch (status)
            {
                case VoiceTalkStatus.Unavailable: return VoiceText.Unavailable;
                case VoiceTalkStatus.ConsentNeeded: return VoiceText.ConsentNeeded;
                case VoiceTalkStatus.Off: return VoiceText.Off;
                case VoiceTalkStatus.Failed: return VoiceText.Failed;
                case VoiceTalkStatus.NotOffered: return VoiceText.NotOffered;
                case VoiceTalkStatus.NotInSession: return VoiceText.NotInSession;
                case VoiceTalkStatus.Connecting: return VoiceText.Connecting;
                case VoiceTalkStatus.NoListeners: return VoiceText.NoListeners;
                case VoiceTalkStatus.MutedByHost: return VoiceText.MutedByHost;
                case VoiceTalkStatus.MicBusy: return VoiceText.MicBusy;
                case VoiceTalkStatus.CannotCheckGame: return VoiceText.CannotCheckGame;
                case VoiceTalkStatus.NoMicrophone: return VoiceText.NoMicrophone;
                case VoiceTalkStatus.MicrophoneFailed: return VoiceText.MicrophoneFailed;
                case VoiceTalkStatus.Testing: return VoiceText.Testing;
                case VoiceTalkStatus.Talking: return VoiceText.Talking;
                default: return mode == InternalVoiceTransmitMode.VoiceActivation ? VoiceText.Listening : readyText;
            }
        }

        private static ScreenTone ToneOf(VoiceTalkStatus status)
        {
            switch (status)
            {
                case VoiceTalkStatus.Failed:
                case VoiceTalkStatus.CannotCheckGame:
                case VoiceTalkStatus.MicrophoneFailed:
                    return ScreenTone.Danger;
                case VoiceTalkStatus.MutedByHost:
                case VoiceTalkStatus.MicBusy:
                case VoiceTalkStatus.NoMicrophone:
                    return ScreenTone.Warning;
                case VoiceTalkStatus.Talking:
                case VoiceTalkStatus.Ready:
                    return ScreenTone.Success;
                case VoiceTalkStatus.Testing:
                    return ScreenTone.Neutral;
                default:
                    return ScreenTone.Muted;
            }
        }

        private static VoiceIndicator IndicatorOf(VoiceTalkStatus status)
        {
            switch (status)
            {
                case VoiceTalkStatus.Talking:
                    return VoiceIndicator.Talking;
                case VoiceTalkStatus.MutedByHost:
                    return VoiceIndicator.MutedByHost;
                case VoiceTalkStatus.MicBusy:
                case VoiceTalkStatus.CannotCheckGame:
                    return VoiceIndicator.MicBusy;
                case VoiceTalkStatus.NoMicrophone:
                case VoiceTalkStatus.MicrophoneFailed:
                    return VoiceIndicator.NoMic;
                case VoiceTalkStatus.Unavailable:
                case VoiceTalkStatus.ConsentNeeded:
                case VoiceTalkStatus.Off:
                case VoiceTalkStatus.Failed:
                case VoiceTalkStatus.NotOffered:
                    return VoiceIndicator.Muted;
                default:
                    return VoiceIndicator.Idle;
            }
        }
    }
}
