namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        internal string? HudPackageDisplayName { get; }

        internal void RefreshHudLayoutNotices()
        {
            if (!lifetime.IsStopping) FakeUiTreeCapture.RefreshHudNotices(this);
        }

        internal void RecordHudOverflow(string message) => toasts.Add(new FakeToast(message, UiTone.Neutral));
    }

    public sealed partial class FakeUiSurface
    {
        private UiHudOverflowEpisode hudOverflowEpisode;

        internal string? HudOverflowText { get; }

        internal void ApplyHudNoticeFit(bool unfit, bool noticeFits)
        {
            if (!unfit) hudOverflowEpisode.Recover();
            else if (!noticeFits && hudOverflowEpisode.TryQueue())
                owner.RecordHudOverflow(HudOverflowText!);
        }
    }
}
