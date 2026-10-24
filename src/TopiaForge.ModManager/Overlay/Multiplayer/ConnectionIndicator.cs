using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>A quiet HUD status; the reconnect banner appears only after the presenter's 1.5-second grace.</summary>
    internal sealed class ConnectionIndicator
    {
        private readonly IInSessionScreen screen;
        private readonly TopiaForgeLabel label;

        internal ConnectionIndicator(TopiaForgeContainer parent, IInSessionScreen screen)
        {
            this.screen = screen;
            label = parent.Label(string.Empty, TopiaForgeTextStyle.Caption).PlainText();
            label.FixedHeight(28f);
        }

        internal void Tick(bool gameplay)
        {
            label.SetShown(gameplay && screen.IsAvailable);
            label.SetText(screen.ConnectionText);
            label.SetTone(screen.IsReconnecting ? TopiaForgeTone.Warning : TopiaForgeTone.Muted);
        }
    }
}
