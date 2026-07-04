using System;
using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Sandbox.Ui
{
    /// <summary>
    /// The PROGRAM chat panel: a Paper window where the operator talks a robot into a program. The RobotChat
    /// coordinator owns the flow and every key (ESC/Tab/V/Return); this window only renders state and offers
    /// SEND/LEAVE clicks (Zombies ConversationModal discipline). Shows the robot's reply with the 2 Hz thinking
    /// ellipsis, the current-program badge, the REC/VOICE/TYPE input badge, and hint variants by voice availability.
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
        private readonly QwButton send;
        private readonly QwLabel hint;
        private bool suppressClosedEvent;

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
            window.SetTitle("TALKING TO " + robotName.ToUpperInvariant());
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
            reply.SetText(chat.Thinking
                ? chat.RobotName + " is thinking" + Ellipsis()
                : (string.IsNullOrEmpty(chat.Reply)
                    ? "Say what you want it to do — or just chat."
                    : "\"" + chat.Reply + "\""));

            program.Set("PROGRAM: " + chat.ProgramDescription, chat.HasProgram ? QwTone.Success : QwTone.Neutral);
            status.SetText(chat.Status.ToUpperInvariant());
            turn.SetText("TURN " + Mathf.Min(chat.Turn + 1, chat.MaxTurns) + "/" + chat.MaxTurns);

            var voiceMode = chat.VoiceMode;
            inputMode.Set(
                voiceMode ? (chat.VoiceRecording ? "REC" : "VOICE") : "TYPE",
                chat.VoiceRecording ? QwTone.Danger : (voiceMode ? QwTone.Primary : QwTone.Accent));

            var canType = !voiceMode && !chat.Thinking && !chat.Closing;
            input.SetEnabled(canType);
            send.SetEnabled(canType);

            hint.SetText(chat.VoiceAvailable
                ? "ENTER SEND  //  TAB TYPE/VOICE  //  HOLD " + chat.VoiceKeyName + " TALK  //  ESC LEAVE"
                : "ENTER SEND  //  ESC LEAVE");
        }

        public void Dispose()
        {
            suppressClosedEvent = true;
            window.Close();
        }

        private static string Ellipsis()
        {
            var dots = 1 + (Mathf.FloorToInt(Time.unscaledTime * 2f) % 3);
            return new string('.', dots);
        }
    }
}
