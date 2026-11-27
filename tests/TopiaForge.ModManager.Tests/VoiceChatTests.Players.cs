using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class VoiceChatTests
    {
        private static void MutesFollowThePlayerKeyAcrossAJoin()
        {
            var book = new VoiceMuteBook();
            var voice = new FakeVoiceService();
            var robin = VoiceRig.Player("p1", "Robin", null, isHost: true);
            var hostA = VoiceRig.Session("a1", robin, VoiceRig.Player("p2", "Sam", "key-sam-a"), VoiceRig.Player("p3", "Kai", "key-kai-a"));
            That(book.SetMuted(new MultiplayerPlayerKey("key-sam-a"), true) && book.Count == 1, "Lee mutes Sam by his key");
            book.Apply(voice, hostA, VoiceRig.FrameSeconds);
            That(voice.Mutes.SequenceEqual(new[] { new ParticipantId("p2") }) && !voice.HearsNobody && voice.MuteCalls == 1, "the engine is given Sam's id at this host");
            book.Apply(voice, hostA, VoiceRig.FrameSeconds);
            That(voice.MuteCalls == 1, "nothing changed, so nothing is set again");

            // A join that finds another host: the engine forgot the names, and until the new host lists its players, only
            // the wish to hear nobody passes.
            voice.ForgetNames();
            var joining = VoiceRig.Session("b1", MultiplayerSessionState.Synchronizing, VoiceRig.Player("p2", "Ana", "key-ana-b"));
            book.Apply(voice, joining, VoiceRig.FrameSeconds);
            That(voice.Mutes.Count == 0 && voice.MuteCalls == 1, "during the join no name is given, though the snapshot already shows a p2");
            book.HearNobody = true;
            book.Apply(voice, joining, VoiceRig.FrameSeconds);
            That(voice.Mutes.Count == 0 && voice.HearsNobody && voice.MuteCalls == 2, "the wish to hear nobody passes during a join");
            book.HearNobody = false;
            book.Apply(voice, joining, VoiceRig.FrameSeconds);

            // At the new host Ana has Sam's old id and another key: she is heard.
            var hostB = VoiceRig.Session("b1", VoiceRig.Player("p1", "Bo", null, isHost: true), VoiceRig.Player("p2", "Ana", "key-ana-b"));
            book.Apply(voice, hostB, VoiceRig.FrameSeconds);
            That(voice.Mutes.Count == 0 && !voice.HearsNobody, "at another host the key names nobody, so Ana is heard though she has Sam's old id");

            // Back at the first host Sam has a new id; his key names him again once the host lists its players.
            voice.ForgetNames();
            var hostAgain = VoiceRig.Session("a2", robin, VoiceRig.Player("p4", "Sam", "key-sam-a"), VoiceRig.Player("p3", "Kai", "key-kai-a"));
            book.Apply(voice, hostAgain, VoiceRig.FrameSeconds);
            That(voice.Mutes.SequenceEqual(new[] { new ParticipantId("p4") }), "back at the first host the mute names Sam's new id");

            // An engine that forgets the names in place is put right within a second.
            voice.ForgetNames();
            book.Apply(voice, hostAgain, 0.5f);
            That(voice.Mutes.Count == 0, "not at once");
            book.Apply(voice, hostAgain, 0.6f);
            That(voice.Mutes.SequenceEqual(new[] { new ParticipantId("p4") }), "but within a second");

            // The host has no key: it is muted by its id for this session alone.
            That(book.SetMuted(robin, hostAgain.Id, true) && book.IsMuted(robin, hostAgain.Id), "the host, without a key, is muted for this session");
            book.Apply(voice, hostAgain, VoiceRig.FrameSeconds);
            That(voice.Mutes.Count == 2 && voice.Mutes.Contains(new ParticipantId("p1")), "and the engine is told");
            var replaced = VoiceRig.Session("a3", robin, VoiceRig.Player("p4", "Sam", "key-sam-a"));
            book.Apply(voice, replaced, VoiceRig.FrameSeconds);
            That(!book.IsMuted(robin, replaced.Id) && voice.Mutes.SequenceEqual(new[] { new ParticipantId("p4") }),
                "in another session that id names nobody for sure, so the host is heard again while Sam stays muted");
            That(book.SetMuted(new MultiplayerPlayerKey("key-sam-a"), false) && book.Count == 1, "unmuting Sam forgets his key");
            That(!book.SetMuted(default(MultiplayerPlayerKey), true) && !book.SetMuted(robin, default(MultiplayerSessionId), true),
                "a key with no value, or a keyless player without a session, names nobody and is refused");
            for (var index = 0; book.Count < VoiceMuteBook.Capacity; index++) book.SetMuted(new MultiplayerPlayerKey("key-" + index), true);
            That(!book.SetMuted(new MultiplayerPlayerKey("one-more"), true), "the book holds at most " + VoiceMuteBook.Capacity);
        }

        private static void PlayersAreMutedOnlyWhileTheSessionListsThem()
        {
            var rig = VoiceRig.On();
            var robin = VoiceRig.Player("p1", "Robin", null, isHost: true);
            var sam = VoiceRig.Player("p2", "Sam<b>!</b>", "key-sam");
            rig.Snapshot = VoiceRig.Session("s1", robin, sam);
            rig.Frame();
            That(!rig.Presenter.PlayersVisible && rig.Presenter.Players.Count == 0, "players are listed only while VOICE shows");
            rig.Presenter.OnShown();
            rig.Frame();
            var rows = rig.Presenter.Players;
            That(rig.Presenter.PlayersVisible && rows.Count == 2 && rows[0].Name == "Robin" && rows[0].IsHost && rows[1].Name == "Sam<b>!</b>" &&
                 rows.All(row => row.CanMute) && rows.All(row => row.Key != "me"), "every other player is listed, by the name they chose, host first, and each can be muted");
            rig.Frame(5);
            That(ReferenceEquals(rig.Presenter.Players, rows), "a list that did not change is kept");

            rig.Presenter.ToggleMute(rows[1].Key);
            rig.Frame();
            That(rig.Presenter.IsMuted(rows[1].Key) && rig.Runtime.Mutes.IsMuted(new MultiplayerPlayerKey("key-sam")) &&
                 rig.Voice.Mutes.SequenceEqual(new[] { new ParticipantId("p2") }) && LastNotice(rig) == VoiceText.Muted("Sam<b>!</b>"),
                "muting Sam reaches the engine by his id at this host");
            rig.Presenter.ToggleMute(rows[0].Key);
            rig.Frame();
            That(rig.Presenter.IsMuted(rows[0].Key) && rig.Voice.Mutes.Count == 2, "the host is muted too");

            // A list a moment old never mutes the wrong player.
            rig.Snapshot = VoiceRig.Session("s1", robin);
            rig.Presenter.ToggleMute(rows[1].Key);
            That(LastNotice(rig) == VoiceText.PlayerGone && rig.Runtime.Mutes.IsMuted(new MultiplayerPlayerKey("key-sam")), "Sam left: nothing changes, and the page says why");
            rig.Snapshot = VoiceRig.Session("s1", robin, VoiceRig.Player("p2", "Ana", "key-ana"));
            rig.Presenter.ToggleMute(rows[1].Key);
            That(LastNotice(rig) == VoiceText.PlayerGone && !rig.Runtime.Mutes.IsMuted(new MultiplayerPlayerKey("key-ana")),
                "a stranger with Sam's id is never muted for him");
            rig.Frame();
            That(rig.Presenter.Players.Count == 2 && rig.Presenter.Players[1].Name == "Ana" && !rig.Presenter.IsMuted(rig.Presenter.Players[1].Key),
                "the list follows the session");

            // While the host does not list its players there is no list.
            rig.Snapshot = VoiceRig.Session("s2", MultiplayerSessionState.Synchronizing);
            rig.Frame();
            That(!rig.Presenter.PlayersVisible && rig.Presenter.PlayersNote == VoiceText.PlayersWaiting, "joining, the page waits for the host's list");
            rig.Snapshot = VoiceRig.Session("s2");
            rig.Frame();
            That(!rig.Presenter.PlayersVisible && rig.Presenter.PlayersNote == VoiceText.PlayersNobody, "alone in a session, the page says so");
            rig.Control.Role = InternalSessionRole.Solo;
            rig.Snapshot = null;
            rig.Frame();
            That(!rig.Presenter.PlayersVisible && rig.Presenter.PlayersNote == VoiceText.PlayersSolo, "playing on one's own, it says how to hear others");

            // Hearing nobody holds across sessions.
            rig.Presenter.SetHearNobody(true);
            rig.Control.Role = InternalSessionRole.Client;
            rig.Snapshot = VoiceRig.Session("s3", MultiplayerSessionState.Connecting);
            rig.Frame();
            That(rig.Voice.HearsNobody && rig.Voice.Mutes.Count == 0 && rig.Store.Stored!.HearNobody, "the wish to hear nobody passes during a join, and is kept");
        }

        private static void SpeakersPlayUntilTheirStreamEnds()
        {
            var rig = VoiceRig.On();
            var sam = new ParticipantId("p2");
            rig.Frame(2);
            That(rig.Emitters.Made.Count == 0, "nobody speaks, nothing plays");
            rig.Voice.Talks(sam);
            rig.Frame();
            var emitter = rig.Emitters.Made.Single();
            That(emitter.Playing && ReferenceEquals(emitter.Stream, rig.Voice.Streams[sam]) && emitter.Volume == 1f && emitter.Position == null &&
                 rig.Voice.MadeStreams == 0 && rig.Runtime.Playback.IsPlaying(sam), "Sam speaks: an emitter plays his own stream, beside the listener, and no stream is made by asking");
            rig.Presenter.SetVoiceVolumePercent(50f);
            rig.Frame();
            That(emitter.Volume == 0.5f, "the voice volume reaches the emitter");

            // Positional voice waits for avatars (WS3.10); its seam places a voice where a source says.
            rig.Runtime.Playback.Positions = new FixedPositions(sam, new VoicePosition(1f, 2f, 3f));
            rig.Frame();
            That(emitter.Position.HasValue && emitter.Position.Value.X == 1f && emitter.Position.Value.Z == 3f, "with a position source the voice plays from there");
            rig.Runtime.Playback.Positions = null;
            rig.Frame();
            That(emitter.Position == null, "without one, beside the listener again");

            // An ended stream gives its emitter back at once, and is never played again.
            rig.Voice.Streams[sam].IsEnded = true;
            rig.Frame();
            That(!emitter.Playing && rig.Emitters.Returned == 1, "the stream ends: the emitter goes back");
            rig.Frame(10);
            That(rig.Emitters.Playing == 0, "a speaker whose stream ended is not played however long they show as speaking");

            // A speaker the session does not list never plays; one it stops listing stops.
            var stranger = new ParticipantId("p9");
            rig.Voice.Talks(stranger);
            rig.Frame();
            That(rig.Emitters.Playing == 0, "a speaker the session does not list never plays");
            rig.Voice.Streams[sam] = new FakePlaybackStream(sam);
            rig.Frame();
            That(rig.Emitters.Playing == 1 && rig.Emitters.Made.Count == 1, "Sam talks again on a new stream, through the same emitter");
            rig.Snapshot = VoiceRig.Session("s1");
            rig.Frame();
            That(rig.Emitters.Playing == 0, "Sam leaves: his voice stops");
            rig.Snapshot = VoiceRig.Session("s1", VoiceRig.Player("p2", "Sam", "key-sam"));
            rig.Frame();
            That(rig.Emitters.Playing == 1, "back, he plays again");
            rig.Control.Voice = null;
            rig.Frame();
            That(rig.Emitters.Playing == 0, "without voice nothing plays");
            rig.Control.Voice = rig.Voice;
            rig.Frame();
            rig.Runtime.Dispose();
            That(rig.Emitters.Playing == 0 && !rig.Microphone.IsOpen, "closing the game stops every voice");
        }

        private sealed class FixedPositions : IVoicePositions
        {
            private readonly ParticipantId speaker;
            private readonly VoicePosition position;

            internal FixedPositions(ParticipantId speaker, VoicePosition position)
            {
                this.speaker = speaker;
                this.position = position;
            }

            public bool TryGetPosition(ParticipantId speaker, out VoicePosition position)
            {
                position = this.position;
                return speaker.Equals(this.speaker);
            }
        }
    }
}
