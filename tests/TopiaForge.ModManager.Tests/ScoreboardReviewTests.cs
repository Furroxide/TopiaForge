using System;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class ScoreboardReviewTests
    {
        internal static void Run()
        {
            ProviderReplacement(); DisconnectedTargets(); BoundedFiniteGain(); ConsentAndPlayback();
            Console.WriteLine("ScoreboardReviewTests passed: provider replacement, disconnected targets, 256-entry gain bound, finite clamps, consent and initial playback.");
        }

        private static void ProviderReplacement()
        {
            var player = SessionsRig.Player("p1", "Sam", "key");
            var before = new RecordingSessionControl { Role = InternalSessionRole.ListenHost, SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "same", player) };
            var after = new RecordingSessionControl { Role = before.Role, SessionSnapshot = before.SessionSnapshot };
            var current = before;
            ScreenPrompt? prompt = null;
            var screen = new ScoreboardPresenter(() => current, () => Array.Empty<ScoreboardData>(), () => null, p => prompt = p, (_, _) => { });
            screen.Tick(); var row = screen.Rows.Single(x => !x.IsLocal); screen.Ban(row);
            current = after; prompt!.Answer(true); screen.ServerMute(row);
            That(before.Calls.Count == 0 && after.Calls.Count == 0, "provider replacement invalidates retained actions even when the entire snapshot is shared");
            screen.Tick();
            That(!ReferenceEquals(row, screen.Rows.Single(x => !x.IsLocal)), "provider replacement creates a fresh row authority");
            screen.Kick(row); That(prompt.IsWithdrawn, "old row never creates a fresh prompt");
        }

        private static void DisconnectedTargets()
        {
            var player = SessionsRig.Player("p1", "Sam", "key");
            var control = new RecordingSessionControl { Role = InternalSessionRole.ListenHost, SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", player) };
            ScreenPrompt? prompt = null;
            var screen = new ScoreboardPresenter(() => control, () => Array.Empty<ScoreboardData>(), () => null, p => prompt = p, (_, _) => { });
            screen.Tick(); var row = screen.Rows.Single(x => !x.IsLocal); screen.Kick(row);
            control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", SessionsRig.Player("p1", "Sam", "key", connected: false));
            prompt!.Answer(true); screen.ServerMute(row);
            That(control.Calls.Count == 0, "disconnected target refuses confirmation and server mute before refresh");
            screen.Tick(); That(!screen.Rows.Single(x => !x.IsLocal).CanModerate, "disconnected row has no moderation");
            control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s"); screen.Tick();
            control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", player); screen.Tick();
            screen.Kick(row); That(control.Calls.Count == 0 && prompt.IsWithdrawn, "a departed row is retired even if its original identity rejoins");
        }

        private static void BoundedFiniteGain()
        {
            var book = new VoiceVolumeBook(); var session = new MultiplayerSessionId("s");
            var first = SessionsRig.Player("p0", "Sam", "key0");
            That(book.Set(first, session, -100) && book.Get(first, session) == 0, "negative gain clamps to silence");
            That(book.Set(first, session, 100) && book.Get(first, session) == 1, "excess gain clamps to unity");
            book.Set(first, session, 0.25f);
            foreach (var value in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
                That(!book.Set(first, session, value) && book.Get(first, session) == 0.25f, "non-finite gain leaves the previous choice unchanged");
            That(!book.Set(first, default, 0), "invalid session cannot update gain");
            for (var i = 1; i < VoiceVolumeBook.Capacity; i++)
                That(book.Set(SessionsRig.Player("p" + i, "Sam", "key" + i), session, 0.5f), "bounded gain entry fits");
            var extra = SessionsRig.Player("extra", "Sam", "extra");
            That(!book.Set(extra, session, 0) && book.Get(extra, session) == 1, "257th key is rejected without changing playback");
            That(book.Set(first, session, 0.75f), "existing choice updates at capacity");
            That(book.Set(first, session, 1) && book.Set(extra, session, 0), "default gain frees retained capacity");
            var fallback = new VoiceVolumeBook(); var host = SessionsRig.Player("host", "Sam", null, host: true);
            fallback.Set(host, session, 0.5f);
            var next = new MultiplayerSessionId("next");
            That(fallback.Get(host, next) == 1, "unkeyed host gain does not cross sessions");
            fallback.Set(extra, next, 0.2f);
            That(fallback.Get(host, session) == 1, "new session discards old fallback retention");
        }

        private static void ConsentAndPlayback()
        {
            var voice = new VoiceRig(InternalSessionRole.ListenHost);
            var player = SessionsRig.Player("p1", "Sam", "key");
            voice.Snapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", player);
            voice.Control.SessionSnapshot = voice.Snapshot; voice.Start();
            var screen = new ScoreboardPresenter(() => voice.Control, () => Array.Empty<ScoreboardData>(), () => voice.Runtime, _ => { }, (_, _) => { });
            screen.Tick(); var row = screen.Rows.Single(x => !x.IsLocal);
            screen.Volume(row, 0.25f); screen.Mute(row); screen.ServerMute(row); voice.Frame();
            That(!voice.Runtime.ConsentCurrent && !voice.Runtime.VoiceEnabled && !voice.Voice.Enabled && voice.Microphone.Opens == 0,
                "listening and moderation never grant consent, enable voice or open microphone");
            That(voice.Control.Muted.Contains("p1") && voice.Runtime.Mutes.IsMuted(player, voice.Snapshot.Id), "local and server mute use separate stores");
            screen.ServerMute(row); That(!voice.Control.Muted.Contains("p1") && voice.Runtime.Mutes.IsMuted(player, voice.Snapshot.Id), "server unmute preserves local mute");
            screen.Mute(row);
            voice.Runtime.ConsentCurrent = true; voice.Runtime.VoiceEnabled = true;
            voice.Presenter.SetVoiceVolumePercent(40); voice.Voice.Talks(player.Id); voice.Frame(2);
            That(voice.Emitters.Made.Single(x => x.Stream != null).Volume == 0.1f, "new emitter starts with player times master gain");
            var bytes = Seam.Allocated(() => voice.Runtime.Playback.Tick(voice.Voice, voice.Snapshot), 10_000);
            That(bytes == 0, "settled gained playback allocates zero bytes: " + bytes);
            voice.Runtime.Dispose();
        }
    }
}
