using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>
    /// A multiplayer provider's control as ModManager's composition root sees it, recording each frame call. The
    /// ModRuntime and ModManager harnesses both compile it.
    /// </summary>
    internal sealed class FakeMultiplayerControl : IInternalMultiplayerControl
    {
        private readonly Action<string> trace;

        internal FakeMultiplayerControl(Action<string>? trace = null)
        {
            this.trace = trace ?? (_ => { });
        }

        internal IInternalMultiplayerRuntimeHost? Runtime { get; private set; }

        internal int Pumps { get; private set; }

        internal int Flushes { get; private set; }

        internal int Detaches { get; private set; }

        internal bool RefuseAttach { get; set; }

        internal Exception? PumpFailure { get; set; }

        internal Exception? FlushFailure { get; set; }

        /// <summary>Gets each realm request the provider received, in order: <c>host</c>, <c>join</c>, <c>leave</c> and <c>stop-hosting</c>.</summary>
        internal List<string> RealmCalls { get; } = new List<string>();

        /// <summary>Gets or sets what hosting answers; it succeeds by default.</summary>
        internal OperationResult<bool> HostingResult { get; set; } = OperationResult<bool>.Success(true);

        /// <summary>Gets or sets what a join answers; it succeeds by default.</summary>
        internal OperationResult<bool> JoinResult { get; set; } = OperationResult<bool>.Success(true);

        /// <summary>Gets or sets the host phase the status reports; by default Hosting for a listen host and Idle otherwise.</summary>
        internal InternalHostPhase? HostPhase { get; set; }

        /// <summary>Gets or sets a failure leaving throws, as a broken provider would.</summary>
        internal Exception? LeaveFailure { get; set; }

        public bool IsLiveAvailable { get; set; } = true;

        public InternalSessionRole Role { get; set; }

        public bool IsReadinessHeld => false;

        public IInternalVoiceService? Voice => null;

        public IReadOnlyList<InternalLanListing> LanSessions => Array.Empty<InternalLanListing>();

        /// <summary>Gets a session of this process's player alone, the same instance each time.</summary>
        public MultiplayerSessionSnapshot SessionSnapshot => SoloSnapshot.Instance;

        public bool AttachRuntime(IInternalMultiplayerRuntimeHost runtime)
        {
            if (RefuseAttach || Runtime != null) return ReferenceEquals(Runtime, runtime);
            Runtime = runtime;
            trace("mp-attach");
            return true;
        }

        public void DetachRuntime(IInternalMultiplayerRuntimeHost runtime)
        {
            if (!ReferenceEquals(Runtime, runtime)) return;
            Runtime = null;
            Detaches++;
            trace("mp-detach");
        }

        public void Pump()
        {
            Pumps++;
            trace("mp-pump");
            if (PumpFailure != null) throw PumpFailure;
        }

        public void Flush()
        {
            Flushes++;
            trace("mp-flush");
            if (FlushFailure != null) throw FlushFailure;
        }

        public InternalLiveStatus GetStatus() => new InternalLiveStatus
        {
            Role = Role,
            IsLiveAvailable = IsLiveAvailable,
            HostPhase = HostPhase ?? (Role == InternalSessionRole.ListenHost ? InternalHostPhase.Hosting : InternalHostPhase.Idle)
        };

        public OperationResult<bool> StartHosting(InternalHostSettings settings)
        {
            RealmCalls.Add("host");
            return HostingResult;
        }

        public void StopHosting()
        {
            RealmCalls.Add("stop-hosting");
            if (LeaveFailure != null) throw LeaveFailure;
        }

        public bool TryCreateJoinTarget(string text, out IInternalJoinTarget? target)
        {
            target = null;
            return false;
        }

        public OperationResult<bool> Join(IInternalJoinTarget target, InternalJoinSettings settings)
        {
            RealmCalls.Add("join");
            return JoinResult;
        }

        public OperationResult<bool> SupplyPassword(string password) => Unsupported<bool>();

        public void Leave()
        {
            RealmCalls.Add("leave");
            if (LeaveFailure != null) throw LeaveFailure;
        }

        public IInternalServerQuery Query(IInternalJoinTarget target, bool allowDirect) => throw new NotSupportedException();

        public void StartBrowsing()
        {
        }

        public void StopBrowsing()
        {
        }

        public OperationResult<bool> Kick(ParticipantId participant, string message) => Unsupported<bool>();

        public OperationResult<bool> Ban(ParticipantId participant) => Unsupported<bool>();

        public OperationResult<bool> Unban(string banId) => Unsupported<bool>();

        public IReadOnlyList<InternalBanEntry> GetBans() => Array.Empty<InternalBanEntry>();

        public OperationResult<bool> SetServerMute(ParticipantId participant, bool muted) => Unsupported<bool>();

        public bool IsServerMuted(ParticipantId participant) => false;

        public bool IsParticipantReady(ParticipantId participant) => false;

        public OperationResult<bool> ReplaceSession() => Unsupported<bool>();

        public OperationResult<IInternalSystemChannel> OpenSystemChannel(InternalSystemChannelKind kind) => Unsupported<IInternalSystemChannel>();

        public IInternalReadinessHold HoldReadiness(string reason) => throw new NotSupportedException();

        public IDisposable SetEntityIdentitySource(IInternalEntityIdentitySource source) => throw new NotSupportedException();

        public IDisposable SetAvatarSource(IInternalAvatarSource source) => throw new NotSupportedException();

        public InternalMultiplayerStatistics GetStatistics() => new InternalMultiplayerStatistics { Role = Role };

        public OperationResult<bool> CreateInvite() => Unsupported<bool>();

        public OperationResult<bool> RevokeInvite(string code) => Unsupported<bool>();

        public OperationResult<IInternalLaunchPlan> PlanLaunchSession(InternalLaunchSession session) => Unsupported<IInternalLaunchPlan>();

        private static OperationResult<T> Unsupported<T>() where T : notnull =>
            OperationResult<T>.Failure(ModErrorCode.Unavailable, "The fake control does not do this.");
    }

    /// <summary>A join target as a provider would make one: an opaque handle that shows only what it is.</summary>
    internal sealed class FakeJoinTarget : IInternalJoinTarget
    {
        public InternalJoinTargetKind Kind => InternalJoinTargetKind.Lan;

        public string Description => "LAN session";

        public string? ServerId => null;

        public override string ToString() => Description;
    }

    /// <summary>A frame stage that records when it runs.</summary>
    internal sealed class RecordingFrameStage : IMultiplayerFrameStage
    {
        private readonly Action<string> trace;
        private readonly string name;

        internal RecordingFrameStage(string name, Action<string> trace)
        {
            this.name = name;
            this.trace = trace;
        }

        internal Exception? Failure { get; set; }

        public void Run(float deltaTime)
        {
            trace(name);
            if (Failure != null) throw Failure;
        }
    }

    /// <summary>A game engine's background setting, recorded.</summary>
    internal sealed class RecordingMultiplayerPlatform : IMultiplayerPlatform
    {
        internal RecordingMultiplayerPlatform(bool initial)
        {
            value = initial;
        }

        internal List<bool> Writes { get; } = new List<bool>();

        public bool RunInBackground
        {
            get => value;
            set
            {
                this.value = value;
                Writes.Add(value);
            }
        }

        private bool value;
    }

    /// <summary>A session of one player, this process's own, for fakes whose session nobody joins.</summary>
    internal static class SoloSnapshot
    {
        internal static readonly MultiplayerSessionSnapshot Instance = new MultiplayerSessionSnapshot(
            new MultiplayerSessionId("solo"),
            MultiplayerSessionState.Ready,
            MultiplayerProcessKind.Interactive,
            MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server,
            new ParticipantId("local"),
            new[] { new MultiplayerParticipant(new ParticipantId("local"), "Player", true, true) },
            new NetworkTick(0),
            new SessionSeed(1));
    }

    /// <summary>Records what the multiplayer runtime logs.</summary>
    internal sealed class RecordingRuntimeLog : IMultiplayerLog
    {
        internal List<string> Lines { get; } = new List<string>();

        public void Info(string message) => Lines.Add("info: " + message);

        public void Warn(string message) => Lines.Add("warn: " + message);

        public void Error(string message) => Lines.Add("error: " + message);
    }
}
