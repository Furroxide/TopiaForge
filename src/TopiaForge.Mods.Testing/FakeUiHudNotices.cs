namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        internal string? HudPackageDisplayName { get; }

        internal void RefreshHudLayoutNotices()
        {
            if (lifetime.IsStopping) return;
            foreach (var surface in surfaces) surface.RefreshHudLayoutNotice();
        }

        internal void RecordHudOverflow(string message) => toasts.Add(new FakeToast(message, UiTone.Neutral));
    }

    public sealed partial class FakeUiSurface
    {
        private UiHudOverflowEpisode hudOverflowEpisode;

        internal string? HudOverflowText { get; }

        internal void RefreshHudLayoutNotice()
        {
            if (HudOverflowText == null || !IsVisible) return;
            if (FakeUiTreeCapture.FitsHudContent(owner, this, out var noticeFits))
                hudOverflowEpisode.Recover();
            else if (!noticeFits && hudOverflowEpisode.TryQueue())
                owner.RecordHudOverflow(HudOverflowText);
        }
    }
}
