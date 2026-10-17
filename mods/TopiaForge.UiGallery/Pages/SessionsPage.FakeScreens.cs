using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>HOST with a made-up game list.</summary>
    internal sealed class FakeHost : IHostScreen
    {
        private static readonly string[] Targets = { "Sandbox · Open Sandbox", "Zombies · Arena" };
        private static readonly string[] Visibilities = { "Public", "Invite", "LAN" };

        private static readonly HostPolicySeam[] Seams =
        {
            new HostPolicySeam("Guests can build", "Not available in this build."),
            new HostPolicySeam("Prop protection", "Not available in this build."),
            new HostPolicySeam("Per-player quotas", "Not available in this build.")
        };

        private readonly GallerySessionScreens owner;

        internal FakeHost(GallerySessionScreens owner)
        {
            this.owner = owner;
        }

        internal bool Running { get; set; }

        internal bool Hosting { get; set; }

        public IContentReadinessScreen Content => owner.ContentFake;

        public string ModeText => Hosting ? "You are hosting this game." : Running ? "Open this game to other players. It keeps running as it is." : "Choose a game to start and open to other players.";

        public bool ChoosesTarget => !Running && !Hosting;

        public IReadOnlyList<string> TargetOptions => Targets;

        public int SelectedTarget { get; private set; }

        public string CurrentGameText => ChoosesTarget ? string.Empty : "This game: Sandbox · Open Sandbox";

        public string Name { get; private set; } = "Sam's session";

        public string NameError => Name.Length >= 1 && Name.Length <= 48 ? string.Empty : "A server name is 1 to 48 printable characters.";

        public int MaxPlayers { get; private set; } = 8;

        public int MinimumPlayers => 2;

        public int MaximumPlayers => 16;

        public string Password { get; private set; } = string.Empty;

        public string PasswordError => Password.Length <= 64 ? string.Empty : "A password is 1 to 64 characters with no control characters.";

        public IReadOnlyList<string> VisibilityOptions => Visibilities;

        public int Visibility { get; private set; } = 2;

        public string VisibilityNote => Visibility == 2
            ? "Players on your local network can find this session and join it."
            : "Public and invite-only sessions, invites and listed servers need the TopiaForge service, which this game is not set up to reach. LAN play still works.";

        public bool Voice { get; private set; }

        public IReadOnlyList<HostPolicySeam> PendingPolicies => Seams;

        public bool FormEnabled => !Hosting;

        public bool CanHost => !Hosting && NameError.Length == 0 && owner.ContentFake.State == ContentReadinessState.Ready;

        public string HostLabel => Running ? "OPEN TO PLAYERS" : "START AND HOST";

        public bool CanEnd => Hosting;

        public string ProgressText { get; private set; } = string.Empty;

        public ScreenTone ProgressTone { get; private set; }

        public void SelectTarget(int index) => SelectedTarget = Math.Max(0, Math.Min(index, Targets.Length - 1));

        public void SetName(string value) => Name = value ?? string.Empty;

        public void SetMaxPlayers(int value) => MaxPlayers = Math.Min(Math.Max(value, 2), 16);

        public void SetPassword(string value) => Password = value ?? string.Empty;

        public void SetVisibility(int index) => Visibility = Math.Max(0, Math.Min(index, 2));

        public void SetVoice(bool value) => Voice = value;

        public void Host()
        {
            void Open()
            {
                Hosting = true;
                ProgressText = "Hosting. Players can join now.";
                ProgressTone = ScreenTone.Success;
                owner.Notify("Your session is open to other players.", ScreenTone.Success);
            }

            if (Visibility == 2)
            {
                Open();
                return;
            }

            owner.Ask(PrivacyPrompts.Direct(allowed =>
            {
                if (allowed) Open();
                else ProgressText = "Hosting did not start. Nothing was opened.";
            }));
        }

        public void End()
        {
            owner.Ask(PrivacyPrompts.Destructive("End session for everyone?", "Every player leaves the session. You keep playing on your own.", "END SESSION", confirmed =>
            {
                if (!confirmed) return;
                Hosting = false;
                ProgressText = "The session ended for everyone.";
                ProgressTone = ScreenTone.Neutral;
            }));
        }
    }

    /// <summary>CONNECT, with the prompts a typed address goes through.</summary>
    internal sealed class FakeConnect : IConnectScreen
    {
        private readonly GallerySessionScreens owner;

        internal FakeConnect(GallerySessionScreens owner)
        {
            this.owner = owner;
        }

        public IContentReadinessScreen Content => owner.ContentFake;

        public string InviteCode { get; private set; } = string.Empty;

        public string InviteError => InviteCode.Length == 0 || InviteCode.Replace("-", string.Empty).Length == 10 ? string.Empty : "An invite code is 10 letters and digits, such as ABCDE-12345.";

        public string InviteNote => "This game is not set up to reach the TopiaForge service, so invites and listed servers are unavailable. LAN and direct play still work.";

        public bool CanJoinInvite => false;

        public string Address { get; private set; } = string.Empty;

        public string AddressError => string.Empty;

        public string Password { get; private set; } = string.Empty;

        public string PasswordError => string.Empty;

        public bool CanJoinAddress => Address.Trim().Length > 0;

        public string ResultText { get; private set; } = string.Empty;

        public ScreenTone ResultTone { get; private set; }

        public bool CanInstallInLauncher => false;

        public void SetInviteCode(string value) => InviteCode = value ?? string.Empty;

        public void JoinInvite() => owner.Notify(InviteNote, ScreenTone.Warning);

        public void SetAddress(string value) => Address = value ?? string.Empty;

        public void SetPassword(string value) => Password = value ?? string.Empty;

        public void JoinAddress()
        {
            ResultText = "Waiting for your choice.";
            owner.Ask(PrivacyPrompts.Direct(allowed =>
            {
                if (!allowed)
                {
                    ResultText = "Nothing was contacted. " + MultiplayerPrivacyText.HideMyIp + " stayed on.";
                    ResultTone = ScreenTone.Neutral;
                    return;
                }

                ResultText = "Joining the session.";
                ResultTone = ScreenTone.Success;
                owner.ConnectingFake.Start("Direct session", failAt: (int)ConnectingStep.VerifyingMods);
            }));
        }

        public void InstallInLauncher()
        {
        }
    }

    /// <summary>SESSION while hosting a made-up session.</summary>
    internal sealed class FakeSession : ISessionScreen
    {
        private static readonly PlayerRow[] Rows =
        {
            new PlayerRow("p0", "Sam", true, true, null, false, false),
            new PlayerRow("p1", "Ada", false, false, 40, false, true),
            new PlayerRow("p2", "Bo", false, false, 130, true, true),
            new PlayerRow("p3", "Cy", false, false, null, false, true)
        };

        private readonly GallerySessionScreens owner;

        internal FakeSession(GallerySessionScreens owner)
        {
            this.owner = owner;
        }

        internal bool Ready { get; set; } = true;

        public string Title => owner.HostFake.Hosting ? "Hosting this game" : "Not in a session";

        public string Subtitle => owner.HostFake.Hosting ? "4 of 8 players" : string.Empty;

        public string InviteText => owner.HostFake.Hosting ? "Players on your local network find this session in BROWSE, under LAN." : string.Empty;

        public bool PlayersVisible => owner.HostFake.Hosting && Ready;

        public string PlayersNote => owner.HostFake.Hosting ? "Players appear once the host lists them." : "Host a game or join one to play together.";

        public IReadOnlyList<PlayerRow> Players => PlayersVisible ? Rows : Array.Empty<PlayerRow>();

        public string? SelectedKey { get; private set; }

        public bool CanKick => PlayersVisible && Find()?.CanModerate == true;

        public bool CanBan => CanKick;

        public bool CanMute => CanKick;

        public bool IsSelectedMuted => Find()?.Muted == true;

        public bool CanEnd => owner.HostFake.Hosting;

        public bool CanLeave => false;

        public void Select(string? key) => SelectedKey = key;

        public void Kick() => Confirm("Remove", "can join again.");

        public void Ban() => Confirm("Ban", "cannot join it again while you host.");

        public void ToggleMute() => owner.Notify("Muted " + Find()?.Name + " for everyone.", ScreenTone.Success);

        public void EndForEveryone() => owner.HostFake.End();

        public void Leave()
        {
        }

        private void Confirm(string verb, string consequence)
        {
            var name = Find()?.Name ?? "the player";
            owner.Ask(PrivacyPrompts.Destructive(verb + " " + name + "?", name + " leaves the session and " + consequence, verb.ToUpperInvariant(), confirmed =>
            {
                if (confirmed) owner.Notify(verb + " " + name + ": done.", ScreenTone.Success);
            }));
        }

        private PlayerRow? Find() => Array.Find(Rows, row => row.Key == SelectedKey);
    }
}
