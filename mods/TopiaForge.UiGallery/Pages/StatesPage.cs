using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>System, focus, overflow, modal, and notification QA states.</summary>
    internal static class StatesPage
    {
        public static void Build(TopiaForgeContainer page)
        {
            var host = page.Host;

            page.SectionHeader("ASYNC + EMPTY STATES");
            State(page, "LOADING", "Discovering installed packages…", TopiaForgeTone.Accent);
            var progress = page.ProgressBar();
            progress.SetFraction(0.35f);
            State(page, "EMPTY", "No compatible mods were found in this source.", TopiaForgeTone.Muted);

            page.SectionHeader("MESSAGES");
            State(page, "INFORMATION", "Package metadata was refreshed from the selected source.", TopiaForgeTone.Accent);
            State(page, "WARNING", "Restart required before staged changes take effect.", TopiaForgeTone.Warning);
            State(page, "ERROR", "Package validation failed; open Diagnostics for details.", TopiaForgeTone.Danger);
            State(page, "SUCCESS", "All package integrity checks passed.", TopiaForgeTone.Success);

            page.SectionHeader("DISABLED + KEYBOARD FOCUS");
            var actions = page.Row(TopiaForgeGap.Sm);
            var focusTarget = actions.Button("FOCUS TARGET", () => host.Toast("Focused action activated."));
            var disabled = actions.Button("UNAVAILABLE", () => { }, TopiaForgeButtonStyle.Outline);
            disabled.SetEnabled(false);
            actions.Button("MOVE FOCUS", focusTarget.Focus, TopiaForgeButtonStyle.Ghost);
            page.Label(
                "Use Tab/Shift+Tab to traverse controls, Space/Enter to activate them, and Escape to dismiss only the top surface.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            page.SectionHeader("LONG + SCROLLING CONTENT");
            page.Label(
                "This deliberately long status copy verifies wrapping and the gallery's scroll container at narrow resolutions and large text settings. "
                + "Package sources, hashes, compatibility findings, dependency capabilities, recovery instructions, and arbitrary-code warnings must remain readable without clipping or overflowing the window.",
                TopiaForgeTextStyle.Body);

            page.SectionHeader("DESTRUCTIVE + TOAST STATES");
            var feedback = page.Row(TopiaForgeGap.Sm);
            feedback.Button("DESTRUCTIVE MODAL", () => host.Modal.Destructive(
                "DELETE PROFILE",
                "This demonstration requires an explicit confirmation and does not delete data.",
                "DELETE",
                () => host.Toast("Demonstration confirmed.", TopiaForgeTone.Warning)), TopiaForgeButtonStyle.Danger);
            feedback.Button("INFO TOAST", () => host.Toast("Information state."), TopiaForgeButtonStyle.Outline);
            feedback.Button("SUCCESS TOAST", () => host.Toast("Success state.", TopiaForgeTone.Success), TopiaForgeButtonStyle.Outline);
            feedback.Button("ERROR TOAST", () => host.Toast("Error state.", TopiaForgeTone.Danger), TopiaForgeButtonStyle.Outline);
        }

        private static void State(TopiaForgeContainer page, string title, string body, TopiaForgeTone tone)
        {
            var row = page.ListRow();
            row.Title.SetText(title);
            row.Title.SetTone(tone);
            row.Subtitle.SetText(body);
            row.Badge.Set(title, tone);
        }
    }
}
