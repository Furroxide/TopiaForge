using System;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>A prompt's dialog as the view showed it: closing it is all the presentation asks of it.</summary>
    internal interface IPromptDialog
    {
        void Close();
    }

    /// <summary>
    /// Shows the screens' prompts one at a time. The view's dialog answers the prompt it shows; a prompt answered any other
    /// way, as when the screens withdraw it because its game ended, has its dialog closed here, so no question stays on
    /// screen over a game that no longer asks it. A dialog that cannot be shown cannot be answered, so its prompt counts as
    /// no and the next one can show. Main thread only; a frame with nothing to change allocates nothing.
    /// </summary>
    internal sealed class PromptPresentation
    {
        private readonly Func<ScreenPrompt, IPromptDialog> show;
        private ScreenPrompt? showing;
        private IPromptDialog? dialog;

        /// <param name="show">Opens a dialog for a prompt; the dialog answers the prompt when the player does.</param>
        internal PromptPresentation(Func<ScreenPrompt, IPromptDialog> show)
        {
            this.show = show ?? throw new ArgumentNullException(nameof(show));
        }

        /// <summary>Gets the prompt on screen, or null.</summary>
        internal ScreenPrompt? Showing => showing;

        /// <summary>Every frame: closes the dialog of a prompt answered elsewhere, then shows the next prompt, if any.</summary>
        internal void Tick(IMultiplayerScreens screens)
        {
            if (screens == null) throw new ArgumentNullException(nameof(screens));
            if (showing != null)
            {
                if (!showing.IsAnswered) return;
                var shown = dialog;
                showing = null;
                dialog = null;
                shown?.Close();
            }

            var next = screens.PendingPrompt;
            if (next == null) return;
            showing = next;
            try
            {
                dialog = show(next);
            }
            catch (Exception)
            {
                showing = null;
                next.Answer(false);
                throw;
            }
        }
    }
}
