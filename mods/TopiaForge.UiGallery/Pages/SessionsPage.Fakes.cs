using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>
    /// Fake multiplayer screens for the gallery: the manager's real Multiplayer tab views and connecting overlay, bound to
    /// made-up sessions, so every state can be checked by hand with F8. Nothing here touches the network or the runtime.
    /// </summary>
    internal sealed class GallerySessionScreens : IMultiplayerScreens
    {
        private readonly Queue<ScreenPrompt> prompts = new Queue<ScreenPrompt>();
        private readonly Queue<ScreenNotice> notices = new Queue<ScreenNotice>();

        public GallerySessionScreens()
        {
            ContentFake = new FakeContent();
            ConnectingFake = new FakeConnecting(this);
            BrowseFake = new FakeBrowse(this);
            HostFake = new FakeHost(this);
            ConnectFake = new FakeConnect(this);
            SessionFake = new FakeSession(this);
            VoiceFake = new FakeVoice(this);
        }

        internal FakeContent ContentFake { get; }

        internal FakeBrowse BrowseFake { get; }

        internal FakeHost HostFake { get; }

        internal FakeConnect ConnectFake { get; }

        internal FakeSession SessionFake { get; }

        internal FakeConnecting ConnectingFake { get; }

        internal FakeVoice VoiceFake { get; }

        public bool IsAvailable { get; set; } = true;

        public string AvailabilityText => IsAvailable ? string.Empty : "Live multiplayer is switched off in this build.";

        public string StatusText { get; set; } = "Playing on your own.";

        public ScreenTone StatusTone { get; set; } = ScreenTone.Muted;

        public bool HideMyIp { get; private set; } = true;

        public string DisplayName { get; private set; } = "Sam";

        public string DisplayNameError { get; private set; } = string.Empty;

        public string SettingsNote { get; set; } = string.Empty;

        /// <summary>Gets VOICE's line: shown while the fake session is hosted with "Offer voice chat" off.</summary>
        public string VoiceNote => HostFake.Hosting && !HostFake.Voice ? VoiceScreenText.NotOffered : string.Empty;

        public MultiplayerSubTab SelectedTab { get; private set; }

        public IBrowseScreen Browse => BrowseFake;

        public IHostScreen Host => HostFake;

        public IConnectScreen Connect => ConnectFake;

        public ISessionScreen Session => SessionFake;

        public IConnectingScreen Connecting => ConnectingFake;

        public ScreenPrompt? PendingPrompt
        {
            get
            {
                while (prompts.Count > 0 && prompts.Peek().IsAnswered) prompts.Dequeue();
                return prompts.Count == 0 ? null : prompts.Peek();
            }
        }

        public void SetHideMyIp(bool on)
        {
            if (on)
            {
                HideMyIp = true;
                return;
            }

            Ask(PrivacyPrompts.Disclosure(acknowledged =>
            {
                if (acknowledged) HideMyIp = false;
            }));
        }

        public void SetDisplayName(string value)
        {
            DisplayName = value ?? string.Empty;
            DisplayNameError = DisplayName.Length >= 2 && DisplayName.Length <= 24 ? string.Empty : "A name is 2 to 24 printable characters.";
        }

        public void SelectTab(MultiplayerSubTab tab) => SelectedTab = tab;

        public bool TryTakeNotice(out ScreenNotice notice)
        {
            if (notices.Count == 0)
            {
                notice = default;
                return false;
            }

            notice = notices.Dequeue();
            return true;
        }

        public void OnShown()
        {
        }

        public void OnHidden()
        {
        }

        internal void Ask(ScreenPrompt prompt) => prompts.Enqueue(prompt);

        internal void Notify(string text, ScreenTone tone = ScreenTone.Neutral) => notices.Enqueue(new ScreenNotice(text, tone));

        internal void Tick(float deltaTime)
        {
            ConnectingFake.Tick(deltaTime);
            VoiceFake.Tick(deltaTime);
        }
    }

    /// <summary>The readiness strip in its three states.</summary>
    internal sealed class FakeContent : IContentReadinessScreen
    {
        public ContentReadinessState State { get; set; } = ContentReadinessState.Ready;

        public string Text => State == ContentReadinessState.Pending ? "Identifying this game's mods for multiplayer: " + (int)(Fraction * 100f) + "%"
            : State == ContentReadinessState.Unreadable ? "This game's mods could not be read: a file of one of them is in use or cannot be opened. Close the program using it, then try again."
            : string.Empty;

        public float Fraction { get; set; } = 0.41f;

        public bool CanRetry => State == ContentReadinessState.Unreadable;

        public void Retry() => State = ContentReadinessState.Pending;
    }

    /// <summary>BROWSE with a few made-up sessions on the local network.</summary>
    internal sealed class FakeBrowse : IBrowseScreen
    {
        private static readonly ServerRow[] Lan =
        {
            new ServerRow(new string('a', 64), false, "Sam's sandbox", "Sandbox", "Open Sandbox", "3/8", 3, 20, ServerReadiness.Ready, "Ready"),
            new ServerRow(new string('b', 64), true, "Zombies night", "Zombies", "Arena", "6/8", 6, 90, ServerReadiness.Needs, "Needs 2"),
            new ServerRow(new string('c', 64), false, "Old build", "Sandbox", "Open Sandbox", "1/4", 1, 240, ServerReadiness.Incompatible, "Incompatible"),
            new ServerRow(new string('d', 64), false, "Waiting for details", string.Empty, string.Empty, string.Empty, 0, null, ServerReadiness.Checking, "Checking")
        };

        private readonly GallerySessionScreens owner;

        internal FakeBrowse(GallerySessionScreens owner)
        {
            this.owner = owner;
        }

        public BrowseList List { get; private set; }

        public IContentReadinessScreen Content => owner.ContentFake;

        public bool IsSearching { get; private set; }

        public bool CanSearch => owner.IsAvailable;

        public IReadOnlyList<ServerRow> Rows { get; private set; } = Array.Empty<ServerRow>();

        public string EmptyText => List == BrowseList.Internet
            ? "The Internet server list needs the TopiaForge service, which this game is not set up to reach."
            : IsSearching ? "No sessions heard on your local network yet." : "Search your local network to find sessions on it.";

        public string? SelectedKey { get; private set; }

        public string SelectionText => SelectedKey == Lan[1].Key ? "This game needs: Zombies 0.2.0 is missing. Lobby 1.0.0 is missing. The TopiaForge launcher can install the host's exact mods." : string.Empty;

        public bool CanJoin => SelectedKey == Lan[0].Key && owner.ContentFake.State == ContentReadinessState.Ready;

        public bool CanInstallInLauncher => SelectedKey == Lan[1].Key;

        public bool CanToggleFavorite => SelectedKey != null;

        public bool IsFavorite { get; private set; }

        public void SelectList(BrowseList list)
        {
            List = list;
            Rows = list == BrowseList.Lan && IsSearching ? Lan : list == BrowseList.History ? new[] { Lan[0] } : Array.Empty<ServerRow>();
        }

        public void StartSearch()
        {
            IsSearching = true;
            SelectList(List);
            owner.Notify("Searching your local network for sessions.");
        }

        public void StopSearch()
        {
            IsSearching = false;
            SelectList(List);
        }

        public void Refresh() => owner.Notify("Checking the listed servers again.");

        public void Select(string? key) => SelectedKey = key;

        public void Join() => owner.ConnectingFake.Start("Sam's sandbox", failAt: -1);

        public void InstallInLauncher() => owner.Notify("Opened the TopiaForge launcher to install this session's mods.", ScreenTone.Success);

        public void ToggleFavorite()
        {
            IsFavorite = !IsFavorite;
            owner.Notify(IsFavorite ? "Added to your favorites." : "Removed from your favorites.", IsFavorite ? ScreenTone.Success : ScreenTone.Neutral);
        }
    }
}
