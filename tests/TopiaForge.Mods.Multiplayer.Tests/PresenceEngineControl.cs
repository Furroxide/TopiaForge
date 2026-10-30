using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

// Only the runtime control surface is adapted. Channels and snapshots come from the production engine.
internal sealed class PresenceEngineControl(SessionEngine engine) : IInternalMultiplayerControl
{
    internal PresenceInbox? Inbox { get; private set; }
    public bool IsLiveAvailable => true;
    public InternalSessionRole Role => engine.Role == EngineRole.Host ? InternalSessionRole.ListenHost : InternalSessionRole.Client;
    public bool IsReadinessHeld => false;
    public IInternalVoiceService? Voice => null;
    public IReadOnlyList<InternalLanListing> LanSessions => Array.Empty<InternalLanListing>();
    public MultiplayerSessionSnapshot SessionSnapshot => engine.Snapshot;
    public OperationResult<IInternalSystemChannel> OpenSystemChannel(InternalSystemChannelKind kind)
    {
        var result = engine.OpenSystemChannel(kind);
        if (!result.Succeeded) return result;
        Inbox = new PresenceInbox(result.Value!);
        return OperationResult<IInternalSystemChannel>.Success(Inbox);
    }
    public IDisposable SetAvatarSource(IInternalAvatarSource source) => new EmptyLease();
    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    public bool AttachRuntime(IInternalMultiplayerRuntimeHost runtime) => throw new NotSupportedException();
    public void DetachRuntime(IInternalMultiplayerRuntimeHost runtime) => throw new NotSupportedException();
    public void Pump() => throw new NotSupportedException();
    public void Flush() => throw new NotSupportedException();
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

// Inject only at the receive boundary for negative tests; honest traffic retains its engine envelope.
internal sealed class PresenceInbox(IInternalSystemChannel inner) : IInternalSystemChannel
{
    internal readonly Queue<InternalSystemMessage> Injected = new();
    internal readonly List<ParticipantId> ReceivedSenders = new();
    public InternalSystemChannelKind Kind => inner.Kind;
    public bool IsOpen => inner.IsOpen;
    public ulong Generation => inner.Generation;
    public ulong GetGeneration(ParticipantId peer) => inner.GetGeneration(peer);
    public bool CanSend(InternalSystemTarget target, int payloadBytes) => inner.CanSend(target, payloadBytes);
    public OperationResult<int> Send(ArraySegment<byte> payload, InternalSystemTarget target, ICollection<ParticipantId>? skipped = null) => inner.Send(payload, target, skipped);
    public bool TryReceive(out InternalSystemMessage message)
    {
        if (Injected.TryDequeue(out message)) return true;
        if (!inner.TryReceive(out message)) return false;
        ReceivedSenders.Add(message.Sender);
        return true;
    }
    public OperationResult<int> SendDatagram(ArraySegment<byte> payload, InternalSystemTarget target) => inner.SendDatagram(payload, target);
    public bool TryReceiveDatagram(out InternalSystemDatagram datagram) => inner.TryReceiveDatagram(out datagram);
    public InternalSystemChannelStatistics GetStatistics() => inner.GetStatistics();
    public void Dispose() => inner.Dispose();
}
