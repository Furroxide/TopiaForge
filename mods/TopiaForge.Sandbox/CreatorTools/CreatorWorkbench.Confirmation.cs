using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private sealed class ConfirmationOwner { }
        private ConfirmationOwner? confirmationOwner;

        private bool IsCurrentSession(int generation) =>
            !disposed && !endingSession && generation == sessionGeneration && IsSessionActive;

        private void ShowConfirmation(UiModalRequest request, Action<bool> completed)
        {
            if (disposed || endingSession || !IsSessionActive || confirmation?.IsOpen == true) return;
            var generation = sessionGeneration;
            var owner = new ConfirmationOwner();
            var previous = confirmation;
            confirmation = null;
            confirmationOwner = owner;
            previous?.Dispose();
            bool OwnsConfirmation() => IsCurrentSession(generation) && ReferenceEquals(confirmationOwner, owner);
            if (!OwnsConfirmation()) return;
            var shown = context.Ui.ShowModal(request, confirmed =>
            {
                if (!OwnsConfirmation()) return;
                // Invalidate before client code; queued duplicates must not act through a replacement modal.
                confirmationOwner = null;
                confirmation = null;
                completed(confirmed);
            });
            if (shown.TryGetValue(out var modal))
            {
                if (OwnsConfirmation()) confirmation = modal;
                else modal.Dispose(); // Also handles a synchronous completion before ShowModal returns.
            }
            else if (OwnsConfirmation())
            {
                confirmationOwner = null;
                status = shown.ErrorMessage;
                context.Ui.ShowToast(status, UiTone.Danger);
            }
        }

        private void DisposeConfirmation()
        {
            var owned = confirmation;
            confirmation = null;
            confirmationOwner = null;
            owned?.Dispose();
        }
    }
}
