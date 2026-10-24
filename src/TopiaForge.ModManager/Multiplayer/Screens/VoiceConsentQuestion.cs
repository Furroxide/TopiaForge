using System;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The first-use voice consent, asked before voice chat first turns on and again whenever its version rises, in the
    /// approved wording of docs/internal/MultiplayerPrivacyText.md, word for word from <see cref="MultiplayerPrivacyText"/>.
    /// It is answered once: yes only by its "Turn on voice chat" button, and no by "Not now", by closing its dialog any
    /// other way, or when the screens withdraw it. The view that shows it hands its dialog over, so the question closes the
    /// dialog however it is answered, and a view built again later never opens a second one. Main thread only.
    /// </summary>
    internal sealed class VoiceConsentQuestion
    {
        private readonly Action<bool> answer;
        private IPromptDialog? dialog;

        internal VoiceConsentQuestion(Action<bool> answer)
        {
            this.answer = answer ?? throw new ArgumentNullException(nameof(answer));
        }

        internal string Title => MultiplayerPrivacyText.VoiceTitle;

        internal string Body => MultiplayerPrivacyText.VoiceBody;

        /// <summary>Gets the button that agrees and turns voice chat on.</summary>
        internal string AcceptLabel => MultiplayerPrivacyText.VoiceAcceptButton;

        /// <summary>Gets the button that leaves voice chat off: the safe choice, which the view makes the default.</summary>
        internal string DeclineLabel => MultiplayerPrivacyText.VoiceDeclineButton;

        internal bool IsAnswered { get; private set; }

        /// <summary>Gets whether a dialog shows the question.</summary>
        internal bool IsShown => dialog != null;

        /// <summary>Takes the dialog a view opened for the question; one already answered closes it at once.</summary>
        internal void Shown(IPromptDialog shown)
        {
            if (shown == null) throw new ArgumentNullException(nameof(shown));
            if (IsAnswered)
            {
                shown.Close();
                return;
            }

            dialog = shown;
        }

        /// <summary>Answers the question; only the first answer counts, and the dialog closes.</summary>
        internal void Answer(bool accepted)
        {
            if (IsAnswered) return;
            IsAnswered = true;
            var shown = dialog;
            dialog = null;
            try
            {
                answer(accepted);
            }
            finally
            {
                shown?.Close();
            }
        }

        /// <summary>Answers no without the player, as when voice chat is no longer available.</summary>
        internal void Withdraw() => Answer(false);
    }
}
