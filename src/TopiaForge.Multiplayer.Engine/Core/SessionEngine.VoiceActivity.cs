using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        private const long VoiceActivityMicros = 250_000;

        private void NoteVoiceActivity(ParticipantId sender, VoiceSpeaker speaker, VoiceFrameFlags flags, ReadOnlySpan<byte> payload)
        {
            if (Role != EngineRole.Host || IsEnded || !voiceOffered || speaker.IsMuted(nowMicros) || !IsParticipantReady(sender)) return;
            // Neither a claimed presence bit nor a forged DTX flag can create voiced activity.
            if ((flags & VoiceFrameFlags.Discontinuous) != 0 || options.Voice!.Codec.IsDiscontinuous(payload)) return;
            speaker.LastAdmittedVoiceMicros = nowMicros;
            speaker.ActivityEpoch = epoch;
        }

        internal bool IsVoiceActive(ParticipantId sender)
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host || IsEnded || !voiceOffered) return false;
            var record = FindParticipant(sender);
            var speaker = record?.Voice;
            return record != null && record.IsConnected && speaker != null && !speaker.IsMuted(nowMicros)
                && speaker.ActivityEpoch == epoch && speaker.LastAdmittedVoiceMicros != long.MinValue
                && nowMicros >= speaker.LastAdmittedVoiceMicros && nowMicros - speaker.LastAdmittedVoiceMicros < VoiceActivityMicros;
        }

        internal bool HasVoiceActivity(ParticipantId sender, ParticipantId listener)
        {
            if (!IsVoiceActive(sender) || !CanCommunicate(sender, listener)) return false;
            if (localParticipantId.HasValue && listener.Equals(localParticipantId.Value))
                return !localVoiceMuteAll && !localVoiceMutes.Contains(sender.Value);
            foreach (var connection in connections)
            {
                if (connection.Participant == null || !connection.Participant.Id.Equals(listener)) continue;
                return connection.Phase == HostConnectionPhase.Ready && connection.Link.State != EngineLinkState.Closed
                    && !connection.VoiceMuteAll && !connection.VoiceMutes.Contains(sender.Value);
            }
            return false;
        }

        internal void ClearVoiceActivity()
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host) return;
            DiscardHeldVoice();
            foreach (var record in participantRecords)
                if (record.Voice != null) record.Voice.LastAdmittedVoiceMicros = long.MinValue;
        }
    }
}
