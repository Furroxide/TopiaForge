using System;
using System.Collections.Generic;
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

        public IInternalVoiceService? Voice => null;

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

        public InternalLiveStatus GetStatus()
        {
            Calls.Add("GetStatus");
            return Status;
        }

        public OperationResult<bool> StartHosting(InternalHostSettings settings)
        {
            Calls.Add("StartHosting");
            LastHost = settings;
            return HostResult;
        }

        public void StopHosting() => Calls.Add("StopHosting");

        public bool TryCreateJoinTarget(string text, out IInternalJoinTarget? target)
        {
            target = null;
            var trimmed = text.Trim();
            if (LauncherJoinLink.NormalizeInviteCode(trimmed) is { } code)
            {
                target = new FakeJoinTarget(InternalJoinTargetKind.Invite, "invite " + code);
            }
            else if (LauncherJoinLink.TryParseHostPort(trimmed, out _, out _) || LauncherJoinLink.IsSessionLink(trimmed))
            {
                target = new FakeJoinTarget(InternalJoinTargetKind.Direct, "direct session");
            }

            return target != null;
        }

        public OperationResult<bool> Join(IInternalJoinTarget target, InternalJoinSettings settings)
        {
            Calls.Add("Join:" + target.Kind + ":" + (settings.AllowDirect ? "direct" : "relay"));
            LastJoin = settings;

            // As the provider does, a join that starts reports itself resolving at once, whatever ended before it.
            if (JoinResult.Succeeded) Status = new InternalLiveStatus { Role = Role, IsLiveAvailable = IsLiveAvailable, JoinPhase = InternalJoinPhase.Resolving };
            return JoinResult;
        }

        public OperationResult<bool> SupplyPassword(string password)
        {
            Calls.Add("SupplyPassword");
            return OperationResult<bool>.Success(true);
        }

        public void Leave() => Calls.Add("Leave");

        public IInternalServerQuery Query(IInternalJoinTarget target, bool allowDirect)
        {
            Calls.Add("Query:" + target.Kind + ":" + (allowDirect ? "direct" : "relay"));
            var query = new FakeServerQuery(allowDirect);
            Queries.Add(query);
            return query;
        }

        public void StartBrowsing() => Calls.Add("StartBrowsing");

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
    }

    internal sealed class FakeJoinTarget : IInternalJoinTarget
    {
        internal FakeJoinTarget(InternalJoinTargetKind kind, string description, string? serverId = null)
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

    /// <summary>The game around the screens, scripted by the test.</summary>
    internal sealed class FakeSessionEnvironment : IMultiplayerSessionEnvironment
    {
        internal FakeSessionEnvironment(RecordingSessionControl? control)
        {
            Control = control;
        }

        public IInternalMultiplayerControl? Control { get; set; }

        public MultiplayerSessionSnapshot? SessionSnapshot { get; set; }

        public InternalContentReadiness ContentReadiness { get; set; } = InternalContentReadiness.Ready;

        internal int Retries { get; private set; }

        public string DisplayName { get; set; } = "Player";

        public string? StandaloneModName { get; set; }

        public WorldSessionPhase GamePhase { get; set; } = WorldSessionPhase.Idle;

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

        public void RetryContentIdentification() => Retries++;

        public IReadOnlyList<HostTargetChoice> ReadHostTargets() => Targets.ToArray();

        public void Launch(HostTargetChoice choice, Action<bool, string> done) => PendingLaunch = done;

        public void ReturnToMainMenu(Action<bool, string> done) => PendingMenu = done;

        public void OpenUrl(string url) => Opened.Add(url);

        public void CopyToClipboard(string text) => Copied.Add(text);
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
        internal SessionsRig(bool acknowledged = true, bool liveAvailable = true, bool withControl = true)
        {
            Control = new RecordingSessionControl { IsLiveAvailable = liveAvailable };
            Control.Status.IsLiveAvailable = liveAvailable;
            Environment = new FakeSessionEnvironment(withControl ? Control : null);
            Store = new MemorySettingsStore { Stored = acknowledged ? MultiplayerSettings.Default.AcknowledgeDisclosure() : MultiplayerSettings.Default };
            Presenter = new MultiplayerSessionsPresenter(Environment, Store, Work);
            Presenter.Start();
            Work.RunAll();
            Presenter.OnShown();
            Frame();
        }

        internal RecordingSessionControl Control { get; }

        internal FakeSessionEnvironment Environment { get; }

        internal MemorySettingsStore Store { get; }

        internal ManualWorkScheduler Work { get; } = new ManualWorkScheduler();

        internal MultiplayerSessionsPresenter Presenter { get; }

        internal List<ScreenNotice> Notices { get; } = new List<ScreenNotice>();

        internal ScreenPrompt? Prompt => Presenter.PendingPrompt;

        /// <summary>Runs one frame: the presenter, its finished file work, and the toasts it raised.</summary>
        internal void Frame(int count = 1, float deltaTime = 0.05f)
        {
            for (var index = 0; index < count; index++)
            {
                Work.RunAll();
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
