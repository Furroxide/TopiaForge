using System.Collections.Generic;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// What the Multiplayer tab's views bind to. Presenters implement these over the runtime and the multiplayer SPI; the UI
    /// Gallery implements them over fake data. Every text is display-ready and holds no address, except
    /// <see cref="IConnectScreen.Address"/>, the player's own typing. Getters allocate nothing once their value settled, so
    /// a view may read them every frame and push them through the kit's dirty-checked setters; a list is replaced, never
    /// changed in place, so a view compares references before it rebinds one. Main thread only.
    /// </summary>
    internal interface IMultiplayerScreens
    {
        /// <summary>Gets whether live multiplayer can be used in this game at all.</summary>
        bool IsAvailable { get; }

        /// <summary>Gets why it cannot, while <see cref="IsAvailable"/> is false.</summary>
        string AvailabilityText { get; }

        /// <summary>Gets the one-line state of this game's session, such as "Hosting Sam's session: 3 of 8 players".</summary>
        string StatusText { get; }

        ScreenTone StatusTone { get; }

        /// <summary>Gets the Hide my IP setting; on unless the player turned it off.</summary>
        bool HideMyIp { get; }

        /// <summary>Turns Hide my IP on at once, or asks through the approved disclosure before turning it off.</summary>
        void SetHideMyIp(bool on);

        string DisplayName { get; }

        /// <summary>Gets why the typed name cannot be used; empty while it can.</summary>
        string DisplayNameError { get; }

        void SetDisplayName(string value);

        /// <summary>Gets a note while the multiplayer settings load or save, off the main thread; empty otherwise.</summary>
        string SettingsNote { get; }

        /// <summary>
        /// Gets the one plain line VOICE shows about this session's voice chat: <see cref="VoiceScreenText.NotOffered"/>
        /// while this game hosts, or has joined, a session that offers none; empty otherwise.
        /// </summary>
        string VoiceNote { get; }

        MultiplayerSubTab SelectedTab { get; }

        void SelectTab(MultiplayerSubTab tab);

        IBrowseScreen Browse { get; }

        IHostScreen Host { get; }

        IConnectScreen Connect { get; }

        ISessionScreen Session { get; }

        IConnectingScreen Connecting { get; }

        /// <summary>Gets the next question to show, or null. The view shows one at a time and answers it.</summary>
        ScreenPrompt? PendingPrompt { get; }

        /// <summary>Takes the next action result to show as a toast.</summary>
        bool TryTakeNotice(out ScreenNotice notice);

        /// <summary>Says the tab became visible.</summary>
        void OnShown();

        /// <summary>Says the tab or the overlay closed: browsing the local network stops.</summary>
        void OnHidden();
    }

    /// <summary>Where identifying this game's mods stands, with its progress and a retry.</summary>
    internal interface IContentReadinessScreen
    {
        ContentReadinessState State { get; }

        /// <summary>Gets the line HOST and BROWSE show; empty once the mods are identified.</summary>
        string Text { get; }

        /// <summary>Gets the progress from 0 to 1 while pending.</summary>
        float Fraction { get; }

        bool CanRetry { get; }

        void Retry();
    }

    /// <summary>BROWSE: the LAN, Internet, Favorites and History lists.</summary>
    internal interface IBrowseScreen
    {
        BrowseList List { get; }

        void SelectList(BrowseList list);

        IContentReadinessScreen Content { get; }

        /// <summary>Gets whether this game listens for sessions on the local network; only the player starts it.</summary>
        bool IsSearching { get; }

        bool CanSearch { get; }

        void StartSearch();

        void StopSearch();

        /// <summary>Checks the listed servers' mods again.</summary>
        void Refresh();

        IReadOnlyList<ServerRow> Rows { get; }

        /// <summary>Gets what the selected list shows while it has no rows.</summary>
        string EmptyText { get; }

        string? SelectedKey { get; }

        void Select(string? key);

        /// <summary>Gets what the selection needs, such as the mods to install; empty without a selection.</summary>
        string SelectionText { get; }

        bool CanJoin { get; }

        void Join();

        bool CanInstallInLauncher { get; }

        void InstallInLauncher();

        bool CanToggleFavorite { get; }

        bool IsFavorite { get; }

        void ToggleFavorite();
    }

    /// <summary>HOST: the session settings, the host policy, and starting or ending the session.</summary>
    internal interface IHostScreen
    {
        IContentReadinessScreen Content { get; }

        /// <summary>Gets what hosting does now: open the running game, start one, or why it cannot.</summary>
        string ModeText { get; }

        /// <summary>Gets whether the player chooses a game to start, as at the main menu.</summary>
        bool ChoosesTarget { get; }

        IReadOnlyList<string> TargetOptions { get; }

        int SelectedTarget { get; }

        void SelectTarget(int index);

        /// <summary>Gets the running game, as hosting opens it in place; empty at the main menu.</summary>
        string CurrentGameText { get; }

        string Name { get; }

        string NameError { get; }

        void SetName(string value);

        int MaxPlayers { get; }

        int MinimumPlayers { get; }

        int MaximumPlayers { get; }

        void SetMaxPlayers(int value);

        string Password { get; }

        string PasswordError { get; }

        void SetPassword(string value);

        IReadOnlyList<string> VisibilityOptions { get; }

        int Visibility { get; }

        void SetVisibility(int index);

        string VisibilityNote { get; }

        /// <summary>Gets whether the session offers voice chat, the one host policy the session engine supports today.</summary>
        bool Voice { get; }

        void SetVoice(bool value);

        /// <summary>Gets the host-policy controls later slices provide, shown as not available yet.</summary>
        IReadOnlyList<HostPolicySeam> PendingPolicies { get; }

        /// <summary>Gets whether the settings may change: not while starting or hosting.</summary>
        bool FormEnabled { get; }

        bool CanHost { get; }

        string HostLabel { get; }

        void Host();

        bool CanEnd { get; }

        /// <summary>Ends the session for everyone, after a destructive confirmation.</summary>
        void End();

        string ProgressText { get; }

        ScreenTone ProgressTone { get; }
    }

    /// <summary>CONNECT: an invite code, or an address and password the player typed.</summary>
    internal interface IConnectScreen
    {
        IContentReadinessScreen Content { get; }

        string InviteCode { get; }

        string InviteError { get; }

        void SetInviteCode(string value);

        /// <summary>Gets why invites cannot be used yet, such as the service not being configured.</summary>
        string InviteNote { get; }

        bool CanJoinInvite { get; }

        void JoinInvite();

        /// <summary>Gets the address the player typed: the one place an address is ever shown, as their own input.</summary>
        string Address { get; }

        string AddressError { get; }

        void SetAddress(string value);

        string Password { get; }

        string PasswordError { get; }

        void SetPassword(string value);

        bool CanJoinAddress { get; }

        void JoinAddress();

        /// <summary>Gets the last check's result, such as "Needs 2 mods"; it never repeats the address.</summary>
        string ResultText { get; }

        ScreenTone ResultTone { get; }

        bool CanInstallInLauncher { get; }

        void InstallInLauncher();
    }

    /// <summary>SESSION: the invite, the players, and leaving or ending the session.</summary>
    internal interface ISessionScreen
    {
        string Title { get; }

        string Subtitle { get; }

        string InviteText { get; }

        /// <summary>Gets whether this game hosts a session that has an invite code to share.</summary>
        bool CanCopyInvite { get; }

        /// <summary>Puts the invite code on the clipboard, for the host to paste where its players are; nothing else keeps it.</summary>
        void CopyInvite();

        /// <summary>Gets whether players are listed: once the session's host has listed them, never another host's list.</summary>
        bool PlayersVisible { get; }

        /// <summary>Gets why no players are listed, while <see cref="PlayersVisible"/> is false.</summary>
        string PlayersNote { get; }

        IReadOnlyList<PlayerRow> Players { get; }

        string? SelectedKey { get; }

        void Select(string? key);

        bool CanKick { get; }

        void Kick();

        bool CanBan { get; }

        void Ban();

        bool CanMute { get; }

        bool IsSelectedMuted { get; }

        void ToggleMute();

        bool CanEnd { get; }

        void EndForEveryone();

        bool CanLeave { get; }

        void Leave();
    }

    /// <summary>The connecting overlay: the join's phases, a password when asked, and the failure view.</summary>
    internal interface IConnectingScreen
    {
        bool IsVisible { get; }

        string Title { get; }

        IReadOnlyList<string> StepLabels { get; }

        /// <summary>Gets the stepper's target state; its version moves whenever any step changes.</summary>
        ConnectingSteps Steps { get; }

        /// <summary>Gets the line under a step, such as "Relay" or "3/7"; empty for none.</summary>
        string DetailOf(int step);

        string StatusText { get; }

        bool NeedsPassword { get; }

        string PasswordError { get; }

        void SubmitPassword(string value);

        bool HasFailed { get; }

        string FailureTitle { get; }

        string FailureMessage { get; }

        /// <summary>Gets the failure's details, such as the mods that differ; host-authored lines are redacted.</summary>
        IReadOnlyList<string> FailureLines { get; }

        /// <summary>Gets what the host said, attributed and redacted; empty when it said nothing.</summary>
        string HostSays { get; }

        bool CanRetry { get; }

        void Retry();

        bool CanOpenLauncher { get; }

        void OpenLauncher();

        bool CanCopyDetails { get; }

        void CopyDetails();

        /// <summary>Gets whether the player may trust a server that changed its identity at a typed address.</summary>
        bool CanTrust { get; }

        void Trust();

        /// <summary>Gets whether a join is running that Cancel stops; it always may.</summary>
        bool CanCancel { get; }

        void Cancel();

        /// <summary>Closes the overlay: cancels a running join, or dismisses the failure view.</summary>
        void Dismiss();
    }
}
