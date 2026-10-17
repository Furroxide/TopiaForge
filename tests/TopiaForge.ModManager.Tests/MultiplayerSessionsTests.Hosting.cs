using System;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>HOST: its bounds, opening a running game in place, starting one first, the direct prompt and ending.</summary>
    internal static partial class MultiplayerSessionsTests
    {
        private static void HostSettingsAreBounded()
        {
            var rig = new SessionsRig();
            var host = rig.Presenter.Host;
            That(host.MaxPlayers == 8 && host.MinimumPlayers == 2 && host.MaximumPlayers == 16, "a session takes 2 to 16 players, 8 by default");
            foreach (var (asked, kept) in new[] { (1, 2), (2, 2), (0, 2), (-5, 2), (9, 9), (16, 16), (17, 16), (int.MaxValue, 16) })
            {
                host.SetMaxPlayers(asked);
                That(host.MaxPlayers == kept, asked + " players is held to " + kept);
            }

            That(host.Name == "Player's session" && host.NameError.Length == 0, "the server name starts from the player's name");
            foreach (var bad in new[] { "", " lead", "trail ", new string('s', 49), "tab\there" })
            {
                host.SetName(bad);
                That(host.NameError == HostPresenter.NameRule, bad.Length + " characters is refused as a server name");
            }

            host.SetName(new string('s', 48));
            That(host.NameError.Length == 0, "48 characters is a server name");
            host.SetPassword(new string('p', 65));
            That(host.PasswordError == ConnectingPresenter.PasswordRule, "a 65-character password is refused");
            host.SetPassword("line\nbreak");
            That(host.PasswordError == ConnectingPresenter.PasswordRule, "a password with a control character is refused");
            host.SetPassword(string.Empty);
            That(host.PasswordError.Length == 0, "no password means an open session");
            That(host.VisibilityOptions.SequenceEqual(new[] { "Public", "Invite", "LAN" }) && host.VisibilityOptions[host.Visibility] == "LAN",
                "visibility offers Public, Invite and LAN, LAN by default");
            That(host.PendingPolicies.Select(seam => seam.Label).SequenceEqual(new[] { "Guests can build", "Prop protection", "Per-player quotas" }),
                "the host policies the engine does not support yet are named seams");

            // The launcher's hosting defaults and display name use the same bounds, so a choice made in either place fits both.
            That(host.MinimumPlayers == TopiaForge.ModManager.Core.LaunchSessionValues.MinimumPlayers &&
                 host.MaximumPlayers == TopiaForge.ModManager.Core.LaunchSessionValues.MaximumPlayers &&
                 InternalHostSettings.MaximumNameLength == TopiaForge.ModManager.Core.LaunchSessionValues.MaximumServerName &&
                 MultiplayerSettings.MinimumDisplayNameLength == TopiaForge.ModManager.Core.LaunchSessionValues.MinimumDisplayName &&
                 MultiplayerSettings.MaximumDisplayNameLength == TopiaForge.ModManager.Core.LaunchSessionValues.MaximumDisplayName,
                "the in-game bounds are the launch contract's");
        }

        private static void HostingAtTheMenuStartsTheGameThenOpensIt()
        {
            var rig = new SessionsRig();
            ReadyHost(rig);
            var host = rig.Presenter.Host;
            That(host.ChoosesTarget && host.TargetOptions.Count == 1 && host.HostLabel == "START AND HOST" && host.CanHost,
                "at the menu, HOST chooses a game to start");
            host.SetPassword("open sesame");
            host.Host();
            rig.Frame();
            That(rig.Environment.PendingLaunch != null && !rig.Control.Calls.Contains("StartHosting"), "the game starts first; nothing is opened yet");
            That(!host.CanHost && !host.FormEnabled, "and the form waits");
            rig.Environment.GamePhase = WorldSessionPhase.Running;
            rig.Environment.PendingLaunch!(true, "Launch target reached Running.");
            rig.Frame();
            var settings = rig.Control.LastHost;
            That(settings != null && settings.Visibility == InternalSessionVisibility.Lan && !settings.AllowDirect && settings.MaxPlayers == 8 &&
                 settings.Password == "open sesame" && settings.Gamemode == "Sandbox" && settings.World == "open-sandbox",
                "once it runs, it opens on the local network, never directly, with the game's names");
            That(host.Password.Length == 0, "and the password is not kept");
            rig.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            rig.Frame();
            That(host.CanEnd && host.ProgressTone == ScreenTone.Success && rig.Noticed("open to other players") && rig.Presenter.StatusText.StartsWith("Hosting", StringComparison.Ordinal),
                "hosting is reported");
            That(rig.Store.Stored!.HostMaxPlayers == 8 && rig.Store.Stored.HostVisibility == InternalSessionVisibility.Lan, "the hosting settings are remembered, but never the password");

            var failed = new SessionsRig();
            ReadyHost(failed);
            failed.Presenter.Host.Host();
            failed.Environment.PendingLaunch!(false, "The target is not available.");
            failed.Frame();
            That(!failed.Control.Calls.Contains("StartHosting") && failed.Presenter.Host.ProgressTone == ScreenTone.Danger && failed.Presenter.Host.FormEnabled,
                "a game that does not start opens nothing, and says why");

            var stopped = new SessionsRig();
            ReadyHost(stopped);
            stopped.Presenter.Host.Host();
            stopped.Environment.PendingLaunch!(true, "Launch target reached Running.");
            stopped.Environment.GamePhase = WorldSessionPhase.Stopping;
            stopped.Frame();
            That(!stopped.Presenter.Host.FormEnabled && !stopped.Control.Calls.Contains("StartHosting"), "a game that stops at once is waited on while it stops");
            stopped.Environment.GamePhase = WorldSessionPhase.Idle;
            stopped.Frame();
            That(!stopped.Control.Calls.Contains("StartHosting") && stopped.Presenter.Host.ProgressTone == ScreenTone.Warning &&
                 stopped.Presenter.Host.FormEnabled && stopped.Presenter.Host.CanHost,
                "and once it stopped, nothing is opened and HOST can start again");
        }

        private static void OpeningARunningGamePromotesItInPlace()
        {
            var rig = new SessionsRig();
            rig.Environment.GamePhase = WorldSessionPhase.Running;
            rig.Environment.CurrentGameText = "This game: Sandbox · open-sandbox";
            rig.Environment.CurrentGame = new HostTargetChoice("Sandbox", new TopiaForge.ModManager.Core.LaunchRequest("io.example.sandbox"), "Sandbox", "open-sandbox");
            rig.Presenter.SelectTab(MultiplayerSubTab.Host);
            rig.Frame();
            var host = rig.Presenter.Host;
            That(!host.ChoosesTarget && host.HostLabel == "OPEN TO PLAYERS" && host.CurrentGameText.Length > 0, "a running game is opened in place");
            host.Host();
            rig.Frame();
            That(rig.Control.Calls.Contains("StartHosting") && rig.Environment.PendingLaunch == null && rig.Control.LastHost!.World == "open-sandbox",
                "promoting starts no other game");

            foreach (var phase in new[] { WorldSessionPhase.Preparing, WorldSessionPhase.LoadingWorld, WorldSessionPhase.StartingMode, WorldSessionPhase.Stopping })
            {
                var busy = new SessionsRig();
                busy.Environment.GamePhase = phase;
                That(!busy.Presenter.Host.CanHost && busy.Presenter.Host.ModeText.StartsWith("Wait", StringComparison.Ordinal), "a game that is " + phase + " cannot be opened yet");
            }

            var joined = new SessionsRig();
            joined.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            joined.Frame();
            That(!joined.Presenter.Host.CanHost && joined.Presenter.Host.ModeText.StartsWith("Leave the joined", StringComparison.Ordinal), "a joined player leaves before hosting");
            var standalone = new SessionsRig();
            standalone.Environment.StandaloneModName = "Solo";
            That(!standalone.Presenter.Host.CanHost, "an enabled standalone mod refuses hosting");
        }

        private static void PublicHostingAsksForADirectConnection()
        {
            var rig = new SessionsRig();
            rig.Environment.GamePhase = WorldSessionPhase.Running;
            var host = rig.Presenter.Host;
            host.SetVisibility(0);
            host.Host();
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.DirectConnection && rig.Prompt.Title == MultiplayerPrivacyText.DirectTitle,
                "a public session, which in this build only direct connections reach, asks the approved prompt");
            rig.Answer(ScreenPromptKind.DirectConnection, false);
            That(rig.Control.NetworkCalls == 0 && rig.Environment.PendingLaunch == null && rig.Presenter.HideMyIp,
                "cancelling leaves Hide my IP on, and nothing is opened or contacted");
            host.Host();
            rig.Answer(ScreenPromptKind.DirectConnection, true);
            That(rig.Control.LastHost!.AllowDirect && rig.Control.LastHost.Visibility == InternalSessionVisibility.Public, "allowing it opens this session directly");

            var hidden = new SessionsRig();
            hidden.Environment.GamePhase = WorldSessionPhase.Running;
            hidden.Presenter.SetHideMyIp(false);
            hidden.Answer(ScreenPromptKind.Disclosure, true);
            hidden.Presenter.Host.SetVisibility(1);
            hidden.Presenter.Host.Host();
            hidden.Frame();
            That(hidden.Prompt == null && hidden.Control.LastHost!.AllowDirect, "with Hide my IP turned off by the player, no per-session prompt is needed");
        }

        private static void HostingFailuresAreWordedAndEndingConfirms()
        {
            var refused = new SessionsRig();
            refused.Environment.GamePhase = WorldSessionPhase.Running;
            refused.Control.HostResult = OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Leave the joined session before hosting.");
            refused.Presenter.Host.Host();
            refused.Frame();
            That(refused.Presenter.Host.ProgressTone == ScreenTone.Danger && refused.Noticed("Hosting did not start"), "a refusal is shown and toasted");

            var failing = new SessionsRig();
            failing.Environment.GamePhase = WorldSessionPhase.Running;
            failing.Presenter.Host.Host();
            failing.Frame();
            failing.Status(InternalSessionRole.Solo, host: InternalHostPhase.Failed);
            failing.Control.Status.HostFailure = InternalLiveFailure.PortInUse;
            failing.Frame();
            That(failing.Presenter.Host.ProgressText.Contains("multiplayer port", StringComparison.Ordinal) && failing.Presenter.Host.FormEnabled,
                "a host that fails to open says why, in this game's words");

            var hosting = new SessionsRig();
            hosting.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            hosting.Frame();
            That(hosting.Presenter.Host.CanEnd, "a host can end the session");
            hosting.Presenter.Host.End();
            hosting.Frame();
            That(hosting.Prompt?.Kind == ScreenPromptKind.Destructive && hosting.Prompt.Title == HostPresenter.EndTitle, "ending confirms through the destructive modal");
            hosting.Answer(ScreenPromptKind.Destructive, false);
            That(!hosting.Control.Calls.Contains("StopHosting"), "cancelling ends nothing");
            hosting.Presenter.Host.End();
            hosting.Answer(ScreenPromptKind.Destructive, true);
            That(hosting.Control.Calls.Contains("StopHosting") && hosting.Noticed("ended for everyone"), "confirming ends it for everyone");
        }

        private static void ContentReadinessGatesHostingAndJoining()
        {
            var rig = new SessionsRig();
            rig.Environment.GamePhase = WorldSessionPhase.Running;
            rig.Environment.ContentReadiness = new InternalContentReadiness(InternalContentState.Pending, 1, 4, 41, 100);
            rig.Frame();
            var content = rig.Presenter.Host.Content;
            That(content.State == ContentReadinessState.Pending && content.Text == ContentReadinessPresenter.PendingPrefix + ": 41%" && Math.Abs(content.Fraction - 0.41f) < 0.001f,
                "HOST shows the identification's progress");
            That(!rig.Presenter.Host.CanHost && !rig.Presenter.Browse.CanJoin && ReferenceEquals(rig.Presenter.Browse.Content, content),
                "and neither hosting nor joining can start; BROWSE shows the same progress");
            rig.Environment.ContentReadiness = new InternalContentReadiness(InternalContentState.Pending, 0, 0, 0, 0);
            rig.Frame();
            That(content.Text == ContentReadinessPresenter.PendingText, "before the work has listed anything it says only that it runs");
            rig.Environment.ContentReadiness = new InternalContentReadiness(InternalContentState.Unreadable, 0, 0, 0, 0);
            rig.Frame();
            That(content.State == ContentReadinessState.Unreadable && content.Text == LiveFailureMessages.UnreadableContent && content.CanRetry,
                "unreadable mods are worded, with a retry");
            content.Retry();
            That(rig.Environment.Retries == 1, "which identifies them again");
            rig.Environment.ContentReadiness = InternalContentReadiness.Ready;
            rig.Frame();
            That(content.State == ContentReadinessState.Ready && content.Text.Length == 0 && rig.Presenter.Host.CanHost, "identified mods let hosting start");

            var hidden = new SessionsRig();
            hidden.Presenter.OnHidden();
            hidden.Environment.ContentReadiness = new InternalContentReadiness(InternalContentState.Pending, 0, 1, 0, 0);
            hidden.Frame(5);
            That(hidden.Presenter.Host.Content.State == ContentReadinessState.Ready, "a tab nobody opened does not read readiness, which would start identifying");
        }
    }
}
