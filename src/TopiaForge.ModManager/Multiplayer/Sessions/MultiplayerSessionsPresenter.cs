using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The Multiplayer tab's presenter: the header, the five sub-tabs, the connecting overlay, the prompts and the toasts,
    /// over the runtime's multiplayer SPI. It ticks every frame, overlay open or not, so a join and the end of a session are
    /// followed while the player plays; in a settled state a frame allocates nothing. It reaches the network only when the
    /// player acts: searching the local network, checking a server, hosting or joining. Main thread only.
    /// </summary>
    internal sealed class MultiplayerSessionsPresenter : IMultiplayerScreens
    {
        internal const string NotInstalled = "Multiplayer is not available: the TopiaForge Multiplayer mod is not loaded.";
        internal const string SwitchedOff = "Live multiplayer is switched off in this build.";
        internal const string LoadingSettings = "Loading multiplayer settings...";
        internal const string SavingSettings = "Saving multiplayer settings...";
        internal const string NameRule = "A name is 2 to 24 printable characters.";
        internal const int MaximumNotices = 16;
        internal const int MaximumPrompts = 4;

        private readonly IMultiplayerSessionEnvironment environment;
        private readonly MultiplayerBackgroundQueue queue;
        private readonly MultiplayerSettingsService settings;
        private readonly MultiplayerStatusTracker status = new MultiplayerStatusTracker();
        private readonly ContentReadinessPresenter content;
        private readonly MultiplayerScreensContext context;
        private readonly ConnectingPresenter connecting;
        private readonly BrowsePresenter browse;
        private readonly HostPresenter host;
        private readonly ConnectPresenter connect;
        private readonly SessionPresenter session;
        private readonly Queue<ScreenPrompt> prompts = new Queue<ScreenPrompt>();
        private readonly Queue<ScreenNotice> notices = new Queue<ScreenNotice>();
        private MultiplayerSubTab selectedTab;
        private string displayName = MultiplayerSettings.DefaultDisplayName;
        private string displayNameError = string.Empty;
        private string statusText = string.Empty;
        private ScreenTone statusTone;
        private int statusVersion = -1;
        private InternalSessionRole statusRole;
        private bool shown;

        /// <param name="environment">The runtime and game around the screens.</param>
        /// <param name="store">Where the multiplayer settings live; it runs on the background queue.</param>
        /// <param name="scheduler">Where file work runs: a background thread in the game.</param>
        internal MultiplayerSessionsPresenter(IMultiplayerSessionEnvironment environment, IMultiplayerSettingsStore store, IMultiplayerWorkScheduler scheduler)
        {
            this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
            queue = new MultiplayerBackgroundQueue(scheduler ?? throw new ArgumentNullException(nameof(scheduler)));
            settings = new MultiplayerSettingsService(queue, store ?? throw new ArgumentNullException(nameof(store)));
            content = new ContentReadinessPresenter(environment);
            context = new MultiplayerScreensContext(environment, settings, status, content, Ask, Notify);
            connecting = new ConnectingPresenter(context);
            browse = new BrowsePresenter(context, connecting);
            host = new HostPresenter(context);
            connect = new ConnectPresenter(context, connecting);
            session = new SessionPresenter(context);
        }

        public bool IsAvailable => status.IsLiveAvailable;

        public string AvailabilityText => environment.Control == null ? NotInstalled : status.IsLiveAvailable ? string.Empty : SwitchedOff;

        public string StatusText => statusText;

        public ScreenTone StatusTone => statusTone;

        public bool HideMyIp => settings.Current.HideMyIp;

        public string DisplayName => displayName;

        public string DisplayNameError => displayNameError;

        public string SettingsNote => !settings.IsLoaded ? LoadingSettings : settings.IsSaving || queue.IsBusy ? SavingSettings : string.Empty;

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
                while (prompts.Count > 0 && prompts.Peek().IsAnswered) prompts.Dequeue();
                return prompts.Count == 0 ? null : prompts.Peek();
            }
        }

        /// <summary>Gets the shared state, for tests.</summary>
        internal MultiplayerScreensContext Context => context;

        internal MultiplayerSettingsService Settings => settings;

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
            if (!settings.IsLoaded) return;
            settings.Update(settings.Current.WithDisplayName(displayName));
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
            if (selectedTab == MultiplayerSubTab.Host) host.RefreshTargets();
        }

        public void OnHidden()
        {
            shown = false;
            browse.OnHidden();
        }

        /// <summary>Starts loading the settings, so a saved name applies to sessions started without the tab.</summary>
        internal void Start() => settings.Load(Loaded);

        /// <summary>Main thread, every frame.</summary>
        internal void Tick(float deltaTime)
        {
            queue.Pump();
            var failure = settings.TakeFailure();
            if (failure.Length > 0) Notify(failure, ScreenTone.Warning);
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

            // Reading readiness starts identifying the mods, so it waits for a player who opened multiplayer.
            if (shown || connecting.IsBusy) content.Tick();
            connecting.Tick(deltaTime);
            host.Tick();
            connect.Tick();
            session.Tick();
            if (shown || browse.IsSearching) browse.Tick();
            if (status.Version != statusVersion) RebuildStatus();
        }

        private void Loaded(MultiplayerSettings loaded)
        {
            displayName = loaded.DisplayName;
            displayNameError = string.Empty;
            ApplyDisplayName(loaded.DisplayName);
            host.ApplyDefaults(loaded);
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
                    statusText = current.JoinPhase == InternalJoinPhase.Reconnecting ? "Reconnecting to the host..."
                        : server.Length == 0 ? "Joined a session." : "Joined " + server + ".";
                    statusTone = current.JoinPhase == InternalJoinPhase.Reconnecting ? ScreenTone.Warning : ScreenTone.Success;
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
            while (prompts.Count > 0 && prompts.Peek().IsAnswered) prompts.Dequeue();
            if (prompts.Count >= MaximumPrompts)
            {
                prompt.Answer(false);
                return;
            }

            prompts.Enqueue(prompt);
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
