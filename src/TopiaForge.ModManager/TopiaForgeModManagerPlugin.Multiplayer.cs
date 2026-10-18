using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        /// <summary>Gets the runtime's multiplayer composition root, for the in-game sessions interface; null before the runtime.</summary>
        internal MultiplayerRuntime? Multiplayer => runtime?.Multiplayer;

        /// <summary>Gets the multiplayer provider's session as mods see it, for the participant list; null without the provider.</summary>
        internal IMultiplayerSession? GetMultiplayerSession() => runtime?.GetService<IMultiplayerSession>();

        /// <summary>Gets the names players know the worlds by, by world id, so HOST and a hosted session name worlds as players do.</summary>
        internal IReadOnlyDictionary<string, string> GetWorldNames()
        {
            if (runtime == null) return HostTargets.WorldNames(null, null);
            var snapshot = runtime.CaptureSessionRuntime();
            return HostTargets.WorldNames(snapshot.Profile, snapshot.Observation);
        }
    }
}
