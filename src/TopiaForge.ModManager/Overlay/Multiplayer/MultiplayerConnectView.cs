using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// CONNECT's kit view: an invite code, which needs the TopiaForge service, or an address and password the player types.
    /// The address field is the one place an address is ever shown, as the player's own typing.
    /// </summary>
    internal sealed class MultiplayerConnectView : IMultiplayerSubView
    {
        private readonly IConnectScreen screen;
        private readonly ContentReadinessStrip readiness;
        private readonly TopiaForgeInputField invite;
        private readonly TopiaForgeLabel inviteError;
        private readonly TopiaForgeButton joinInvite;
        private readonly TopiaForgeLabel inviteNote;
        private readonly TopiaForgeInputField address;
        private readonly TopiaForgeLabel addressError;
        private readonly TopiaForgeInputField password;
        private readonly TopiaForgeLabel passwordError;
        private readonly TopiaForgeButton joinAddress;
        private readonly TopiaForgeLabel result;
        private readonly TopiaForgeButton install;

        internal MultiplayerConnectView(TopiaForgeContainer parent, IConnectScreen screen)
        {
            this.screen = screen;
            readiness = new ContentReadinessStrip(parent, screen.Content);

            parent.SectionHeader("INVITE CODE");
            var inviteRow = parent.Row(TopiaForgeGap.Sm);
            inviteRow.FixedHeight(TopiaForgeTokens.ControlHeight);
            invite = inviteRow.Input("ABCDE-12345", screen.InviteCode, screen.SetInviteCode);
            invite.SetCharacterLimit(32);
            joinInvite = inviteRow.Button("JOIN", screen.JoinInvite);
            inviteError = parent.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger).PlainText();
            inviteNote = MultiplayerViewKit.Note(parent, screen.InviteNote);

            parent.SectionHeader("ADDRESS");
            address = parent.Input("Host and port, or a topiaforge:// link", screen.Address, screen.SetAddress);
            address.SetCharacterLimit(512);
            address.OnSubmit(_ => screen.JoinAddress());
            addressError = parent.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger).PlainText();
            var passwordRow = parent.Row(TopiaForgeGap.Sm);
            passwordRow.FixedHeight(TopiaForgeTokens.ControlHeight);
            password = passwordRow.Input("Password, if the session has one", screen.Password, screen.SetPassword).Password();
            password.SetCharacterLimit(64);
            password.OnSubmit(_ => screen.JoinAddress());
            joinAddress = passwordRow.Button("JOIN", screen.JoinAddress);
            passwordError = parent.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger).PlainText();

            result = parent.Label(string.Empty, TopiaForgeTextStyle.Body).PlainText();
            var actions = parent.Row(TopiaForgeGap.Sm);
            actions.FixedHeight(TopiaForgeTokens.ControlHeight);
            install = actions.Button("INSTALL & JOIN IN LAUNCHER", screen.InstallInLauncher, TopiaForgeButtonStyle.Outline);
            Tick();
        }

        public void Tick()
        {
            readiness.Bind();
            invite.SyncText(screen.InviteCode);
            invite.SetError(screen.InviteError.Length != 0);
            inviteError.SetText(screen.InviteError);
            joinInvite.SetEnabled(screen.CanJoinInvite);
            inviteNote.SetText(screen.InviteNote);
            address.SyncText(screen.Address);
            address.SetError(screen.AddressError.Length != 0);
            addressError.SetText(screen.AddressError);
            password.SyncText(screen.Password);
            password.SetError(screen.PasswordError.Length != 0);
            passwordError.SetText(screen.PasswordError);
            joinAddress.SetEnabled(screen.CanJoinAddress);
            result.SetText(screen.ResultText);
            result.SetTone(MultiplayerViewKit.Tone(screen.ResultTone));
            install.SetVisible(screen.CanInstallInLauncher);
            install.SetEnabled(screen.CanInstallInLauncher);
        }
    }
}
