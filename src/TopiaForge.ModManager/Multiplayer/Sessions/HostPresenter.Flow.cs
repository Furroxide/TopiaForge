using System;
using System.Globalization;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>The HOST presenter's flow: the privacy and realm gates, starting a game, opening it, and ending the session.</summary>
    internal sealed partial class HostPresenter
    {
        public void Host()
        {
            if (!CanHost) return;

            // A LAN session stays on the local network. A Public or Invite session goes through the TopiaForge service's
            // relay, and takes direct connections too only when the player turned Hide my IP off, after the disclosure.
            var internet = VisibilityValues[visibility] != InternalSessionVisibility.Lan;

            // The disclosure asks about what hosting does now, opening this game or starting the chosen one; once that
            // changes, as when the running game ends, it is withdrawn and nothing starts.
            var decided = Decision();
            Func<bool> holds = () => stage == Stage.Idle && Decision() == decided;
            context.Privacy.EnsureDisclosure(() => Go(direct: internet && !context.Settings.Current.HideMyIp), Abandon, holds);
        }

        public void End()
        {
            if (!CanEnd) return;
            context.Ask(PrivacyPrompts.Destructive(EndTitle, EndBody, EndConfirm, confirmed =>
            {
                if (!confirmed) return;
                stage = Stage.Idle;
                SetProgress("The session ended for everyone.", ScreenTone.Neutral);
                context.LeaveOnline("The session ended for everyone.");
            }, context.IsHosting));
        }

        /// <summary>Main thread, every frame: follows a game starting, hosting opening, and a session that stops.</summary>
        internal void Tick()
        {
            var status = context.Status;
            switch (stage)
            {
                case Stage.WaitingForGame:
                    // The runtime's rules say when the started game has settled: busy while it starts, then open in place.
                    var decision = Decision();
                    if (decision == RealmDecision.PromoteInPlace)
                    {
                        Open(launching?.Gamemode, launching?.World);
                    }
                    else if (decision == RealmDecision.StartAsHost)
                    {
                        // The game started and stopped again before it could be opened: nothing is hosted.
                        launching = null;
                        stage = Stage.Idle;
                        SetProgress("The game stopped before it could be opened to other players.", ScreenTone.Warning);
                    }
                    else if (decision == RealmDecision.AlreadyThere)
                    {
                        launching = null;
                        stage = Stage.Hosting;
                    }
                    else if (decision != RealmDecision.Busy)
                    {
                        launching = null;
                        Failed(new FailureMessage("Hosting did not start", ModeText, ConnectingStep.Resolving, true));
                    }

                    break;
                case Stage.Opening:
                    if (status.Status.HostPhase == InternalHostPhase.Hosting && status.Role == InternalSessionRole.ListenHost)
                    {
                        stage = Stage.Hosting;
                        SetProgress("Hosting. Players can join now.", ScreenTone.Success);
                        context.Notify("Your session is open to other players.", ScreenTone.Success);
                    }
                    else if (status.Status.HostPhase == InternalHostPhase.Failed)
                    {
                        Failed(LiveFailureMessages.ForHost(status.Status.HostFailure));
                    }

                    break;
                case Stage.Hosting:
                    if (status.Role != InternalSessionRole.ListenHost)
                    {
                        stage = Stage.Idle;
                        listingVersion = -1;
                        if (status.Status.HostPhase == InternalHostPhase.Failed) Failed(LiveFailureMessages.ForHost(status.Status.HostFailure));
                        else SetProgress(string.Empty, ScreenTone.Neutral);
                    }
                    else if (status.Version != listingVersion)
                    {
                        listingVersion = status.Version;
                        ShowListing(status.Status.Internet);
                    }

                    break;
                default:
                    if (status.Role == InternalSessionRole.ListenHost && stage == Stage.Idle) stage = Stage.Hosting;
                    break;
            }
        }

        private void Go(bool direct)
        {
            allowDirect = direct;
            var settings = context.Settings;
            if (settings.IsLoaded)
            {
                settings.Update(settings.Current.WithHosting(name, maxPlayers, VisibilityValues[visibility], voice));
            }

            switch (Decision())
            {
                case RealmDecision.PromoteInPlace:
                    var current = context.Environment.CurrentGame;
                    Open(current?.Gamemode, current?.World);
                    break;
                case RealmDecision.StartAsHost:
                    if (selectedTarget >= targets.Count) return;
                    var choice = targets[selectedTarget];
                    launching = choice;
                    stage = Stage.Launching;
                    SetProgress("Starting " + choice.Label + "...", ScreenTone.Neutral);
                    context.Environment.Launch(choice, (started, message) =>
                    {
                        if (stage != Stage.Launching || !ReferenceEquals(launching, choice)) return;
                        if (started)
                        {
                            stage = Stage.WaitingForGame;
                            return;
                        }

                        launching = null;
                        Failed(new FailureMessage("The game did not start", HostText.ForDisplay(message, HostText.LineChars), ConnectingStep.Resolving, true));
                    });
                    break;
                default:
                    context.Notify(ModeText, ScreenTone.Warning);
                    break;
            }
        }

        private void Open(string? gamemode, string? world)
        {
            launching = null;
            OperationResult<bool> result;
            try
            {
                var settings = new InternalHostSettings(name, maxPlayers, VisibilityValues[visibility])
                {
                    Password = password.Length == 0 ? null : password,
                    AllowDirect = allowDirect,
                    Voice = voice,
                    Gamemode = Announced(gamemode),
                    World = Announced(world)
                };

                // The runtime checks the realm rules again before the provider hears of it.
                result = context.Environment.OpenToMultiplayer(settings).Result;
            }
            catch (Exception exception)
            {
                // Opening can run from the frame that waited for the game, so whatever the provider throws ends this
                // attempt here, once, rather than escaping that frame and being tried again on the next.
                result = LiveFailureMessages.Thrown(exception);
            }

            // The password went to the session and is never kept here.
            password = string.Empty;
            passwordError = string.Empty;
            if (!result.Succeeded)
            {
                Failed(new FailureMessage("Hosting did not start", HostText.ForDisplay(result.ErrorMessage, HostText.LineChars), ConnectingStep.Resolving, true));
                return;
            }

            stage = Stage.Opening;
            SetProgress("Opening this game to other players...", ScreenTone.Neutral);
            context.Status.RequestRefresh();
        }

        /// <summary>Says how a hosted session stands with the TopiaForge service; a LAN session has nothing to say.</summary>
        private void ShowListing(InternalInternetStatus internet)
        {
            switch (internet.ListingPhase)
            {
                case InternalListingPhase.Registering:
                    SetProgress(Registering, ScreenTone.Neutral);
                    break;
                case InternalListingPhase.Online when internet.ListingFailure == InternalLiveFailure.None:
                    SetProgress(internet.Listed ? Listed : Invited, ScreenTone.Success);
                    break;
                case InternalListingPhase.Online:
                case InternalListingPhase.Retrying:
                    SetProgress(internet.ListingFailure == InternalLiveFailure.None ? Retrying : LiveFailureMessages.ForHost(internet.ListingFailure).Message, ScreenTone.Warning);
                    break;
                case InternalListingPhase.Failed:
                    var failed = LiveFailureMessages.ForHost(internet.ListingFailure);
                    SetProgress(failed.Title + ": " + failed.Message, ScreenTone.Danger);
                    break;
            }
        }

        private void Failed(FailureMessage message)
        {
            stage = Stage.Idle;
            SetProgress(message.Title + ": " + message.Message, ScreenTone.Danger);
            context.Notify(message.Title + ". " + message.Message, ScreenTone.Danger);
        }

        private void Abandon()
        {
            SetProgress("Hosting did not start. Nothing was opened.", ScreenTone.Neutral);
        }

        private void SetProgress(string text, ScreenTone tone)
        {
            progressText = text;
            progressTone = tone;
        }

        /// <summary>Gets the player count line while hosting, such as "3 of 8 players".</summary>
        internal static string PlayersLine(int connected, int maximum) =>
            connected.ToString(CultureInfo.InvariantCulture) + " of " + maximum.ToString(CultureInfo.InvariantCulture) + " players";
    }
}
