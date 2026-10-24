using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The Multiplayer tab's kit view, on the overlay's Paper card: the session status, Hide my IP and the player's name,
    /// then the BROWSE, HOST, CONNECT, VOICE and SESSION sub-tabs. It binds the screens every frame through dirty-checked
    /// setters and rebuilds a sub-tab only when the player switches to another. Kit only, so the UI Gallery shows it too.
    /// </summary>
    internal sealed class MultiplayerTabView
    {
        private static readonly string[] SubTabs = { "BROWSE", "HOST", "CONNECT", "VOICE", "SESSION" };

        private readonly UiHost host;
        private readonly IMultiplayerScreens screens;
        private readonly IVoiceScreen? voice;
        private TopiaForgeLabel? status;
        private TopiaForgeLabel? unavailable;
        private TopiaForgeLabel? settingsNote;
        private TopiaForgeLabel? nameError;
        private TopiaForgeToggle? hideMyIp;
        private TopiaForgeInputField? name;
        private TopiaForgeContainer? header;
        private TopiaForgeTabs? tabs;
        private TopiaForgeContainer? body;
        private IMultiplayerSubView? current;
        private MultiplayerVoiceView? voiceView;
        private MultiplayerSubTab shownTab;
        private bool shownAvailable;
        private bool built;

        /// <param name="host">The kit host.</param>
        /// <param name="screens">The multiplayer screens.</param>
        /// <param name="voice">VOICE's screen; null when voice chat could not start, which VOICE then says.</param>
        internal MultiplayerTabView(UiHost host, IMultiplayerScreens screens, IVoiceScreen? voice)
        {
            this.host = host;
            this.screens = screens;
            this.voice = voice;
        }

        internal void Build(TopiaForgeContainer content)
        {
            content.Label("MULTIPLAYER", TopiaForgeTextStyle.Display).FixedHeight(34f);
            // The status names the joined server as its host wrote it, so it renders as plain text.
            status = content.Label(screens.StatusText, TopiaForgeTextStyle.Caption).PlainText().Ellipsis();
            status.FixedHeight(22f);
            unavailable = content.Label(string.Empty, TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Warning).PlainText();

            header = content.Row(TopiaForgeGap.Md);
            header.FixedHeight(TopiaForgeTokens.ControlHeight);
            hideMyIp = header.Toggle(MultiplayerPrivacyText.HideMyIp, screens.HideMyIp, on =>
            {
                screens.SetHideMyIp(on);
                hideMyIp?.SetValue(screens.HideMyIp);
            });
            header.Label("Your name", TopiaForgeTextStyle.Label).Tone(TopiaForgeTone.Muted).NoWrap();
            name = header.Input("2 to 24 characters", screens.DisplayName, screens.SetDisplayName);
            name.SetCharacterLimit(24);
            name.FixedWidth(220f);
            nameError = header.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger).PlainText().Ellipsis();
            nameError.Flex(1f, 0f);
            settingsNote = header.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).NoWrap().PlainText();

            tabs = content.Tabs(SubTabs);
            tabs.Select((int)screens.SelectedTab);
            tabs.OnSelected(index => screens.SelectTab((MultiplayerSubTab)index));
            body = content.Column(TopiaForgeGap.Sm);
            body.Flex(1f, 1f);
            built = true;
            ShowSubTab(screens.SelectedTab);
            Tick();
        }

        /// <summary>Main thread, every frame while the tab shows.</summary>
        internal void Tick()
        {
            if (!built) return;
            var available = screens.IsAvailable;
            status!.SetText(screens.StatusText);
            status.SetTone(MultiplayerViewKit.Tone(screens.StatusTone));
            unavailable!.SetText(screens.AvailabilityText);
            unavailable.SetShown(!available);
            header!.SetShown(available);
            var subTabs = tabs!;
            subTabs.SetShown(available);
            hideMyIp!.SetValue(screens.HideMyIp);
            name!.SyncText(screens.DisplayName);
            name.SetError(screens.DisplayNameError.Length != 0);
            nameError!.SetText(screens.DisplayNameError);
            settingsNote!.SetText(screens.SettingsNote);
            if (available != shownAvailable || screens.SelectedTab != shownTab)
            {
                subTabs.Select((int)screens.SelectedTab);
                ShowSubTab(screens.SelectedTab);
            }

            current?.Tick();
        }

        /// <summary>Says the tab stopped showing, as when the overlay closes: VOICE stops a microphone test and a capture.</summary>
        internal void OnHidden()
        {
            voiceView?.Hide();
            voiceView = null;
        }

        private void ShowSubTab(MultiplayerSubTab tab)
        {
            shownTab = tab;
            shownAvailable = screens.IsAvailable;
            OnHidden();
            host.Clear(body!);
            current = null;
            if (!shownAvailable) return;
            switch (tab)
            {
                case MultiplayerSubTab.Browse:
                    current = new MultiplayerBrowseView(body!, screens.Browse);
                    break;
                case MultiplayerSubTab.Host:
                    current = new MultiplayerHostView(host, body!, screens.Host);
                    break;
                case MultiplayerSubTab.Connect:
                    current = new MultiplayerConnectView(body!, screens.Connect);
                    break;
                case MultiplayerSubTab.Voice:
                    voiceView = new MultiplayerVoiceView(host, body!, screens, voice);
                    current = voiceView;
                    break;
                default:
                    current = new MultiplayerSessionView(host, body!, screens.Session);
                    break;
            }
        }
    }
}
