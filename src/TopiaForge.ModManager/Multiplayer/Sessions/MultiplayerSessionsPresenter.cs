using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The Multiplayer tab's presenter: the header, the five sub-tabs, the connecting overlay, the prompts and the toasts,
    /// over the runtime's multiplayer SPI, and the sessions the TopiaForge launcher asks of this game. It ticks every frame,
    /// overlay open or not, so a join and the end of a session are followed while the player plays; in a settled state a
    /// frame allocates nothing. It reaches the network only when the player acts (searching the local network, checking a
    /// server, hosting or joining) or the launcher asks it to host or join. Main thread only.
    /// </summary>
    internal sealed class MultiplayerSessionsPresenter : IMultiplayerScreens
    {
        internal const string NotInstalled = "Multiplayer is not available: the TopiaForge Multiplayer mod is not loaded. Enable it in the TopiaForge launcher, then start the game again.";
        internal const string SwitchedOff = "Live multiplayer is switched off in this build.";
        internal const string LoadingSettings = "Loading multiplayer settings...";
        internal const string SavingSettings = "Saving multiplayer settings...";
        internal const string NameRule = "A name is 2 to 24 printable characters.";
        internal const string PromptWithdrawn = "The question closed because the game or session it asked about changed. Nothing was done.";
        internal const int MaximumNotices = 16;
        internal const int MaximumPrompts = 4;

        private readonly IMultiplayerSessionEnvironment environment;
        private readonly MultiplayerBackgroundQueue queue;
        private readonly MultiplayerSettingsService settings;
        private readonly MultiplayerPreferencesService preferences;
        private readonly LaunchSessionPresenter launches;
        private readonly LiveJoinIntake? intake;
        private readonly MultiplayerStatusTracker status = new MultiplayerStatusTracker();
        private readonly ContentReadinessPresenter content;
        private readonly MultiplayerScreensContext context;
        private readonly ConnectingPresenter connecting;
        private readonly BrowsePresenter browse;
        private readonly HostPresenter host;
        private readonly ConnectPresenter connect;
        private readonly SessionPresenter session;
        private readonly Queue<ScreenPrompt> prompts = new Queue<ScreenPrompt>();
        private readonly List<ScreenPrompt> withdrawing = new List<ScreenPrompt>(MaximumPrompts);
        private readonly Queue<ScreenNotice> notices = new Queue<ScreenNotice>();
        private MultiplayerSubTab selectedTab;
        private string displayName = MultiplayerSettings.DefaultDisplayName;
        private string displayNameError = string.Empty;
        private string statusText = string.Empty;
        private ScreenTone statusTone;
        private int statusVersion = -1;
        private string statusWaiting = string.Empty;
        private InternalSessionRole statusRole;
        private bool shown;

        /// <param name="environment">The runtime and game around the screens.</param>
        /// <param name="store">Where the multiplayer settings live; it runs on the background queue.</param>
        /// <param name="scheduler">Where file work runs: a background thread in the game.</param>
        internal MultiplayerSessionsPresenter(IMultiplayerSessionEnvironment environment, IMultiplayerSettingsStore store, IMultiplayerWorkScheduler scheduler, ExecuteChatCommand? executeCommand = null)
        {
            this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
            queue = new MultiplayerBackgroundQueue(scheduler ?? throw new ArgumentNullException(nameof(scheduler)));
            settings = new MultiplayerSettingsService(queue, store ?? throw new ArgumentNullException(nameof(store)));
            var launcher = environment.Launcher;
            preferences = new MultiplayerPreferencesService(queue, launcher);
            content = new ContentReadinessPresenter(environment);
            context = new MultiplayerScreensContext(environment, settings, preferences, status, content, queue, Ask, Notify);
            connecting = new ConnectingPresenter(context);
            browse = new BrowsePresenter(context, connecting);
            host = new HostPresenter(context);
            connect = new ConnectPresenter(context, connecting);
            session = new SessionPresenter(context);
            InSession = new InSessionPresenter(context, session, executeCommand ?? NoCommand);
            launches = new LaunchSessionPresenter(context, connecting, queue);
            intake = launcher == null ? null : new LiveJoinIntake(launcher);
        }

        public bool IsAvailable => status.IsLiveAvailable;

        internal InSessionPresenter InSession { get; }

        private static bool NoCommand(string name, IReadOnlyList<string> arguments, out OperationResult<string>? result) { result = null; return false; }

        public string AvailabilityText => environment.Control == null ? NotInstalled : status.IsLiveAvailable ? string.Empty : SwitchedOff;

        public string StatusText => statusText;

        public ScreenTone StatusTone => statusTone;

        public bool HideMyIp => settings.Current.HideMyIp;

        public string DisplayName => displayName;

        public string DisplayNameError => displayNameError;

        public string SettingsNote =>
            !settings.IsLoaded || !preferences.IsLoaded ? LoadingSettings
            : settings.IsSaving || preferences.IsSaving || queue.IsBusy ? SavingSettings
            : string.Empty;

        public string VoiceNote
        {
            get
            {
                // The voice service applies the session's setting, the stricter of what the host said and what the join was
                // shown, so its state is the one to tell. Reading it allocates nothing.
                try
                {
                    return environment.Control?.Voice?.TransmitState == InternalVoiceTransmitState.NotOffered ? VoiceScreenText.NotOffered : string.Empty;
                }
                catch (Exception)
                {
                    // A control that cannot answer says nothing about voice; the next frame asks again.
                    return string.Empty;
                }
            }
        }

        public MultiplayerSubTab SelectedTab => selectedTab;

        public IBrowseScreen Browse => browse;

        public IHostScreen Host => host;

        public IConnectScreen Connect => connect;

        public ISessionScreen Session => session;

        public IConnectingScreen Connecting => connecting;

        public ScreenPrompt? PendingPrompt
        {
            get
            {
                if (DropAnswered()) Notify(PromptWithdrawn, ScreenTone.Neutral);
                return prompts.Count == 0 ? null : prompts.Peek();
            }
        }

        /// <summary>Gets the shared state, for tests.</summary>
        internal MultiplayerScreensContext Context => context;

        internal MultiplayerSettingsService Settings => settings;

        /// <summary>Gets the launcher's preferences as this game uses them, for tests.</summary>
        internal MultiplayerPreferencesService Preferences => preferences;

        /// <summary>Gets what starts the sessions the launcher asks for, for tests.</summary>
        internal LaunchSessionPresenter Launches => launches;

        /// <summary>Gets what takes the launcher's live joins, for tests; null when no launcher started this game.</summary>
        internal LiveJoinIntake? Intake => intake;

        public void SelectTab(MultiplayerSubTab tab)
        {
            if (!Enum.IsDefined(typeof(MultiplayerSubTab), tab) || tab == selectedTab) return;
            if (selectedTab == MultiplayerSubTab.Browse) browse.OnHidden();
            selectedTab = tab;
            if (tab == MultiplayerSubTab.Host) host.RefreshTargets();
        }

        public void SetHideMyIp(bool on)
        {
            if (!settings.IsLoaded) return;
            context.Privacy.SetHideMyIp(on, kept => Notify(kept ? MultiplayerPrivacyText.HideMyIp + " is on." : MultiplayerPrivacyText.HideMyIp + " is off.", ScreenTone.Neutral));
        }

        public void SetDisplayName(string value)
        {
            displayName = value ?? string.Empty;
            if (!MultiplayerSettings.IsValidDisplayName(displayName))
            {
                displayNameError = NameRule;
                return;
            }

            displayNameError = string.Empty;
            if (!preferences.IsLoaded) return;

            // The name is the launcher's preference: it is recorded in the launcher's journal, never in manager data too.
            preferences.ChangeDisplayName(displayName);
            ApplyDisplayName(displayName);
        }

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
            shown = true;
            settings.Load(Loaded);
            preferences.Load(PreferencesLoaded);
            if (selectedTab == MultiplayerSubTab.Host) host.RefreshTargets();
        }

        public void OnHidden()
        {
            shown = false;
            browse.OnHidden();
        }

        /// <summary>
        /// Starts loading the settings and the launcher's preferences, so a saved name applies to sessions started without the
        /// tab, and, in a game a launcher started, starts taking its live joins.
        /// </summary>
        internal void Start()
        {
            settings.Load(Loaded);
            preferences.Load(PreferencesLoaded);
            intake?.Start();
        }

        /// <summary>Stops taking live joins and lets go of a launch still starting, as the game closes.</summary>
        internal void Stop()
        {
            InSession.Dispose();
            intake?.Dispose();
            launches.Stop();
            context.LanBrowsing.Stop();
        }

        /// <summary>Main thread, every frame.</summary>
        internal void Tick(float deltaTime)
        {
            queue.Pump();
            var failure = settings.TakeFailure();
            if (failure.Length > 0) Notify(failure, ScreenTone.Warning);
            var recorded = preferences.TakeFailure();
            if (recorded.Length > 0) Notify(recorded, ScreenTone.Warning);
            MultiplayerSessionSnapshot? snapshot = null;
            try
            {
                snapshot = environment.SessionSnapshot;
            }
            catch (Exception)
            {
                snapshot = null;
            }

            status.Tick(environment.Control, snapshot, deltaTime);

            // What the launcher asks for: the session this game was launched to host or join, once planned, and live joins.
            var launched = environment.TakeLaunchedSession();
            if (launched != null) launches.Begin(launched);
            var join = intake?.TakeArrived();
            if (join != null) launches.Offer(join);

            // Reading readiness starts identifying the mods, so it waits for a player who opened multiplayer, or a launch.
            if (shown || connecting.IsBusy || launches.IsActive) content.Tick();
            connecting.Tick(deltaTime);
            launches.Tick(deltaTime);
            host.Tick();
            connect.Tick();
            session.Tick();
            InSession.Tick(deltaTime);
            if (shown || browse.IsSearching) browse.Tick();

            // Why a joined game waits at the main menu moves with the follow alone, not with the control's status.
            if (status.Version != statusVersion || !string.Equals(environment.Follow.WaitingLine, statusWaiting, StringComparison.Ordinal)) RebuildStatus();
            WithdrawStale();
        }

        private void Loaded(MultiplayerSettings loaded)
        {
            if (preferences.IsLoaded) host.ApplyDefaults(preferences.Current);
        }

        private void PreferencesLoaded(MultiplayerPreferenceValues loaded)
        {
            displayName = loaded.DisplayName;
            displayNameError = string.Empty;
            ApplyDisplayName(loaded.DisplayName);
            if (settings.IsLoaded) host.ApplyDefaults(loaded);
        }

        private void ApplyDisplayName(string name)
        {
            try
            {
                environment.DisplayName = name;
            }
            catch (ArgumentException)
            {
                displayNameError = NameRule;
            }
        }

        private void RebuildStatus()
        {
            statusVersion = status.Version;
            statusWaiting = environment.Follow.WaitingLine;
            var current = status.Status;
            var wasJoined = statusRole == InternalSessionRole.Client;
            statusRole = status.Role;

            // A session that ends while the player plays, as when the host removes them, is told once; a join the overlay
            // follows reports its own end, and leaving or cancelling needs no word.
            if (wasJoined && status.Role != InternalSessionRole.Client && !connecting.IsVisible && current.JoinPhase == InternalJoinPhase.Ended &&
                current.JoinFailure != InternalLiveFailure.Left && current.JoinFailure != InternalLiveFailure.Cancelled && current.JoinFailure != InternalLiveFailure.None)
            {
                var ended = LiveFailureMessages.ForJoin(current.JoinFailure, current.RejectionCode);
                var hostSays = HostText.HostSays(current.HostMessage);
                Notify(ended.Message + (hostSays.Length == 0 ? string.Empty : " " + hostSays), ScreenTone.Warning);
            }

            var server = current.Server == null ? string.Empty : HostText.ForDisplay(current.Server.Name);
            switch (status.Role)
            {
                case InternalSessionRole.ListenHost:
                    statusText = "Hosting: " + HostPresenter.PlayersLine(Math.Max(current.ConnectedPlayers, 1), current.MaxPlayers) + ".";
                    statusTone = ScreenTone.Success;
                    break;
                case InternalSessionRole.Client:
                    // A joined game that waits at the main menu says why, for as long as it waits (session follow).
                    statusText = current.JoinPhase == InternalJoinPhase.Reconnecting ? "Reconnecting to the host..."
                        : statusWaiting.Length > 0 ? statusWaiting
                        : server.Length == 0 ? "Joined a session." : "Joined " + server + ".";
                    statusTone = current.JoinPhase == InternalJoinPhase.Reconnecting ? ScreenTone.Warning
                        : statusWaiting.Length > 0 ? ScreenTone.Neutral : ScreenTone.Success;
                    break;
                default:
                    statusText = MultiplayerStatusTracker.IsInFlight(current) ? "Starting multiplayer..." : "Playing on your own.";
                    statusTone = ScreenTone.Muted;
                    break;
            }
        }

        /// <summary>
        /// Queues a question. One question is enough at a time: a further one while the queue is full answers no at once, so
        /// a prompt that cannot show never leaves an action half done.
        /// </summary>
        private void Ask(ScreenPrompt prompt)
        {
            if (DropAnswered()) Notify(PromptWithdrawn, ScreenTone.Neutral);
            if (prompts.Count >= MaximumPrompts)
            {
                prompt.Answer(false);
                return;
            }

            prompts.Enqueue(prompt);
        }

        /// <summary>
        /// Withdraws every waiting question that stopped applying, as when the game it asks about ended: the view closes the
        /// one showing, and the player is told once that nothing was done. A frame with nothing to withdraw allocates nothing.
        /// </summary>
        private void WithdrawStale()
        {
            foreach (var prompt in prompts)
            {
                if (!prompt.IsAnswered && !prompt.StillHolds) withdrawing.Add(prompt);
            }

            // Withdrawing answers no, and what that runs must not change the queue under its own walk.
            for (var index = 0; index < withdrawing.Count; index++) withdrawing[index].Withdraw();
            withdrawing.Clear();
            var told = DropAnswered();
            foreach (var prompt in prompts) told |= prompt.TakeWithdrawal();
            if (told) Notify(PromptWithdrawn, ScreenTone.Neutral);
        }

        /// <summary>Drops the answered questions at the head of the queue; true when one of them was withdrawn.</summary>
        private bool DropAnswered()
        {
            var withdrawn = false;
            while (prompts.Count > 0 && prompts.Peek().IsAnswered) withdrawn |= prompts.Dequeue().TakeWithdrawal();
            return withdrawn;
        }

        /// <summary>Queues a toast; past the bound the oldest goes, so nothing unshown can pile up.</summary>
        private void Notify(string text, ScreenTone tone)
        {
            if (string.IsNullOrEmpty(text)) return;
            while (notices.Count >= MaximumNotices) notices.Dequeue();
            notices.Enqueue(new ScreenNotice(text, tone));
        }
    }
}
