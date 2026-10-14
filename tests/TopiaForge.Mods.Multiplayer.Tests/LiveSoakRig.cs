extern alias provider;

using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;
using ProviderEngine = provider::TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Engine reports from one provider, kept so a soak can prove nothing failed quietly.</summary>
internal sealed class ProviderDiagnostics : ProviderEngine.IEngineDiagnostics
{
    internal List<string> Reports { get; } = new();

    public void Report(ProviderEngine.EngineDiagnostic diagnostic) => Reports.Add(diagnostic.ToString());
}

/// <summary>One command or input a soak peer submitted, and what became of it.</summary>
internal sealed class SoakSubmission
{
    private readonly Func<OperationResult<bool>> outcome;

    internal SoakSubmission(int peer, int nonce, bool nudge, bool remote, Task task, Func<OperationResult<bool>> outcome)
    {
        Peer = peer;
        Nonce = nonce;
        Nudge = nudge;
        Remote = remote;
        Task = task;
        this.outcome = outcome;
    }

    internal int Peer { get; }

    internal int Nonce { get; }

    internal bool Nudge { get; }

    /// <summary>Gets whether it went to a host, rather than running in the peer's own solo session.</summary>
    internal bool Remote { get; }

    internal Task Task { get; }

    internal OperationResult<bool> Outcome => outcome();
}

/// <summary>One soak process: a provider with its own files, identity, network factory and workload.</summary>
internal sealed class SoakPeer
{
    internal SoakPeer(string name, int index, Provider provider, MemoryDataFiles files, SteppedNetworkFactory factory, ProviderDiagnostics diagnostics, string fingerprint)
    {
        Name = name;
        Index = index;
        Provider = provider;
        Files = files;
        Factory = factory;
        Diagnostics = diagnostics;
        Fingerprint = fingerprint;
    }

    internal string Name { get; }

    internal int Index { get; }

    internal Provider Provider { get; }

    internal MemoryDataFiles Files { get; }

    internal SteppedNetworkFactory Factory { get; }

    internal ProviderDiagnostics Diagnostics { get; }

    /// <summary>Gets the identity's transport key in hexadecimal, which bans are keyed by.</summary>
    internal string Fingerprint { get; }

    internal SoakWorkload Workload { get; set; } = null!;

    internal ProviderEngine.SessionEngine Engine => Provider.Engine;

    internal LiveJoin? Join => Provider.Live!.Join;

    internal bool InSession => Join?.Phase == JoinPhase.InSession;

    /// <summary>Gets whether the peer is in its own solo session: never joined, or its last join ended.</summary>
    internal bool Solo => Join == null || Join.Phase == JoinPhase.Ended;

    /// <summary>Gets the virtual node of the network the peer runs now, or null when it has none on the network.</summary>
    internal VirtualNode? Node => Factory.Networks.Count > 0 && !Factory.Networks[^1].Node.Removed ? Factory.Networks[^1].Node : null;

    internal NetworkStack? Stack => Node?.Stack;

    /// <summary>Gets or sets whether the peer joins over a direct path rather than the LAN.</summary>
    internal bool Direct { get; set; }

    internal long StalledUntil { get; set; }

    internal long NextActionAt { get; set; }

    internal long HealAt { get; set; } = long.MaxValue;

    internal VirtualNode? PartitionedFrom { get; set; }

    internal int Nonce { get; set; }

    internal List<SoakSubmission> Submissions { get; } = new();

    internal JoinPhase LastPhase { get; set; } = JoinPhase.Ended;

    internal ParticipantId? ParticipantBeforeLoss { get; set; }

    internal long LostAt { get; set; }
}

/// <summary>The largest value each growing table reached during a soak, and the ceilings it is held to.</summary>
internal sealed class SoakCensus
{
    private readonly Dictionary<string, long> maxima = new(StringComparer.Ordinal);

    internal void Observe(string what, long value, long ceiling)
    {
        That(value <= ceiling, what + " stays within " + ceiling + " (" + value + ")");
        maxima[what] = Math.Max(maxima.GetValueOrDefault(what), value);
    }

    internal long Maximum(string what) => maxima.GetValueOrDefault(what);

    public override string ToString() => string.Join(", ", maxima.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + " " + entry.Value));
}

/// <summary>
/// A host and four players on the deterministic network, each a whole provider with real cryptography, transport,
/// engine and live flows. Identities and every endpoint's randomness come from the seed, and slow work is waited for
/// without moving virtual time, so a seed repeats every byte. Each frame lasts a seeded 8 to 34 ms, as a game's frames
/// vary, and a stalled peer misses its pumps while its network carries on, as a scene load does.
/// </summary>
internal sealed class SoakRig : IDisposable
{
    internal const int Players = 4;
    internal const int MaximumPlayers = 8;

    private readonly int seed;

    internal SoakRig(int seed)
    {
        this.seed = seed;
        Random = new Random(seed);
        Network = new VirtualDatagramNetwork(seed);
        Host = NewPeer("host", 0);
        for (var index = 1; index <= Players; index++) Clients.Add(NewPeer("guest" + index, index));
        Resolver.Host = () => Host.Provider.Live!.Host?.Descriptor is { } descriptor && Host.Node != null ? (Host.Node, descriptor) : null;
    }

    internal VirtualDatagramNetwork Network { get; }

    internal Random Random { get; }

    internal SoakPeer Host { get; }

    internal List<SoakPeer> Clients { get; } = new();

    internal IEnumerable<SoakPeer> Peers => Clients.Prepend(Host);

    internal SteppedResolver Resolver { get; } = new();

    internal RecordingTransportDiagnostics TransportDiagnostics { get; } = new();

    internal SoakCensus Census { get; } = new();

    /// <summary>Gets or sets whether the workload holds still, so the peers can settle and be compared.</summary>
    internal bool Quiet { get; set; }

    internal long Frames { get; private set; }

    /// <summary>Loads every identity, then opens the host's session to the others under a session id the seed names.</summary>
    internal void Start()
    {
        foreach (var peer in Peers)
        {
            // The identity loads on a background task; waiting for it in real time keeps virtual time, and so the run, exact.
            peer.Provider.Live!.PrepareAsync().Wait();
            peer.Provider.Pump();
            That(peer.Provider.Live.IsReady, peer.Name + "'s identity loads");
            peer.Workload = new SoakWorkload(peer.Provider, (seed * 31) + peer.Index, () => Quiet);
        }

        Host.Engine.ReplaceSession(new MultiplayerSessionId("soak:" + seed + ":0"), new SessionSeed((ulong)seed));
        Host.Provider.Live!.StartHosting(new HostSettings("Soak host", MaximumPlayers) { Port = 0, AllowDirect = true });
        Frame();
        That(Host.Provider.Live.HostPhase == HostPhase.Hosting, "the host opens its session (" + Host.Provider.Live.HostFailure + ")");
    }

    /// <summary>Runs one frame: the network to the frame's end, then every peer that is not stalled pumps once.</summary>
    internal void Frame(Action? beforePumps = null)
    {
        Network.RunUntil(Network.Now + Random.Next(8_000, 34_000));
        beforePumps?.Invoke();
        foreach (var peer in Peers)
        {
            if (Network.Now >= peer.StalledUntil) peer.Provider.Pump();
        }

        Frames++;
    }

    /// <summary>Starts a join for <paramref name="peer"/>, over its own kind of path.</summary>
    internal LiveJoin Join(SoakPeer peer)
    {
        Resolver.Kind = peer.Direct ? TransportPathKind.Direct : TransportPathKind.Lan;
        return peer.Provider.Live!.StartJoin(JoinTarget.Direct("host.test", 27250), new JoinSettings(peer.Name) { AllowDirect = peer.Direct });
    }

    /// <summary>Measures every growing table against its ceiling; a table past its ceiling fails the soak.</summary>
    internal void Sample()
    {
        var host = Host.Engine.Census();
        Census.Observe("host connections", host.Connections, 2 * Players);
        Census.Observe("host participants", host.ParticipantRecords, 1 + Players);
        Census.Observe("host objects", host.Objects, SoakWorkload.MaximumMarkers);
        Census.Observe("host rate history", host.RateHistoryEntries, Math.Max(1, host.Connections) * (1 + SoakWorkload.MaximumMarkers));

        // A limiter belongs to a participant the host still knows: one per sender per command it used (add for any
        // peer, ping only for the host), and one per sender per object.
        Census.Observe("host command limiters", host.CommandRateLimiters, host.ParticipantRecords + 1);
        Census.Observe("host object limiters", host.ObjectRateLimiters, host.ParticipantRecords * host.Objects);
        Census.Observe("host queued items", host.QueuedOutbound, 64);
        Census.Observe("network nodes", Network.NodeCount, (Players * 3) + 1);
        foreach (var peer in Peers)
        {
            var engine = peer.Engine.Census();
            Census.Observe("queued callbacks", engine.QueuedCallbacks, 0);
            Census.Observe("unbound values", engine.UnboundStates + engine.UnboundObjects, 0);
            Census.Observe("pending submissions", engine.PendingSubmissions, 96);

            // A tombstone per object despawned in the current session, cleared at replacement. The ceiling is well under
            // TombstoneSet's 8,192 capacity, which the old ceiling equalled and so could never catch a leak.
            Census.Observe("tombstones", engine.Tombstones, 2_000);
            Census.Observe("participant list", engine.ParticipantRecords, 1 + Players);
            var stack = peer.Stack;
            if (stack == null) continue;
            var transport = stack.Endpoint.Census();
            var side = peer == Host ? "host " : "client ";
            Census.Observe(side + "transport connections", transport.Connections, peer == Host ? 4 * Players : 4);
            Census.Observe("half-open handshakes", transport.HalfOpen, Players);

            // A responder keeps one initiation entry per half-open handshake, removed when the connection completes or
            // closes; before the leak was fixed these outlived connections that changed path, so a clients-come-and-go
            // soak grew them without bound. A client accepts nobody, so it keeps none.
            Census.Observe(side + "initiations", transport.Initiations, peer == Host ? 2 * Players : 0);
            Census.Observe("queued datagrams", transport.OutboundDatagrams + transport.QueuedDatagrams, TransportEndpoint.MaximumQueuedDatagrams);
            Census.Observe("reassembly bytes", transport.ReassemblyBytes, 4 * 1024 * 1024);
            Census.Observe("send backlog bytes", transport.SendBacklogBytes, 4 * 1024 * 1024);
            Census.Observe("packets in flight", transport.PacketsInFlight, 4_096);
            Census.Observe("info queries", transport.OutstandingQueries + transport.QueuedInfoReplies, 0);
            Census.Observe("links", stack.LinkCount + stack.PendingCount, peer == Host ? 4 * Players : 4);
            Census.Observe("link inbound bytes", stack.InboundBytes, NetworkLink.InboundBudgetBytes + (64 * 1024));
            Census.Observe("link outbound bytes", stack.OutboundBytes, 4 * 1024 * 1024);
        }
    }

    public void Dispose()
    {
        foreach (var peer in Peers.Reverse()) peer.Provider.Dispose();
    }

    private SoakPeer NewPeer(string name, int index)
    {
        // The identity is written before the provider first reads it, from bytes the seed decides.
        var files = new MemoryDataFiles();
        var bytes = new byte[96];
        new Random((seed * 977) + index).NextBytes(bytes);
        string fingerprint;
        using (var identity = new LocalIdentity(bytes[..32], bytes[32..64], bytes[64..], 1_760_000_000 + index))
        {
            files.Contents[IdentityStore.IdentityPath] = identity.Serialize();
            fingerprint = identity.Fingerprint;
        }

        var factory = new SteppedNetworkFactory(Network, name) { RetireStopped = true };
        var diagnostics = new ProviderDiagnostics();
        var setup = new LiveSessionsSetup(files, new TestContent())
        {
            Factory = factory,
            Resolver = Resolver,
            TransportOptions = new SeededTransportOptions((seed * 101) + index, TransportDiagnostics).Next
        };
        var created = new Provider(LiveRig.ProviderId, diagnostics, () => Network.Now, setup);
        return new SoakPeer(name, index, created, files, factory, diagnostics, fingerprint);
    }
}
