extern alias provider;

using System.Diagnostics;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.NatTraversal;
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

    internal SteppedNetwork(VirtualDatagramNetwork network, string name, NetworkStack stack, int port, bool retireWhenStopped = false, VirtualNode? replaces = null)
    {
        this.network = network;
        this.stack = stack;
        this.retireWhenStopped = retireWhenStopped;
        LocalPort = port;
        Node = network.AddStack(name, stack, replaces, samePort: replaces != null);
    }

    internal VirtualNode Node { get; }

    internal NetworkStack Stack => stack;

    internal bool Stopped { get; private set; }

    /// <summary>Makes the next event say the network gave up, as the game's network thread does after repeated faults.</summary>
    internal bool FailNextEvent { get; set; }

    public int LocalPort { get; }

    /// <summary>
    /// Gets every connection a join flow started through this network, oldest first. A succeeded one holds the link the
    /// join hands its engine, which a test can write on as a modified client would, past its own engine.
    /// </summary>
    internal List<ConnectAttempt> Attempts { get; } = new();

    public ConnectAttempt Connect(TransportPath path, byte[]? expectedKey)
    {
        var attempt = new ConnectAttempt();
        Attempts.Add(attempt);
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

    /// <summary>Gets or sets whether a stopped network leaves the virtual network after its grace period.</summary>
    internal bool RetireStopped { get; set; }

    /// <summary>
    /// Gets or sets a node that has left the network, whose address and port the next network takes, as a game that
    /// restarts binds the port its last run held. It applies once.
    /// </summary>
    internal VirtualNode? Replaces { get; set; }

    public INetworkControl Start(int port, NetworkStack stack)
    {
        if (FailStarts) throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressFamilyNotSupported);
        ++created;
        var started = new SteppedNetwork(network, name + "-" + created, stack, port == 0 ? 40_000 + created : port, RetireStopped, Replaces);
        Replaces = null;
        Networks.Add(started);
        return started;
    }

    /// <summary>These networks have no NAT layer; the Internet tests build theirs with <see cref="TraversalNetworkFactory"/>.</summary>
    public TraversalNetwork StartTraversal(int port, NetworkStack stack, NatAgentOptions options) =>
        throw new NotSupportedException("The stepped networks carry no NAT layer; Internet sessions run on the traversal factory.");

    public DescriptorEndpoint? CreateDescriptorEndpoint(int port, bool relayOnly) => null;

    public LanAnnouncer? CreateAnnouncer(LanBeacon beacon) => null;

    public LanBrowser? CreateBrowser(int discoveryPort) => null;

    public string? SourceOf(TransportPath path) => VirtualDatagramNetwork.SourceOf(path);

    public byte[]? NetworkOf(TransportPath path) => VirtualDatagramNetwork.NetworkOf(path);

    /// <summary>
    /// Gets or sets whether a game socket this factory opened runs over IPv4 alone. The virtual network never falls back,
    /// so it is false unless a test sets it to stand for the fallback.
    /// </summary>
    public bool OpenedIPv4Only { get; set; }
}

/// <summary>
/// Seeded options for the endpoints one provider's live flows build, handed out in the order they build them: each
/// endpoint draws connection ids, ephemeral keys, cookie secrets, path challenges and query ids from its own seeded
/// source from the moment it exists, so a run repeats every byte, a stateless Retry included.
/// </summary>
internal sealed class SeededTransportOptions
{
    private readonly int seed;
    private readonly ITransportDiagnostics? diagnostics;
    private readonly Action<TransportOptions>? configure;
    private int created;

    internal SeededTransportOptions(int seed, ITransportDiagnostics? diagnostics = null, Action<TransportOptions>? configure = null)
    {
        this.seed = seed;
        this.diagnostics = diagnostics;
        this.configure = configure;
    }

    internal TransportOptions Next()
    {
        ++created;
        var options = new TransportOptions { Random = new SeededTransportRandom((seed * 7_919) + created), Diagnostics = diagnostics };
        configure?.Invoke(options);
        return options;
    }
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

    /// <summary>Gets or sets a resolver that takes every target in this one's place, such as the real one over a test's fetch.</summary>
    internal IJoinTargetResolver? Inner { get; set; }

    /// <summary>Gets whether the last join asked to allow direct paths; the join itself refuses one it was not allowed.</summary>
    internal bool? LastAllowDirect { get; private set; }

    /// <summary>Gets the server the last join said the player trusts at a changed address, if any.</summary>
    internal string? LastTrustedServerId { get; private set; }

    public Task<ResolvedTarget> ResolveAsync(JoinTarget target, bool allowDirect, string? trustedServerId, CancellationToken cancellationToken)
    {
        LastAllowDirect = allowDirect;
        LastTrustedServerId = trustedServerId;
        if (Inner != null) return Inner.ResolveAsync(target, allowDirect, trustedServerId, cancellationToken);
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

    /// <summary>Gets or sets what this player presents when it joins; a host admits only the bytes 1, 2, 3.</summary>
    internal byte[] Presents { get; set; } = { 1, 2, 3 };

    /// <summary>Gets or sets the host's verdict on every player, overriding the check of what they present.</summary>
    internal ContentAdmission? Verdict { get; set; }

    public IReadOnlyList<DescriptorPackage> HostPackages() => Array.Empty<DescriptorPackage>();

    public byte[] ClientContent() => Presents;

    public ContentAdmission Admit(byte[] clientContent) =>
        Verdict ?? (clientContent.SequenceEqual(new byte[] { 1, 2, 3 }) ? ContentAdmission.Admitted : ContentAdmission.Refused(new[] { "Unknown content." }));

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

    /// <summary>Gets the files whose writes fail, as on a full disk, while the rest write normally.</summary>
    internal HashSet<string> FailWritesOf { get; } = new();

    /// <summary>Gets or sets what each write waits for before it lands, so a test can make one slow.</summary>
    internal Func<string, Task>? BeforeWrite { get; set; }

    /// <summary>Gets or sets what each read waits for before it answers, so a test can make a load slow.</summary>
    internal Func<string, Task>? BeforeRead { get; set; }

    /// <summary>Gets how many writes wait on a <see cref="BeforeWrite"/> gate that has not opened; a rig never waits for those.</summary>
    internal int HeldWrites => Volatile.Read(ref held);

    private int held;

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var gate = BeforeRead;
        if (gate != null) await gate(relativePath).ConfigureAwait(false);
        if (FailReads || FailReadsOf.Contains(relativePath)) throw new IOException("The disk could not be read.");
        lock (Contents) return Contents.TryGetValue(relativePath, out var bytes) ? bytes.ToArray() : null;
    }

    public async Task WriteAsync(string relativePath, byte[] content, CancellationToken cancellationToken)
    {
        var copy = content.ToArray();
        var gate = BeforeWrite?.Invoke(relativePath);
        if (gate != null && !gate.IsCompleted)
        {
            Interlocked.Increment(ref held);
            try
            {
                await gate.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref held);
            }
        }

        // Like the SDK's mod files, a cancelled write is refused, and a write that fails leaves the file as it was.
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWritesOf.Contains(relativePath)) throw new IOException("The disk is full.");
        lock (Contents) Contents[relativePath] = copy;
    }
}

/// <summary>A host provider and as many joining providers as a test needs, all on one deterministic network.</summary>
internal sealed class LiveRig : IDisposable
{
    internal const string ProviderId = "io.github.furroxide.topiaforge.multiplayer";

    /// <param name="seed">Seeds the network.</param>
    /// <param name="seededTransports">Gives every endpoint seeded randomness from the start, so a run repeats every byte.</param>
    /// <param name="configureHostTransport">Adjusts the options of every endpoint the host builds, such as demanding cookies.</param>
    internal LiveRig(int seed, bool seededTransports = false, Action<TransportOptions>? configureHostTransport = null)
    {
        this.seed = seed;
        this.seededTransports = seededTransports;
        Network = new VirtualDatagramNetwork(seed);
        Host = NewProvider("host", HostFiles, HostContent, out var hostFactory, configureHostTransport);
        HostFactory = hostFactory;
        Resolver.Host = () => Host.Live!.Host?.Descriptor is { } descriptor && HostFactory.Networks.Count > 0
            ? (HostFactory.Networks[^1].Node, descriptor)
            : null;
    }

    internal VirtualDatagramNetwork Network { get; }
    internal MemoryDataFiles HostFiles { get; } = new();

    /// <summary>Gets the host's content, whose verdict a test may set.</summary>
    internal TestContent HostContent { get; } = new();

    internal Provider Host { get; }
    internal SteppedNetworkFactory HostFactory { get; }
    internal SteppedResolver Resolver { get; } = new();
    internal List<Provider> Clients { get; } = new();

    internal Dictionary<Provider, SteppedNetworkFactory> Factories { get; } = new();

    private readonly Dictionary<Provider, MemoryDataFiles> clientFiles = new();
    private readonly int seed;
    private readonly bool seededTransports;
    private int providers;

    /// <summary>Gets the data folder of a client the rig added.</summary>
    internal MemoryDataFiles ClientFiles(Provider client) => clientFiles[client];

    /// <summary>Writes an identity made from <paramref name="identitySeed"/> before the provider reads one, so its keys repeat run to run.</summary>
    internal static void SeedIdentity(MemoryDataFiles files, int identitySeed)
    {
        var bytes = new byte[96];
        new Random(identitySeed).NextBytes(bytes);
        using var identity = new LocalIdentity(bytes[..32], bytes[32..64], bytes[64..], 1_760_000_000 + identitySeed);
        files.Contents[IdentityStore.IdentityPath] = identity.Serialize();
    }

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

    /// <summary>How long a run waits, in real time, for work the providers do on other threads before calling it stuck.</summary>
    internal static readonly TimeSpan BackgroundWorkBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the network and pumps every provider until <paramref name="condition"/> holds or virtual time runs out.
    /// Identity loads, password keys, resolves and saves finish on other threads. Before virtual time moves on, the run
    /// waits for them in real time, so their results land at the next pump: at the same point of the run however slow
    /// the machine is, and never after a virtual deadline that real time happened to beat.
    /// </summary>
    internal bool Run(Func<bool> condition, long maximumMicros = 10_000_000)
    {
        var deadline = Network.Now + maximumMicros;
        var waiting = new Stopwatch();
        while (true)
        {
            Host.Pump();
            foreach (var client in Clients) client.Pump();
            if (condition()) return true;
            if (AwaitBackgroundWork(waiting)) continue;
            if (Network.Now >= deadline) return false;
            Network.RunFor(5_000);
        }
    }

    /// <summary>
    /// Waits in real time, without moving virtual time, until no provider has work running on other threads; true when
    /// there was some. A save that a test's gate holds is left alone, since only the test can release it.
    /// </summary>
    private bool AwaitBackgroundWork(Stopwatch waiting)
    {
        var waited = false;
        waiting.Start();
        try
        {
            while (true)
            {
                var pending = Host.Live!.PendingBackgroundWork(saves: HostFiles.HeldWrites == 0);
                foreach (var client in Clients) pending.AddRange(client.Live!.PendingBackgroundWork(saves: clientFiles[client].HeldWrites == 0));
                if (pending.Count == 0) return waited;
                if (waiting.Elapsed > BackgroundWorkBound)
                {
                    throw new InvalidOperationException("Assertion failed: work on other threads finishes within " + BackgroundWorkBound.TotalSeconds + " s of real time");
                }

                Task.WaitAny(pending.ToArray(), 10);
                waited = true;
            }
        }
        finally
        {
            waiting.Stop();
        }
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

    private Provider NewProvider(string name, MemoryDataFiles files, TestContent content, out SteppedNetworkFactory factory, Action<TransportOptions>? configureTransport = null)
    {
        factory = new SteppedNetworkFactory(Network, name);
        var setup = new LiveSessionsSetup(files, content) { Factory = factory, Resolver = Resolver };
        var number = ++providers;
        if (seededTransports) setup.TransportOptions = new SeededTransportOptions((seed * 131) + number, configure: configureTransport).Next;
        else if (configureTransport != null) setup.TransportOptions = () => Configured(configureTransport);
        return new Provider(ProviderId, null, () => Network.Now, setup);
    }

    private static TransportOptions Configured(Action<TransportOptions> configure)
    {
        var options = new TransportOptions();
        configure(options);
        return options;
    }
}
