extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>One allocation on the fake server, as RFC 8656 describes it: a 5-tuple, a relayed address and its peers.</summary>
internal sealed class FakeAllocation
{
    internal FakeAllocation(IPEndPoint client, IPEndPoint serverSide, IPEndPoint relay, string user, long expiresAt)
    {
        Client = client;
        ServerSide = serverSide;
        Relay = relay;
        User = user;
        ExpiresAt = expiresAt;
    }

    internal IPEndPoint Client { get; }

    internal IPEndPoint ServerSide { get; }

    internal IPEndPoint Relay { get; }

    internal string User { get; }

    internal long ExpiresAt { get; set; }

    internal Dictionary<IPAddress, long> Permissions { get; } = new();

    internal Dictionary<ushort, (IPEndPoint Peer, long ExpiresAt)> Channels { get; } = new();

    internal StunTransactionId AllocateId { get; set; }

    internal byte[]? AllocateResponse { get; set; }
}

/// <summary>
/// A STUN and TURN server for tests, sans-I/O. It answers Binding requests, challenges every unauthenticated TURN
/// request with a 401, refuses stale nonces with a 438, and enforces the lifetimes a real server does: allocations by
/// their LIFETIME, permissions five minutes, channels ten, and an expired credential ends its allocations. Knobs fail
/// requests, silence the server or forbid peers, and counters let tests see what the client did.
/// </summary>
internal sealed class FakeTurnServer
{
    private const long PermissionMicros = 300_000_000;
    private const long ChannelMicros = 600_000_000;

    private readonly List<int> ports;
    private readonly Dictionary<string, long> nonces = new();
    private readonly List<FakeAllocation> allocations = new();
    private readonly StunMessageWriter writer = new();
    private readonly Random random;
    private int nextRelayPort = 50_000;
    private int allocated;
    private int nonceCounter;
    private long currentTime;

    internal FakeTurnServer(IPAddress address, int seed, params int[] ports)
    {
        Address = address;
        random = new Random(seed);
        this.ports = ports.Length == 0 ? new List<int> { 3478 } : ports.ToList();
    }

    internal IPAddress Address { get; }

    internal IPEndPoint EndPoint => new(Address, ports[0]);

    internal IReadOnlyList<int> Ports => ports;

    internal string Realm { get; set; } = "relay.example.test";

    /// <summary>Gets the accounts: username to password and the time the credential stops working.</summary>
    internal Dictionary<string, (string Password, long ExpiresAt)> Users { get; } = new();

    internal long NonceLifetimeMicros { get; set; } = long.MaxValue;

    /// <summary>Gets or sets the nonce text every challenge carries, as a server with very long nonces would; numbered nonces otherwise.</summary>
    internal string? NonceText { get; set; }

    internal uint LifetimeSeconds { get; set; } = 600;

    /// <summary>Gets or sets an error code every Allocate meets, such as 486 or 508.</summary>
    internal int? AllocateError { get; set; }

    /// <summary>Gets the error codes the next authenticated Allocates meet, one each, before any are granted.</summary>
    internal Queue<int> AllocateErrors { get; } = new();

    /// <summary>Gets or sets whether the server can verify no credential at all, as with a misconfigured key: every authenticated request meets a 401.</summary>
    internal bool RefuseEveryCredential { get; set; }

    /// <summary>Gets or sets whether every ChannelBind is refused, so data travels as Send and Data indications.</summary>
    internal bool RefuseChannelBinds { get; set; }

    /// <summary>Gets or sets a filter that loses requests on their way to the server: the client's address and the bytes.</summary>
    internal Func<IPEndPoint, byte[], bool>? LoseRequest { get; set; }

    /// <summary>Gets how many zero-lifetime Refreshes, releases, reached the server.</summary>
    internal int Releases { get; private set; }

    /// <summary>Gets how many Allocates met an allocation already on their 5-tuple and drew a 437.</summary>
    internal int Mismatches { get; private set; }

    /// <summary>Forgets every allocation at once, as a server that restarted would.</summary>
    internal void ForgetAllocations() => allocations.Clear();

    /// <summary>Gets or sets whether the server answers nothing at all, as when UDP never reaches it.</summary>
    internal bool Silent { get; set; }

    /// <summary>Gets or sets whether the server only answers Binding requests: a plain STUN server.</summary>
    internal bool StunOnly { get; set; }

    internal HashSet<IPAddress> Forbidden { get; } = new();

    /// <summary>Gets or sets where relayed ports come from; real-socket tests bind a socket for each.</summary>
    internal Func<int>? RelayPorts { get; set; }

    /// <summary>
    /// Gets or sets the address each new allocation relays from, by the allocation's number from zero; the server's own
    /// address by default. Distinct addresses mean a peer's permission for one relay never covers the next.
    /// </summary>
    internal Func<int, IPAddress>? RelayAddresses { get; set; }

    internal Queue<(IPEndPoint From, IPEndPoint To, byte[] Bytes)> Outbox { get; } = new();

    internal IReadOnlyList<FakeAllocation> Allocations => allocations;

    internal int Bindings { get; private set; }

    internal int Challenges { get; private set; }

    internal int StaleNonces { get; private set; }

    internal int AllocateRequests { get; private set; }

    internal int Refreshes { get; private set; }

    internal int PermissionRequests { get; private set; }

    internal int ChannelBinds { get; private set; }

    internal int Keepalives { get; private set; }

    /// <summary>Gets the peer datagrams dropped because their sender held no live permission.</summary>
    internal int Unpermitted { get; private set; }

    internal int Relayed { get; private set; }

    /// <summary>Gets whether <paramref name="destination"/> is one of this server's addresses: a listening port or a relayed address.</summary>
    internal bool Owns(IPEndPoint destination) =>
        (NatAddresses.SameAddress(destination.Address, Address) && ports.Contains(destination.Port)) ||
        allocations.Any(allocation => NatAddresses.SameEndPoint(allocation.Relay, destination));

    internal void Receive(IPEndPoint from, IPEndPoint to, byte[] bytes, long now)
    {
        currentTime = now;
        if (Silent) return;
        Expire(now);
        if (NatAddresses.SameAddress(to.Address, Address) && ports.Contains(to.Port))
        {
            ReceiveFromClient(from, new IPEndPoint(Address, to.Port), bytes, now);
            return;
        }

        var allocation = allocations.FirstOrDefault(candidate => NatAddresses.SameEndPoint(candidate.Relay, to));
        if (allocation == null) return;
        if (!allocation.Permissions.TryGetValue(NatAddresses.Unmap(from.Address), out var expires) || expires <= now)
        {
            Unpermitted++;
            return;
        }

        Relayed++;
        var channel = allocation.Channels.FirstOrDefault(entry => NatAddresses.SameEndPoint(entry.Value.Peer, from) && entry.Value.ExpiresAt > now);
        if (channel.Key != 0)
        {
            var output = new byte[bytes.Length + 4];
            ChannelData.Write(output, channel.Key, bytes, 0, bytes.Length);
            Send(allocation.ServerSide, allocation.Client, output);
            return;
        }

        var buffer = new byte[StunMessage.MaximumBytes];
        writer.Begin(buffer, StunMethod.Data, StunClass.Indication, NewId());
        writer.AddAddress(StunAttribute.XorPeerAddress, from);
        writer.AddBytes(StunAttribute.Data, bytes, 0, bytes.Length);
        Send(allocation.ServerSide, allocation.Client, Trim(buffer, writer.Finish(null, true)));
    }

    /// <summary>Ends allocations whose lifetime or credential ran out, silent or not: silence only stops the answers.</summary>
    internal void Poll(long now)
    {
        currentTime = now;
        Expire(now);
    }

    internal long NextTimeoutMicros()
    {
        var next = long.MaxValue;
        foreach (var allocation in allocations)
        {
            next = Math.Min(next, allocation.ExpiresAt);
            if (Users.TryGetValue(allocation.User, out var user)) next = Math.Min(next, user.ExpiresAt);
        }

        return next;
    }

    private void Expire(long now) =>
        allocations.RemoveAll(allocation => allocation.ExpiresAt <= now || !Users.TryGetValue(allocation.User, out var user) || user.ExpiresAt <= now);

    private void ReceiveFromClient(IPEndPoint from, IPEndPoint serverSide, byte[] bytes, long now)
    {
        if (LoseRequest != null && LoseRequest(from, bytes)) return;
        if (bytes.Length > 0 && bytes[0] >= 0x40 && bytes[0] <= 0x4F)
        {
            var owner = Find(from, serverSide);
            if (owner == null || !ChannelData.TryRead(bytes, 0, bytes.Length, out var number, out var offset, out var length)) return;
            if (!owner.Channels.TryGetValue(number, out var bound) || bound.ExpiresAt <= now) return;
            Send(owner.Relay, bound.Peer, bytes.AsSpan(offset, length).ToArray());
            return;
        }

        if (!StunMessage.TryParse(bytes, 0, bytes.Length, out var message)) return;
        if (message.Method == StunMethod.Binding)
        {
            if (message.Class == StunClass.Indication)
            {
                Keepalives++;
                return;
            }

            if (message.Class != StunClass.Request) return;
            Bindings++;
            writer.Begin(Buffer(out var buffer), StunMethod.Binding, StunClass.Success, message.TransactionId);
            writer.AddAddress(StunAttribute.XorMappedAddress, from);
            Send(serverSide, from, Trim(buffer, writer.Finish(null, true)));
            return;
        }

        if (StunOnly) return;
        var allocation = Find(from, serverSide);
        if (message.Method == StunMethod.Send && message.Class == StunClass.Indication)
        {
            if (allocation == null || !message.TryGetAddress(StunAttribute.XorPeerAddress, out var peer) ||
                !message.TryGetValue(StunAttribute.Data, out var dataOffset, out var dataLength)) return;
            if (!allocation.Permissions.TryGetValue(NatAddresses.Unmap(peer.Address), out var expires) || expires <= now) return;
            Send(allocation.Relay, peer, message.Bytes.AsSpan(dataOffset, dataLength).ToArray());
            return;
        }

        if (message.Class != StunClass.Request) return;
        if (!Authenticate(message, from, serverSide, now, out var key, out var user)) return;

        // Requests on an allocation must carry the credential that made it (RFC 8656, section 5): another one, as a
        // restarted process would have, can neither refresh nor release an allocation it did not make.
        if (allocation != null && message.Method != StunMethod.Allocate && allocation.User != user)
        {
            Error(message, from, serverSide, 441, key, false);
            return;
        }

        switch (message.Method)
        {
            case StunMethod.Allocate:
                Allocate(message, from, serverSide, allocation, key, user!, now);
                break;
            case StunMethod.Refresh:
                Refresh(message, from, serverSide, allocation, key, now);
                break;
            case StunMethod.CreatePermission:
                Permit(message, from, serverSide, allocation, key, now);
                break;
            case StunMethod.ChannelBind:
                Bind(message, from, serverSide, allocation, key, now);
                break;
        }
    }

    private bool Authenticate(StunMessage message, IPEndPoint from, IPEndPoint serverSide, long now, out byte[] key, out string? user)
    {
        key = Array.Empty<byte>();
        user = null;
        if (!message.HasIntegrity)
        {
            Challenges++;
            Error(message, from, serverSide, 401, null, true);
            return false;
        }

        if (RefuseEveryCredential || !message.TryGetText(StunAttribute.Username, 513, out var name) || !Users.TryGetValue(name, out var account) || account.ExpiresAt <= now ||
            !message.TryGetText(StunAttribute.Realm, 763, out var realm) || realm != Realm ||
            !message.TryGetText(StunAttribute.Nonce, 763, out var nonce))
        {
            Error(message, from, serverSide, 401, null, true);
            return false;
        }

        key = StunCredentials.LongTermKey(name, realm, account.Password);
        if (!message.VerifyIntegrity(key))
        {
            Error(message, from, serverSide, 401, null, true);
            return false;
        }

        if (!nonces.TryGetValue(nonce, out var issued) || now - issued >= NonceLifetimeMicros)
        {
            StaleNonces++;
            Error(message, from, serverSide, 438, null, true);
            return false;
        }

        user = name;
        return true;
    }

    private void Allocate(StunMessage message, IPEndPoint from, IPEndPoint serverSide, FakeAllocation? existing, byte[] key, string user, long now)
    {
        AllocateRequests++;
        if (existing != null)
        {
            if (existing.AllocateId.Equals(message.TransactionId) && existing.AllocateResponse != null)
            {
                Send(serverSide, from, existing.AllocateResponse);
            }
            else
            {
                Mismatches++;
                Error(message, from, serverSide, 437, key, false);
            }

            return;
        }

        if (AllocateErrors.Count > 0 || AllocateError is int)
        {
            Error(message, from, serverSide, AllocateErrors.Count > 0 ? AllocateErrors.Dequeue() : AllocateError!.Value, key, false);
            return;
        }

        if (!message.TryGetRequestedTransport(out var protocol) || protocol != 17)
        {
            Error(message, from, serverSide, 442, key, false);
            return;
        }

        var relayPort = RelayPorts?.Invoke() ?? nextRelayPort++;
        var relayAddress = RelayAddresses?.Invoke(allocated++) ?? Address;
        var allocation = new FakeAllocation(from, serverSide, new IPEndPoint(relayAddress, relayPort), user, now + (LifetimeSeconds * 1_000_000L));
        writer.Begin(Buffer(out var buffer), StunMethod.Allocate, StunClass.Success, message.TransactionId);
        writer.AddAddress(StunAttribute.XorRelayedAddress, allocation.Relay);
        writer.AddAddress(StunAttribute.XorMappedAddress, from);
        writer.AddUInt32(StunAttribute.Lifetime, LifetimeSeconds);
        var response = Trim(buffer, writer.Finish(key, true));
        allocation.AllocateId = message.TransactionId;
        allocation.AllocateResponse = response;
        allocations.Add(allocation);
        Send(serverSide, from, response);
    }

    private void Refresh(StunMessage message, IPEndPoint from, IPEndPoint serverSide, FakeAllocation? allocation, byte[] key, long now)
    {
        Refreshes++;
        if (allocation == null)
        {
            Error(message, from, serverSide, 437, key, false);
            return;
        }

        var lifetime = message.TryGetUInt32(StunAttribute.Lifetime, out var requested) ? Math.Min(requested, LifetimeSeconds) : LifetimeSeconds;
        if (lifetime == 0) Releases++;
        if (lifetime == 0) allocations.Remove(allocation);
        else allocation.ExpiresAt = now + (lifetime * 1_000_000L);
        writer.Begin(Buffer(out var buffer), StunMethod.Refresh, StunClass.Success, message.TransactionId);
        writer.AddUInt32(StunAttribute.Lifetime, lifetime);
        Send(serverSide, from, Trim(buffer, writer.Finish(key, true)));
    }

    private void Permit(StunMessage message, IPEndPoint from, IPEndPoint serverSide, FakeAllocation? allocation, byte[] key, long now)
    {
        PermissionRequests++;
        if (allocation == null)
        {
            Error(message, from, serverSide, 437, key, false);
            return;
        }

        var peers = PeerAddresses(message);
        if (peers.Count == 0 || peers.Any(peer => Forbidden.Contains(peer)))
        {
            Error(message, from, serverSide, peers.Count == 0 ? 400 : 403, key, false);
            return;
        }

        foreach (var peer in peers) allocation.Permissions[peer] = now + PermissionMicros;
        writer.Begin(Buffer(out var buffer), StunMethod.CreatePermission, StunClass.Success, message.TransactionId);
        Send(serverSide, from, Trim(buffer, writer.Finish(key, true)));
    }

    private void Bind(StunMessage message, IPEndPoint from, IPEndPoint serverSide, FakeAllocation? allocation, byte[] key, long now)
    {
        ChannelBinds++;
        if (allocation == null)
        {
            Error(message, from, serverSide, 437, key, false);
            return;
        }

        if (!message.TryGetChannelNumber(out var number) || !ChannelData.IsChannel(number) || !message.TryGetAddress(StunAttribute.XorPeerAddress, out var peer))
        {
            Error(message, from, serverSide, 400, key, false);
            return;
        }

        if (RefuseChannelBinds || Forbidden.Contains(NatAddresses.Unmap(peer.Address)))
        {
            Error(message, from, serverSide, 403, key, false);
            return;
        }

        // A number, or a peer, is taken only while its binding lasts (RFC 8656, section 12.2); one that lapsed binds afresh.
        var other = allocation.Channels.FirstOrDefault(entry => NatAddresses.SameEndPoint(entry.Value.Peer, peer) && entry.Key != number && entry.Value.ExpiresAt > now);
        if ((allocation.Channels.TryGetValue(number, out var bound) && bound.ExpiresAt > now && !NatAddresses.SameEndPoint(bound.Peer, peer)) || other.Key != 0)
        {
            Error(message, from, serverSide, 400, key, false);
            return;
        }

        allocation.Channels[number] = (new IPEndPoint(NatAddresses.Unmap(peer.Address), peer.Port), now + ChannelMicros);
        allocation.Permissions[NatAddresses.Unmap(peer.Address)] = now + PermissionMicros;
        writer.Begin(Buffer(out var buffer), StunMethod.ChannelBind, StunClass.Success, message.TransactionId);
        Send(serverSide, from, Trim(buffer, writer.Finish(key, true)));
    }

    private static List<IPAddress> PeerAddresses(StunMessage message)
    {
        // The parser exposes the first occurrence of an attribute; walk the raw bytes for every XOR-PEER-ADDRESS.
        var result = new List<IPAddress>();
        var bytes = message.Bytes;
        var position = 20;
        while (position + 4 <= bytes.Length)
        {
            var type = (bytes[position] << 8) | bytes[position + 1];
            var length = (bytes[position + 2] << 8) | bytes[position + 3];
            if (type == StunAttribute.MessageIntegrity) break;
            if (type == StunAttribute.XorPeerAddress && (length == 8 || length == 20))
            {
                var size = length - 4;
                var address = new byte[size];
                for (var index = 0; index < size; index++) address[index] = (byte)(bytes[position + 8 + index] ^ bytes[4 + index]);
                result.Add(NatAddresses.Unmap(new IPAddress(address)));
            }

            position += 4 + ((length + 3) & ~3);
        }

        return result;
    }

    private void Error(StunMessage message, IPEndPoint from, IPEndPoint serverSide, int code, byte[]? key, bool challenge)
    {
        writer.Begin(Buffer(out var buffer), message.Method, StunClass.Error, message.TransactionId);
        writer.AddErrorCode(code);
        if (challenge)
        {
            writer.AddText(StunAttribute.Realm, Realm);
            writer.AddText(StunAttribute.Nonce, NewNonce());
        }

        Send(serverSide, from, Trim(buffer, writer.Finish(key, true)));
    }

    private string NewNonce()
    {
        var nonce = NonceText ?? "nonce" + (++nonceCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        nonces[nonce] = currentTime;
        return nonce;
    }

    private FakeAllocation? Find(IPEndPoint client, IPEndPoint serverSide) =>
        allocations.FirstOrDefault(allocation => NatAddresses.SameEndPoint(allocation.Client, client) && allocation.ServerSide.Port == serverSide.Port);

    private void Send(IPEndPoint from, IPEndPoint to, byte[] bytes) => Outbox.Enqueue((from, to, bytes));

    private StunTransactionId NewId()
    {
        var bytes = new byte[12];
        random.NextBytes(bytes);
        return StunTransactionId.Read(bytes, 0);
    }

    private static byte[] Buffer(out byte[] buffer) => buffer = new byte[StunMessage.MaximumBytes];

    private static byte[] Trim(byte[] buffer, int length) => buffer.AsSpan(0, length).ToArray();
}
