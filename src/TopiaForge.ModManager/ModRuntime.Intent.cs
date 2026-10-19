using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private MultiplayerIntentKitHook? intentKitHook;

        /// <summary>
        /// Runs a command as the local player runs it, as the chat overlay will (WS5): its handler acts on the player's intent,
        /// as an owner command of the command's own mod. The name is qualified, <c>mod:command</c>.
        /// </summary>
        internal bool TryExecutePlayerCommand(string qualifiedName, IReadOnlyList<string> arguments, out OperationResult<string>? result)
        {
            UnityMainThreadGuard.AssertCurrent();
            return serviceRegistry.TryExecutePlayerCommand(qualifiedName, arguments, multiplayer.Intents, out result);
        }

        /// <summary>Connects the TopiaForgeUi kit's controls to this runtime's intent tracking (WS3.4); the latest runtime wins.</summary>
        private void InstallIntentKitHook()
        {
            intentKitHook = new MultiplayerIntentKitHook(multiplayer.Intents);
            TopiaForgeIntent.Install(intentKitHook);
        }

        /// <summary>Disconnects the kit from this runtime's intent tracking, unless another runtime replaced it.</summary>
        private void UninstallIntentKitHook()
        {
            var hook = intentKitHook;
            if (hook == null) return;
            intentKitHook = null;
            try
            {
                TopiaForgeIntent.Uninstall(hook);
            }
            catch (Exception exception)
            {
                try { logger.Error(exception, "Disconnecting the UI kit from intent tracking failed."); }
                catch { /* Shutdown continues even when the diagnostic sink has stopped. */ }
            }
        }
    }
}
