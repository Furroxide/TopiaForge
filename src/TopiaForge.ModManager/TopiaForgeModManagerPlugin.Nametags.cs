namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        internal IPresenceNametagSource? Nametags => runtime?.Presence;
    }
}
