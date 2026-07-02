using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Zombies
{
    /// <summary>
    /// The JACK-IN chat panel. The ZombiesController owns the flow and every key
    /// (ESC/Tab/V/Return); this modal only renders state and offers SEND/LEAVE clicks.
    /// Ported verbatim: the channel timer turning danger under 25%, the persuasion bar
    /// (success at/above the convert threshold, warning below) with its
    /// "PERSUASION n% // CONVERT m%" label, the echo sync that never fights a focused
    /// input, the REC/VOICE/TYPE badge, the hint variants by voice availability, and
    /// the 2 Hz unscaled thinking ellipsis.
    /// </summary>
    internal sealed class ConversationModal
    {
        private readonly HudContext context;
        private readonly QwContainer root;
        private readonly QwLabel title;
        private readonly QwProgressBar timer;
        private readonly QwLabel reply;
        private readonly QwLabel status;
        private readonly QwLabel turn;
        private readonly QwStatBar persuasion;
        private readonly QwBadge inputMode;
        private readonly QwInputField input;
        private readonly QwButton send;
        private readonly QwLabel hint;

        public ConversationModal(HudContext context, QwContainer parent)
        {
            this.context = context;
            root = parent.Stack("Conversation");

            var backdrop = root.FreeImage("Backdrop").Stretch();
            backdrop.SetColor(context.Theme.Backdrop);
            backdrop.Image.raycastTarget = true;

            title = root.Label(QwTextStyle.Title).Tone(QwTone.Accent).AlignCenter().NoWrap();
            var titleRect = title.Rect;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -34f);
            titleRect.sizeDelta = new Vector2(0f, 34f);

            timer = root.ProgressBar();
            var timerRect = timer.Rect;
            timerRect.anchorMin = new Vector2(0.5f, 1f);
            timerRect.anchorMax = new Vector2(0.5f, 1f);
            timerRect.pivot = new Vector2(0.5f, 1f);
            timerRect.anchoredPosition = new Vector2(0f, -78f);
            timerRect.sizeDelta = new Vector2(420f, 8f);

            var dialog = root.Panel(QwPanelStyle.HudPanel);
            var dialogRect = dialog.Rect;
            dialogRect.anchorMin = new Vector2(0.5f, 0f);
            dialogRect.anchorMax = new Vector2(0.5f, 0f);
            dialogRect.pivot = new Vector2(0.5f, 0f);
            dialogRect.anchoredPosition = new Vector2(0f, 42f);
            dialogRect.sizeDelta = new Vector2(820f, 270f);

            reply = dialog.Label(QwTextStyle.Heading).AlignTopLeft();
            HudContext.Place(reply, 24f, 20f, 772f, 66f);

            status = dialog.Label(QwTextStyle.Label).Tone(QwTone.Muted).NoWrap();
            HudContext.Place(status, 24f, 92f, 520f, 22f);

            turn = dialog.Label(QwTextStyle.Caption).Tone(QwTone.Warning).AlignRight();
            HudContext.Place(turn, 612f, 92f, 184f, 22f);

            persuasion = dialog.StatBar("PERSUASION");
            HudContext.Place(persuasion, 24f, 124f, 772f, 18f);

            inputMode = dialog.Badge("TYPE", QwTone.Accent);
            HudContext.Place(inputMode, 24f, 162f, 108f, 34f);

            input = dialog.Input("Say something that changes its mind", string.Empty, _ => { });
            HudContext.Place(input, 142f, 162f, 456f, 34f);

            send = dialog.Button("SEND", Submit, QwButtonStyle.Filled);
            HudContext.Place(send, 610f, 162f, 86f, 34f);

            var leave = dialog.Button("LEAVE", () => this.context.Controller.LeaveConversationFromHud(), QwButtonStyle.Danger);
            HudContext.Place(leave, 710f, 162f, 86f, 34f);

            hint = dialog.Label(QwTextStyle.Caption).Tone(QwTone.Muted);
            HudContext.Place(hint, 24f, 218f, 772f, 24f);

            root.SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            root.SetVisible(visible);
        }

        public void Tick()
        {
            var controller = context.Controller;

            title.SetText("CHANNEL OPEN // " + controller.ConversationTargetName.ToUpperInvariant());

            var windowFraction = Mathf.Clamp01(controller.ConversationWindowFraction);
            timer.SetFraction(windowFraction);
            timer.SetTone(windowFraction < 0.25f ? QwTone.Danger : QwTone.Accent);

            reply.SetText(controller.ConversationThinking
                ? controller.ConversationTargetName + " is thinking" + Ellipsis()
                : (string.IsNullOrEmpty(controller.ConversationReply)
                    ? "Open channel. Make a case."
                    : "\"" + controller.ConversationReply + "\""));
            status.SetText(controller.ConversationStatus.ToUpperInvariant());
            turn.SetText("TURN " + Mathf.Min(controller.ConversationTurn + 1, controller.ConversationMaxTurns) + "/" + controller.ConversationMaxTurns);

            var disposition = Mathf.Clamp01(controller.ConversationDisposition);
            var threshold = Mathf.Clamp01(controller.ConversationConvertThreshold);
            persuasion.SetFraction(disposition);
            persuasion.SetTone(disposition >= threshold ? QwTone.Success : QwTone.Warning);
            persuasion.SetLabel("PERSUASION  " + Mathf.RoundToInt(disposition * 100f) + "%  //  CONVERT " + Mathf.RoundToInt(threshold * 100f) + "%");

            var voiceMode = controller.ConversationVoiceMode;
            inputMode.Set(
                voiceMode ? (controller.ConversationVoiceRecording ? "REC" : "VOICE") : "TYPE",
                controller.ConversationVoiceRecording ? QwTone.Danger : (voiceMode ? QwTone.Primary : QwTone.Accent));

            input.SetEnabled(!voiceMode && !controller.ConversationThinking);
            input.SyncText(controller.ConversationPlayerEcho);
            send.SetEnabled(!controller.ConversationThinking && !voiceMode);

            hint.SetText(controller.ConversationVoiceAvailable
                ? "ENTER SEND  //  TAB TYPE/VOICE  //  ESC LEAVE"
                : "ENTER SEND  //  ESC LEAVE");
        }

        private void Submit()
        {
            context.Controller.SubmitConversationTextFromHud(input.Text);
            input.SetText(string.Empty);
        }

        private static string Ellipsis()
        {
            var dots = 1 + (Mathf.FloorToInt(Time.unscaledTime * 2f) % 3);
            return new string('.', dots);
        }
    }
}
