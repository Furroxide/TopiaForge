using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        /// <summary>Gets the runtime's multiplayer composition root, for the in-game sessions interface; null before the runtime.</summary>
        internal MultiplayerRuntime? Multiplayer => runtime?.Multiplayer;

        /// <summary>Gets the multiplayer provider's session as mods see it, for the participant list; null without the provider.</summary>
        internal IMultiplayerSession? GetMultiplayerSession() => runtime?.GetService<IMultiplayerSession>();
    }
}
