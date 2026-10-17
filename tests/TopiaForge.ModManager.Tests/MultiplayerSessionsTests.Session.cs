using System;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>SESSION: players only once the session is Ready, moderation checked against the current snapshot, and leaving.</summary>
    internal static partial class MultiplayerSessionsTests
    {
        private static void PlayersShowOnlyOnceTheSessionIsReady()
        {
            // During a join the engine's snapshot can still carry the previous host's players, mislabelled.
            var rig = new SessionsRig();
            rig.Presenter.SelectTab(MultiplayerSubTab.Session);
            var stale = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s-old",
                SessionsRig.Player("p1", "Ada", "key-a"), SessionsRig.Player("p2", "Bo", "key-b"));
            foreach (var phase in new[] { InternalJoinPhase.Resolving, InternalJoinPhase.Connecting, InternalJoinPhase.Synchronizing, InternalJoinPhase.Reconnecting })
            {
                rig.Status(phase == InternalJoinPhase.Synchronizing || phase == InternalJoinPhase.Reconnecting ? InternalSessionRole.Client : InternalSessionRole.Solo, phase);
                rig.Environment.SessionSnapshot = stale;
                rig.Frame();
                var session = rig.Presenter.Session;
                That(!session.PlayersVisible && session.Players.Count == 0 && session.PlayersNote == SessionPresenter.WaitingNote,
                    "while the join is " + phase + " no player is listed, only why");
                session.Select("p1");
                That(!session.CanKick && !session.CanBan && !session.CanMute, "and nobody can be kicked, banned or muted (" + phase + ")");
                session.Kick();
                session.Ban();
                session.ToggleMute();
                rig.Frame();
                That(rig.Prompt == null && !rig.Control.Calls.Any(call => call.StartsWith("Kick", StringComparison.Ordinal) || call.StartsWith("Ban", StringComparison.Ordinal) || call.StartsWith("Mute", StringComparison.Ordinal)),
                    "asking anyway does nothing (" + phase + ")");
            }

            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Synchronizing, "s-new", "p1");
            rig.Frame();
            That(!rig.Presenter.Session.PlayersVisible, "in session but with the snapshot not Ready, nobody is listed yet");
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-new", "p1", SessionsRig.Player("p2", "Cy", "key-c"));
            rig.Frame();
            var joined = rig.Presenter.Session;
            That(joined.PlayersVisible && joined.Players.Select(row => row.Name).SequenceEqual(new[] { "Ada", "Sam", "Cy" }) && joined.Players.All(row => !row.CanModerate),
                "once Ready the session's own players are listed; a joined player moderates nobody");

            var host = new SessionsRig();
            host.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            host.Environment.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s-host",
                SessionsRig.Player("p1", "Ada", "key-a"), SessionsRig.Player("p2", "Bo", null), SessionsRig.Player("p3", "Gone", "key-g", connected: false));
            host.Frame();
            var rows = host.Presenter.Session.Players;
            That(host.Presenter.Session.PlayersVisible && rows.Count == 3 && rows[0].IsHost && rows[0].IsLocal && !rows[0].CanModerate,
                "a host lists its connected players, itself first and not moderatable");
            That(rows[1].CanModerate && !rows[2].CanModerate, "a player is moderatable only with a key that says who holds the id");
        }

        private static void ModerationChecksThePlayerKeyBeforeActing()
        {
            var rig = HostingWithAda();
            var session = rig.Presenter.Session;
            session.Select("p1");
            That(session.CanKick && session.CanBan && session.CanMute, "Ada can be moderated");

            // The id is reused in the same frame: Ada left and Bo got p1, before the rows were rebuilt.
            rig.Environment.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s-host", SessionsRig.Player("p1", "Bo", "key-b"));
            session.ToggleMute();
            That(!rig.Control.Calls.Any(call => call.StartsWith("Mute", StringComparison.Ordinal)) && rig.Presenter.TryTakeNotice(out var changed) && changed.Text == SessionPresenter.ListChanged,
                "a mute fired from Ada's row acts on nobody, and says the list changed");

            rig = HostingWithAda();
            session = rig.Presenter.Session;
            session.Select("p1");
            session.Kick();
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.Destructive && rig.Prompt.Title == "Remove Ada?", "kicking confirms through the destructive modal");
            rig.Environment.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s-host", SessionsRig.Player("p1", "Bo", "key-b"));
            rig.Answer(ScreenPromptKind.Destructive, true);
            That(!rig.Control.Calls.Any(call => call.StartsWith("Kick", StringComparison.Ordinal)) && rig.Noticed(SessionPresenter.ListChanged),
                "confirming after Bo took the id kicks nobody");
            That(!session.CanKick && session.SelectedKey == null, "and the selection does not carry over to Bo");

            rig = HostingWithAda();
            session = rig.Presenter.Session;
            session.Select("p1");
            session.Ban();
            rig.Frame();
            rig.Environment.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s-host");
            rig.Answer(ScreenPromptKind.Destructive, true);
            That(!rig.Control.Calls.Any(call => call.StartsWith("Ban", StringComparison.Ordinal)), "a ban for a player who left bans nobody");

            rig = HostingWithAda();
            session = rig.Presenter.Session;
            session.Select("p1");
            session.Kick();
            rig.Answer(ScreenPromptKind.Destructive, true);
            That(rig.Control.Calls.Contains("Kick:p1") && rig.Noticed("Removed Ada"), "while Ada still holds p1 the kick reaches her");
            session.Select("p1");
            session.ToggleMute();
            rig.Frame();
            That(rig.Control.Calls.Contains("Mute:p1") && session.IsSelectedMuted, "and so does a mute, which the row then shows");
            session.Ban();
            rig.Answer(ScreenPromptKind.Destructive, false);
            That(!rig.Control.Calls.Any(call => call.StartsWith("Ban", StringComparison.Ordinal)), "cancelling a ban bans nobody");
        }

        private static void LeavingAndEndingConfirm()
        {
            var rig = new SessionsRig();
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-c", "p1");
            rig.Frame();
            var session = rig.Presenter.Session;
            That(session.CanLeave && !session.CanEnd && session.Title == "Joined a session", "a joined player can leave the server");
            session.Leave();
            rig.Frame();
            That(rig.Prompt?.Title == "Leave server?", "leaving confirms through the destructive modal");
            rig.Answer(ScreenPromptKind.Destructive, true);
            That(rig.Control.Calls.Contains("Leave") && rig.Noticed("You left the session."), "and is never refused");

            var host = HostingWithAda();
            That(host.Presenter.Session.CanEnd && !host.Presenter.Session.CanLeave && host.Presenter.Session.InviteText == SessionPresenter.LanInvite,
                "a host ends the session for everyone, and finds its LAN invite text");
            host.Presenter.Session.EndForEveryone();
            host.Answer(ScreenPromptKind.Destructive, true);
            That(host.Control.Calls.Contains("StopHosting"), "ending stops hosting");
        }

        private static void ASessionThatEndsWhilePlayingIsToasted()
        {
            var rig = new SessionsRig();
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-kicked", "p1");
            rig.Frame();
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Kicked);
            rig.Control.Status.HostMessage = "bye from 198.51.100.4";
            rig.Environment.SessionSnapshot = null;
            rig.Frame();
            var kicked = rig.Notices.Single(notice => notice.Text.StartsWith("The host removed you", StringComparison.Ordinal));
            That(kicked.Tone == ScreenTone.Warning && kicked.Text.Contains(HostText.HostSaysPrefix, StringComparison.Ordinal) && !Shows(kicked.Text),
                "a kick while playing is toasted, with the host's words attributed and redacted (" + kicked.Text + ")");

            var left = new SessionsRig();
            left.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            left.Frame();
            left.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
            left.Frame();
            That(left.Notices.Count == 0, "leaving on purpose raises no toast");
        }

        private static SessionsRig HostingWithAda()
        {
            var rig = new SessionsRig();
            rig.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            rig.Environment.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s-host", SessionsRig.Player("p1", "Ada", "key-a"));
            rig.Frame();
            return rig;
        }
    }
}
