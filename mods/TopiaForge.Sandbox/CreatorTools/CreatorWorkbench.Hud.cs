using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private bool hudStateInitialized;
        private bool hudSessionActive;
        private bool hudWorkbenchVisible;
        private bool hudMutationIsolated;
        private int hudRosterCount = -1;
        private string hudSafetyMessage = string.Empty;

        private void OnWindowDismissed()
        {
            requestHide();
            RefreshHud(force: true);
        }

        private void ReleaseControl()
        {
            controlLease?.Dispose();
            controlLease = null;
        }

        private void EnsureHud()
        {
            if (!options.ShowHud || hud != null) return;
            var created = context.Ui.CreateSurface(new UiSurfaceRequest(
                options.SurfaceId + "-hud",
                "CREATOR SESSION",
                string.Empty,
                UiSurfaceKind.Hud,
                390f,
                130f));
            if (created.TryGetValue(out hud)) hud.Show();
        }

        private void RefreshHud(bool force)
        {
            if (hud == null) return;
            var sessionActive = IsSessionActive;
            var workbenchVisible = IsVisible;
            var rosterCount = roster.Count;
            var mutationIsolated = CanMutate;
            var safetyMessage = options.ProjectScope == CreatorProjectScope.Global && !mutationIsolated
                ? mutationSafety?.Status.Message ?? string.Empty
                : string.Empty;
            if (!force && hudStateInitialized
                && hudSessionActive == sessionActive
                && hudWorkbenchVisible == workbenchVisible
                && hudRosterCount == rosterCount
                && hudMutationIsolated == mutationIsolated
                && string.Equals(hudSafetyMessage, safetyMessage, StringComparison.Ordinal))
            {
                return;
            }
            hudStateInitialized = true;
            hudSessionActive = sessionActive;
            hudWorkbenchVisible = workbenchVisible;
            hudRosterCount = rosterCount;
            hudMutationIsolated = mutationIsolated;
            hudSafetyMessage = safetyMessage;
            var mutationText = options.ProjectScope == CreatorProjectScope.Sandbox
                ? "Sandbox isolation active."
                : mutationIsolated
                    ? "GLOBAL MUTATIONS ISOLATED  •  temporary changes acknowledged"
                    : string.IsNullOrWhiteSpace(safetyMessage)
                        ? "GLOBAL MUTATIONS LOCKED  •  persistence isolation unavailable"
                        : safetyMessage;
            var next = sessionActive
                ? "SESSION ACTIVE  •  " + rosterCount + " TARGETS\n"
                    + (workbenchVisible ? "F5 HIDE WORKBENCH" : "F5 REOPEN WORKBENCH")
                    + "\nUse END SESSION & RESTORE to remove owned content and restore edits."
                    + (options.ProjectScope == CreatorProjectScope.Global
                        ? "\n" + mutationText
                        : string.Empty)
                : "NO ACTIVE CREATOR SESSION\nPress F5 to open the workbench.";
            if (force || !string.Equals(next, hudText, StringComparison.Ordinal))
            {
                hudText = next;
                hud.SetBody(next);
            }
        }
    }
}
