using Robotopia.Mods;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Entry point of the QuantumWorks UI kit.
    ///
    ///   var ui  = QwUi.For(context);                  // in a mod's OnLoad
    ///   var hud = ui.HudLayer("myhud");               // dark scheme, gameplay overlay
    ///   var bar = hud.Panel(QwPanelStyle.HudPanel)
    ///                .Dock(QwCorner.TopLeft).Size(380, 200)
    ///                .Column(QwGap.Sm, QwGap.Md)
    ///                .Label("HELLO", QwTextStyle.Heading);
    ///
    /// Dispose the host in OnUnload to tear everything down.
    /// </summary>
    public static class QwUi
    {
        /// <summary>Creates a host wired to a mod's id, data directory, and logger.</summary>
        public static UiHost For(IModContext context)
        {
            return Create(new QwUiOptions
            {
                OwnerId = context.ModId,
                DataDirectory = context.Paths.DataPath,
                LogInfo = context.Logger.Info,
                LogWarn = context.Logger.Warn,
                LogError = context.Logger.Error,
            });
        }

        /// <summary>Creates a host from explicit options (used by the manager overlay).</summary>
        public static UiHost Create(QwUiOptions options)
        {
            if (options.LogInfo != null && options.LogWarn != null && options.LogError != null)
            {
                QwLog.UseSinks(options.LogInfo, options.LogWarn, options.LogError);
            }

            return new UiHost(options);
        }
    }
}
