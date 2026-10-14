extern alias provider;

using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>A network stack on the deterministic network, behind the same control the game's network thread offers.</summary>
internal sealed class SteppedNetwork : INetworkControl
{
    private readonly VirtualDatagramNetwork network;
    private readonly NetworkStack stack;
    private readonly bool retireWhenStopped;

    internal SteppedNetwork(VirtualDatagramNetwork network, string name, NetworkStack stack, int port, bool retireWhenStopped = false)
    {
        this.network = network;
        this.stack = stack;
        this.retireWhenStopped = retireWhenStopped;
        LocalPort = port;
        Node = network.AddStack(name, stack);
    }

    internal VirtualNode Node { get; }

    internal NetworkStack Stack => stack;

    internal bool Stopped { get; private set; }

    /// <summary>Makes the next event say the network gave up, as the game's network thread does after repeated faults.</summary>
    internal bool FailNextEvent { get; set; }

    public int LocalPort { get; }

    public ConnectAttempt Connect(TransportPath path, byte[]? expectedKey)
    {
        var attempt = new ConnectAttempt();
        stack.Connect(attempt, path, expectedKey, network.Now);
        return attempt;
    }

    public void Post(Action<NetworkStack, long> command) => command(stack, network.Now);

    public bool TryTakeEvent(out NetworkEvent networkEvent)
    {
        if (FailNextEvent)
        {
            FailNextEvent = false;
            networkEvent = NetworkEvent.Ended();
            return true;
        }

        if (stack.TryAccept(out var link))
        {
            networkEvent = NetworkEvent.Accepted(link);
            return true;
        }

        if (stack.Endpoint.TryReceiveInfo(out var reply))
        {
            networkEvent = NetworkEvent.Received(reply);
            return true;
        }

        networkEvent = null!;
        return false;
    }

    public void Attach(INetworkService service) => service.Dispose();

    public void Detach(INetworkService service) => service.Dispose();

    /// <summary>
    /// Closes the links gracefully; the virtual network keeps carrying the closes. A network that retires when stopped
    /// then leaves the virtual network once the grace period ends, its stack disposed, as the network thread does.
    /// </summary>
    public void Stop(long gracefulMicros)
    {
        if (Stopped) return;
        Stopped = true;
        stack.CloseAll();
        if (retireWhenStopped) network.Retire(Node, network.Now + Math.Max(0, gracefulMicros));
    }

    public void Dispose() => Stop(0);
}

/// <summary>Creates stepped networks on one virtual network; hosts get no TCP or LAN services there.</summary>
internal sealed class SteppedNetworkFactory : INetworkFactory
{
    private readonly VirtualDatagramNetwork network;
    private readonly string name;
    private int created;

    internal SteppedNetworkFactory(VirtualDatagramNetwork network, string name)
    {
        this.network = network;
        this.name = name;
    }

    internal List<SteppedNetwork> Networks { get; } = new();

    /// <summary>Gets or sets whether starting a network fails, as binding a socket can.</summary>
    internal bool FailStarts { get; set; }

    /// <summary>
    /// Gets or sets a seed for the transport's randomness. The flows build their endpoints with the platform's generator;
    /// with a seed, each endpoint this factory starts draws connection ids, ephemeral keys, path challenges and query ids
    /// from its own seeded source instead, before it has made a connection, so a run repeats every byte.
    /// </summary>
    internal int? TransportSeed { get; set; }

    /// <summary>Gets or sets where every endpoint this factory starts reports transport events.</summary>
    internal ITransportDiagnostics? TransportDiagnostics { get; set; }

    /// <summary>Gets or sets whether a stopped network leaves the virtual network after its grace period.</summary>
    internal bool RetireStopped { get; set; }

    public INetworkControl Start(int port, NetworkStack stack)
    {
        if (FailStarts) throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressFamilyNotSupported);
        ++created;
        if (TransportSeed is int seed) stack.Endpoint.Options.Random = new SeededTransportRandom((seed * 7_919) + created);
        if (TransportDiagnostics != null) stack.Endpoint.Options.Diagnostics = TransportDiagnostics;
        var started = new SteppedNetwork(network, name + "-" + created, stack, port == 0 ? 40_000 + created : port, RetireStopped);
        Networks.Add(started);
        return started;
    }

    public DescriptorEndpoint? CreateDescriptorEndpoint(int port, bool relayOnly) => null;

    public LanAnnouncer? CreateAnnouncer(LanBeacon beacon) => null;

    public LanBrowser? CreateBrowser(int discoveryPort) => null;
}

/// <summary>
/// Resolves every target to one host's virtual node and the descriptor it signed, the way the real resolver would after
/// fetching and verifying it. The path kind decides whether relay-only lets the join through.
/// </summary>
internal sealed class SteppedResolver : IJoinTargetResolver
{
    internal Func<(VirtualNode Node, SignedServerDescriptor Descriptor)?>? Host { get; set; }

    internal TransportPathKind Kind { get; set; } = TransportPathKind.Lan;

    internal ResolvedTarget? Override { get; set; }

    /// <summary>Gets whether the last join asked to allow direct paths; the join itself refuses one it was not allowed.</summary>
    internal bool? LastAllowDirect { get; private set; }

    /// <summary>Gets the server the last join said the player trusts at a changed address, if any.</summary>
    internal string? LastTrustedServerId { get; private set; }

    public Task<ResolvedTarget> ResolveAsync(JoinTarget target, bool allowDirect, string? trustedServerId, CancellationToken cancellationToken)
    {
        LastAllowDirect = allowDirect;
        LastTrustedServerId = trustedServerId;
        if (Override != null) return Task.FromResult(Override);
        var host = Host?.Invoke();
        return Task.FromResult(host == null
            ? ResolvedTarget.Failed(LiveFailure.NotFound, "No host is running.")
            : ResolvedTarget.Found(VirtualDatagramNetwork.PathTo(host.Value.Node, Kind), host.Value.Descriptor));
    }
}

/// <summary>Content that reports a fixed build and can be told to find packages missing.</summary>
internal sealed class TestContent : ILiveSessionContent
{
    internal TestContent(string gameBuild = "2545")
    {
        GameBuild = gameBuild;
    }

    public string GameBuild { get; }

    public string RuntimeVersion => "0.2.0";

    internal IReadOnlyList<string> Missing { get; set; } = Array.Empty<string>();

    public IReadOnlyList<DescriptorPackage> HostPackages() => Array.Empty<DescriptorPackage>();

    public byte[] ClientContent() => new byte[] { 1, 2, 3 };

    public provider::TopiaForge.Multiplayer.Engine.JoinDecision? Admit(byte[] clientContent) =>
        clientContent.SequenceEqual(new byte[] { 1, 2, 3 })
            ? null
            : provider::TopiaForge.Multiplayer.Engine.JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "Unknown content.");

    public ContentCheck Check(ServerDescriptor descriptor) => Missing.Count == 0 ? ContentCheck.Ready : new ContentCheck(Missing);
}

/// <summary>An in-memory data folder.</summary>
internal sealed class MemoryDataFiles : INetworkDataFiles
{
    internal Dictionary<string, byte[]> Contents { get; } = new();

    /// <summary>Gets or sets whether reads fail, as on a damaged or locked disk.</summary>
    internal bool FailReads { get; set; }

    /// <summary>Gets the files whose reads fail, while the rest read normally.</summary>
    internal HashSet<string> FailReadsOf { get; } = new();

    /// <summary>Gets or sets what each write waits for before it lands, so a test can make one slow.</summary>
    internal Func<string, Task>? BeforeWrite { get; set; }

    public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (FailReads || FailReadsOf.Contains(relativePath)) return Task.FromException<byte[]?>(new IOException("The disk could not be read."));
        lock (Contents) return Task.FromResult(Contents.TryGetValue(relativePath, out var bytes) ? (byte[]?)bytes.ToArray() : null);
    }

    public async Task WriteAsync(string relativePath, byte[] content, CancellationToken cancellationToken)
    {
        var copy = content.ToArray();
        var gate = BeforeWrite;
        if (gate != null) await gate(relativePath).ConfigureAwait(false);

        // Like the SDK's mod files, a cancelled write is refused.
        cancellationToken.ThrowIfCancellationRequested();
        lock (Contents) Contents[relativePath] = copy;
    }
}

/// <summary>A host provider and as many joining providers as a test needs, all on one deterministic network.</summary>
internal sealed class LiveRig : IDisposable
{
    internal const string ProviderId = "io.github.furroxide.topiaforge.multiplayer";

    internal LiveRig(int seed, Action<TestContent>? configureClient = null)
    {
        Network = new VirtualDatagramNetwork(seed);
        Host = NewProvider("host", HostFiles, new TestContent(), out var hostFactory);
        HostFactory = hostFactory;
        Resolver.Host = () => Host.Live!.Host?.Descriptor is { } descriptor && HostFactory.Networks.Count > 0
            ? (HostFactory.Networks[^1].Node, descriptor)
            : null;
    }

    internal VirtualDatagramNetwork Network { get; }
    internal MemoryDataFiles HostFiles { get; } = new();
    internal Provider Host { get; }
    internal SteppedNetworkFactory HostFactory { get; }
    internal SteppedResolver Resolver { get; } = new();
    internal List<Provider> Clients { get; } = new();

    internal Dictionary<Provider, SteppedNetworkFactory> Factories { get; } = new();

    private readonly Dictionary<Provider, MemoryDataFiles> clientFiles = new();

    /// <summary>Gets the data folder of a client the rig added.</summary>
    internal MemoryDataFiles ClientFiles(Provider client) => clientFiles[client];

    internal Provider AddClient(string name, TestContent? content = null)
    {
        var files = new MemoryDataFiles();
        var client = NewProvider(name, files, content ?? new TestContent(), out var factory);
        clientFiles[client] = files;
        Clients.Add(client);
        Factories[client] = factory;
        return client;
    }

    /// <summary>Gets the virtual node of the network a client's latest join runs on.</summary>
    internal VirtualNode NodeOf(Provider client) => Factories[client].Networks[^1].Node;

    /// <summary>Runs the network and pumps every provider until <paramref name="condition"/> holds or virtual time runs out.</summary>
    internal bool Run(Func<bool> condition, long maximumMicros = 10_000_000)
    {
        var deadline = Network.Now + maximumMicros;
        var spins = 0;
        while (Network.Now < deadline)
        {
            Host.Pump();
            foreach (var client in Clients) client.Pump();
            if (condition()) return true;

            // Identity loading and password keys finish on background threads; give them a moment of real time.
            if (++spins % 20 == 0) Thread.Sleep(1);
            Network.RunFor(5_000);
        }

        return condition();
    }

    /// <summary>Opens the host's session and waits until it is serving.</summary>
    internal void StartHosting(HostSettings settings)
    {
        Host.Live!.StartHosting(settings);
        if (!Run(() => Host.Live.HostPhase == HostPhase.Hosting || Host.Live.HostPhase == HostPhase.Failed))
        {
            throw new InvalidOperationException("Assertion failed: the host starts");
        }
    }

    /// <summary>Starts a join once the client's identity is ready.</summary>
    internal LiveJoin Join(Provider client, JoinSettings settings, string host = "host.test")
    {
        // The load finishes on a background task, but the identity lands at the next pump, so wait for that.
        client.Live!.PrepareAsync();
        if (!Run(() => client.Live.IsReady)) throw new InvalidOperationException("Assertion failed: the client's identity loads");
        return client.Live.StartJoin(JoinTarget.Direct(host, 27250), settings);
    }

    public void Dispose()
    {
        foreach (var client in Clients) client.Dispose();
        Host.Dispose();
    }

    private Provider NewProvider(string name, MemoryDataFiles files, TestContent content, out SteppedNetworkFactory factory)
    {
        factory = new SteppedNetworkFactory(Network, name);
        var setup = new LiveSessionsSetup(files, content) { Factory = factory, Resolver = Resolver };
        return new Provider(ProviderId, null, () => Network.Now, setup);
    }
}
