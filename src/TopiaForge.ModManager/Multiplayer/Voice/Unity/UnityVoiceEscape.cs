using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// A push-to-talk capture on the kit's Escape stack (<see cref="TopiaForgeDismissStack"/>), in the modal band above the
    /// manager overlay's window: the Escape press that cancels a capture is the capture's alone, so it never closes the
    /// overlay too, whichever of the kit's driver and voice chat's frame reads the press first. A modal opened after the
    /// capture, as the consent, still comes first. Main thread only.
    /// </summary>
    internal sealed class UnityVoiceEscape : IVoiceEscape, ITopiaForgeDismissable
    {
        private Action? cancel;

        TopiaForgeLayerBand ITopiaForgeDismissable.Band => TopiaForgeLayerBand.Modal;

        public void Hold(Action onEscape)
        {
            cancel = onEscape ?? throw new ArgumentNullException(nameof(onEscape));
            TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Keyboard, null);
        }

        public void Release()
        {
            cancel = null;
            TopiaForgeDismissStack.Remove(this);
        }

        void ITopiaForgeDismissable.Dismiss()
        {
            var escaped = cancel;
            Release();
            try
            {
                escaped?.Invoke();
            }
            catch (Exception)
            {
                // The kit's driver runs this in Unity's frame, where the game's crash reporter takes what escapes; a cancel
                // only clears the capture's own fields, so there is nothing left to undo.
            }
        }
    }
}
