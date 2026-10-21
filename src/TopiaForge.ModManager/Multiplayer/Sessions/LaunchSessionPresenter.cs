using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// Starts what the launcher asked of this game, through the realm as the in-game flows start sessions: the session this
    /// game was launched to host or join (launch request V5), and live joins of it while it runs. A host opens once the
    /// launch's own game runs, or at the menu, after the player types its password when it needs one; a join goes through
    /// the connecting overlay, with "Leave current game?" when a game is running, and a LAN session is looked for on the local
    /// network first. Neither asks this game's own disclosure again: the launcher told the player before it planned the
    /// session, and the session keeps the launch's own privacy. Each is followed into the launcher's records until its
    /// session ends, even once a newer launch begins, and a launch that cannot start says why in game too, where the player
    /// is looking. Main thread only; a frame with nothing to start allocates nothing.
    /// </summary>
    internal sealed partial class LaunchSessionPresenter
    {
        internal const float LanSearchSeconds = 15f;
        internal const string PasswordTitle = "Password for this session";
        internal const string PasswordBody = "The TopiaForge launcher asked this game to host a session that needs a password. Type the password players will join with. It is used for this session only and never kept.";
        internal const string PasswordConfirm = "HOST";
        internal const string NothingTyped = "The session needs a password, and none was typed, so it did not open.";
        internal const string NotHeard = "The session was not heard on your local network.";
        internal const string GameStopped = "The launched game stopped before it could be opened to other players.";
        internal const string Opened = "Your session is open to other players.";
        internal const string AlreadyStarting = "This game is already starting a session the TopiaForge launcher asked for.";
        internal const string NotStarted = "The session the TopiaForge launcher asked for did not start.";
        internal const string LookingOnLan = "Looking for the session on your local network...";
        internal const string NotHeardHere = "The session the TopiaForge launcher asked for was not heard on your local network. Check that its host is still hosting on this network, then join it again from the launcher.";

        private readonly MultiplayerScreensContext context;
        private readonly ConnectingPresenter connecting;
        private readonly MultiplayerBackgroundQueue queue;
        private ILaunchedSession? current;

        // A launch whose session still ran when a newer launch began: followed to that session's end, for its record.
        private ILaunchedSession? earlier;
        private Stage stage;
        private float searched;
        private bool browsingForLaunch;
        private bool toldOpened;

        internal LaunchSessionPresenter(MultiplayerScreensContext context, ConnectingPresenter connecting, MultiplayerBackgroundQueue queue)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.connecting = connecting ?? throw new ArgumentNullException(nameof(connecting));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        }

        private enum Stage
        {
            None,
            Waiting,
            Password,
            FindingLan,
            Joining,
            Following
        }

        /// <summary>Gets whether a launch is being started or followed, so this game's mods are identified for it.</summary>
        internal bool IsActive => current != null;

        /// <summary>Gets the launch being started or followed, for tests.</summary>
        internal ILaunchedSession? Current => current;

        /// <summary>Starts <paramref name="launch"/>. While another launch is still starting it keeps the game, and this one is refused.</summary>
        internal void Begin(ILaunchedSession launch)
        {
            if (launch == null) throw new ArgumentNullException(nameof(launch));
            if (current != null && !current.IsSettled)
            {
                launch.Refuse(ModErrorCode.Conflict, AlreadyStarting);
                return;
            }

            // A launch whose session still runs is followed on to that session's end, so its record says when and why it
            // ended; the realm turns the newer launch away while that session runs.
            var running = current != null && !current.IsFinished ? current : null;
            Finish();
            if (running != null) earlier = running;
            current = launch;
            stage = Stage.Waiting;
            toldOpened = false;
        }

        /// <summary>Main thread, every frame: follows the launch into its records, and takes its next step once the game allows.</summary>
        internal void Tick(float deltaTime)
        {
            var status = context.Status;
            var before = earlier;
            if (before != null)
            {
                before.Follow(status.Status, status.Version, status.Reads);
                if (before.IsFinished) earlier = null;
            }

            var launch = current;
            if (launch == null) return;
            launch.Follow(status.Status, status.Version, status.Reads);
            switch (stage)
            {
                case Stage.Waiting:
                    Wait(launch);
                    break;
                case Stage.FindingLan:
                    FindLan(launch, deltaTime);
                    break;
                case Stage.Joining:
                    // The connecting overlay runs the join, and tells the launch when the provider takes it or why it did not.
                    if (launch.HasStarted) stage = Stage.Following;
                    break;
                case Stage.Following:
                    TellOpened(launch);
                    TellNotOpened(launch);
                    break;
            }

            if (launch.IsSettled) StopLanSearch();
            if (launch.IsFinished && ReferenceEquals(current, launch)) Finish();
        }

        /// <summary>
        /// Stops looking on the local network for a launch and lets go of every launch, as the game closes: a live join whose
        /// session still runs ends in its records as the player leaving it.
        /// </summary>
        internal void Stop()
        {
            StopLanSearch();
            earlier?.Closing();
            current?.Closing();
            earlier = null;
            current = null;
            stage = Stage.None;
        }

        private void Wait(ILaunchedSession launch)
        {
            if (launch.IsSettled || launch.Game != LaunchedGame.Ready) return;
            if (context.Environment.Control == null)
            {
                Refuse(launch, ModErrorCode.Unavailable, SessionLaunch.ProviderMissing);
                return;
            }

            if (!context.Status.IsLiveAvailable)
            {
                Refuse(launch, ModErrorCode.Unavailable, MultiplayerRuntime.LiveSwitchedOff);
                return;
            }

            if (!context.Settings.IsLoaded || !context.Preferences.IsLoaded) return;
            if (context.Content.State == ContentReadinessState.Unreadable)
            {
                Refuse(launch, ModErrorCode.Io, LiveFailureMessages.UnreadableContent);
                return;
            }

            if (!context.Content.IsReady) return;
            if (launch.IsHost) Host(launch);
            else Join(launch);
        }

        private void Host(ILaunchedSession launch)
        {
            var environment = context.Environment;
            switch (SessionRealmGate.ForHost(environment.EvaluateRealmChange(NetRealm.ListenHost)))
            {
                case RealmDecision.Busy:
                    return;
                case RealmDecision.PromoteInPlace:
                    break;
                case RealmDecision.StartAsHost:
                    // A launch that started a game opens that game; one that stopped before it could be opened opens nothing.
                    if (!launch.StartsGame) break;
                    Refuse(launch, ModErrorCode.InvalidState, GameStopped);
                    return;
                case RealmDecision.AlreadyThere:
                    Refuse(launch, ModErrorCode.Conflict, MultiplayerRuntime.AlreadyHosting);
                    return;
                case RealmDecision.StandaloneRefused:
                    Refuse(launch, ModErrorCode.InvalidState, MultiplayerRuntime.StandaloneRefusal(environment.StandaloneMods, "host"));
                    return;
                default:
                    Refuse(launch, ModErrorCode.InvalidState, MultiplayerRuntime.LeaveSessionFirst);
                    return;
            }

            if (launch.Plan.PasswordRequired) AskPassword(launch);
            else Open(launch, null);
        }

        /// <summary>Asks for the session's password, typed in game and never kept; withdrawn once the launch no longer waits for it.</summary>
        private void AskPassword(ILaunchedSession launch)
        {
            stage = Stage.Password;
            ScreenPrompt? prompt = null;
            prompt = new ScreenPrompt(ScreenPromptKind.Password, PasswordTitle, new[] { PasswordBody }, PasswordConfirm, "CANCEL", entered =>
            {
                var typed = prompt!.TakeValue();
                if (!ReferenceEquals(current, launch) || stage != Stage.Password) return;
                if (!entered)
                {
                    launch.Refuse(ModErrorCode.Cancelled, NothingTyped);
                    return;
                }

                if (!HostPresenter.IsPassword(typed))
                {
                    context.Notify(ConnectingPresenter.PasswordRule, ScreenTone.Warning);
                    AskPassword(launch);
                    return;
                }

                Open(launch, typed);
            }, () => ReferenceEquals(current, launch) && stage == Stage.Password && !launch.IsSettled);
            context.Ask(prompt);
        }

        private void Open(ILaunchedSession launch, string? password)
        {
            var settings = launch.Plan.Host!;
            MultiplayerRealmChange change;
            try
            {
                // A running game opened in place is named for browsing players, as HOST names it.
                var game = context.Environment.CurrentGame;
                if (game != null)
                {
                    settings.Gamemode ??= HostPresenter.Announced(game.Gamemode);
                    settings.World ??= HostPresenter.Announced(game.World);
                }

                context.Environment.DisplayName = launch.Plan.DisplayName;
                settings.Password = password;
                change = context.Environment.OpenToMultiplayer(settings);
            }
            catch (Exception exception)
            {
                var thrown = LiveFailureMessages.Thrown(exception);
                Refuse(launch, thrown.ErrorCode, thrown.ErrorMessage);
                return;
            }
            finally
            {
                // The session took its own copy of the password; this game keeps none.
                settings.Password = null;
            }

            if (change.Verdict == NetRealmTransitionVerdict.Busy)
            {
                stage = Stage.Waiting;
                return;
            }

            if (!change.Succeeded)
            {
                Refuse(launch, change.Result.ErrorCode, change.Result.ErrorMessage);
                return;
            }

            launch.Started(context.Status.Reads);
            stage = Stage.Following;
            context.Status.RequestRefresh();
        }

        private void Join(ILaunchedSession launch)
        {
            var target = launch.Plan.Target!;

            // A LAN session is found by the beacons it sends, which this game hears only while it listens on the local network.
            if (target.Kind == InternalJoinTargetKind.Lan && !Heard(target.ServerId))
            {
                StartLanSearch();
                searched = 0f;
                stage = Stage.FindingLan;
                context.Notify(LookingOnLan, ScreenTone.Neutral);
                return;
            }

            stage = Stage.Joining;
            connecting.Begin(JoinRequest.ForLaunch(launch, ConnectPresenter.UnnamedTitle(target.Kind)));
        }

        private void FindLan(ILaunchedSession launch, float deltaTime)
        {
            if (launch.IsSettled) return;
            searched += deltaTime > 0f && !float.IsNaN(deltaTime) ? deltaTime : 0f;
            if (Heard(launch.Plan.Target!.ServerId))
            {
                stage = Stage.Joining;
                connecting.Begin(JoinRequest.ForLaunch(launch, ConnectPresenter.UnnamedTitle(InternalJoinTargetKind.Lan)));
                return;
            }

            if (searched < LanSearchSeconds) return;
            StopLanSearch();
            launch.Fail("timeout", NotHeard);
            context.Notify(NotHeardHere, ScreenTone.Warning);
        }

        /// <summary>Tells the player once that a launched host opened, as HOST tells it of its own.</summary>
        private void TellOpened(ILaunchedSession launch)
        {
            if (toldOpened || !launch.IsHost || context.Status.Role != InternalSessionRole.ListenHost) return;
            toldOpened = true;
            context.Notify(Opened, ScreenTone.Success);
        }

        /// <summary>
        /// Tells the player when a launched host's hosting failed before it opened, in the words HOST uses for its own; once it
        /// opened, HOST follows the hosting and tells of a later failure itself.
        /// </summary>
        private void TellNotOpened(ILaunchedSession launch)
        {
            if (toldOpened || !launch.IsHost || !launch.IsFinished || context.Status.Status.HostPhase != InternalHostPhase.Failed) return;
            var failure = LiveFailureMessages.ForHost(context.Status.Status.HostFailure);
            context.Notify(failure.Title + ". " + failure.Message, ScreenTone.Danger);
        }

        /// <summary>
        /// Refuses <paramref name="launch"/> for a reason of this game's own, and tells the player too: the launcher reads the
        /// launch's records, but the player is looking at this game.
        /// </summary>
        private void Refuse(ILaunchedSession launch, ModErrorCode code, string message)
        {
            launch.Refuse(code, message);
            context.Notify(NotStarted + " " + HostText.ForDisplay(message, HostText.LineChars), ScreenTone.Warning);
        }

        /// <summary>Gets whether the local network lists <paramref name="serverId"/>, by index: enumerating the list would allocate.</summary>
        private bool Heard(string? serverId)
        {
            var control = context.Environment.Control;
            if (control == null || serverId == null) return false;
            IReadOnlyList<InternalLanListing> heard;
            try
            {
                heard = control.LanSessions;
            }
            catch (Exception)
            {
                return false;
            }

            for (var index = 0; index < heard.Count; index++)
            {
                if (string.Equals(heard[index].ServerId, serverId, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private void StartLanSearch()
        {
            if (browsingForLaunch || context.Status.Status.IsBrowsing) return;
            try
            {
                context.Environment.Control?.StartBrowsing();
                browsingForLaunch = true;
            }
            catch (Exception)
            {
                // Without the local network the session is never heard, and the search ends as not heard.
            }
        }

        private void StopLanSearch()
        {
            if (!browsingForLaunch) return;
            browsingForLaunch = false;
            try
            {
                context.Environment.Control?.StopBrowsing();
            }
            catch (Exception)
            {
                // Stopping is best effort; the provider stops browsing with its sessions anyway.
            }
        }

        private void Finish()
        {
            StopLanSearch();
            current = null;
            stage = Stage.None;
        }
    }
}
