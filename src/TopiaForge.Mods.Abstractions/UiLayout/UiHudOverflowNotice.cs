namespace TopiaForge.Mods
{
    internal static class UiHudOverflowNotice
    {
        internal const string CaptureKind = "HudOverflowQueued";
        private const int MaximumPackageName = 64;

        internal static string Text(string? packageDisplayName, UiHudAnchor anchor, UiHudVisibility visibility)
        {
            var name = UiPlainText.Sanitize(packageDisplayName, MaximumPackageName);
            var dock = UiHudViewport.Anchor(anchor, visibility) switch
            {
                UiHudAnchor.TopLeft => "top left",
                UiHudAnchor.Top => "top",
                UiHudAnchor.TopRight => "top right",
                UiHudAnchor.Left => "left",
                UiHudAnchor.Right => "right",
                UiHudAnchor.BottomLeft => "bottom left",
                UiHudAnchor.Bottom => "bottom",
                _ => "bottom right",
            };
            var location = "HUD at " + dock;
            return name.Length == 0 ? location : name + " · " + location;
        }
    }

    internal struct UiHudOverflowEpisode
    {
        private bool notified;

        internal bool TryQueue()
        {
            if (notified) return false;
            notified = true;
            return true;
        }

        internal void Recover() => notified = false;
    }
}
