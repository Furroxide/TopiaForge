using Robotopia.Mods;
using Robotopia.Mods.UnityUi;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// F8 toggles a QwUi window. QwUi handles theming (Paper/HUD schemes), accessibility toggles, and layout;
    /// see the UI Gallery mod (press F8 in game) for a live catalog of every widget.
    /// </summary>
    public sealed class {{TYPE_NAME}}Mod : IRobotopiaMod
    {
        private UiHost? ui;
        private QwWindow? window;

        public void OnLoad(IModContext context)
        {
            ui = QwUi.For(context);
            ui.Hotkey(QwKey.F8, () =>
            {
                window ??= BuildWindow(ui);
                if (window.IsOpen)
                {
                    window.Close();
                }
                else
                {
                    window.Show();
                }
            });
            context.Logger.Info("{{DISPLAY_NAME}} loaded - press F8 to open.");
        }

        public void OnUnload()
        {
            ui?.Dispose();
            ui = null;
            window = null;
        }

        private static QwWindow BuildWindow(UiHost host)
        {
            var window = host.Window(
                "{{MOD_ID}}.window",
                "{{DISPLAY_NAME}}",
                width: 420f,
                height: 320f);

            var column = window.Content;
            column.Label("Hello from {{DISPLAY_NAME}}.");
            column.Button("CLOSE", window.Close, QwButtonStyle.Outline);
            return window;
        }
    }
}
