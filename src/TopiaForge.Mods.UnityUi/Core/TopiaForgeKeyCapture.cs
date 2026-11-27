using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A key capture's place on the dismiss stack, Unity-free. It sits in the modal band, above the window or overlay that
    /// shows its field, so one Back press cancels the capture and nothing else. Pushed with the keyboard claim, it keeps the
    /// key the player captures, and every other, from the game until the capture ends.
    /// </summary>
    internal sealed class TopiaForgeKeyCapture : ITopiaForgeDismissable
    {
        private readonly Action cancel;

        /// <summary>Creates the entry; <paramref name="cancel"/> ends the capture without a key.</summary>
        public TopiaForgeKeyCapture(Action cancel)
        {
            this.cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
        }

        /// <summary>Gets the modal band, above every window and the manager overlay.</summary>
        public TopiaForgeLayerBand Band => TopiaForgeLayerBand.Modal;

        /// <summary>Cancels the capture: Back ends it, never the surface around it.</summary>
        public void Dismiss()
        {
            cancel();
        }
    }
}
