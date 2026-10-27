using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Reads the canonical roster and Players scoreboard. Settled frames allocate nothing, including sorting.</summary>
    internal sealed partial class ScoreboardPresenter : IScoreboardScreen
    {
        internal const int MaximumPlayers = 32;
        private readonly Func<IInternalMultiplayerControl?> control;
        private readonly Func<IReadOnlyList<ScoreboardData>> scores;
        private readonly Func<VoiceChatRuntime?> voice;
        private readonly Action<ScreenPrompt> ask;
        private readonly Action<string, ScreenTone> notify;
        private readonly List<ScoreboardRow> rows = new List<ScoreboardRow>(MaximumPlayers);
        private readonly List<ScoreboardRow> refreshed = new List<ScoreboardRow>(MaximumPlayers);
        private readonly Dictionary<ScoreboardRow, Target> targets = new Dictionary<ScoreboardRow, Target>(MaximumPlayers);
        private readonly List<ScoreboardData> columns = new List<ScoreboardData>(64);
        private readonly List<string> titles = new List<string>(64);
        private MultiplayerSessionSnapshot? shown;
        private IInternalMultiplayerControl? owner;
        private bool ready;

        internal ScoreboardPresenter(Func<IInternalMultiplayerControl?> control, Func<IReadOnlyList<ScoreboardData>> scores,
            Func<VoiceChatRuntime?> voice, Action<ScreenPrompt> ask, Action<string, ScreenTone> notify)
        { this.control = control; this.scores = scores; this.voice = voice; this.ask = ask; this.notify = notify; }

        public IReadOnlyList<ScoreboardRow> Rows => rows;
        public IReadOnlyList<string> Columns => titles;
        public string Note { get; private set; } = "Join or host a session to see its players.";
        public int SortColumn { get; private set; } = -1;
        public bool Descending { get; private set; }
        internal bool Available { get; private set; }

        internal void Tick()
        {
            var current = control();
            var snapshot = current?.SessionSnapshot;
            var wasAvailable = Available;
            Available = current != null && current.IsLiveAvailable && current.Role != InternalSessionRole.Solo && snapshot != null;
            ready = Available && snapshot!.State == MultiplayerSessionState.Ready;
            Note = !Available ? "Join or host a session to see its players."
                : !ready ? "Reconnecting or synchronizing. Player actions wait for the current roster."
                : snapshot!.Participants.Count == 0 ? "Waiting for the host's player list." : string.Empty;
            if (wasAvailable != Available || !ReferenceEquals(shown, snapshot) || !ReferenceEquals(owner, current))
            {
                RefreshRows(current, Available ? snapshot : null);
                shown = snapshot; owner = current;
            }
            if (!Available) { rows.Clear(); targets.Clear(); }
            RefreshColumns(Available ? scores() : Array.Empty<ScoreboardData>());
            var chat = voice();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; var participant = targets[row].Participant;
                row.CanModerate = ready && current!.Role == InternalSessionRole.ListenHost && !participant.IsLocal && !participant.IsHost && participant.PlayerKey.IsValid && participant.IsConnected;
                row.CanHear = ready && chat?.Voice != null && !participant.IsLocal && participant.IsConnected;
                row.Muted = chat != null && (chat.Mutes.HearNobody || chat.Mutes.IsMuted(participant, snapshot!.Id));
                row.Volume = chat?.Playback.Volumes.Get(participant, snapshot!.Id) ?? 1f;
                row.ServerMuted = current!.IsServerMuted(participant.Id);
                row.Talk = participant.IsLocal ? LocalTalk(chat?.Status ?? VoiceTalkStatus.Unavailable) : row.ServerMuted ? VoiceIndicator.MutedByHost : row.Muted || chat?.IsOn != true ? VoiceIndicator.Muted
                    : chat.Voice?.IsSpeaking(participant.Id) == true ? VoiceIndicator.Talking : VoiceIndicator.Idle;
                row.Signal = participant.IsConnected ? Signal(participant.Quality) : -1;
                for (var c = 0; c < columns.Count; c++)
                {
                    var display = columns[c].Values.TryGetValue(participant.Id, out var value) ? value.Display : "—";
                    var target = targets[row];
                    if (target.Raw[c] == display) continue;
                    target.Raw[c] = display;
                    row.Values[c] = HostText.ForDisplay(display, 32);
                }
            }
            // Stable insertion sort over at most 32 existing references: no comparer/delegate/array allocation per frame.
            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i]; var j = i - 1;
                while (j >= 0 && Compare(rows[j], row) > 0) { rows[j + 1] = rows[j]; j--; }
                rows[j + 1] = row;
            }
        }

        public void Sort(int column)
        {
            if (column < -1 || column >= columns.Count) return;
            Descending = SortColumn == column ? !Descending : column >= 0;
            SortColumn = column;
        }

        private void RefreshRows(IInternalMultiplayerControl? current, MultiplayerSessionSnapshot? snapshot)
        {
            refreshed.Clear();
            if (snapshot != null && current != null)
                for (var i = 0; i < Math.Min(MaximumPlayers, snapshot.Participants.Count); i++)
                {
                    var participant = snapshot.Participants[i];
                    ScoreboardRow? row = null;
                    for (var previous = 0; previous < rows.Count; previous++)
                    {
                        var candidate = rows[previous]; var target = targets[candidate];
                        if (!ReferenceEquals(target.Owner, current) || !target.Session.Equals(snapshot.Id) ||
                            !target.Participant.Id.Equals(participant.Id) || !target.Participant.PlayerKey.Equals(participant.PlayerKey) ||
                            target.Participant.DisplayName != participant.DisplayName || candidate.IsHost != participant.IsHost || candidate.IsLocal != participant.IsLocal) continue;
                        row = candidate; target.Participant = participant; break;
                    }
                    if (row == null)
                    {
                        row = new ScoreboardRow(HostText.ForDisplay(participant.DisplayName, 32), participant.IsHost, participant.IsLocal);
                        targets.Add(row, new Target(current, snapshot.Id, participant));
                    }
                    refreshed.Add(row);
                }
            for (var i = 0; i < rows.Count; i++) if (!refreshed.Contains(rows[i])) targets.Remove(rows[i]);
            rows.Clear(); rows.AddRange(refreshed);
        }

        private int Compare(ScoreboardRow left, ScoreboardRow right)
        {
            var a = targets[left].Participant.Id; var b = targets[right].Participant.Id;
            var order = 0;
            if (SortColumn >= 0 && SortColumn < columns.Count)
            {
                var data = columns[SortColumn];
                var hasA = data.Values.TryGetValue(a, out var av); var hasB = data.Values.TryGetValue(b, out var bv);
                if (hasA != hasB) return hasA ? -1 : 1; // Missing values always last, including descending order.
                order = av.SortKey.CompareTo(bv.SortKey);
            }
            else order = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (order != 0) return Descending ? -order : order;
            return StringComparer.Ordinal.Compare(a.Value, b.Value);
        }

        private void RefreshColumns(IReadOnlyList<ScoreboardData> values)
        {
            var count = Math.Min(64, values.Count);
            var same = columns.Count == count;
            for (var i = 0; same && i < count; i++) same = columns.Contains(values[i]);
            if (same) return;
            var selected = SortColumn >= 0 && SortColumn < columns.Count ? columns[SortColumn] : null;
            columns.Clear(); titles.Clear();
            for (var i = 0; i < count; i++) columns.Add(values[i]);
            columns.Sort((a, b) =>
            {
                var order = a.Definition.Order.CompareTo(b.Definition.Order);
                if (order != 0) return order;
                order = StringComparer.Ordinal.Compare(a.Mod, b.Mod);
                return order != 0 ? order : StringComparer.Ordinal.Compare(a.Definition.Id, b.Definition.Id);
            });
            foreach (var column in columns) titles.Add(HostText.ForDisplay(column.Definition.Title, 64));
            foreach (var target in targets.Values) Array.Clear(target.Raw, 0, target.Raw.Length);
            if (selected == null) return;
            for (var i = 0; i < columns.Count; i++)
                if (columns[i].Mod == selected.Mod && columns[i].Definition.Id == selected.Definition.Id) { SortColumn = i; return; }
            SortColumn = -1; Descending = false;
        }

        private static VoiceIndicator LocalTalk(VoiceTalkStatus status) => status == VoiceTalkStatus.Talking ? VoiceIndicator.Talking
            : status == VoiceTalkStatus.MutedByHost ? VoiceIndicator.MutedByHost
            : status == VoiceTalkStatus.MicBusy || status == VoiceTalkStatus.CannotCheckGame ? VoiceIndicator.MicBusy
            : status == VoiceTalkStatus.NoMicrophone || status == VoiceTalkStatus.MicrophoneFailed ? VoiceIndicator.NoMic
            : status == VoiceTalkStatus.Ready || status == VoiceTalkStatus.Testing || status == VoiceTalkStatus.NoListeners || status == VoiceTalkStatus.Connecting ? VoiceIndicator.Idle : VoiceIndicator.Muted;

        internal static int Signal(MultiplayerConnectionQuality quality) => quality == MultiplayerConnectionQuality.Excellent ? 4
            : quality == MultiplayerConnectionQuality.Good ? 3 : quality == MultiplayerConnectionQuality.Fair ? 2
            : quality == MultiplayerConnectionQuality.Poor ? 1 : -1;

        private sealed class Target
        {
            internal Target(IInternalMultiplayerControl owner, MultiplayerSessionId session, MultiplayerParticipant participant)
            { Owner = owner; Session = session; Participant = participant; }
            internal readonly IInternalMultiplayerControl Owner;
            internal readonly MultiplayerSessionId Session;
            internal MultiplayerParticipant Participant;
            internal readonly string?[] Raw = new string?[64];
        }
    }
}
