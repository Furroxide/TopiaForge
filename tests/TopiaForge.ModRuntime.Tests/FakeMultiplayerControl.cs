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

        public bool IsLiveAvailable => true;

        public InternalSessionRole Role { get; set; }

        public bool IsReadinessHeld => false;

        public IInternalVoiceService? Voice => null;

        public IReadOnlyList<InternalLanListing> LanSessions => Array.Empty<InternalLanListing>();

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

        public InternalLiveStatus GetStatus() => new InternalLiveStatus { Role = Role, IsLiveAvailable = true };

        public OperationResult<bool> StartHosting(InternalHostSettings settings) => Unsupported<bool>();

        public void StopHosting()
        {
        }

        public bool TryCreateJoinTarget(string text, out IInternalJoinTarget? target)
        {
            target = null;
            return false;
        }

        public OperationResult<bool> Join(IInternalJoinTarget target, InternalJoinSettings settings) => Unsupported<bool>();

        public OperationResult<bool> SupplyPassword(string password) => Unsupported<bool>();

        public void Leave()
        {
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

        private static OperationResult<T> Unsupported<T>() where T : notnull =>
            OperationResult<T>.Failure(ModErrorCode.Unavailable, "The fake control does not do this.");
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

    /// <summary>Records what the multiplayer runtime logs.</summary>
    internal sealed class RecordingRuntimeLog : IMultiplayerLog
    {
        internal List<string> Lines { get; } = new List<string>();

        public void Info(string message) => Lines.Add("info: " + message);

        public void Warn(string message) => Lines.Add("warn: " + message);

        public void Error(string message) => Lines.Add("error: " + message);
    }
}
