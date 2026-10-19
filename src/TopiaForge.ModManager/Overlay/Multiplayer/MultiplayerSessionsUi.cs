using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// What the multiplayer screens show outside the tab: their prompts as modals, one at a time, their action results as
    /// toasts, and the connecting overlay. It runs every frame, overlay open or not, so a join is followed while the player
    /// plays. Destructive confirmations use the kit's destructive modal; the privacy prompts use the approved wording,
    /// verbatim, with the safe choice as the primary button. A prompt the screens withdraw, because the game or session it
    /// asked about changed, has its modal closed (<see cref="PromptPresentation"/>).
    /// </summary>
    internal sealed class MultiplayerSessionsUi
    {
        private readonly UiHost host;
        private readonly IMultiplayerScreens screens;
        private readonly ConnectingOverlay connecting;
        private readonly PromptPresentation prompts;

        internal MultiplayerSessionsUi(UiHost host, IMultiplayerScreens screens)
        {
            this.host = host;
            this.screens = screens;
            connecting = new ConnectingOverlay(host, screens.Connecting);
            prompts = new PromptPresentation(Show);
        }

        internal void Tick()
        {
            prompts.Tick(screens);
            ShowNotices(screens);
            connecting.Tick();
        }

        internal void Close() => connecting.Close();

        /// <summary>
        /// Shows the screens' action results as toasts; it needs no host, so it runs before the overlay is built. A result can
        /// name what another player wrote, a server's name or a kick message, so toasts render as plain text.
        /// </summary>
        internal static void ShowNotices(IMultiplayerScreens screens)
        {
            while (screens.TryTakeNotice(out var notice))
            {
                TopiaForgeToasts.ShowPlain(notice.Text, MultiplayerViewKit.Tone(notice.Tone), notice.Tone == ScreenTone.Danger ? 5f : 3.5f);
            }
        }

        private IPromptDialog Show(ScreenPrompt prompt)
        {
            if (prompt.Kind == ScreenPromptKind.Destructive)
            {
                // A confirmation can name a player by the name they chose, so it renders as plain text. Prompts persist
                // across scene loads, as a session that ends meanwhile can load another: a prompt destroyed unanswered
                // would hold the queue for good, and one that no longer applies is closed through its dialog instead.
                return new PromptDialog(host.Modal.Destructive(
                    prompt.Title,
                    prompt.Paragraphs.Count == 0 ? string.Empty : prompt.Paragraphs[0],
                    prompt.ConfirmLabel,
                    () => prompt.Answer(true),
                    () => prompt.Answer(false),
                    prompt.CancelLabel.Length == 0 ? "CANCEL" : prompt.CancelLabel,
                    plainText: true,
                    persistent: true));
            }

            var modal = host.Modal.Custom(prompt.Title, TopiaForgeScheme.Paper, 560f, persistent: true);
            foreach (var paragraph in prompt.Paragraphs) modal.Content.Label(paragraph, TopiaForgeTextStyle.Body).PlainText();
            if (prompt.Kind == ScreenPromptKind.Password) return ShowPassword(modal, prompt);
            var row = modal.Content.Row(TopiaForgeGap.Sm);
            row.Spacer().Flex(1f, 0f);
            TopiaForgeButton primary;
            if (prompt.CancelLabel.Length == 0)
            {
                // The disclosure has one button; closing it any other way answers no.
                primary = row.Button(prompt.ConfirmLabel, () => Answer(modal, prompt, true));
            }
            else
            {
                // Allowing a direct connection is the choice that shows the address, so it is never the default.
                row.Button(prompt.ConfirmLabel, () => Answer(modal, prompt, true), TopiaForgeButtonStyle.Outline);
                primary = row.Button(prompt.CancelLabel, () => Answer(modal, prompt, false));
            }

            modal.Closed += () => prompt.Answer(false);
            modal.Show();
            primary.Focus();
            return new PromptDialog(modal);
        }

        /// <summary>
        /// A session password typed in game, in a masked field: the answer carries it once, and the field is emptied before the
        /// modal closes, so nothing on screen keeps it. Cancel stays the focused choice, as on the connecting overlay.
        /// </summary>
        private static IPromptDialog ShowPassword(TopiaForgeModalInstance modal, ScreenPrompt prompt)
        {
            var field = modal.Content.Input("Session password", string.Empty, _ => { }).Password();
            field.SetCharacterLimit(64);
            field.OnSubmit(_ => AnswerTyped(modal, prompt, field));
            var row = modal.Content.Row(TopiaForgeGap.Sm);
            row.Spacer().Flex(1f, 0f);
            row.Button(prompt.ConfirmLabel, () => AnswerTyped(modal, prompt, field), TopiaForgeButtonStyle.Outline);
            var cancel = row.Button(prompt.CancelLabel.Length == 0 ? "CANCEL" : prompt.CancelLabel, () => Answer(modal, prompt, false));
            modal.Closed += () => prompt.Answer(false);
            modal.Show();
            cancel.Focus();
            return new PromptDialog(modal);
        }

        private static void AnswerTyped(TopiaForgeModalInstance modal, ScreenPrompt prompt, TopiaForgeInputField field)
        {
            var typed = field.Text;
            field.SetText(string.Empty);
            prompt.Answer(true, typed);
            modal.Close();
        }

        private static void Answer(TopiaForgeModalInstance modal, ScreenPrompt prompt, bool confirmed)
        {
            prompt.Answer(confirmed);
            modal.Close();
        }

        /// <summary>A prompt's kit modal; closing one that already closed does nothing.</summary>
        private sealed class PromptDialog : IPromptDialog
        {
            private readonly TopiaForgeModalInstance modal;

            internal PromptDialog(TopiaForgeModalInstance modal)
            {
                this.modal = modal;
            }

            public void Close() => modal.Close();
        }
    }
}
