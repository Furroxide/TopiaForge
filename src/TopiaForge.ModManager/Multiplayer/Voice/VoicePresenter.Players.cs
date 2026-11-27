using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// VOICE's players: whom this player hears, once the host has listed its players. A row's key is the participant's
    /// session-scoped id, which the view hands back and never shows; a mute goes to the player's stable key, or for one
    /// without a key, as the host, to their id in this session (<see cref="VoiceMuteBook"/>), checked against the current
    /// session first, so a list a moment old never mutes the wrong player. The rows are rebuilt only while VOICE shows and
    /// the players changed, so the game's frames allocate nothing for them.
    /// </summary>
    internal sealed partial class VoicePresenter
    {
        private readonly Dictionary<string, MultiplayerParticipant> rowSources = new Dictionary<string, MultiplayerParticipant>(StringComparer.Ordinal);
        private readonly List<MultiplayerParticipant> rowOrder = new List<MultiplayerParticipant>(16);
        private IReadOnlyList<VoicePlayerRow> players = Array.Empty<VoicePlayerRow>();
        private MultiplayerSessionSnapshot? playersFrom;
        private bool playersStale = true;
        private bool playersVisible;
        private string playersNote = VoiceText.PlayersSolo;

        public bool PlayersVisible => playersVisible;

        public string PlayersNote => playersNote;

        public IReadOnlyList<VoicePlayerRow> Players => players;

        public bool IsMuted(string key) =>
            key != null && playersFrom != null && rowSources.TryGetValue(key, out var participant) && runtime.Mutes.IsMuted(participant, playersFrom.Id);

        public bool IsSpeaking(string key)
        {
            var voice = runtime.Voice;
            if (voice == null || key == null || !rowSources.TryGetValue(key, out var participant)) return false;
            try
            {
                return voice.IsSpeaking(participant.Id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void ToggleMute(string key)
        {
            var session = ReadSnapshot();
            var current = key != null && rowSources.TryGetValue(key, out var listed) ? FindCurrent(session, listed) : null;
            if (current == null || session == null)
            {
                playersStale = true;
                Notify(VoiceText.PlayerGone, ScreenTone.Warning);
                return;
            }

            var mute = !runtime.Mutes.IsMuted(current, session.Id);
            if (!runtime.Mutes.SetMuted(current, session.Id, mute))
            {
                Notify(VoiceText.MutesFull, ScreenTone.Warning);
                return;
            }

            var name = NameOf(current);
            Notify(mute ? VoiceText.Muted(name) : VoiceText.Unmuted(name), ScreenTone.Neutral);
        }

        private void TickPlayers()
        {
            if (!shown) return;
            var session = ReadSnapshot();
            if (!playersStale && ReferenceEquals(session, playersFrom)) return;
            playersStale = false;
            playersFrom = session;
            if (ReadRole() == InternalSessionRole.Solo || session == null)
            {
                ShowNoPlayers(VoiceText.PlayersSolo);
                return;
            }

            if (!VoiceMuteBook.ListsHostsPlayers(session))
            {
                ShowNoPlayers(VoiceText.PlayersWaiting);
                return;
            }

            if (!SameListing(session)) Rebuild(session);
            playersVisible = rowOrder.Count > 0;
            playersNote = playersVisible ? string.Empty : VoiceText.PlayersNobody;
        }

        private void ShowNoPlayers(string note)
        {
            playersVisible = false;
            playersNote = note;
            if (rowOrder.Count == 0) return;
            rowOrder.Clear();
            rowSources.Clear();
            players = Array.Empty<VoicePlayerRow>();
        }

        /// <summary>Gets whether <paramref name="session"/> lists the same other players, by the same names, as the rows show.</summary>
        private bool SameListing(MultiplayerSessionSnapshot session)
        {
            var position = 0;
            var participants = session.Participants;
            for (var index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (participant.IsLocal) continue;
                if (position >= rowOrder.Count) return false;
                var row = rowOrder[position++];
                if (!row.Id.Equals(participant.Id) || !row.PlayerKey.Equals(participant.PlayerKey) || row.IsHost != participant.IsHost ||
                    !string.Equals(row.DisplayName, participant.DisplayName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return position == rowOrder.Count;
        }

        private void Rebuild(MultiplayerSessionSnapshot session)
        {
            rowOrder.Clear();
            rowSources.Clear();
            var rows = new List<VoicePlayerRow>(session.Participants.Count);
            foreach (var participant in session.Participants)
            {
                if (participant.IsLocal || rowSources.ContainsKey(participant.Id.Value)) continue;
                rowOrder.Add(participant);
                rowSources.Add(participant.Id.Value, participant);
                rows.Add(new VoicePlayerRow(participant.Id.Value, NameOf(participant), participant.IsHost, canMute: true));
            }

            players = rows;
        }

        /// <summary>Finds the participant a row showed in the session as it is now: the same id with the same player key.</summary>
        private static MultiplayerParticipant? FindCurrent(MultiplayerSessionSnapshot? session, MultiplayerParticipant listed)
        {
            if (!VoiceMuteBook.ListsHostsPlayers(session)) return null;
            foreach (var participant in session!.Participants)
            {
                if (!participant.IsLocal && participant.Id.Equals(listed.Id) && participant.PlayerKey.Equals(listed.PlayerKey)) return participant;
            }

            return null;
        }

        private static string NameOf(MultiplayerParticipant participant)
        {
            var name = HostText.ForDisplay(participant.DisplayName);
            return name.Length == 0 ? VoiceText.UnnamedPlayer : name;
        }

        private InternalSessionRole ReadRole()
        {
            try
            {
                var current = control();
                return current == null ? InternalSessionRole.Solo : current.Role;
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
