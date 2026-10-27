using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class ScoreboardTests
    {
        internal static void Run()
        {
            RowsAndColumns(); CurrentTargets(); RolesAndLifetime(); HoldOwnership(); PlaybackGain(); NametagsAndAllocations();
            Console.WriteLine("ScoreboardTests passed: 6 production model/action/input/playback/nametag groups.");
        }
        private static void RowsAndColumns()
        {
            var rig = new Rig();
            var a = new ScoreboardData("mode", new ScoreboardColumnDefinition("score", "<b>Points</b>", 2));
            var b = new ScoreboardData("mode", new ScoreboardColumnDefinition("wins", "Wins", 1));
            a.Values[new ParticipantId("p1")] = new ScoreboardValue("<i>9</i>", 9);
            a.Values[new ParticipantId("p2")] = new ScoreboardValue("10", 10);
            rig.Scores.Add(a); rig.Scores.Add(b); rig.Screen.Tick();
            That(rig.Screen.Columns.SequenceEqual(new[] { "Wins", "<b>Points</b>" }), "column order and literal titles survive");
            That(rig.Screen.Rows.Count == 3 && rig.Screen.Rows.Single(x => x.IsLocal).IsHost, "canonical roster includes local host badge");
            rig.Screen.Sort(1); rig.Screen.Tick();
            That(rig.Screen.Rows[0].Name == "Bo" && rig.Screen.Rows[1].Values[1] == "<i>9</i>", "numeric score order differs from display text order");
            rig.Screen.Sort(1); rig.Screen.Tick();
            That(rig.Screen.Rows[0].Name == "Ada" && rig.Screen.Rows[2].IsHost, "ascending still places missing values last");
            a.Values[new ParticipantId("p1")] = new ScoreboardValue("15", 15); rig.Screen.Tick();
            That(rig.Screen.Rows[1].Name == "Ada" && rig.Screen.Rows[1].Values[1] == "15", "in-place host values update and re-sort");
            var replacement = new ScoreboardData("mode", a.Definition);
            replacement.Values[new ParticipantId("p1")] = new ScoreboardValue("2", 2);
            rig.Scores.Remove(a); rig.Scores.Add(replacement); rig.Screen.Tick();
            That(rig.Screen.SortColumn == 1 && !rig.Screen.Descending && rig.Screen.Rows[0].Name == "Ada", "fresh client baseline retains selected column and direction");
            rig.Scores.Remove(replacement);
            rig.Scores.Remove(a); rig.Screen.Tick();
            That(rig.Screen.Columns.Count == 1 && rig.Screen.SortColumn == -1, "retired columns reset sorting");
            foreach (var pair in new[] { (MultiplayerConnectionQuality.Unknown, -1), (MultiplayerConnectionQuality.Excellent, 4),
                (MultiplayerConnectionQuality.Good, 3), (MultiplayerConnectionQuality.Fair, 2), (MultiplayerConnectionQuality.Poor, 1) })
                That(ScoreboardPresenter.Signal(pair.Item1) == pair.Item2, "bucketed signal " + pair.Item1);
        }
        private static void CurrentTargets()
        {
            var rig = new Rig(); var ada = rig.Screen.Rows.Single(x => x.Name == "Ada");
            rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", Rig.Bo, SessionsRig.Player("p1", "Ada", "a", ping: 120));
            rig.Screen.Tick();
            That(ReferenceEquals(ada, rig.Screen.Rows.Single(x => x.Name == "Ada")), "roster and ping refresh retains the keyed row for keyboard focus");
            rig.Screen.Kick(ada); That(rig.Prompt?.Kind == ScreenPromptKind.Destructive, "kick confirms destructively");
            rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", Rig.Bo, Rig.Ada);
            rig.Prompt!.Answer(true); rig.Prompt.Answer(true);
            That(rig.Control.Calls.Count(x => x.StartsWith("Kick:", StringComparison.Ordinal)) == 1 && rig.Control.Calls.Contains("Kick:p1"), "sorted and reordered row kicks exactly its original key once");
            rig.Screen.Tick(); ada = rig.Screen.Rows.Single(x => x.Name == "Ada"); rig.Screen.Ban(ada);
            rig.Control.Calls.Clear();
            rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", SessionsRig.Player("p1", "Imposter", "other"), Rig.Bo);
            rig.Prompt!.Answer(true); rig.Screen.ServerMute(ada);
            That(rig.Control.Calls.Count == 0, "reused id with another key refuses pending ban and immediate server mute");
            rig.Screen.Tick(); rig.Screen.Kick(rig.Screen.Rows.Single(x => x.Name == "Bo")); rig.Prompt!.Answer(false);
            That(rig.Control.Calls.Count == 0, "cancel does nothing");
        }
        private static void RolesAndLifetime()
        {
            var rig = new Rig(); var ada = rig.Screen.Rows.Single(x => x.Name == "Ada");
            rig.Screen.Kick(ada); rig.Control.Role = InternalSessionRole.Client; rig.Prompt!.Answer(true);
            That(rig.Control.Calls.Count == 0, "current client role invalidates host confirmation before a presenter refresh");
            rig.Screen.Tick(); That(rig.Screen.Rows.All(x => !x.CanModerate), "client rows offer no host actions");
            rig.Screen.ServerMute(ada); That(rig.Control.Calls.Count == 0, "client cannot call server mute through a stale row");
            rig.Control.Role = InternalSessionRole.ListenHost; rig.Screen.Tick();
            rig.Screen.Ban(ada); rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "replacement", Rig.Ada, Rig.Bo);
            rig.Prompt!.Answer(true); That(rig.Control.Calls.Count == 0, "new session invalidates confirmation even with the same key");
            rig.Screen.Tick(); rig.Control.Role = InternalSessionRole.Solo; rig.Screen.Tick();
            That(!rig.Screen.Available && rig.Screen.Rows.Count == 0, "solo keeps scoreboard absent");
            rig.Control.Role = InternalSessionRole.ListenHost; rig.Screen.Tick(); That(rig.Screen.Rows.Count == 3, "role return rebuilds an unchanged snapshot");
            rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Synchronizing, "replacement", Rig.Ada);
            rig.Screen.Tick(); That(rig.Screen.Rows.All(x => !x.CanModerate && !x.CanHear) && rig.Screen.Note.Length > 0, "synchronizing shows status without actions");
        }
        private static void HoldOwnership()
        {
            var hold = new ScoreboardHold();
            That(hold.Tick(true, true, false, true), "fresh Tab opens");
            That(hold.Tick(true, false, false, true), "held Tab stays open");
            That(!hold.Tick(true, false, true, true), "typing or a modal closes it");
            That(!hold.Tick(true, false, false, true), "held Tab cannot reopen after typing");
            hold.Tick(false, false, false, true); That(hold.Tick(true, true, false, true), "new press after release opens");
            hold.Dismiss(); That(!hold.Tick(true, false, false, true), "Escape remains dismissed until release");
            hold.Tick(false, false, false, true); That(!hold.Tick(true, true, false, false), "no session never opens");
            That(!hold.Tick(true, false, false, true), "session start does not inherit a held Tab");
        }
        private static void PlaybackGain()
        {
            var rig = new Rig(); var voice = VoiceRig.On(InternalSessionRole.ListenHost);
            voice.Snapshot = rig.Control.SessionSnapshot; rig.Voice = voice.Runtime; voice.Frame();
            voice.Voice.Talks(Rig.Ada.Id); voice.Voice.Talks(Rig.Bo.Id); voice.Frame(); rig.Screen.Tick();
            var ada = rig.Screen.Rows.Single(x => x.Name == "Ada");
            rig.Screen.Volume(ada, 0.25f); voice.Presenter.SetVoiceVolumePercent(50); voice.Frame();
            That(voice.Emitters.Made.Single(x => x.Stream!.Speaker.Equals(Rig.Ada.Id)).Volume == 0.125f &&
                voice.Emitters.Made.Single(x => x.Stream!.Speaker.Equals(Rig.Bo.Id)).Volume == 0.5f, "per-player gain multiplies master without changing another speaker");
            voice.Runtime.Playback.Volume = 0.8f;
            That(voice.Emitters.Made.Single(x => x.Stream!.Speaker.Equals(Rig.Ada.Id)).Volume == 0.2f, "master changes retain player gain immediately");
            rig.Screen.Mute(ada); voice.Frame();
            That(voice.Voice.Mutes.Contains(Rig.Ada.Id), "scoreboard mute reaches production runtime and voice SPI");
            rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", SessionsRig.Player("p1", "Other", "other"));
            rig.Screen.Volume(ada, 0); rig.Screen.Mute(ada);
            That(voice.Runtime.Playback.Volumes.Get(Rig.Ada, voice.Snapshot!.Id) == 0.25f, "stale row cannot change gain");
            var book = voice.Runtime.Playback.Volumes;
            That(book.Get(SessionsRig.Player("new-id", "Ada", "a"), new MultiplayerSessionId("next")) == 0.25f, "gain follows stable key");
            That(book.Get(SessionsRig.Player("p1", "Other", "other"), new MultiplayerSessionId("s")) == 1f, "gain never follows recycled id");
            var host = SessionsRig.Player("p0", "Host", null, host: true);
            book.Set(host, new MultiplayerSessionId("s"), 0.4f);
            That(book.Get(host, new MultiplayerSessionId("next")) == 1f && !book.Set(host, new MultiplayerSessionId("s"), float.NaN), "unkeyed gain is session-only and rejects NaN");
            That(!voice.Runtime.MicrophoneOpen, "scoreboard never opens microphone"); voice.Runtime.Dispose();
        }
        private static void NametagsAndAllocations()
        {
            var rig = new Rig(); var players = new MultiplayerParticipant[15];
            for (var i = 0; i < players.Length; i++) players[i] = SessionsRig.Player("p" + (i + 1), "Player " + i, "key" + i);
            rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", players);
            for (var i = 0; i < 64; i++)
            {
                var column = new ScoreboardData("mode", new ScoreboardColumnDefinition("c" + i, "Column " + i));
                foreach (var player in players) column.Values[player.Id] = new ScoreboardValue("value", i);
                rig.Scores.Add(column);
            }
            rig.Screen.Tick(); rig.Screen.Sort(63); rig.Screen.Tick();
            var source = new Tags(); var feed = new ScoreboardNametagFeed(); feed.Tick(source, true);
            That(feed.Active.Count(x => x) == 8 && feed.Names[0] == "<b>Ada</b>", "eight avatar tags retain literal display text");
            source.X = 4; feed.Tick(source, true); That(feed.Positions[0].X == 4, "tag follows current avatar anchor");
            var bytes = Seam.Allocated(() => { rig.Screen.Tick(); feed.Tick(source, true); }, 10_000);
            That(bytes == 0, "scoreboard 16 players/64 columns and eight nametag feed allocate zero: " + bytes);
            var firstSnapshot = rig.Control.SessionSnapshot;
            var secondSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", players);
            var refreshBytes = Seam.Allocated(() =>
            {
                rig.Control.SessionSnapshot = ReferenceEquals(rig.Control.SessionSnapshot, firstSnapshot) ? secondSnapshot : firstSnapshot;
                rig.Screen.Tick();
            }, 10_000);
            That(refreshBytes == 0, "unchanged roster refresh allocates zero: " + refreshBytes);
            feed.Tick(null, true); That(!feed.Active.Any(x => x), "lost source hides pooled tags");
            feed.Tick(source, false); That(!feed.Active.Any(x => x), "leaving gameplay hides all tags");
            source.Count = 50; feed.Tick(source, true); That(feed.Active.Count(x => x) == ScoreboardNametagFeed.Capacity, "tag pool stays bounded");
            Console.WriteLine("Scoreboard and eight nametag feed: " + bytes + " bytes across 10,000 settled frames; native kit/render allocation remains qualification work.");
        }
        private sealed class Rig
        {
            internal static readonly MultiplayerParticipant Ada = SessionsRig.Player("p1", "Ada", "a");
            internal static readonly MultiplayerParticipant Bo = SessionsRig.Player("p2", "Bo", "b");
            internal readonly RecordingSessionControl Control = new RecordingSessionControl
            {
                Role = InternalSessionRole.ListenHost,
                SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", Ada, Bo)
            };
            internal readonly List<ScoreboardData> Scores = new List<ScoreboardData>();
            internal readonly ScoreboardPresenter Screen;
            internal ScreenPrompt? Prompt;
            internal VoiceChatRuntime? Voice;
            internal Rig() { Screen = new ScoreboardPresenter(() => Control, () => Scores, () => Voice, p => Prompt = p, (_, _) => { }); Screen.Tick(); }
        }
        private sealed class Tags : IPresenceNametagSource
        {
            internal int Count = 8; internal float X;
            public int NametagCount => Count;
            public int NametagVersion => 0;
            public bool TryGetNametag(int slot, out PresenceNametag tag) { tag = new PresenceNametag("<b>Ada</b>", new NetVector3(X, 2, slot + 4)); return true; }
        }
    }
}
