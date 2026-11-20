namespace TopiaForge.ModManager.Sessions
{
    internal sealed partial class ChatOverlay
    {
        internal bool OccupiesHud => open || log.HasVisibleLines;
    }
}
