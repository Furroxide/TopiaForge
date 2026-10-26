using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A multiplayer control for the sessions presenters: it records every call, in order, so a test can prove what was and
    /// was not contacted, and plays back whatever status, listings and query results the test scripts.
    /// </summary>
    internal sealed class RecordingSessionControl : IInternalMultiplayerControl
    {
        internal List<string> Calls { get; } = new List<string>();

        internal InternalLiveStatus Status { get; set; } = new InternalLiveStatus { IsLiveAvailable = true };

        internal IReadOnlyList<InternalLanListing> Lan { get; set; } = Array.Empty<InternalLanListing>();

        internal List<FakeServerQuery> Queries { get; } = new List<FakeServerQuery>();

        internal OperationResult<bool> JoinResult { get; set; } = OperationResult<bool>.Success(true);

        internal OperationResult<bool> HostResult { get; set; } = OperationResult<bool>.Success(true);

        internal InternalJoinSettings? LastJoin { get; private set; }

        internal InternalHostSettings? LastHost { get; private set; }

        internal HashSet<string> Muted { get; } = new HashSet<string>(StringComparer.Ordinal);

        public bool IsLiveAvailable { get; set; } = true;

        public InternalSessionRole Role { get; set; }

        public bool IsReadinessHeld { get; set; }

        /// <summary>Gets or sets the voice service the control hands out; none unless a test gives one.</summary>
        public IInternalVoiceService? Voice { get; set; }

        public IReadOnlyList<InternalLanListing> LanSessions => Lan;

        /// <summary>Gets the calls that reach the network: hosting, joining, checking a server or listening on the local network.</summary>
        internal int NetworkCalls
        {
            get
            {
                var count = 0;
                foreach (var call in Calls)
                {
                    if (call.StartsWith("Join", StringComparison.Ordinal) || call.StartsWith("Query", StringComparison.Ordinal) ||
                        call == "StartHosting" || call == "StartBrowsing") count++;
                }

                return count;
            }
        }

        public bool AttachRuntime(IInternalMultiplayerRuntimeHost runtime) => true;

        public void DetachRuntime(IInternalMultiplayerRuntimeHost runtime)
        {
        }

        public void Pump()
        {
        }

        public void Flush()
        {
        }

        /// <summary>Gets or sets whether a TopiaForge service is configured, as every status this control reports says.</summary>
        internal bool ServiceConfigured { get; set; }

        public InternalLiveStatus GetStatus()
        {
            Calls.Add("GetStatus");
            Status.Internet.ServiceConfigured = ServiceConfigured;
            return Status;
        }

        /// <summary>Gets or sets what hosting throws, as a failing provider might; null to answer <see cref="HostResult"/>.</summary>
        internal Exception? ThrowOnStartHosting { get; set; }

        /// <summary>Gets or sets what joining throws, as a failing provider might; null to answer <see cref="JoinResult"/>.</summary>
        internal Exception? ThrowOnJoin { get; set; }

        /// <summary>Gets the password hosting started with, as the provider read it then; the settings themselves keep none after.</summary>
        internal string? LastHostPassword { get; private set; }

        public OperationResult<bool> StartHosting(InternalHostSettings settings)
        {
            Calls.Add("StartHosting");
            LastHost = settings;
            LastHostPassword = settings.Password;
            if (ThrowOnStartHosting != null) throw ThrowOnStartHosting;
            return HostResult;
        }

        public void StopHosting() => Calls.Add("StopHosting");

        public bool TryCreateJoinTarget(string text, out IInternalJoinTarget? target)
        {
            target = null;
            var trimmed = text.Trim();
            if (LauncherJoinLink.NormalizeInviteCode(trimmed) is { } code)
            {
                target = new ScriptedJoinTarget(InternalJoinTargetKind.Invite, "invite " + code);
            }
            else if (trimmed.StartsWith(LauncherJoinLink.Prefix + "server/", StringComparison.Ordinal) && LauncherJoinLink.IsSessionLink(trimmed))
            {
                target = new ScriptedJoinTarget(InternalJoinTargetKind.ListedServer, "listed server", trimmed.Substring(LauncherJoinLink.Prefix.Length + 7));
            }
            else if (LauncherJoinLink.TryParseHostPort(trimmed, out _, out _) || LauncherJoinLink.IsSessionLink(trimmed))
            {
                target = new ScriptedJoinTarget(InternalJoinTargetKind.Direct, "direct session");
            }

            return target != null;
        }

        public OperationResult<bool> Join(IInternalJoinTarget target, InternalJoinSettings settings)
        {
            Calls.Add("Join:" + target.Kind + ":" + (settings.AllowDirect ? "direct" : "relay"));
            LastJoin = settings;
            if (ThrowOnJoin != null) throw ThrowOnJoin;

            // As the provider does, a join that starts reports itself resolving at once, whatever ended before it.
            if (JoinResult.Succeeded) Status = new InternalLiveStatus { Role = Role, IsLiveAvailable = IsLiveAvailable, JoinPhase = InternalJoinPhase.Resolving };
            return JoinResult;
        }

        public OperationResult<bool> SupplyPassword(string password)
        {
            Calls.Add("SupplyPassword");
            return OperationResult<bool>.Success(true);
        }

        /// <summary>Gets or sets whether leaving throws, as a failing provider might, with an address in its message.</summary>
        internal bool ThrowOnLeave { get; set; }

        public void Leave()
        {
            Calls.Add("Leave");
            if (ThrowOnLeave) throw new InvalidOperationException("the link to 203.0.113.7:27250 could not close");
        }

        public IInternalServerQuery Query(IInternalJoinTarget target, bool allowDirect)
        {
            Calls.Add("Query:" + target.Kind + ":" + (allowDirect ? "direct" : "relay"));
            var query = new FakeServerQuery(allowDirect);
            Queries.Add(query);
            return query;
        }

        /// <summary>Gets or sets what starting to listen on the local network throws, as a failing provider might; null to listen.</summary>
        internal Exception? ThrowOnStartBrowsing { get; set; }

        public void StartBrowsing()
        {
            Calls.Add("StartBrowsing");
            if (ThrowOnStartBrowsing != null) throw ThrowOnStartBrowsing;
        }

        public void StopBrowsing() => Calls.Add("StopBrowsing");

        public OperationResult<bool> Kick(ParticipantId participant, string message)
        {
            Calls.Add("Kick:" + participant.Value);
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> Ban(ParticipantId participant)
        {
            Calls.Add("Ban:" + participant.Value);
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> Unban(string banId) => OperationResult<bool>.Success(true);

        public IReadOnlyList<InternalBanEntry> GetBans() => Array.Empty<InternalBanEntry>();

        public OperationResult<bool> SetServerMute(ParticipantId participant, bool muted)
        {
            Calls.Add((muted ? "Mute:" : "Unmute:") + participant.Value);
            if (muted) Muted.Add(participant.Value);
            else Muted.Remove(participant.Value);
            return OperationResult<bool>.Success(true);
        }

        public bool IsServerMuted(ParticipantId participant) => Muted.Contains(participant.Value);

        public bool IsParticipantReady(ParticipantId participant) => true;

        public OperationResult<bool> ReplaceSession() => OperationResult<bool>.Success(true);

        public OperationResult<IInternalSystemChannel> OpenSystemChannel(InternalSystemChannelKind kind) =>
            OperationResult<IInternalSystemChannel>.Failure(ModErrorCode.Unavailable, "Not in this fake.");

        public IInternalReadinessHold HoldReadiness(string reason) => throw new NotSupportedException();

        public IDisposable SetEntityIdentitySource(IInternalEntityIdentitySource source) => throw new NotSupportedException();

        public IDisposable SetAvatarSource(IInternalAvatarSource source) => throw new NotSupportedException();

        public InternalMultiplayerStatistics GetStatistics() => new InternalMultiplayerStatistics { Role = Role };

        public OperationResult<bool> CreateInvite()
        {
            Calls.Add("CreateInvite");
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> RevokeInvite(string code)
        {
            Calls.Add("RevokeInvite");
            return OperationResult<bool>.Success(true);
        }

        /// <summary>Gets the launch sessions the screens or the runtime asked to plan, in order.</summary>
        internal List<InternalLaunchSession> Planned { get; } = new List<InternalLaunchSession>();

        /// <summary>Gets or sets what planning answers; null plans every session as <see cref="ScriptedLaunchPlan"/> does.</summary>
        internal OperationResult<IInternalLaunchPlan>? PlanResult { get; set; }

        public OperationResult<IInternalLaunchPlan> PlanLaunchSession(InternalLaunchSession session)
        {
            Calls.Add("PlanLaunchSession:" + (session.Host != null ? "host" : "join"));
            Planned.Add(session);
            return PlanResult ?? OperationResult<IInternalLaunchPlan>.Success(new ScriptedLaunchPlan(session));
        }
    }

    /// <summary>A launch plan as a provider makes one: the launch's host settings, or a target named by its kind.</summary>
    internal sealed class ScriptedLaunchPlan : IInternalLaunchPlan
    {
        internal ScriptedLaunchPlan(InternalLaunchSession session)
        {
            IntentId = session.IntentId;
            DisplayName = session.DisplayName;
            AllowDirect = session.AllowDirect;
            var host = session.Host;
            if (host != null)
            {
                Host = new InternalHostSettings(host.Name, host.MaxPlayers, host.Visibility) { AllowDirect = session.AllowDirect, Voice = host.Voice };
                PasswordRequired = host.PasswordRequired;
            }
            else
            {
                Target = new ScriptedJoinTarget(session.Target!.Kind, ConnectPresenter.UnnamedTitle(session.Target.Kind), session.Target.ServerId);
            }
        }

        public string IntentId { get; }

        public string DisplayName { get; }

        public bool AllowDirect { get; }

        public InternalHostSettings? Host { get; }

        public bool PasswordRequired { get; }

        public IInternalJoinTarget? Target { get; }
    }

    /// <summary>A voice service whose transmit state the test sets, or makes throw; the screens read nothing else of it.</summary>
    internal sealed class ScriptedVoiceService : IInternalVoiceService
    {
        internal InternalVoiceTransmitState State { get; set; }

        internal Exception? Failure { get; set; }

        public bool Enabled { get; set; }

        public InternalVoiceTransmitMode Mode { get; set; }

        public float ActivationThresholdDb { get; set; } = -40f;

        public float InputLevelDb => -96f;

        public InternalVoiceTransmitState TransmitState => Failure != null ? throw Failure : State;

        public void SubmitCapture(float[] samples, int count, int sampleRate) => throw new NotSupportedException();

        public void SetTransmitting(bool transmitting) => throw new NotSupportedException();

        public IVoicePlaybackStream GetPlaybackStream(ParticipantId speaker) => throw new NotSupportedException();

        public bool TryGetPlaybackStream(ParticipantId speaker, out IVoicePlaybackStream? stream) => throw new NotSupportedException();

        public bool IsSpeaking(ParticipantId speaker) => false;

        public void SetListenerMutes(IReadOnlyCollection<ParticipantId> muted, bool muteAll = false) => throw new NotSupportedException();

        public bool GetListenerMutes(ICollection<ParticipantId> into) => throw new NotSupportedException();

        public InternalVoiceStatistics GetStatistics() => throw new NotSupportedException();
    }

    internal sealed class ScriptedJoinTarget : IInternalJoinTarget
    {
        internal ScriptedJoinTarget(InternalJoinTargetKind kind, string description, string? serverId = null)
        {
            Kind = kind;
            Description = description;
            ServerId = serverId;
        }

        public InternalJoinTargetKind Kind { get; }

        public string Description { get; }

        public string? ServerId { get; }
    }

    /// <summary>A server check that completes when the test says so.</summary>
    internal sealed class FakeServerQuery : IInternalServerQuery
    {
        internal FakeServerQuery(bool allowDirect)
        {
            AllowDirect = allowDirect;
        }

        internal bool AllowDirect { get; }

        internal bool Disposed { get; private set; }

        public bool IsCompleted { get; private set; }

        public InternalLiveFailure Failure { get; private set; }

        public string Detail => string.Empty;

        public InternalServerInfo? Server { get; private set; }

        public IReadOnlyList<string> ContentProblems { get; private set; } = Array.Empty<string>();

        public string? ChangedServerId { get; private set; }

        internal void Complete(InternalLiveFailure failure, InternalServerInfo? server = null, IReadOnlyList<string>? problems = null, string? changed = null)
        {
            Failure = failure;
            Server = server;
            ContentProblems = problems ?? Array.Empty<string>();
            ChangedServerId = changed;
            IsCompleted = true;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// The game around the screens, scripted by the test, over the real composition root: hosting, joining and leaving go
    /// through <see cref="MultiplayerRuntime"/>'s realm, which reads <see cref="Phase"/> as ModRuntime's points it at the
    /// session orchestrator's <c>RealmPhase</c>, and follows the control's role at each <see cref="Pump"/>.
    /// </summary>
    internal sealed class FakeSessionEnvironment : IMultiplayerSessionEnvironment
    {
        private readonly RecordingSessionControl? control;

        internal FakeSessionEnvironment(RecordingSessionControl? control)
        {
            this.control = control;
            PhaseSource = () => Phase;
            Log = new RecordingRuntimeLog();
            Runtime = new MultiplayerRuntime(() => this.control, new MultiplayerRuntimeHost("2545", "0.2.0", Log, contentScheduler: new ManualWorkScheduler()), Log)
            {
                SessionPhaseSource = () => PhaseSource()
            };
            Runtime.Connect();
        }

        /// <summary>Gets the composition root whose realm the screens ask.</summary>
        internal MultiplayerRuntime Runtime { get; }

        internal RecordingRuntimeLog Log { get; }

        /// <summary>
        /// Gets or sets the local session's phase as the realm rules read it: the orchestrator's <c>RealmPhase</c>, which says
        /// stopping for an idle session that is still changing, as while the main menu loads.
        /// </summary>
        internal SessionPhase Phase { get; set; } = SessionPhase.Idle;

        /// <summary>Gets or sets where the runtime reads that phase: <see cref="Phase"/>, or a real orchestrator's <c>RealmPhase</c>.</summary>
        internal Func<SessionPhase> PhaseSource { get; set; }

        /// <summary>Gets or sets what reading the control throws, as a provider that broke might; null to answer it.</summary>
        internal Exception? ControlFailure { get; set; }

        public IInternalMultiplayerControl? Control => ControlFailure != null ? throw ControlFailure : control;

        public NetRealm Realm => Runtime.Realm;

        public IReadOnlyList<string> StandaloneMods => Runtime.StandaloneMods;

        public MultiplayerSessionSnapshot? SessionSnapshot { get; set; }

        public InternalContentReadiness ContentReadiness { get; set; } = InternalContentReadiness.Ready;

        internal int Retries { get; private set; }

        public string DisplayName { get; set; } = "Player";

        public string CurrentGameText { get; set; } = string.Empty;

        public HostTargetChoice? CurrentGame { get; set; }

        internal List<HostTargetChoice> Targets { get; } = new List<HostTargetChoice>();

        internal List<string> Opened { get; } = new List<string>();

        internal List<string> Copied { get; } = new List<string>();

        internal Action<bool, string>? PendingLaunch { get; private set; }

        internal Action<bool, string>? PendingMenu { get; private set; }

        public string GameBuild => "2545";

        public string RuntimeVersion => "0.2.0";

        public long UnixSeconds => 1_800_000_000;

        public NetRealmTransition EvaluateRealmChange(NetRealm target) => Runtime.EvaluateRealmChange(target);

        public MultiplayerRealmChange OpenToMultiplayer(InternalHostSettings settings) => Runtime.OpenToMultiplayer(settings);

        public MultiplayerRealmChange Join(IInternalJoinTarget target, InternalJoinSettings settings, string? launchRequestId = null) =>
            Runtime.Join(target, settings, launchRequestId);

        public MultiplayerRealmChange Leave() => Runtime.Leave();

        /// <summary>One pump of the composition root, as ModRuntime runs it each frame: the realm follows the control's role.</summary>
        internal void Pump() => Runtime.BeforeModsUpdate(0.016f);

        /// <summary>Loads a package into the runtime's inventory, as ModRuntime records each mod it loads.</summary>
        internal void Load(string id, string? mode = null) => Runtime.Host.Inventory.Add(Seam.Manifest(id, mode), "packages/" + id);

        public void RetryContentIdentification() => Retries++;

        public IReadOnlyList<HostTargetChoice> ReadHostTargets() => Targets.ToArray();

        public void Launch(HostTargetChoice choice, Action<bool, string> done) => PendingLaunch = done;

        public void ReturnToMainMenu(Action<bool, string> done) => PendingMenu = done;

        public void OpenUrl(string url) => Opened.Add(url);

        /// <summary>Gets or sets what the clipboard throws, as a failing system clipboard might; null to take the text.</summary>
        internal Exception? CopyFailure { get; set; }

        public void CopyToClipboard(string text)
        {
            if (CopyFailure != null) throw CopyFailure;
            Copied.Add(text);
        }

        /// <summary>Gets or sets the launcher's channels; null for a game with no staging to share them through.</summary>
        public ILauncherChannels? Launcher { get; set; }

        public SessionPhase LocalPhase => Phase;

        /// <summary>Gets the follow status the connecting overlay reads, which a test moves as a session follower would.</summary>
        public SessionFollowStatus Follow { get; } = new SessionFollowStatus();

        /// <summary>Gets or sets the session this game was launched to host or join, which the screens take once.</summary>
        internal ILaunchedSession? Launched { get; set; }

        public ILaunchedSession? TakeLaunchedSession()
        {
            var launched = Launched;
            Launched = null;
            return launched;
        }

        /// <summary>Gets the subscriptions to the session orchestrator's progress that are open now, by request.</summary>
        internal List<(string RequestId, Action<LaunchProgress> Progress)> ProgressSubscribers { get; } = new List<(string, Action<LaunchProgress>)>();

        public IDisposable SubscribeSessionProgress(string requestId, Action<LaunchProgress> progress)
        {
            var subscriber = (requestId, progress);
            ProgressSubscribers.Add(subscriber);
            return new Unsubscriber(() => ProgressSubscribers.Remove(subscriber));
        }

        /// <summary>Publishes <paramref name="progress"/> as the session orchestrator does, to each open subscription under its request.</summary>
        internal void ReportSessionProgress(LaunchProgress progress)
        {
            foreach (var (requestId, handler) in ProgressSubscribers.ToArray())
                if (string.Equals(requestId, progress.RequestId, StringComparison.Ordinal)) handler(progress);
        }

        private sealed class Unsubscriber : IDisposable
        {
            private Action? release;

            internal Unsubscriber(Action release)
            {
                this.release = release;
            }

            public void Dispose()
            {
                var current = release;
                release = null;
                current?.Invoke();
            }
        }
    }

    /// <summary>
    /// The launcher's channels in memory, recording what the game writes the way the staging store does: a recorded change
    /// appends to the journal under its epoch, dropping what a projection counting it applied and what it supersedes. A test
    /// sets the projection, stages live joins, or makes a write fail; every call notes the thread it ran on.
    /// </summary>
    internal sealed class MemoryLauncherChannels : ILauncherChannels
    {
        internal MemoryLauncherChannels(string? requestId = null, string? packageSetDigest = null, string? serviceOrigin = null)
        {
            RequestId = requestId;
            PackageSetDigest = packageSetDigest;
            ServiceOrigin = serviceOrigin;
        }

        public string? RequestId { get; set; }

        public string? PackageSetDigest { get; set; }

        public string? ServiceOrigin { get; set; }

        internal MultiplayerPreferencesProjection? Projection { get; set; }

        internal MultiplayerPreferencesJournal? Journal { get; set; }

        internal List<SessionHandoff> Handoffs { get; } = new List<SessionHandoff>();

        internal Queue<SessionJoinRequest> Joins { get; } = new Queue<SessionJoinRequest>();

        internal List<LaunchProgress> Progress { get; } = new List<LaunchProgress>();

        internal List<LaunchOutcome> Outcomes { get; } = new List<LaunchOutcome>();

        /// <summary>Gets how many changes were recorded.</summary>
        internal int Records { get; private set; }

        internal bool FailRecord { get; set; }

        internal bool FailHandoff { get; set; }

        /// <summary>Gets the managed thread every call ran on, in order.</summary>
        internal List<int> Threads { get; } = new List<int>();

        public MultiplayerPreferencesProjection? ReadPreferences()
        {
            Note();
            return Projection;
        }

        public MultiplayerPreferencesJournal? ReadPreferencesJournal()
        {
            Note();
            return Journal;
        }

        public MultiplayerPreferencesJournal RecordPreferenceChange(Func<int, MultiplayerPreferenceChange> change)
        {
            Note();
            if (FailRecord) throw new System.IO.IOException("The journal is locked.");
            var journal = Journal ?? MultiplayerPreferencesJournal.Start();
            Journal = journal.Append(change(journal.NextSequence(Projection)), Projection);
            Records++;
            return Journal;
        }

        public void WriteHandoff(SessionHandoff handoff)
        {
            Note();
            if (FailHandoff) throw new System.IO.IOException("The launcher is reading the handoff.");
            Handoffs.Add(handoff);
        }

        /// <summary>Stages a live join, as the launcher writes one; the intake's timer may take it on its own thread.</summary>
        internal void Stage(SessionJoinRequest join)
        {
            lock (Joins) Joins.Enqueue(join);
        }

        public SessionJoinRequest? TakeJoin()
        {
            Note();
            lock (Joins) return Joins.Count == 0 ? null : Joins.Dequeue();
        }

        public void WriteProgress(LaunchProgress progress)
        {
            Note();
            Progress.Add(progress);
        }

        public void WriteOutcome(LaunchOutcome outcome)
        {
            Note();
            Outcomes.Add(outcome);
        }

        private void Note()
        {
            lock (Threads) Threads.Add(System.Environment.CurrentManagedThreadId);
        }
    }

    /// <summary>Settings kept in memory, written through the background queue as the file store is.</summary>
    internal sealed class MemorySettingsStore : IMultiplayerSettingsStore
    {
        internal MultiplayerSettings? Stored { get; set; }

        internal int Saves { get; private set; }

        internal bool FailLoad { get; set; }

        internal bool FailSave { get; set; }

        public MultiplayerSettings Load()
        {
            if (FailLoad) throw new System.IO.InvalidDataException("unreadable");
            return Stored ?? MultiplayerSettings.Default;
        }

        public void Save(MultiplayerSettings settings)
        {
            if (FailSave) throw new System.IO.IOException("disk full");
            Stored = settings;
            Saves++;
        }
    }

    /// <summary>The sessions presenter over the recording control, with file work held until the test runs it.</summary>
    internal sealed class SessionsRig
    {
        /// <param name="settings">What the settings file holds as the game starts, as an earlier run left it; null for defaults.</param>
        /// <param name="launcher">The launcher's channels, shared with an earlier rig to start where it left off; null for new ones.</param>
        /// <param name="channels">Other channels the game uses in place of <paramref name="launcher"/>, such as the staging store's.</param>
        internal SessionsRig(bool acknowledged = true, bool liveAvailable = true, bool withControl = true, bool service = false, MultiplayerSettings? settings = null,
            MemoryLauncherChannels? launcher = null, ILauncherChannels? channels = null)
        {
            Control = new RecordingSessionControl { IsLiveAvailable = liveAvailable, ServiceConfigured = service };
            Control.Status.IsLiveAvailable = liveAvailable;
            Environment = new FakeSessionEnvironment(withControl ? Control : null);
            Launcher = launcher ?? new MemoryLauncherChannels();
            Environment.Launcher = channels ?? Launcher;
            Store = new MemorySettingsStore { Stored = settings ?? (acknowledged ? MultiplayerSettings.Default.AcknowledgeDisclosure() : MultiplayerSettings.Default) };
            Presenter = new MultiplayerSessionsPresenter(Environment, Store, Work);
            Presenter.Start();
            Work.RunAll();
            Presenter.OnShown();
            Frame();
        }

        internal RecordingSessionControl Control { get; }

        internal FakeSessionEnvironment Environment { get; }

        /// <summary>Gets the launcher's channels the presenter writes its preferences and handoffs to.</summary>
        internal MemoryLauncherChannels Launcher { get; }

        internal MemorySettingsStore Store { get; }

        internal ManualWorkScheduler Work { get; } = new ManualWorkScheduler();

        internal MultiplayerSessionsPresenter Presenter { get; }

        internal List<ScreenNotice> Notices { get; } = new List<ScreenNotice>();

        internal ScreenPrompt? Prompt => Presenter.PendingPrompt;

        /// <summary>Runs one frame: the composition root's pump, the presenter, its finished file work, and the toasts it raised.</summary>
        internal void Frame(int count = 1, float deltaTime = 0.05f)
        {
            for (var index = 0; index < count; index++)
            {
                Work.RunAll();
                Environment.Pump();
                Presenter.Tick(deltaTime);
                while (Presenter.TryTakeNotice(out var notice)) Notices.Add(notice);
            }
        }

        /// <summary>Answers the pending prompt, which must be of <paramref name="kind"/>.</summary>
        internal void Answer(ScreenPromptKind kind, bool yes)
        {
            var prompt = Prompt ?? throw new InvalidOperationException("Assertion failed: a " + kind + " prompt is pending");
            if (prompt.Kind != kind) throw new InvalidOperationException("Assertion failed: the pending prompt is " + prompt.Kind + ", not " + kind);
            prompt.Answer(yes);
            Frame();
        }

        internal bool Noticed(string text)
        {
            foreach (var notice in Notices)
            {
                if (notice.Text.IndexOf(text, StringComparison.Ordinal) >= 0) return true;
            }

            return false;
        }

        /// <summary>Puts the control in a state, as if its status changed: role, phases and failure.</summary>
        internal void Status(InternalSessionRole role, InternalJoinPhase join = InternalJoinPhase.None, InternalLiveFailure failure = InternalLiveFailure.None,
            InternalHostPhase host = InternalHostPhase.Idle, InternalAdmissionCode code = InternalAdmissionCode.Other)
        {
            Control.Role = role;
            Control.Status = new InternalLiveStatus
            {
                Role = role,
                IsLiveAvailable = Control.IsLiveAvailable,
                JoinPhase = join,
                JoinFailure = failure,
                HostPhase = host,
                RejectionCode = code,
                MaxPlayers = 8,
                ConnectedPlayers = 1
            };
            Presenter.Context.Status.RequestRefresh();
        }

        internal static MultiplayerParticipant Player(string id, string name, string? key, bool local = false, bool host = false, bool connected = true, int? ping = 40) =>
            new MultiplayerParticipant(new ParticipantId(id), name, local, connected, host, MultiplayerConnectionQuality.Good, ping,
                key == null ? default : new MultiplayerPlayerKey(key));

        /// <summary>A host's session snapshot in <paramref name="state"/>, with this game as the host p0.</summary>
        internal static MultiplayerSessionSnapshot HostSnapshot(MultiplayerSessionState state, string session, params MultiplayerParticipant[] others)
        {
            var participants = new List<MultiplayerParticipant> { Player("p0", "Sam", null, local: true, host: true) };
            participants.AddRange(others);
            return new MultiplayerSessionSnapshot(new MultiplayerSessionId(session), state, MultiplayerProcessKind.Interactive,
                MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server, new ParticipantId("p0"), participants, new NetworkTick(1), new SessionSeed(7),
                new ParticipantId("p0"), MultiplayerSessionEndReason.None);
        }

        /// <summary>A joined player's snapshot in <paramref name="state"/>, with the host as p0 and this game as <paramref name="self"/>.</summary>
        internal static MultiplayerSessionSnapshot ClientSnapshot(MultiplayerSessionState state, string session, string self, params MultiplayerParticipant[] others)
        {
            var participants = new List<MultiplayerParticipant> { Player("p0", "Ada", "host-key", host: true), Player(self, "Sam", "my-key", local: true) };
            participants.AddRange(others);
            return new MultiplayerSessionSnapshot(new MultiplayerSessionId(session), state, MultiplayerProcessKind.Interactive,
                MultiplayerExecutionSide.Client, new ParticipantId(self), participants, new NetworkTick(1), new SessionSeed(7),
                new ParticipantId("p0"), MultiplayerSessionEndReason.None);
        }
    }

    internal static class SessionAssert
    {
        internal static void That(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }

        internal static void Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Assertion failed: " + message + " (expected " + typeof(TException).Name + ")");
        }
    }
}
