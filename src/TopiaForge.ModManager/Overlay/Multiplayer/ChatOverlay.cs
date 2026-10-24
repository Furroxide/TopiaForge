using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>The same fading HUD log and kit text composer in the game and the gallery. No native input is disabled.</summary>
    internal sealed class ChatOverlay : ITopiaForgeDismissable, IDisposable
    {
        private readonly IInSessionScreen screen;
        private readonly TopiaForgeContainer root;
        private readonly TopiaForgeChatLog log;
        private readonly TopiaForgeContainer composer;
        private readonly TopiaForgeInputField field;
        private readonly TopiaForgeCursorLease cursor = new TopiaForgeCursorLease();
        private bool open;
        private int revision = -1;

        internal ChatOverlay(TopiaForgeContainer parent, IInSessionScreen screen)
        {
            this.screen = screen;
            root = parent.Column(TopiaForgeGap.Sm);
            root.Stretch();
            root.Dynamic();
            log = root.ChatLog(128, 300).ClosedLines(8);
            log.Flex(1f, 1f);
            composer = root.Row(TopiaForgeGap.Sm);
            composer.FixedHeight(TopiaForgeTokens.ControlHeight);
            field = composer.Input("Message or /help", string.Empty, _ => { }).PlainText();
            field.SetCharacterLimit(256);
            field.OnSubmit(Submit);
            composer.Button("SEND", () => Submit(field.Text));
            composer.Button("CLOSE", Close, TopiaForgeButtonStyle.Ghost);
            composer.SetShown(false);
        }

        public TopiaForgeLayerBand Band => TopiaForgeLayerBand.Window;
        public void Dismiss() => Close();

        internal void Tick(bool gameplay)
        {
            if (!gameplay || !screen.IsAvailable) screen.Close();
            root.SetShown(gameplay && screen.IsAvailable);
            if (revision != screen.Revision) { revision = screen.Revision; log.Clear(); field.SetText(string.Empty); }
            while (screen.TryTakeLine(out var line))
                log.Push(line.Author, line.Text, line.Error ? TopiaForgeChatLineKind.Error : line.Author.Length == 0 ? TopiaForgeChatLineKind.System : TopiaForgeChatLineKind.Player);
            if (open != screen.IsOpen)
            {
                open = screen.IsOpen;
                if (!open) ReleaseInput();
                composer.SetShown(open);
                log.SetOpen(open);
                cursor.SetActive(open);
                if (open) { TopiaForgeDismissStack.Push(this); field.Focus(); }
            }
            screen.SetTyping(open && field.IsFocused);
        }

        private void Submit(string text)
        {
            if (screen.Submit(text)) field.SetText(string.Empty);
            if (screen.IsOpen) field.Focus();
            else Close();
        }

        internal void Close()
        {
            screen.Close();
            ReleaseInput();
            open = false;
            composer.SetShown(false);
            log.SetOpen(false);
        }

        private void ReleaseInput()
        {
            field.Blur();
            // A click on CLOSE can select its button; clearing just the field leaves that hidden selection behind.
            composer.ReleaseFocus();
            field.SetText(string.Empty);
            cursor.Release();
            TopiaForgeDismissStack.Remove(this);
            screen.SetTyping(false);
        }

        internal void Lost()
        {
            screen.Close();
            cursor.Release();
            TopiaForgeDismissStack.Remove(this);
            open = false;
        }

        public void Dispose() { Close(); }
    }
}
