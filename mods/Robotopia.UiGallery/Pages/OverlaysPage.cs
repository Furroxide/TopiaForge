using Robotopia.Mods.UnityUi;

namespace Robotopia.UiGallery.Pages
{
    /// <summary>Modals, toasts, windows: the ESC stack and persistence demos.</summary>
    internal static class OverlaysPage
    {
        private static QwWindow? demoWindow;

        public static void Build(QwContainer page)
        {
            var host = page.Host;

            page.SectionHeader("MODALS");
            var modalRow = page.Row(QwGap.Sm);
            modalRow.Button("CONFIRM", () => host.Modal.Confirm(
                "APPLY CHANGES",
                "The staged package changes will apply on the next restart.",
                "APPLY",
                () => host.Toast("Changes staged.", QwTone.Success)), QwButtonStyle.Outline);
            modalRow.Button("DESTRUCTIVE", () => host.Modal.Destructive(
                "REMOVE MOD",
                "Robotopia Zombies 0.9.0 will be uninstalled on the next restart.",
                "REMOVE",
                () => host.Toast("Removal staged.", QwTone.Warning)), QwButtonStyle.Danger);
            page.Label("ESC closes the top-most surface only: open a modal over this window and press ESC twice.", QwTextStyle.Caption).Tone(QwTone.Muted);

            page.SectionHeader("TOASTS");
            var toastRow = page.Row(QwGap.Sm);
            toastRow.Button("INFO", () => host.Toast("Package list refreshed."), QwButtonStyle.Outline);
            toastRow.Button("SUCCESS", () => host.Toast("Mod enabled.", QwTone.Success), QwButtonStyle.Outline);
            toastRow.Button("ERROR", () => host.Toast("Install failed: manifest invalid.", QwTone.Danger), QwButtonStyle.Outline);
            toastRow.Button("SPAM 6", () =>
            {
                for (var index = 1; index <= 6; index++)
                {
                    host.Toast("Queued toast " + index + " of 6");
                }
            }, QwButtonStyle.Ghost);

            page.SectionHeader("WINDOWS");
            page.Button("OPEN DEMO WINDOW", () =>
            {
                demoWindow ??= BuildDemoWindow(host);
                demoWindow.Show();
            }, QwButtonStyle.Outline);
            page.Label("Drag it by the title bar — position snaps to edges, clamps on-screen, and persists across restarts.", QwTextStyle.Caption).Tone(QwTone.Muted);
        }

        private static QwWindow BuildDemoWindow(UiHost host)
        {
            var window = host.Window("gallery-demo", "DRAG ME", width: 340f);
            window.Content.Label("This window remembers where you left it (data-dir state store, not the registry).", QwTextStyle.Body);
            window.Content.Button("CLOSE", window.Close, QwButtonStyle.Ghost);
            return window;
        }
    }
}
