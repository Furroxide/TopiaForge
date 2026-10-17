using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// What the multiplayer screens show outside the tab: their prompts as modals, one at a time, their action results as
    /// toasts, and the connecting overlay. It runs every frame, overlay open or not, so a join is followed while the player
    /// plays. Destructive confirmations use the kit's destructive modal; the privacy prompts use the approved wording,
    /// verbatim, with the safe choice as the primary button.
    /// </summary>
    internal sealed class MultiplayerSessionsUi
    {
        private readonly UiHost host;
        private readonly IMultiplayerScreens screens;
        private readonly ConnectingOverlay connecting;
        private ScreenPrompt? showing;

        internal MultiplayerSessionsUi(UiHost host, IMultiplayerScreens screens)
        {
            this.host = host;
            this.screens = screens;
            connecting = new ConnectingOverlay(host, screens.Connecting);
        }

        internal void Tick()
        {
            if (showing != null && showing.IsAnswered) showing = null;
            if (showing == null)
            {
                var next = screens.PendingPrompt;
                if (next != null)
                {
                    showing = next;
                    Show(next);
                }
            }

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

        private void Show(ScreenPrompt prompt)
        {
            if (prompt.Kind == ScreenPromptKind.Destructive)
            {
                // A confirmation can name a player by the name they chose, so it renders as plain text. Prompts persist
                // across scene loads, as a session that ends meanwhile can load another: a prompt destroyed unanswered
                // would hold the queue for good.
                host.Modal.Destructive(
                    prompt.Title,
                    prompt.Paragraphs.Count == 0 ? string.Empty : prompt.Paragraphs[0],
                    prompt.ConfirmLabel,
                    () => prompt.Answer(true),
                    () => prompt.Answer(false),
                    prompt.CancelLabel.Length == 0 ? "CANCEL" : prompt.CancelLabel,
                    plainText: true,
                    persistent: true);
                return;
            }

            var modal = host.Modal.Custom(prompt.Title, TopiaForgeScheme.Paper, 560f, persistent: true);
            foreach (var paragraph in prompt.Paragraphs) modal.Content.Label(paragraph, TopiaForgeTextStyle.Body).PlainText();
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
        }

        private static void Answer(TopiaForgeModalInstance modal, ScreenPrompt prompt, bool confirmed)
        {
            prompt.Answer(confirmed);
            modal.Close();
        }
    }
}
