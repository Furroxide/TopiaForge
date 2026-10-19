using System;
using System.Linq;
using TopiaForge.ModManager.Core;
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
            rig.Environment.Phase = SessionPhase.Running;
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
            var hosting = rig.Launcher.Journal!.Changes[rig.Launcher.Journal.Changes.Count - 1].NewHosting;
            That(hosting != null && hosting.MaxPlayers == 8 && hosting.Visibility == "lan" && hosting.Port == MultiplayerPreferenceValues.DefaultPort,
                "the hosting defaults are recorded in the launcher's journal, which holds no password");

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
            stopped.Environment.Phase = SessionPhase.Stopping;
            stopped.Frame();
            That(!stopped.Presenter.Host.FormEnabled && !stopped.Control.Calls.Contains("StartHosting"), "a game that stops at once is waited on while it stops");
            stopped.Environment.Phase = SessionPhase.Idle;
            stopped.Frame();
            That(!stopped.Control.Calls.Contains("StartHosting") && stopped.Presenter.Host.ProgressTone == ScreenTone.Warning &&
                 stopped.Presenter.Host.FormEnabled && stopped.Presenter.Host.CanHost,
                "and once it stopped, nothing is opened and HOST can start again");
        }

        /// <summary>
        /// At the main menu HOST offers only games the menu can start: a choice that adds an arena to a running game is
        /// refused there ("An additive arena requires an existing loaded gameplay scene"), and HOST selects its first
        /// choice by default.
        /// </summary>
        private static void HostOffersOnlyGamesTheMenuCanStart()
        {
            using var fixture = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(Program.FindRepoRoot(),
                "tests", "fixtures", "gamemode-v6", "resolution", "player-choice-honours-an-offered-transition.json")));
            var manifest = ModManifestJson.Deserialize(
                fixture.RootElement.GetProperty("profile").GetProperty("packages")[0].GetProperty("manifest").GetRawText());
            var profile = new EffectiveProfile("host-targets", 1, new[] { new ResolvedPackage(manifest.Id, manifest.Version, manifest) });
            var previews = LaunchTargetPreviewBuilder.Build(profile);
            var all = previews.SelectMany(preview => preview.Choices).ToArray();
            That(all.Any(choice => choice.Transition == ModTransitions.AdditiveArena) && all.Any(choice => choice.Transition == ModTransitions.SceneReplacement),
                "the declared target offers both a scene and an arena");

            var offered = HostTargets.AtTheMenu(previews);
            var startable = all.Where(choice => choice.Transition != ModTransitions.AdditiveArena).ToArray();
            That(offered.Count == startable.Length && offered.Count > 0 &&
                 offered.All(target => startable.Any(choice => ReferenceEquals(choice.Request, target.Request))),
                "HOST offers every choice that replaces the scene, and no arena choice (" + offered.Count + " of " + all.Length + ")");

            var rig = new SessionsRig();
            rig.Environment.Targets.AddRange(offered);
            rig.Presenter.SelectTab(MultiplayerSubTab.Host);
            rig.Frame();
            That(rig.Presenter.Host.ChoosesTarget && rig.Presenter.Host.TargetOptions.Count == offered.Count && rig.Presenter.Host.CanHost,
                "and its first, default, choice is one the menu can start");
        }

        private static void OpeningARunningGamePromotesItInPlace()
        {
            var rig = new SessionsRig();
            rig.Environment.Phase = SessionPhase.Running;
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

            foreach (var phase in new[] { SessionPhase.Preparing, SessionPhase.LoadingWorld, SessionPhase.StartingMode, SessionPhase.Stopping })
            {
                var busy = new SessionsRig();
                busy.Environment.Phase = phase;
                That(!busy.Presenter.Host.CanHost && busy.Presenter.Host.ModeText.StartsWith("Wait", StringComparison.Ordinal), "a game that is " + phase + " cannot be opened yet");
            }

            var joined = new SessionsRig();
            joined.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            joined.Frame();
            That(!joined.Presenter.Host.CanHost && joined.Presenter.Host.ModeText.StartsWith("Leave the joined", StringComparison.Ordinal), "a joined player leaves before hosting");
            var standalone = new SessionsRig();
            standalone.Environment.Load("io.example.solo", "standalone");
            That(!standalone.Presenter.Host.CanHost, "an enabled standalone mod refuses hosting");
        }

        /// <summary>
        /// Public and Invite sessions need the TopiaForge service. Without one HOST offers neither: hosting them stays
        /// disabled, says why, and contacts nothing. With one they go through the relay, with no per-session prompt, and take
        /// direct connections too only when the player turned Hide my IP off.
        /// </summary>
        private static void PublicAndInviteHostingNeedTheService()
        {
            var bare = new SessionsRig();
            bare.Environment.Phase = SessionPhase.Running;
            var host = bare.Presenter.Host;
            foreach (var choice in new[] { 0, 1 })
            {
                host.SetVisibility(choice);
                That(!host.CanHost && host.VisibilityNote == LiveFailureMessages.NoService, host.VisibilityOptions[choice] + " needs the TopiaForge service, and says so");
                host.Host();
                bare.Frame();
                That(bare.Prompt == null && bare.Control.NetworkCalls == 0, "so it opens nothing and asks nothing (" + host.VisibilityOptions[choice] + ")");
            }

            host.SetVisibility(2);
            That(host.CanHost && host.VisibilityNote == HostPresenter.LanNote, "LAN hosting needs no service");

            var relayed = new SessionsRig(service: true);
            relayed.Environment.Phase = SessionPhase.Running;
            relayed.Presenter.Host.SetVisibility(0);
            That(relayed.Presenter.Host.CanHost && relayed.Presenter.Host.VisibilityNote == HostPresenter.PublicNote, "with the service, Public hosting is offered");
            relayed.Presenter.Host.Host();
            relayed.Frame();
            That(relayed.Prompt == null && relayed.Control.LastHost!.Visibility == InternalSessionVisibility.Public && !relayed.Control.LastHost.AllowDirect,
                "and goes through the relay with no per-session prompt while Hide my IP is on");

            var open = new SessionsRig(service: true);
            open.Environment.Phase = SessionPhase.Running;
            open.Presenter.SetHideMyIp(false);
            open.Answer(ScreenPromptKind.Disclosure, true);
            open.Presenter.Host.SetVisibility(1);
            That(open.Presenter.Host.VisibilityNote == HostPresenter.InviteNote, "Invite hosting says players join with its code");
            open.Presenter.Host.Host();
            open.Frame();
            That(open.Prompt == null && open.Control.LastHost!.Visibility == InternalSessionVisibility.Invite && open.Control.LastHost.AllowDirect,
                "with Hide my IP turned off by the player, it takes direct connections too");
        }

        /// <summary>A hosted session that the service lists says so; one the service cannot list says why, and keeps hosting.</summary>
        private static void AHostedSessionSaysHowTheServiceListsIt()
        {
            var rig = new SessionsRig(service: true);
            rig.Environment.Phase = SessionPhase.Running;
            rig.Presenter.Host.SetVisibility(0);
            rig.Presenter.Host.Host();
            rig.Frame();
            var host = rig.Presenter.Host;
            void Listing(InternalListingPhase phase, InternalLiveFailure failure = InternalLiveFailure.None, bool listed = false)
            {
                rig.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
                rig.Control.Status.Internet = new InternalInternetStatus { ServiceConfigured = true, ListingPhase = phase, ListingFailure = failure, Listed = listed };
                rig.Frame(2);
            }

            Listing(InternalListingPhase.Registering);
            That(host.ProgressText == HostPresenter.Registering, "registering with the service is shown");
            Listing(InternalListingPhase.Online, listed: true);
            That(host.ProgressText == HostPresenter.Listed && host.ProgressTone == ScreenTone.Success, "and so is being listed");
            Listing(InternalListingPhase.Retrying, InternalLiveFailure.ServiceUnavailable);
            That(host.ProgressTone == ScreenTone.Warning && host.ProgressText == LiveFailureMessages.ForHost(InternalLiveFailure.ServiceUnavailable).Message,
                "a service that cannot be reached is said, and hosting goes on");
            Listing(InternalListingPhase.Online, InternalLiveFailure.RelayUnavailable, listed: true);
            That(host.ProgressText.Contains("until the relay returns", StringComparison.Ordinal), "a relay-only host without a relay says players cannot reach it yet");
            Listing(InternalListingPhase.Failed, InternalLiveFailure.ServiceRefused);
            That(host.ProgressTone == ScreenTone.Danger && host.CanEnd, "a refused listing is shown as failed, and the session can still end");
        }

        private static void HostingFailuresAreWordedAndEndingConfirms()
        {
            var refused = new SessionsRig();
            refused.Environment.Phase = SessionPhase.Running;
            refused.Control.HostResult = OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Leave the joined session before hosting.");
            refused.Presenter.Host.Host();
            refused.Frame();
            That(refused.Presenter.Host.ProgressTone == ScreenTone.Danger && refused.Noticed("Hosting did not start"), "a refusal is shown and toasted");

            var failing = new SessionsRig();
            failing.Environment.Phase = SessionPhase.Running;
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
            rig.Environment.Phase = SessionPhase.Running;
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
