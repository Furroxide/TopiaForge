using System;
using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// HOST's kit view: the game to host, the session's name, size, password and visibility, the host policy, and starting
    /// or ending the session. Long content scrolls. The game section is rebuilt only when hosting switches between opening
    /// the running game and choosing one to start.
    /// </summary>
    internal sealed class MultiplayerHostView : IMultiplayerSubView
    {
        private readonly UiHost host;
        private readonly IHostScreen screen;
        private readonly ContentReadinessStrip readiness;
        private readonly TopiaForgeLabel mode;
        private readonly TopiaForgeContainer game;
        private readonly TopiaForgeInputField name;
        private readonly TopiaForgeLabel nameError;
        private readonly TopiaForgeSlider players;
        private readonly TopiaForgeInputField password;
        private readonly TopiaForgeLabel passwordError;
        private readonly TopiaForgeDropdown visibility;
        private readonly TopiaForgeLabel visibilityNote;
        private readonly TopiaForgeToggle voice;
        private readonly TopiaForgeButton start;
        private readonly TopiaForgeButton end;
        private readonly TopiaForgeLabel progress;
        private TopiaForgeDropdown? targets;
        private TopiaForgeLabel? current;
        private IReadOnlyList<string>? shownTargets;
        private bool shownChooses;

        internal MultiplayerHostView(UiHost host, TopiaForgeContainer parent, IHostScreen screen)
        {
            this.host = host;
            this.screen = screen;
            readiness = new ContentReadinessStrip(parent, screen.Content);
            var scroll = parent.Scroll(TopiaForgeGap.Sm);
            scroll.Flex(1f, 1f);
            var form = scroll.Content;
            mode = MultiplayerViewKit.Note(form, screen.ModeText);
            game = form.Column(TopiaForgeGap.Xs);
            BuildGame();

            form.SectionHeader("SESSION");
            name = form.Input("Server name", screen.Name, screen.SetName);
            name.SetCharacterLimit(48);
            nameError = form.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger).PlainText();
            var size = form.Row(TopiaForgeGap.Md);
            size.FixedHeight(TopiaForgeTokens.ControlHeight);
            players = size.Slider("Max players", screen.MinimumPlayers, screen.MaximumPlayers, screen.MaxPlayers, value => screen.SetMaxPlayers((int)Math.Round(value)));
            players.Flex(1f, 0f);
            password = form.Input("Password (optional)", screen.Password, screen.SetPassword).Password();
            password.SetCharacterLimit(64);
            passwordError = form.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger).PlainText();
            visibility = form.Dropdown(screen.VisibilityOptions, screen.Visibility, screen.SetVisibility);
            visibilityNote = MultiplayerViewKit.Note(form);

            form.SectionHeader("HOST POLICY");
            voice = form.Toggle("Offer voice chat", screen.Voice, screen.SetVoice);
            foreach (var seam in screen.PendingPolicies)
            {
                var row = form.Row(TopiaForgeGap.Md);
                row.Toggle(seam.Label, false, _ => { }).SetEnabled(false);
                MultiplayerViewKit.Note(row, seam.Note);
            }

            var actions = parent.Row(TopiaForgeGap.Sm);
            actions.FixedHeight(TopiaForgeTokens.ControlHeight);
            start = actions.Button(screen.HostLabel, screen.Host);
            actions.Spacer().Flex(1f, 0f);
            end = actions.Button("END SESSION FOR EVERYONE", screen.End, TopiaForgeButtonStyle.Danger);
            progress = parent.Label(string.Empty, TopiaForgeTextStyle.Caption).PlainText();
            Tick();
        }

        public void Tick()
        {
            readiness.Bind();
            mode.SetText(screen.ModeText);
            if (screen.ChoosesTarget != shownChooses || (screen.ChoosesTarget && !ReferenceEquals(screen.TargetOptions, shownTargets)))
            {
                host.Clear(game);
                BuildGame();
            }

            targets?.SetValue(screen.SelectedTarget);
            current?.SetText(screen.CurrentGameText);
            var editable = screen.FormEnabled;
            name.SyncText(screen.Name);
            name.SetEnabled(editable);
            name.SetError(screen.NameError.Length != 0);
            nameError.SetText(screen.NameError);
            players.SetValue(screen.MaxPlayers);
            players.SetEnabled(editable);
            password.SyncText(screen.Password);
            password.SetEnabled(editable);
            password.SetError(screen.PasswordError.Length != 0);
            passwordError.SetText(screen.PasswordError);
            visibility.SetValue(screen.Visibility);
            visibility.SetEnabled(editable);
            visibilityNote.SetText(screen.VisibilityNote);
            voice.SetValue(screen.Voice);
            voice.SetEnabled(editable);
            start.SetText(screen.HostLabel);
            start.SetEnabled(screen.CanHost);
            end.SetVisible(screen.CanEnd);
            end.SetEnabled(screen.CanEnd);
            progress.SetText(screen.ProgressText);
            progress.SetTone(MultiplayerViewKit.Tone(screen.ProgressTone));
        }

        private void BuildGame()
        {
            shownChooses = screen.ChoosesTarget;
            shownTargets = screen.TargetOptions;
            targets = null;
            current = null;
            if (shownChooses)
            {
                game.SectionHeader("GAME");
                if (shownTargets.Count == 0)
                {
                    game.Label("No game can be started: no enabled package declares a launch target.", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Warning);
                    return;
                }

                targets = game.Dropdown(shownTargets, screen.SelectedTarget, screen.SelectTarget);
                return;
            }

            current = game.Label(screen.CurrentGameText, TopiaForgeTextStyle.Body).PlainText().Ellipsis();
        }
    }
}
