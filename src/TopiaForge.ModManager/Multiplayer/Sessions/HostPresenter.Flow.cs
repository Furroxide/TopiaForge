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

            // In this build only the local network can find a session without the TopiaForge service; Public and Invite
            // sessions can only be reached directly, so they need the player's choice for this session.
            var needsDirect = VisibilityValues[visibility] != InternalSessionVisibility.Lan;
            context.Privacy.EnsureDisclosure(
                () =>
                {
                    if (needsDirect) context.Privacy.EnsureDirect(() => Go(direct: true), Abandon);
                    else Go(direct: false);
                },
                Abandon);
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
            }));
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
                        if (status.Status.HostPhase == InternalHostPhase.Failed) Failed(LiveFailureMessages.ForHost(status.Status.HostFailure));
                        else SetProgress(string.Empty, ScreenTone.Neutral);
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
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
            {
                result = OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
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
