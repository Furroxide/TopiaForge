using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Entry point of the TopiaForge UI kit.
    ///
    ///   var ui  = TopiaForgeUi.For(context);                  // in a mod's OnLoad
    ///   var hud = ui.HudLayer("myhud");               // dark scheme, gameplay overlay
    ///   var bar = hud.Panel(TopiaForgePanelStyle.HudPanel)
    ///                .Dock(TopiaForgeCorner.TopLeft).Size(380, 200)
    ///                .Column(TopiaForgeGap.Sm, TopiaForgeGap.Md)
    ///                .Label("HELLO", TopiaForgeTextStyle.Heading);
    ///
    /// Dispose the host in OnUnload to tear everything down.
    /// </summary>
    public static class TopiaForgeUi
    {
        private static readonly List<UiHost> Hosts = new List<UiHost>();

        /// <summary>Creates a host wired to a mod's id, data directory, and logger.</summary>
        public static UiHost For(IModContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return Create(new TopiaForgeUiOptions
            {
                OwnerId = context.Identity.Id,
                // The safe SDK never exposes a filesystem path. Context.Ui supplies persistent window state;
                // direct kit hosts intentionally use the in-memory state store.
                DataDirectory = null,
                LogInfo = context.Logger.Info,
                LogWarn = context.Logger.Warn,
                LogError = context.Logger.Error,
            });
        }

        /// <summary>Creates a host from explicit options (used by the manager overlay).</summary>
        public static UiHost Create(TopiaForgeUiOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.OwnerId))
            {
                throw new ArgumentException("A stable UI owner id is required.", nameof(options));
            }

            if (options.LogInfo != null && options.LogWarn != null && options.LogError != null)
            {
                TopiaForgeLog.UseSinks(options.LogInfo, options.LogWarn, options.LogError);
            }

            var host = new UiHost(options);
            Hosts.Add(host);
            return host;
        }

        /// <summary>
        /// Tears down every process-wide TopiaForgeUi surface and runtime service. Call this on
        /// the Unity main thread when the loader shuts down. The operation is idempotent;
        /// hosts that owners forgot to dispose are reclaimed as a final safety net.
        /// </summary>
        public static void Shutdown()
        {
            TopiaForgeDebugOverlay.Dispose();
            TopiaForgeToasts.Reset();

            while (Hosts.Count > 0)
            {
                var index = Hosts.Count - 1;
                var host = Hosts[index];
                Hosts.RemoveAt(index);
                try
                {
                    host.Dispose();
                }
                catch (Exception exception)
                {
                    TopiaForgeLog.Error(exception, "TopiaForgeUi host teardown failed for '" + host.OwnerId + "'.");
                }
            }

            // These are normally emptied by UiHost.Dispose. Reset them explicitly so a
            // partially initialized or misbehaving consumer cannot survive loader teardown.
            TopiaForgeTween.Reset();
            TopiaForgeDismissStack.Reset();
            TopiaForgeHotkeys.Reset();
            TopiaForgeCursor.Reset();
            TopiaForgeSafeMode.Reset();
            TopiaForgeEventSystems.Reset();
            TopiaForgeRuntime.Shutdown();
            TopiaForgeLog.Reset();
        }

        internal static void OnHostDisposed(UiHost host)
        {
            Hosts.Remove(host);
        }
    }
}
