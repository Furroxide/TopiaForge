using System;
using System.Globalization;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// CONNECT: an invite code, or an address and password the player types. The typed address is the one place an address
    /// appears, as the player's own input: nothing here repeats it in a result, a toast or a log. JOIN first shows the
    /// disclosure if it is due, then checks the server without joining, under Hide my IP's rule: a host beyond the local
    /// network is not contacted, and the check says so without contacting it. Only then does the per-session prompt ask;
    /// cancelling it leaves Hide my IP on and contacts nothing. A server that fits is joined through the realm rules; one
    /// that needs mods can be handed to the launcher. An invite code, or an invite or server link pasted as the address,
    /// needs the TopiaForge service: the check then asks the service alone, never the host, and the join goes through the
    /// relay unless the player turned Hide my IP off. Without a service, neither is offered.
    /// </summary>
    internal sealed partial class ConnectPresenter : IConnectScreen
    {
        internal const string AddressRule = "Enter a host name or address with an optional port, or a topiaforge:// link.";
        internal const string InviteRule = "An invite code is 10 letters and digits, such as ABCDE-12345.";
        internal const string NothingContacted = "Nothing was contacted. " + MultiplayerPrivacyText.HideMyIp + " stayed on.";
        internal const string TrustTitle = "Trust this server?";
        internal const string TrustBody = "This address now leads to a different server than it did before. Join it only if you trust it.";
        internal const string TrustConfirm = "TRUST AND JOIN";
        internal const string InviteHelp = "Paste the invite code a host shared with you. The join goes through the TopiaForge service.";

        private readonly MultiplayerScreensContext context;
        private readonly ConnectingPresenter joins;
        private IInternalJoinTarget? target;
        private IInternalJoinTarget? checking;
        private string? checkingInvite;
        private IInternalServerQuery? query;
        private bool queryAllowsDirect;
        private string inviteCode = string.Empty;
        private string inviteError = string.Empty;
        private string address = string.Empty;
        private string addressError = string.Empty;
        private string password = string.Empty;
        private string passwordError = string.Empty;
        private string resultText = string.Empty;
        private ScreenTone resultTone;
        private string? launcherLink;
        private string? trustedServerId;

        internal ConnectPresenter(MultiplayerScreensContext context, ConnectingPresenter joins)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.joins = joins ?? throw new ArgumentNullException(nameof(joins));
        }

        public IContentReadinessScreen Content => context.Content;

        public string InviteCode => inviteCode;

        public string InviteError => inviteError;

        public string InviteNote => context.HasService ? InviteHelp : LiveFailureMessages.ServiceNotConfigured;

        /// <summary>Gets whether the typed invite can be joined: it needs the TopiaForge service, and nothing else may be checking.</summary>
        public bool CanJoinInvite =>
            context.HasService && context.CanStartSession && !joins.IsBusy && query == null && inviteError.Length == 0 &&
            LauncherJoinLink.NormalizeInviteCode(inviteCode) != null;

        public string Address => address;

        public string AddressError => addressError;

        public string Password => password;

        public string PasswordError => passwordError;

        public bool CanJoinAddress =>
            context.CanStartSession && !joins.IsBusy && query == null && target != null && addressError.Length == 0 && passwordError.Length == 0;

        public string ResultText => resultText;

        public ScreenTone ResultTone => resultTone;

        public bool CanInstallInLauncher => launcherLink != null && query == null;

        public void SetInviteCode(string value)
        {
            var next = value ?? string.Empty;
            if (string.Equals(next, inviteCode, StringComparison.Ordinal)) return;
            inviteCode = next;
            if (checkingInvite != null) CancelCheck();
            inviteError = inviteCode.Trim().Length == 0 || LauncherJoinLink.NormalizeInviteCode(inviteCode) != null ? string.Empty : InviteRule;
        }

        public void JoinInvite()
        {
            if (!context.HasService)
            {
                // Invites resolve through the TopiaForge service, which this game does not reach; nothing is contacted.
                context.Notify(LiveFailureMessages.ServiceNotConfigured, ScreenTone.Warning);
                return;
            }

            if (!CanJoinInvite) return;
            var code = LauncherJoinLink.NormalizeInviteCode(inviteCode)!;
            IInternalJoinTarget? parsed = null;
            var ok = false;
            try
            {
                ok = context.Environment.Control?.TryCreateJoinTarget(code, out parsed) == true;
            }
            catch (Exception)
            {
                ok = false;
            }

            if (!ok || parsed == null || parsed.Kind != InternalJoinTargetKind.Invite)
            {
                inviteError = InviteRule;
                return;
            }

            // The service resolves the invite; it never connects to the host, so the check contacts no one else.
            var invite = parsed;
            context.Privacy.EnsureDisclosure(
                () => Check(invite, allowDirect: false, code),
                () => Result("Nothing was joined.", ScreenTone.Neutral));
        }

        public void SetAddress(string value)
        {
            var next = value ?? string.Empty;
            if (string.Equals(next, address, StringComparison.Ordinal)) return;
            address = next;
            CancelCheck();
            launcherLink = null;
            trustedServerId = null;
            resultText = string.Empty;
            target = null;
            var text = address.Trim();
            if (text.Length == 0)
            {
                addressError = string.Empty;
                return;
            }

            var control = context.Environment.Control;
            IInternalJoinTarget? parsed = null;
            var ok = false;
            try
            {
                ok = control != null && control.TryCreateJoinTarget(text, out parsed);
            }
            catch (Exception)
            {
                ok = false;
            }

            if (!ok || parsed == null)
            {
                addressError = AddressRule;
                return;
            }

            if ((parsed.Kind == InternalJoinTargetKind.Invite || parsed.Kind == InternalJoinTargetKind.ListedServer) && !context.HasService)
            {
                addressError = LiveFailureMessages.ServiceNotConfigured;
                return;
            }

            addressError = string.Empty;
            target = parsed;
        }

        public void SetPassword(string value)
        {
            password = value ?? string.Empty;
            passwordError = password.Length == 0 || HostPresenter.IsPassword(password) ? string.Empty : ConnectingPresenter.PasswordRule;
        }

        public void JoinAddress()
        {
            if (!CanJoinAddress) return;
            var chosen = target!;
            context.Privacy.EnsureDisclosure(
                () => Check(chosen, allowDirect: !context.Settings.Current.HideMyIp, null),
                () => Result("Nothing was joined.", ScreenTone.Neutral));
        }

        public void InstallInLauncher()
        {
            if (!CanInstallInLauncher) return;
            context.Environment.OpenUrl(launcherLink!);
            context.Notify("Opened the TopiaForge launcher to install this session's mods.", ScreenTone.Success);
        }

        /// <summary>Main thread, every frame: completes a running check.</summary>
        internal void Tick()
        {
            var running = query;
            if (running == null) return;
            bool completed;
            try
            {
                completed = running.IsCompleted;
            }
            catch (Exception)
            {
                completed = true;
            }

            if (!completed) return;
            query = null;
            Checked(running);
        }

        /// <param name="invite">The canonical invite code when <paramref name="chosen"/> came from the invite field; null otherwise.</param>
        private void Check(IInternalJoinTarget chosen, bool allowDirect, string? invite)
        {
            var control = context.Environment.Control;
            if (control == null) return;
            CancelCheck();
            try
            {
                query = control.Query(chosen, allowDirect);
            }
            catch (Exception)
            {
                Result("This game could not check the server.", ScreenTone.Danger);
                return;
            }

            checking = chosen;
            checkingInvite = invite;
            queryAllowsDirect = allowDirect;
            Result(invite == null ? "Checking the server..." : "Checking the invite...", ScreenTone.Neutral);
        }

        private void CancelCheck()
        {
            var running = query;
            query = null;
            checking = null;
            checkingInvite = null;
            try
            {
                running?.Dispose();
            }
            catch (Exception)
            {
                // A check that cannot stop has nothing left to report.
            }
        }

        private void Result(string text, ScreenTone tone)
        {
            resultText = text;
            resultTone = tone;
        }

        private static string Plural(int count, string one, string many) =>
            count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
    }
}
