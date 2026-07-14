using System;
using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Sandbox.Ui
{
    /// <summary>
    /// The PROGRAM chat panel: a Paper window where the operator talks a robot into a program. The RobotChat
    /// coordinator owns the flow and every key (ESC/Tab/V/Return); this window only renders state and offers
    /// SEND/LEAVE clicks and deterministic quick-program controls (Zombies ConversationModal discipline). Shows the
    /// robot's reply with the 2 Hz thinking ellipsis, the current-program badge, the REC/VOICE/TYPE input badge, and
    /// hint variants by voice availability.
    /// </summary>
    internal sealed class RobotChatWindow : IDisposable
    {
        private readonly RobotChat chat;
        private readonly QwWindow window;
        private readonly QwLabel reply;
        private readonly QwBadge program;
        private readonly QwLabel status;
        private readonly QwLabel turn;
        private readonly QwBadge inputMode;
        private readonly QwInputField input;
        private readonly QwButton followMe;
        private readonly QwButton idle;
        private readonly QwButton setFree;
        private readonly QwButton send;
        private readonly QwLabel hint;
        private bool suppressClosedEvent;
        private bool renderStateValid;
        private bool renderedThinking;
        private int renderedThinkingDots = -1;
        private string renderedReply = string.Empty;
        private string renderedProgramDescription = string.Empty;
        private string renderedInteractionVerb = string.Empty;
        private bool renderedHasProgram;
        private string renderedStatus = string.Empty;
        private int renderedTurn = -1;
        private int renderedMaxTurns = -1;
        private bool renderedVoiceAvailable;
        private string renderedVoiceKey = string.Empty;
        private string thinkingOneDot = string.Empty;
        private string thinkingTwoDots = string.Empty;
        private string thinkingThreeDots = string.Empty;

        public RobotChatWindow(UiHost ui, RobotChat chat)
        {
            this.chat = chat;

            // Created while any scene may be active; persistent so the canvas survives Single-mode scene swaps
            // (same reasoning as the spawn menu).
            window = ui.Window("robotchat", "TALKING TO ROBOT", width: 560f, height: 0f, persistent: true);
            window.Closed += () =>
            {
                if (!suppressClosedEvent)
                {
                    chat.LeaveFromHud();
                }
            };

            var content = window.Content;
            var column = content.Column(QwGap.Sm, QwGap.Sm);

            reply = column.Label(QwTextStyle.Heading);

            var statusRow = column.Row(QwGap.Sm);
            program = statusRow.Badge("PROGRAM: NONE", QwTone.Neutral);
            status = statusRow.Label(QwTextStyle.Caption).Tone(QwTone.Muted);
            turn = statusRow.Label(QwTextStyle.Caption).Tone(QwTone.Warning).NoWrap();

            var quickRow = column.Row(QwGap.Sm);
            followMe = quickRow.Button("FOLLOW ME", chat.FollowMeFromHud, QwButtonStyle.Outline);
            idle = quickRow.Button("IDLE", chat.IdleFromHud, QwButtonStyle.Outline);
            setFree = quickRow.Button("SET FREE", chat.SetFreeFromHud, QwButtonStyle.Ghost);

            input = column.Input("Tell it what to do…", string.Empty, _ => { });
            input.OnSubmit(text => chat.SubmitFromHud(text));

            var actionsRow = column.Row(QwGap.Sm);
            inputMode = actionsRow.Badge("TYPE", QwTone.Accent);
            send = actionsRow.Button("SEND", () => chat.SubmitFromHud(input.Text), QwButtonStyle.Filled);
            actionsRow.Button("LEAVE", chat.LeaveFromHud, QwButtonStyle.Danger);

            hint = column.Label(QwTextStyle.Caption).Tone(QwTone.Muted);
        }

        public void Show(string robotName)
        {
            window.SetTitle(chat.InteractionVerb + " " + robotName.ToUpperInvariant());
            // Cache the three animation frames once per open. Tick then only swaps stable strings at 2 Hz.
            thinkingOneDot = robotName + " is thinking.";
            thinkingTwoDots = robotName + " is thinking..";
            thinkingThreeDots = robotName + " is thinking...";
            renderStateValid = false;
            renderedThinkingDots = -1;
            input.SetText(string.Empty);
            suppressClosedEvent = false;
            window.Show();
        }

        public void Hide()
        {
            // Closing programmatically must not loop back into the chat's leave path. Closed fires after the
            // close animation, so the flag stays set until the next Show() re-arms it.
            suppressClosedEvent = true;
            window.Close();
        }

        public void ClearInput()
        {
            input.SetText(string.Empty);
        }

        /// <summary>Per-frame render of the chat's state (the chat owns the flow; this only displays it).</summary>
        public void Tick()
        {
            RenderReply();
            RenderProgram();
            RenderStatusAndTurn();

            var voiceMode = chat.VoiceMode;
            inputMode.Set(
                voiceMode ? (chat.VoiceRecording ? "REC" : "VOICE") : "TYPE",
                chat.VoiceRecording ? QwTone.Danger : (voiceMode ? QwTone.Primary : QwTone.Accent));

            var canType = !voiceMode && !chat.Thinking && !chat.Closing;
            input.SetEnabled(canType);
            send.SetEnabled(canType);
            var quickControlsEnabled = chat.QuickControlsEnabled;
            followMe.SetEnabled(quickControlsEnabled);
            idle.SetEnabled(quickControlsEnabled);
            setFree.SetEnabled(quickControlsEnabled);

            RenderHint();
            renderStateValid = true;
        }

        public void Dispose()
        {
            suppressClosedEvent = true;
            window.Close();
        }

        private void RenderReply()
        {
            var thinking = chat.Thinking;
            if (thinking)
            {
                var dots = 1 + (Mathf.FloorToInt(Time.unscaledTime * 2f) % 3);
                if (!renderStateValid || !renderedThinking || dots != renderedThinkingDots)
                {
                    reply.SetText(dots == 1
                        ? thinkingOneDot
                        : dots == 2
                            ? thinkingTwoDots
                            : thinkingThreeDots);
                }

                renderedThinkingDots = dots;
            }
            else
            {
                var currentReply = chat.Reply;
                if (!renderStateValid || renderedThinking
                    || !string.Equals(renderedReply, currentReply, StringComparison.Ordinal))
                {
                    reply.SetText(string.IsNullOrEmpty(currentReply)
                        ? "Say what you want it to do — or just chat."
                        : "\"" + currentReply + "\"");
                }

                renderedReply = currentReply;
            }

            renderedThinking = thinking;
        }

        private void RenderProgram()
        {
            var description = chat.ProgramDescription;
            var verb = chat.InteractionVerb;
            var hasProgram = chat.HasProgram;
            if (!renderStateValid
                || !string.Equals(renderedProgramDescription, description, StringComparison.Ordinal)
                || !string.Equals(renderedInteractionVerb, verb, StringComparison.Ordinal)
                || renderedHasProgram != hasProgram)
            {
                program.Set(verb + ": " + description, hasProgram ? QwTone.Success : QwTone.Neutral);
                renderedProgramDescription = description;
                renderedInteractionVerb = verb;
                renderedHasProgram = hasProgram;
            }
        }

        private void RenderStatusAndTurn()
        {
            var currentStatus = chat.Status;
            if (!renderStateValid || !string.Equals(renderedStatus, currentStatus, StringComparison.Ordinal))
            {
                status.SetText(currentStatus.ToUpperInvariant());
                renderedStatus = currentStatus;
            }

            var maxTurns = chat.MaxTurns;
            var currentTurn = Mathf.Min(chat.Turn + 1, maxTurns);
            if (!renderStateValid || renderedTurn != currentTurn || renderedMaxTurns != maxTurns)
            {
                turn.SetText("TURN " + currentTurn + "/" + maxTurns);
                renderedTurn = currentTurn;
                renderedMaxTurns = maxTurns;
            }
        }

        private void RenderHint()
        {
            var voiceAvailable = chat.VoiceAvailable;
            var voiceKey = chat.VoiceKeyName;
            if (renderStateValid && renderedVoiceAvailable == voiceAvailable
                && string.Equals(renderedVoiceKey, voiceKey, StringComparison.Ordinal))
            {
                return;
            }

            hint.SetText(voiceAvailable
                ? "ENTER SEND  //  TAB TYPE/VOICE  //  HOLD " + voiceKey + " TALK  //  ESC LEAVE"
                : "ENTER SEND  //  ESC LEAVE");
            renderedVoiceAvailable = voiceAvailable;
            renderedVoiceKey = voiceKey;
        }
    }
}
