extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Crypto;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using ProviderLink = provider::TopiaForge.Multiplayer.Engine.IEngineLink;
using ProviderLinkState = provider::TopiaForge.Multiplayer.Engine.EngineLinkState;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Seeded randomness for endpoints, so connection ids, ephemeral keys and cookie secrets repeat run to run.</summary>
internal sealed class SeededTransportRandom : ITransportRandom
{
    private readonly Random random;

    internal SeededTransportRandom(int seed)
    {
        random = new Random(seed);
    }

    public void Fill(byte[] buffer) => random.NextBytes(buffer);
}

internal sealed class RecordingTransportDiagnostics : ITransportDiagnostics
{
    internal List<(TransportDiagnosticKind Kind, long Connection, string Message)> Reports { get; } = new();

    public void Report(TransportDiagnosticKind kind, long connectionNumber, string message) => Reports.Add((kind, connectionNumber, message));

    internal int Count(TransportDiagnosticKind kind) => Reports.Count(report => report.Kind == kind);
}

/// <summary>Two endpoints on one virtual network: a host that accepts and a client that connects.</summary>
internal sealed class TransportPair
{
    internal TransportPair(int seed, Action<TransportOptions>? configureHost = null, Action<TransportOptions>? configureClient = null)
    {
        Network = new VirtualDatagramNetwork(seed);
        HostOptions = Options(seed * 31 + 1, HostDiagnostics, configureHost);
        ClientOptions = Options(seed * 31 + 2, ClientDiagnostics, configureClient);
        HostKey = Key(seed, 1);
        ClientKey = Key(seed, 2);
        Host = new TransportEndpoint(HostKey, HostOptions, Network.Now);
        Client = new TransportEndpoint(ClientKey, ClientOptions, Network.Now);
        HostNode = Network.AddEndpoint("host", Host);
        ClientNode = Network.AddEndpoint("client", Client);
    }

    internal VirtualDatagramNetwork Network { get; }

    internal TransportOptions HostOptions { get; }

    internal TransportOptions ClientOptions { get; }

    internal byte[] HostKey { get; }

    internal byte[] ClientKey { get; }

    internal TransportEndpoint Host { get; }

    internal TransportEndpoint Client { get; }

    internal VirtualNode HostNode { get; }

    internal VirtualNode ClientNode { get; }

    internal RecordingTransportDiagnostics HostDiagnostics { get; } = new();

    internal RecordingTransportDiagnostics ClientDiagnostics { get; } = new();

    /// <summary>Connects the client over <paramref name="kind"/> and runs until the host accepts; returns both ends.</summary>
    internal (TransportConnection Client, TransportConnection Host) Establish(
        byte[]? pin = null,
        long maximumMicros = 10_000_000,
        TransportPathKind kind = TransportPathKind.Direct)
    {
        var client = Client.Connect(VirtualDatagramNetwork.PathTo(HostNode, kind), Network.Now, pin);
        TransportConnection? host = null;
        var done = Network.RunUntil(
            () => client.State == TransportConnectionState.Established && (host != null || Host.TryAccept(out host)),
            maximumMicros);
        TransportAssert.That(done && host != null, "the handshake completes and the host accepts the connection");
        return (client, host!);
    }

    internal static byte[] Key(int seed, int role)
    {
        var key = new byte[32];
        new Random(seed * 7919 + role).NextBytes(key);
        return key;
    }

    private static TransportOptions Options(int seed, RecordingTransportDiagnostics diagnostics, Action<TransportOptions>? configure)
    {
        // The pair connects over direct paths, which a test chooses as a player who allowed direct play would.
        var options = new TransportOptions { Random = new SeededTransportRandom(seed), Diagnostics = diagnostics, RelayOnly = false };
        configure?.Invoke(options);
        return options;
    }
}

/// <summary>Records each frame of the packets a raw peer opens.</summary>
internal sealed class RecordingFrameSink : IFrameSink
{
    internal int Pings { get; private set; }

    internal List<PacketRange> LastAck { get; } = new();

    internal List<CloseCode> Closes { get; } = new();

    internal List<(ulong Offset, byte[] Data)> Streams { get; } = new();

    internal List<ulong> Windows { get; } = new();

    internal List<(byte Channel, ulong Sequence, byte[] Payload)> Datagrams { get; } = new();

    public void OnPing() => Pings++;

    public void OnAck(List<PacketRange> ranges, ulong ackDelayMicros)
    {
        LastAck.Clear();
        LastAck.AddRange(ranges);
    }

    public void OnClose(CloseCode code) => Closes.Add(code);

    public bool OnStream(ulong offset, byte[] data)
    {
        Streams.Add((offset, data));
        return true;
    }

    public void OnWindow(ulong maximumOffset) => Windows.Add(maximumOffset);

    public void OnDatagram(byte channel, ulong sequence, byte[] payload) => Datagrams.Add((channel, sequence, payload));

    internal List<byte[]> Challenges { get; } = new();

    internal List<byte[]> Responses { get; } = new();

    public void OnPathChallenge(byte[] data) => Challenges.Add(data);

    public void OnPathResponse(byte[] data) => Responses.Add(data);
}

/// <summary>
/// A peer that speaks the transport by hand with the provider's own building blocks, so a test can send exactly the
/// frames it wants, including ones a correct peer never sends.
/// </summary>
internal sealed class RawTransportPeer
{
    private readonly VirtualDatagramNetwork network;
    private readonly byte[] staticKey;
    private NoiseHandshakeXX? noise;
    private CipherState? send;
    private CipherState? receive;
    private ulong nextPacketNumber;

    internal RawTransportPeer(VirtualDatagramNetwork network, string name, int seed)
    {
        this.network = network;
        Node = network.AddRaw(name);
        staticKey = TransportPair.Key(seed, 9);
        LocalId = 0x0807060504030201UL ^ (ulong)seed;
    }

    internal VirtualNode Node { get; }

    internal ulong LocalId { get; }

    internal ulong RemoteId { get; private set; }

    internal RecordingFrameSink Received { get; } = new();

    /// <summary>Runs the handshake against <paramref name="target"/> by hand, advertising <paramref name="window"/>.</summary>
    internal void Connect(VirtualNode target, ulong window = 4 * 1024 * 1024)
    {
        BeginConnect(target);
        FinishConnect(target, window);
    }

    /// <summary>Sends the Initiation only, leaving the responder holding a half-open handshake.</summary>
    internal void BeginConnect(VirtualNode target)
    {
        noise = new NoiseHandshakeXX(true, staticKey, HandshakePackets.Prologue(LocalId));
        var initiation = new byte[HandshakePackets.InitiationSize];
        var writer = new PacketWriter();
        writer.Reset(initiation, 0, initiation.Length);
        HandshakePackets.WriteInitiation(writer, LocalId, null, noise.WriteMessage(Array.Empty<byte>()));
        network.Inject(Node, target, initiation);
    }

    /// <summary>Reads the Response and sends the Completion.</summary>
    internal void FinishConnect(VirtualNode target, ulong window = 4 * 1024 * 1024)
    {
        var writer = new PacketWriter();
        var response = Await(PacketType.Response);
        ulong responderId = 0;
        var noise = this.noise!;
        var read = HandshakePackets.TryReadAddressed(response, 0, response.Length, PacketType.Response, out var receiverId, out var message2) &&
            receiverId == LocalId && noise.TryReadMessage(message2, out var payload) &&
            HandshakePayloads.TryReadResponse(payload, out responderId, out _);
        TransportAssert.That(read, "the raw peer reads the Response");
        RemoteId = responderId;
        var message3 = noise.WriteMessage(HandshakePayloads.WriteCompletion(window));
        var keys = noise.Split();
        noise.Dispose();
        this.noise = null;
        send = keys.Send;
        receive = keys.Receive;
        var completion = new byte[1 + HandshakePackets.ConnectionIdSize + message3.Length];
        writer.Reset(completion, 0, completion.Length);
        HandshakePackets.WriteAddressed(writer, PacketType.Completion, RemoteId, message3);
        network.Inject(Node, target, completion);
    }

    /// <summary>Seals whatever frames <paramref name="write"/> produces into the next protected packet, sent over <paramref name="kind"/>.</summary>
    internal byte[] Send(VirtualNode target, Action<PacketWriter> write, TransportPathKind kind = TransportPathKind.Direct)
    {
        var plaintext = new byte[ProtectedPacket.MaximumFramesBytes];
        var writer = new PacketWriter();
        writer.Reset(plaintext, 0, plaintext.Length);
        write(writer);
        var number = nextPacketNumber++;
        var datagram = new byte[ProtectedPacket.Overhead + writer.Written];
        ProtectedPacket.WriteHeader(datagram, RemoteId, number);
        var header = datagram.AsSpan(0, ProtectedPacket.HeaderSize).ToArray();
        send!.Seal(number, header, plaintext, 0, writer.Written, datagram, ProtectedPacket.HeaderSize);
        network.Inject(Node, target, datagram, kind);
        return datagram;
    }

    /// <summary>Gets the size of every protected datagram <see cref="ReadAll"/> opened.</summary>
    internal List<int> OpenedSizes { get; } = new();

    /// <summary>Opens every protected packet that has arrived and records its frames.</summary>
    internal void ReadAll()
    {
        while (Node.Inbox.Count > 0)
        {
            var (_, bytes) = Node.Inbox.Dequeue();
            if (bytes[0] != PacketType.Protected || receive == null) continue;
            var plaintext = new byte[bytes.Length];
            var header = bytes.AsSpan(0, ProtectedPacket.HeaderSize).ToArray();
            var number = ProtectedPacket.ReadPacketNumber(bytes, 0);
            if (!receive.TryOpen(number, header, bytes, ProtectedPacket.HeaderSize, bytes.Length - ProtectedPacket.HeaderSize, plaintext, 0, out var length)) continue;
            OpenedSizes.Add(bytes.Length);
            FrameReader.Read(plaintext, 0, length, Received, new List<PacketRange>());
        }
    }

    private byte[] Await(byte type)
    {
        byte[]? found = null;
        network.RunUntil(
            () =>
            {
                while (found == null && Node.Inbox.Count > 0)
                {
                    var (_, bytes) = Node.Inbox.Dequeue();
                    if (bytes[0] == type) found = bytes;
                }

                return found != null;
            },
            5_000_000);
        return found ?? throw new InvalidOperationException("Assertion failed: a datagram of type " + type + " arrives");
    }
}

/// <summary>
/// A session engine's view of a transport connection, on one thread as the tests run them. The live provider puts the
/// network thread between the two.
/// </summary>
internal sealed class TransportEngineLink : ProviderLink
{
    private readonly TransportConnection connection;

    internal TransportEngineLink(TransportConnection connection)
    {
        this.connection = connection;
        PeerIdentity = Convert.ToHexString(connection.RemoteStaticKey).ToLowerInvariant();
    }

    public string PeerIdentity { get; }

    public MultiplayerPlayerKey PlayerKey => default;

    public byte[] ChannelBinding => connection.HandshakeHash;

    public ProviderLinkState State => connection.State != TransportConnectionState.Established
        ? ProviderLinkState.Closed
        : connection.IsStalled ? ProviderLinkState.Stalled : ProviderLinkState.Open;

    public void Send(ProviderChannel channel, ArraySegment<byte> frame) => connection.Send(channel, frame.Array!, frame.Offset, frame.Count);

    public bool TryReceive(out ProviderChannel channel, out byte[] frame) => connection.TryReceive(out channel, out frame);

    public void Close() => connection.Close();
}

internal static class TransportFrames
{
    /// <summary>Gets a frame whose every byte depends on its position and <paramref name="seed"/>, so any corruption shows.</summary>
    internal static byte[] Pattern(int size, int seed)
    {
        var frame = new byte[size];
        for (var index = 0; index < size; index++) frame[index] = (byte)((index * 31) + (seed * 17) + (index >> 8));
        return frame;
    }

    /// <summary>Moves every session frame a connection holds into <paramref name="frames"/>; datagrams go to <paramref name="datagrams"/>.</summary>
    internal static void Drain(TransportConnection connection, List<byte[]> frames, List<(ProviderChannel Channel, byte[] Payload)>? datagrams = null)
    {
        while (connection.TryReceive(out var channel, out var frame))
        {
            if (channel == ProviderChannel.Session) frames.Add(frame);
            else datagrams?.Add((channel, frame));
        }
    }
}

/// <summary>Reads the key material the transport keeps in private fields, so tests can see it zeroed.</summary>
internal static class TransportSecrets
{
    internal static CipherState KeyOf(TransportConnection connection, string field) => (CipherState)Field(connection, field);

    internal static bool IsZero(object owner, string field) => ((byte[])Field(owner, field)).All(value => value == 0);

    internal static object Field(object owner, string name)
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null) return field.GetValue(owner) ?? throw new InvalidOperationException("Field " + name + " is null.");
        }

        throw new InvalidOperationException("No field " + name + " on " + owner.GetType().Name + ".");
    }
}

internal static class TransportAssert
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

        throw new InvalidOperationException("Assertion failed: " + message);
    }
}
