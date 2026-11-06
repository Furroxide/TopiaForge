using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

// Only the runtime control surface is adapted. Channels and snapshots come from the production engine.
internal sealed class ReferenceEngineControl(SessionEngine engine) : IInternalMultiplayerControl
{
    internal ReferenceChannel? Views;
    internal MultiplayerSessionSnapshot? SnapshotOverride;
    public bool IsLiveAvailable => true;
    public InternalSessionRole Role => engine.Role == EngineRole.Host ? InternalSessionRole.ListenHost : InternalSessionRole.Client;
    public bool IsReadinessHeld => false;
    public IInternalVoiceService? Voice => null;
    public IReadOnlyList<InternalLanListing> LanSessions => Array.Empty<InternalLanListing>();
    public MultiplayerSessionSnapshot SessionSnapshot => SnapshotOverride ?? engine.Snapshot;
    public OperationResult<IInternalSystemChannel> OpenSystemChannel(InternalSystemChannelKind kind)
    {
        var result = engine.OpenSystemChannel(kind);
        if (!result.Succeeded) return result;
        if (kind == InternalSystemChannelKind.PerPlayer)
        {
            Views = new ReferenceChannel(result.Value!);
            return OperationResult<IInternalSystemChannel>.Success(Views);
        }
        return result;
    }
    public IDisposable SetAvatarSource(IInternalAvatarSource source) => engine.SetAvatarSource(source);
    public bool AttachRuntime(IInternalMultiplayerRuntimeHost runtime) => true;
    public void DetachRuntime(IInternalMultiplayerRuntimeHost runtime) { }
    public void Pump() { }
    public void Flush() => engine.FlushOutbound();
    public InternalLiveStatus GetStatus() => throw new NotSupportedException();
    public OperationResult<bool> StartHosting(InternalHostSettings settings) => throw new NotSupportedException();
    public void StopHosting() => throw new NotSupportedException();
    public bool TryCreateJoinTarget(string text, out IInternalJoinTarget? target) => throw new NotSupportedException();
    public OperationResult<bool> Join(IInternalJoinTarget target, InternalJoinSettings settings) => throw new NotSupportedException();
    public OperationResult<bool> SupplyPassword(string password) => throw new NotSupportedException();
    public void Leave() => throw new NotSupportedException();
    public IInternalServerQuery Query(IInternalJoinTarget target, bool allowDirect) => throw new NotSupportedException();
    public void StartBrowsing() => throw new NotSupportedException();
    public void StopBrowsing() => throw new NotSupportedException();
    public OperationResult<bool> Kick(ParticipantId participant, string message) => throw new NotSupportedException();
    public OperationResult<bool> Ban(ParticipantId participant) => throw new NotSupportedException();
    public OperationResult<bool> Unban(string banId) => throw new NotSupportedException();
    public IReadOnlyList<InternalBanEntry> GetBans() => throw new NotSupportedException();
    public OperationResult<bool> SetServerMute(ParticipantId participant, bool muted) => throw new NotSupportedException();
    public bool IsServerMuted(ParticipantId participant) => throw new NotSupportedException();
    public bool IsParticipantReady(ParticipantId participant) => throw new NotSupportedException();
    public OperationResult<bool> ReplaceSession() => throw new NotSupportedException();
    public IInternalReadinessHold HoldReadiness(string reason) => throw new NotSupportedException();
    public IDisposable SetEntityIdentitySource(IInternalEntityIdentitySource source) => throw new NotSupportedException();
    public InternalMultiplayerStatistics GetStatistics() => throw new NotSupportedException();
    public OperationResult<bool> CreateInvite() => throw new NotSupportedException();
    public OperationResult<bool> RevokeInvite(string code) => throw new NotSupportedException();
    public OperationResult<IInternalLaunchPlan> PlanLaunchSession(InternalLaunchSession session) => throw new NotSupportedException();
}

// Only backpressure is controlled; honest payloads and sender/generation attribution stay engine-owned.
internal sealed class ReferenceChannel(IInternalSystemChannel inner) : IInternalSystemChannel
{
    internal int SendLimit = int.MaxValue;
    internal int Sent;
    internal int Begins;
    internal void Frame() => Sent = 0;
    public InternalSystemChannelKind Kind => inner.Kind;
    public bool IsOpen => inner.IsOpen;
    public ulong Generation => inner.Generation;
    public ulong GetGeneration(ParticipantId peer) => inner.GetGeneration(peer);
    public bool CanSend(InternalSystemTarget target, int bytes) => Sent < SendLimit && inner.CanSend(target, bytes);
    public OperationResult<int> Send(ArraySegment<byte> bytes, InternalSystemTarget target, ICollection<ParticipantId>? skipped = null)
    {
        if (Sent >= SendLimit) return OperationResult<int>.Success(0);
        var result = inner.Send(bytes, target, skipped);
        if (result.Succeeded && result.Value > 0)
        {
            Sent++;
            if (bytes.Count > 1 && bytes.Array![bytes.Offset + 1] == 1) Begins++;
        }
        return result;
    }
    public bool TryReceive(out InternalSystemMessage message) => inner.TryReceive(out message);
    public OperationResult<int> SendDatagram(ArraySegment<byte> bytes, InternalSystemTarget target) => inner.SendDatagram(bytes, target);
    public bool TryReceiveDatagram(out InternalSystemDatagram message) => inner.TryReceiveDatagram(out message);
    public InternalSystemChannelStatistics GetStatistics() => inner.GetStatistics();
    public void Dispose() => inner.Dispose();
}
