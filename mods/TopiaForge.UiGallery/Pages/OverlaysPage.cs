using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>Modals, toasts, windows: the ESC stack and persistence demos.</summary>
    internal static class OverlaysPage
    {
        private static TopiaForgeWindow? demoWindow;

        public static void Build(TopiaForgeContainer page)
        {
            var host = page.Host;

            page.SectionHeader("MODALS");
            var modalRow = page.Row(TopiaForgeGap.Sm);
            modalRow.Button("CONFIRM", () => host.Modal.Confirm(
                "APPLY CHANGES",
                "The staged package changes will apply on the next restart.",
                "APPLY",
                () => host.Toast("Changes staged.", TopiaForgeTone.Success)), TopiaForgeButtonStyle.Outline);
            modalRow.Button("DESTRUCTIVE", () => host.Modal.Destructive(
                "REMOVE MOD",
                "TopiaForge Zombies 0.9.0 will be uninstalled on the next restart.",
                "REMOVE",
                () => host.Toast("Removal staged.", TopiaForgeTone.Warning)), TopiaForgeButtonStyle.Danger);
            page.Label("ESC closes the top-most surface only: open a modal over this window and press ESC twice.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            page.SectionHeader("TOASTS");
            var toastRow = page.Row(TopiaForgeGap.Sm);
            toastRow.Button("INFO", () => host.Toast("Package list refreshed."), TopiaForgeButtonStyle.Outline);
            toastRow.Button("SUCCESS", () => host.Toast("Mod enabled.", TopiaForgeTone.Success), TopiaForgeButtonStyle.Outline);
            toastRow.Button("ERROR", () => host.Toast("Install failed: manifest invalid.", TopiaForgeTone.Danger), TopiaForgeButtonStyle.Outline);
            toastRow.Button("SPAM 6", () =>
            {
                for (var index = 1; index <= 6; index++)
                {
                    host.Toast("Queued toast " + index + " of 6");
                }
            }, TopiaForgeButtonStyle.Ghost);

            page.SectionHeader("WINDOWS");
            page.Button("OPEN DEMO WINDOW", () =>
            {
                demoWindow ??= BuildDemoWindow(host);
                demoWindow.Show();
            }, TopiaForgeButtonStyle.Outline);
            page.Label("Drag it by the title bar — position snaps to edges, clamps on-screen, and persists across restarts.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
        }

        private static TopiaForgeWindow BuildDemoWindow(UiHost host)
        {
            var window = host.Window("gallery-demo", "DRAG ME", width: 340f);
            window.Content.Label("This window remembers where you left it (data-dir state store, not the registry).", TopiaForgeTextStyle.Body);
            window.Content.Button("CLOSE", window.Close, TopiaForgeButtonStyle.Ghost);
            return window;
        }

        public static void Reset()
        {
            demoWindow?.Destroy();
            demoWindow = null;
        }
    }
}
