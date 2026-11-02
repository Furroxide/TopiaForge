using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

// Only the runtime control surface is adapted. Channels and snapshots come from the production engine.
internal sealed class PresenceEngineControl(SessionEngine engine) : IInternalMultiplayerControl, IInternalCommunicationControl, IInternalCommunicationActivityControl
{
    internal PresenceInbox? Inbox { get; private set; }
    public bool HasCommunicationRouting => engine.HasCommunicationRouting;
    public bool CanCommunicate(ParticipantId sender, ParticipantId listener) => engine.CanCommunicate(sender, listener);
    public OperationResult<ICommunicationRouting> RouteCommunications(IModLifetime lifetime, CommunicationPolicy policy) => engine.RouteCommunications(lifetime, policy);
    public bool IsVoiceActive(ParticipantId sender) => engine.IsVoiceActive(sender);
    public bool HasVoiceActivity(ParticipantId sender, ParticipantId listener) => engine.HasVoiceActivity(sender, listener);
    public void ClearVoiceActivity() => engine.ClearVoiceActivity();
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
    public IDisposable SetAvatarSource(IInternalAvatarSource source) => engine.SetAvatarSource(source);
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
    internal readonly Queue<InternalSystemDatagram> InjectedDatagrams = new();
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
    internal bool RecordPoses;
    internal ParticipantId? DropDatagramsTo;
    internal int DroppedDatagrams;
    private readonly Dictionary<ParticipantId, byte[]> datagrams = new();
    internal byte[] LastDatagramTo(ParticipantId listener) => datagrams[listener];
    private readonly Dictionary<(ParticipantId Listener, byte Index), PresenceFlags> sentFlags = new();
    internal PresenceFlags? LastWireFlags(ParticipantId listener, byte index) => sentFlags.TryGetValue((listener, index), out var flags) ? flags : null;
    public OperationResult<int> SendDatagram(ArraySegment<byte> payload, InternalSystemTarget target)
    {
        if (DropDatagramsTo.HasValue && target.Participant.Equals(DropDatagramsTo.Value))
        { DroppedDatagrams++; return OperationResult<int>.Success(0); }
        if (RecordPoses && target.Participant.IsValid)
        {
            datagrams[target.Participant] = payload.ToArray();
            var reader = new NetBitReader();
            if (TopiaForge.ModManager.PresenceWire.TryReadDatagramType(payload, reader, out var type)
                && type == TopiaForge.ModManager.PresenceMessages.Poses
                && TopiaForge.ModManager.PresenceWire.TryReadPosesHeader(reader, out _, out var count))
                for (var i = 0; i < count; i++)
                    if (TopiaForge.ModManager.PresenceWire.TryReadRelay(reader, out var pose))
                        sentFlags[(target.Participant, pose.Index)] = pose.Flags & (PresenceFlags.Talking | PresenceFlags.Typing);
        }
        return inner.SendDatagram(payload, target);
    }
    public bool TryReceiveDatagram(out InternalSystemDatagram datagram) =>
        InjectedDatagrams.TryDequeue(out datagram) || inner.TryReceiveDatagram(out datagram);
    public InternalSystemChannelStatistics GetStatistics() => inner.GetStatistics();
    public void Dispose() => inner.Dispose();
}
