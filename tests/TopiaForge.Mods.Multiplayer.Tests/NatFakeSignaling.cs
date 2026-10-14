extern alias provider;

using System.Net;
using System.Text;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The control plane's signaling room for tests, on the virtual clock: welcome, joined, left and host-status as the
/// service sends them, forwarding with the peer id added for the host and removed for peers, error frames, close codes
/// and a peer lifetime. It reads what clients send as strictly as the service does. The service strips non-relay
/// candidates from relay-only sides; <see cref="Strip"/> turns that off, so a test can prove the client withholds on its own.
/// Every frame a client sent is kept as text.
/// </summary>
internal sealed class FakeSignalingService : IVirtualAttachment
{
    private readonly List<(long At, long Order, Action Run)> due = new();
    private readonly Dictionary<string, FakeSignalingClient> peers = new(StringComparer.Ordinal);
    private FakeSignalingClient? host;
    private long order;

    /// <summary>Gets the lock every entry point takes, so network threads in real-socket tests can share the room.</summary>
    internal object Gate { get; } = new();

    internal FakeSignalingService(long latencyMicros = 20_000)
    {
        LatencyMicros = latencyMicros;
    }

    internal long LatencyMicros { get; }

    internal long Now { get; private set; }

    internal bool Strip { get; set; } = true;

    internal long PeerLifetimeMicros { get; set; } = 15L * 60 * 1_000_000;

    internal bool HostRelayOnly { get; private set; }

    /// <summary>Gets every frame clients sent, with the side that sent it.</summary>
    internal List<(SignalRole Role, string? Peer, string Frame)> Sent { get; } = new();

    internal int Withheld { get; private set; }

    /// <summary>Gets or sets a filter that swallows frames clients send, as a broken or hostile side would.</summary>
    internal Func<SignalMessage, bool>? Swallow { get; set; }

    /// <summary>Gets how many sockets each side opened, counting reconnections.</summary>
    internal int HostConnections { get; private set; }

    internal int PeerConnections { get; private set; }

    internal int Invalid { get; private set; }

    internal FakeSignalingClient ConnectHost(bool relayOnly)
    {
        lock (Gate) return OpenHost(relayOnly);
    }

    internal FakeSignalingClient ConnectPeer(string peerId, bool relayOnly)
    {
        lock (Gate) return OpenPeer(peerId, relayOnly);
    }

    private FakeSignalingClient OpenHost(bool relayOnly)
    {
        var client = new FakeSignalingClient(this, SignalRole.Host, null, relayOnly);
        HostConnections++;
        Later(() =>
        {
            host?.CloseBy(4409);
            host = client;
            HostRelayOnly = relayOnly;
            client.Open();
            client.Deliver(new SignalMessage(SignalType.Welcome) { Role = SignalRole.Host, Peers = peers.Values.Select(peer => new SignalPeer(peer.PeerId!, peer.RelayOnly)).ToList() });
            foreach (var peer in peers.Values) peer.Deliver(new SignalMessage(SignalType.HostStatus) { Online = true, RelayOnly = relayOnly });
        });
        return client;
    }

    private FakeSignalingClient OpenPeer(string peerId, bool relayOnly)
    {
        var client = new FakeSignalingClient(this, SignalRole.Peer, peerId, relayOnly);
        PeerConnections++;
        Later(() =>
        {
            if (peers.TryGetValue(peerId, out var older)) older.CloseBy(4409);
            peers[peerId] = client;
            client.Open();
            client.Deliver(new SignalMessage(SignalType.Welcome) { Role = SignalRole.Peer, Peer = peerId, Online = host != null, RelayOnly = HostRelayOnly });
            host?.Deliver(new SignalMessage(SignalType.Joined) { Peer = peerId, RelayOnly = relayOnly });
            var expiresAt = Now + PeerLifetimeMicros;
            At(expiresAt, () =>
            {
                if (peers.TryGetValue(peerId, out var current) && ReferenceEquals(current, client)) client.CloseBy(4411);
            });
        });
        return client;
    }

    /// <summary>Takes one frame a client sent, as the service would: strictly, with privacy enforced, then forwarded.</summary>
    internal void Submit(FakeSignalingClient from, byte[] frame)
    {
        lock (Gate)
        {
            Sent.Add((from.Role, from.PeerId, Encoding.UTF8.GetString(frame)));
            Later(() => Handle(from, frame));
        }
    }

    internal void Closed(FakeSignalingClient client)
    {
        lock (Gate) Later(() =>
        {
            if (client.Role == SignalRole.Peer && client.PeerId != null && peers.TryGetValue(client.PeerId, out var current) && ReferenceEquals(current, client))
            {
                peers.Remove(client.PeerId);
                host?.Deliver(new SignalMessage(SignalType.Left) { Peer = client.PeerId });
            }
            else if (client.Role == SignalRole.Host && ReferenceEquals(host, client))
            {
                host = null;
                foreach (var peer in peers.Values) peer.Deliver(new SignalMessage(SignalType.HostStatus) { Online = false, RelayOnly = client.RelayOnly });
            }
        });
    }

    /// <summary>Delivers a frame of the service's own to a peer, as the service would.</summary>
    internal void SendToPeer(string peerId, SignalMessage message)
    {
        lock (Gate)
        {
            Later(() =>
            {
                if (peers.TryGetValue(peerId, out var peer)) peer.Deliver(message);
            });
        }
    }

    /// <summary>Ends the host's session: every socket closes with 4410.</summary>
    internal void EndSession()
    {
        foreach (var peer in peers.Values.ToList()) peer.CloseBy(4410);
        host?.CloseBy(4410);
    }

    public void Receive(TransportPath source, TransportPath destination, byte[] datagram, long now)
    {
    }

    public void Poll(long now)
    {
        lock (Gate) Run(now);
    }

    private void Run(long now)
    {
        Now = now;
        while (true)
        {
            var next = due.Where(item => item.At <= now).OrderBy(item => item.At).ThenBy(item => item.Order).FirstOrDefault();
            if (next.Run == null) break;
            due.Remove(next);
            next.Run();
        }
    }

    public long NextTimeoutMicros()
    {
        lock (Gate) return due.Count == 0 ? long.MaxValue : due.Min(item => item.At);
    }

    public bool TryDequeue(out TransportPath? source, out TransportPath destination, out byte[] datagram)
    {
        source = null;
        destination = null!;
        datagram = null!;
        return false;
    }

    private void Handle(FakeSignalingClient from, byte[] frame)
    {
        if (from.State == SignalingState.Closed) return;
        if (Swallow != null && SignalCodec.TryDecode(frame, false, out var swallowed) && Swallow(swallowed)) return;
        var addressed = from.Role == SignalRole.Host;
        if (!SignalCodec.TryDecode(frame, true, out var message) || (message.Peer != null) != addressed ||
            !(message.Type is SignalType.Offer or SignalType.Answer or SignalType.Candidate or SignalType.Punch or SignalType.Bye) ||
            (message.Type == SignalType.Offer && addressed) || (message.Type == SignalType.Answer && !addressed))
        {
            Invalid++;
            from.Deliver(new SignalMessage(SignalType.Error) { Error = SignalErrorCode.InvalidMessage });
            return;
        }

        FakeSignalingClient? target = addressed ? (peers.TryGetValue(message.Peer!, out var peer) ? peer : null) : host;
        if (target == null)
        {
            from.Deliver(new SignalMessage(SignalType.Error) { Error = addressed ? SignalErrorCode.UnknownPeer : SignalErrorCode.HostOffline });
            return;
        }

        var offered = message.Type == SignalType.Candidate ? new List<SignalCandidate> { message.Candidate! } : message.Candidates.ToList();
        var kept = offered.Where(candidate => !Strip || !from.RelayOnly || Relayable(candidate)).ToList();
        if (kept.Count < offered.Count)
        {
            Withheld += offered.Count - kept.Count;
            from.Deliver(new SignalMessage(SignalType.Notice) { Withheld = offered.Count - kept.Count });
        }

        if (message.Type == SignalType.Candidate && kept.Count == 0) return;
        var forwarded = new SignalMessage(message.Type)
        {
            Peer = addressed ? null : from.PeerId,
            Ice = message.Ice,
            Candidates = kept,
            Candidate = message.Type == SignalType.Candidate ? kept[0] : null,
            DelayMs = message.DelayMs,
            Reason = message.Reason
        };
        target.Deliver(forwarded);
        if (message.Type == SignalType.Bye) (addressed ? target : from).CloseBy(1000);
    }

    private static bool Relayable(SignalCandidate candidate) =>
        candidate.Kind == IceCandidateKind.Relayed && NatAddresses.TryParseCanonical(candidate.Address, out var address) && NatAddresses.IsPublic(address);

    private void Later(Action run) => At(Now + LatencyMicros, run);

    internal void At(long at, Action run) => due.Add((at, ++order, run));
}

/// <summary>One side's socket to the fake service. Frames travel through the real codec both ways.</summary>
internal sealed class FakeSignalingClient : ISignalingClient
{
    private readonly FakeSignalingService service;
    private readonly Queue<SignalMessage> inbound = new();
    private int state = (int)SignalingState.Connecting;

    internal FakeSignalingClient(FakeSignalingService service, SignalRole role, string? peerId, bool relayOnly)
    {
        this.service = service;
        Role = role;
        PeerId = peerId;
        RelayOnly = relayOnly;
    }

    internal SignalRole Role { get; }

    internal string? PeerId { get; }

    internal bool RelayOnly { get; }

    public SignalingState State => (SignalingState)state;

    public SignalingCloseReason CloseReason { get; private set; }

    public bool TrySend(SignalMessage message)
    {
        if (State == SignalingState.Closed) return false;
        service.Submit(this, SignalCodec.Encode(message));
        return true;
    }

    public bool TryReceive(out SignalMessage message)
    {
        lock (service.Gate) return inbound.TryDequeue(out message!);
    }

    public void Close()
    {
        lock (service.Gate)
        {
            if (State == SignalingState.Closed) return;
            service.At(service.Now + service.LatencyMicros + 1, () => CloseBy(1000));
        }
    }

    public void Dispose() => CloseBy(1000);

    internal void Open()
    {
        if (State == SignalingState.Connecting) state = (int)SignalingState.Open;
    }

    /// <summary>Delivers a frame the way the wire would: encoded by the service, decoded by the client's codec.</summary>
    internal void Deliver(SignalMessage message)
    {
        if (State == SignalingState.Closed) return;
        var bytes = message.Type is SignalType.Offer or SignalType.Answer or SignalType.Candidate or SignalType.Punch or SignalType.Bye
            ? SignalCodec.Encode(message)
            : ServiceFrame(message);
        if (SignalCodec.TryDecode(bytes, false, out var decoded)) inbound.Enqueue(decoded);
    }

    internal void CloseBy(int code)
    {
        lock (service.Gate) Shut(code);
    }

    private void Shut(int code)
    {
        if (State == SignalingState.Closed) return;
        state = (int)SignalingState.Closed;
        CloseReason = code switch
        {
            1000 => SignalingCloseReason.Normal,
            4409 => SignalingCloseReason.Replaced,
            4410 => SignalingCloseReason.SessionEnded,
            4411 => SignalingCloseReason.Expired,
            _ => SignalingCloseReason.Invalid
        };
        service.Closed(this);
    }

    /// <summary>Writes the frames only the service sends, in its canonical form.</summary>
    private static byte[] ServiceFrame(SignalMessage message)
    {
        var node = new provider::TopiaForge.Multiplayer.Network.JsonObjectNode();
        switch (message.Type)
        {
            case SignalType.Welcome when message.Role == SignalRole.Host:
                node.Add("type", "welcome").Add("role", "host");
                node.Add("peers", new provider::TopiaForge.Multiplayer.Network.JsonArrayNode(message.Peers.Select(peer =>
                    (provider::TopiaForge.Multiplayer.Network.JsonNode)new provider::TopiaForge.Multiplayer.Network.JsonObjectNode().Add("peer", peer.Peer).Add("relayOnly", peer.RelayOnly)).ToList()));
                break;
            case SignalType.Welcome:
                node.Add("type", "welcome").Add("role", "peer").Add("peer", message.Peer).Add("hostOnline", message.Online).Add("hostRelayOnly", message.RelayOnly);
                break;
            case SignalType.Joined:
                node.Add("type", "joined").Add("peer", message.Peer).Add("relayOnly", message.RelayOnly);
                break;
            case SignalType.Left:
                node.Add("type", "left").Add("peer", message.Peer);
                break;
            case SignalType.HostStatus:
                node.Add("type", "host-status").Add("online", message.Online).Add("relayOnly", message.RelayOnly);
                break;
            case SignalType.Notice:
                node.Add("type", "notice").Add("code", "candidates-withheld").Add("withheld", message.Withheld);
                break;
            default:
                node.Add("type", "error").Add("code", message.Error switch
                {
                    SignalErrorCode.HostOffline => "host-offline",
                    SignalErrorCode.UnknownPeer => "unknown-peer",
                    SignalErrorCode.RateLimited => "rate-limited",
                    _ => "invalid-message"
                });
                break;
        }

        return provider::TopiaForge.Multiplayer.Network.CanonicalJson.Serialize(node);
    }
}
