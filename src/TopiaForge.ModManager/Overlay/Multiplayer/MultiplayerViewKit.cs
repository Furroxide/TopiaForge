using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>One sub-tab's kit view: built once into its container, then bound every frame through dirty-checked setters.</summary>
    internal interface IMultiplayerSubView
    {
        void Tick();
    }

    /// <summary>Helpers the multiplayer views share. Kit only: no Unity type appears here, so the UI Gallery compiles these views.</summary>
    internal static class MultiplayerViewKit
    {
        internal static TopiaForgeTone Tone(ScreenTone tone)
        {
            switch (tone)
            {
                case ScreenTone.Muted:
                    return TopiaForgeTone.Muted;
                case ScreenTone.Success:
                    return TopiaForgeTone.Success;
                case ScreenTone.Warning:
                    return TopiaForgeTone.Warning;
                case ScreenTone.Danger:
                    return TopiaForgeTone.Danger;
                default:
                    return TopiaForgeTone.Neutral;
            }
        }

        /// <summary>A caption that wraps, for notes and results, as plain text: a note can name what another player wrote.</summary>
        internal static TopiaForgeLabel Note(TopiaForgeContainer parent, string text = "") =>
            parent.Label(text, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).PlainText();
    }

    /// <summary>The readiness line HOST, BROWSE and CONNECT show while this game's mods are identified, with a retry on failure.</summary>
    internal sealed class ContentReadinessStrip
    {
        private readonly TopiaForgeContainer root;
        private readonly TopiaForgeLabel text;
        private readonly TopiaForgeProgressBar progress;
        private readonly TopiaForgeButton retry;
        private IContentReadinessScreen screen;

        internal ContentReadinessStrip(TopiaForgeContainer parent, IContentReadinessScreen screen)
        {
            this.screen = screen;
            root = parent.Row(TopiaForgeGap.Sm);
            root.FixedHeight(TopiaForgeTokens.ControlHeight);
            text = root.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Warning).PlainText().Ellipsis();
            text.Flex(1f, 0f);
            progress = root.ProgressBar();
            progress.FixedWidth(160f);
            retry = root.Button("RETRY", () => this.screen.Retry(), TopiaForgeButtonStyle.Outline);
            Bind();
        }

        internal void Bind()
        {
            var state = screen.State;
            // Absent once this game's mods are identified, so the screen below closes up instead of sitting under a gap.
            root.SetShown(state != ContentReadinessState.Ready);
            text.SetText(screen.Text);
            text.SetTone(state == ContentReadinessState.Unreadable ? TopiaForgeTone.Danger : TopiaForgeTone.Warning);
            progress.SetVisible(state == ContentReadinessState.Pending);
            progress.SetFraction(screen.Fraction);
            retry.SetVisible(screen.CanRetry);
            retry.SetEnabled(screen.CanRetry);
        }
    }
}
