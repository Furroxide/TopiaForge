using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    internal sealed partial class ScoreboardPresenter
    {
        public void Kick(ScoreboardRow row) => Confirm(row, false);
        public void Ban(ScoreboardRow row) => Confirm(row, true);

        private void Confirm(ScoreboardRow row, bool ban)
        {
            if (!targets.TryGetValue(row, out var target) || !Current(target, true, out _, out _)) { Changed(); return; }
            ask(PrivacyPrompts.Destructive((ban ? "Ban " : "Remove ") + row.Name + "?",
                ban ? row.Name + " leaves the session and cannot join it again while you host." : row.Name + " leaves the session and can join again.",
                ban ? "BAN" : "REMOVE", confirmed =>
                {
                    if (!confirmed) return;
                    if (!Current(target, true, out var current, out var participant)) { Changed(); return; }
                    Result(ban ? current!.Ban(participant!.Id) : current!.Kick(participant!.Id, SessionPresenter.KickMessage),
                        ban ? "Player banned." : "Player removed.");
                }, () => Current(target, true, out _, out _)));
        }

        public void ServerMute(ScoreboardRow row)
        {
            if (!targets.TryGetValue(row, out var target) || !Current(target, true, out var current, out var participant)) { Changed(); return; }
            var muted = !current!.IsServerMuted(participant!.Id);
            Result(current.SetServerMute(participant.Id, muted), muted ? "Player muted for everyone." : "Player unmuted for everyone.");
        }

        public void Mute(ScoreboardRow row)
        {
            if (!targets.TryGetValue(row, out var target) || !Current(target, false, out _, out var participant)) { Changed(); return; }
            var chat = voice();
            if (chat?.Voice == null) { notify("Voice is unavailable.", ScreenTone.Warning); return; }
            if (chat.Mutes.HearNobody) { notify("Turn off Mute everyone in VOICE to hear individual players.", ScreenTone.Warning); return; }
            var muted = !chat.Mutes.IsMuted(participant!, target.Session);
            if (!chat.Mutes.SetMuted(participant!, target.Session, muted)) { notify("The mute list is full.", ScreenTone.Warning); return; }
            notify(muted ? "Player muted for you." : "Player unmuted for you.", ScreenTone.Success);
        }

        public void Volume(ScoreboardRow row, float value)
        {
            if (!targets.TryGetValue(row, out var target) || !Current(target, false, out _, out var participant)) { Changed(); return; }
            var chat = voice();
            if (chat?.Voice == null || !chat.Playback.Volumes.Set(participant!, target.Session, value))
                notify("The player volume could not be changed.", ScreenTone.Warning);
        }

        private bool Current(Target target, bool moderate, out IInternalMultiplayerControl? current, out MultiplayerParticipant? participant)
        {
            current = control(); participant = null;
            // A delayed confirmation cannot revive authority removed by a roster refresh.
            if (!targets.ContainsValue(target)) return false;
            if (!ReferenceEquals(current, target.Owner) || current == null || !current.IsLiveAvailable || current.Role == InternalSessionRole.Solo) return false;
            if (moderate && current.Role != InternalSessionRole.ListenHost) return false;
            var snapshot = current.SessionSnapshot;
            if (snapshot.State != MultiplayerSessionState.Ready || !snapshot.Id.Equals(target.Session)) return false;
            for (var i = 0; i < snapshot.Participants.Count; i++)
            {
                var candidate = snapshot.Participants[i];
                if (!candidate.Id.Equals(target.Participant.Id) || !candidate.PlayerKey.Equals(target.Participant.PlayerKey)) continue;
                if (!candidate.IsConnected || candidate.IsLocal || (moderate && (candidate.IsHost || !candidate.PlayerKey.IsValid))) return false;
                participant = candidate; return true;
            }
            return false;
        }

        private void Changed() => notify(SessionPresenter.ListChanged, ScreenTone.Warning);
        private void Result(OperationResult<bool> result, string success) => notify(result.Succeeded ? success : "The player action could not be completed.",
            result.Succeeded ? ScreenTone.Success : ScreenTone.Warning);
    }
}
