extern alias provider;

using System.Diagnostics;
using System.Net;
using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.ControlPlane;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.Network;
using Control = provider::TopiaForge.Multiplayer.InternalControl;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;
using RoutedDiagnostics = provider::TopiaForge.Multiplayer.RoutedDiagnostics;
using RuntimeContent = provider::TopiaForge.Multiplayer.RuntimeContent;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// One player as the game composes it, on the NAT simulator: the provider with live sessions, its internal control, and
/// ModManager's runtime host and composition root, reaching the fake TopiaForge service through the real client. Its
/// machine sits at <see cref="Address"/> on its own site, behind its own NAT.
/// </summary>
internal sealed class InternetPeer
{
    internal InternetPeer(string name, InternetRig rig, SimulatedSite site, IPAddress address, int seed, string packageRoot, IReadOnlyList<PackageSpec> packages, bool service)
    {
        Name = name;
        Address = address;
        Site = site;
        Factory = new TraversalNetworkFactory(rig.Scenario, site, address, seed);
        LiveRig.SeedIdentity(Files, seed);
        var content = new RuntimeContent("2545", "0.2.0");
        var diagnostics = new RoutedDiagnostics(null);
        Services = service ? rig.ServicesFor(this) : null;
        var setup = new LiveSessionsSetup(Files, content)
        {
            Factory = Factory,
            Services = Services,
            Resolver = rig.Resolver?.Invoke(),
            TransportOptions = new SeededTransportOptions(seed).Next,
            NatOptions = () => new NatAgentOptions()
        };
        Provider = new Provider(LiveRig.ProviderId, diagnostics, () => rig.Scenario.Network.Now, setup);
        Control = new Control(Provider, content, diagnostics);
        Runtime = new MultiplayerRuntimeHost("2545", "0.2.0", Log, Platform);
        foreach (var package in packages) Runtime.Inventory.Add(package.ToManifest(), package.Install(Path.Combine(packageRoot, name)));
        Composition = new MultiplayerRuntime(() => Control, Runtime, Log);
        if (!Composition.Connect()) throw new InvalidOperationException("Assertion failed: the runtime attaches to " + name);
    }

    internal string Name { get; }

    /// <summary>Gets the machine's own address on its site: never to be seen by anyone but the NAT layer.</summary>
    internal IPAddress Address { get; }

    /// <summary>Gets the site the machine sits on, behind its own NAT.</summary>
    internal SimulatedSite Site { get; }

    internal TraversalNetworkFactory Factory { get; }
    internal MemoryDataFiles Files { get; } = new();
    internal InternetServices? Services { get; }
    internal Provider Provider { get; }
    internal Control Control { get; }
    internal MultiplayerRuntimeHost Runtime { get; }
    internal MultiplayerRuntime Composition { get; }
    internal RecordingMultiplayerLog Log { get; } = new();
    internal RecordingPlatform Platform { get; } = new();

    /// <summary>How many times the flows listed this machine's own addresses: never, for a relay-only session.</summary>
    internal int AddressListings;

    internal InternalLiveStatus Status => Control.GetStatus();

    internal InternalInternetStatus Internet => Control.GetStatus().Internet;

    /// <summary>Runs one frame the way ModRuntime does: the composition root around an empty mod update.</summary>
    internal void Frame()
    {
        Composition.BeforeModsUpdate(0.016f);
        Composition.AfterModsUpdate(0.016f);
        Composition.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));
    }

    internal IInternalJoinTarget Target(string text)
    {
        if (!Control.TryCreateJoinTarget(text, out var target)) throw new InvalidOperationException("Assertion failed: '" + text + "' makes a target");
        return target!;
    }

    public override string ToString() => Name;
}

/// <summary>
/// Internet sessions end to end: the NAT simulator with its sites, NATs, STUN and TURN server and signaling room
/// (<see cref="NatScenario"/>), the fake TopiaForge service, and whole players composed as the game composes them. Work
/// that runs off the main thread lands at the same point of every run, since virtual time waits for it.
/// </summary>
internal sealed class InternetRig : IDisposable
{
    /// <summary>Gets or sets the cumulative real-time bound for background work in each run.</summary>
    internal TimeSpan BackgroundWorkBound { get; set; } = TimeSpan.FromSeconds(30);

    private readonly string packageRoot = Directory.CreateTempSubdirectory("TopiaForgeInternetRig-").FullName;
    private readonly int seed;
    private int peers;

    internal InternetRig(int seed, long ttlSeconds = 600)
    {
        this.seed = seed;
        Scenario = new NatScenario(seed, ttlSeconds) { Renewable = true };
        Service = new FakeControlPlane(Scenario, seed);
    }

    internal NatScenario Scenario { get; }

    internal FakeControlPlane Service { get; }

    internal List<InternetPeer> Peers { get; } = new();

    /// <summary>
    /// Gets or sets what stands between the players added next and the fake service, such as a slow network that holds
    /// answers back; null puts nothing between them.
    /// </summary>
    internal Func<IControlPlaneTransport, IControlPlaneTransport>? WrapTransport { get; set; }

    /// <summary>
    /// Gets or sets what makes the resolver of the players added next, which takes every target the TopiaForge service
    /// does not; null resolves over real networks, as the game does.
    /// </summary>
    internal Func<IJoinTargetResolver>? Resolver { get; set; }

    internal long Now => Scenario.Network.Now;

    /// <summary>Adds a player machine at <paramref name="local"/> behind its own NAT with public address <paramref name="publicAddress"/>.</summary>
    internal InternetPeer AddPeer(string name, NatBehaviour behaviour, string publicAddress, string local, bool service = true, params PackageSpec[] packages)
    {
        var site = Scenario.Behind(name, behaviour, publicAddress);
        var peer = new InternetPeer(name, this, site, IPAddress.Parse(local), (seed * 59) + (++peers), packageRoot, packages, service);
        Peers.Add(peer);
        return peer;
    }

    /// <summary>The services one player reaches: the real client over the fake service, its signaling rooms, and the simulator's DNS.</summary>
    internal InternetServices ServicesFor(InternetPeer peer) => new(
        new ControlPlaneClient(WrapTransport?.Invoke(Service) ?? Service),
        Service.OpenSignaling,
        host => host == NatScenario.StunHost ? Task.FromResult(new[] { NatScenario.ServerAddress })
            : host == NatScenario.TurnHost ? Task.FromResult(Scenario.TurnAddresses)
            : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException(11001)),
        () =>
        {
            Interlocked.Increment(ref peer.AddressListings);
            return new[] { peer.Address };
        });

    /// <summary>Runs every player's frame and the network until <paramref name="condition"/> holds or virtual time runs out.</summary>
    internal bool Run(Func<bool> condition, long maximumMicros = 30_000_000, long frameMicros = 10_000)
    {
        var deadline = Now + maximumMicros;
        var waiting = new Stopwatch();
        while (true)
        {
            foreach (var peer in Peers) peer.Frame();
            Service.Tick();
            if (condition()) return true;
            if (AwaitBackgroundWork(waiting)) continue;
            if (Now >= deadline) return false;
            Scenario.Network.RunFor(frameMicros);
        }
    }

    /// <summary>Hosts through the SPI and waits until the service lists the session with an invite and the relay is up.</summary>
    internal InternalInvite Host(InternetPeer host, InternalHostSettings settings)
    {
        var started = host.Control.StartHosting(settings);
        if (!started.Succeeded) throw new InvalidOperationException("Assertion failed: hosting starts (" + started.ErrorMessage + ")");
        var online = Run(() => host.Internet.ListingPhase == InternalListingPhase.Online && host.Internet.Invite != null &&
                               (host.Internet.RelayReady || settings.AllowDirect));
        if (!online)
        {
            var status = host.Internet;
            throw new InvalidOperationException("Assertion failed: the host comes online (" + status.ListingPhase + ", " + status.ListingFailure + ")");
        }

        return host.Internet.Invite!;
    }

    /// <summary>Joins through the SPI and waits until the player is in the session.</summary>
    internal void Join(InternetPeer peer, string target, InternalJoinSettings? settings = null, long maximumMicros = 60_000_000)
    {
        var joined = peer.Control.Join(peer.Target(target), settings ?? new InternalJoinSettings());
        if (!joined.Succeeded) throw new InvalidOperationException("Assertion failed: the join starts (" + joined.ErrorMessage + ")");
        if (!Run(() => peer.Status.JoinPhase == InternalJoinPhase.InSession || peer.Status.JoinPhase == InternalJoinPhase.Ended, maximumMicros) ||
            peer.Status.JoinPhase != InternalJoinPhase.InSession)
        {
            var status = peer.Status;
            throw new InvalidOperationException("Assertion failed: " + peer.Name + " joins (" + status.JoinPhase + ", " + status.JoinFailure + ": " + status.JoinDetail + ")");
        }
    }

    public void Dispose()
    {
        foreach (var peer in Peers)
        {
            peer.Composition.Disconnect();
            peer.Control.Dispose();
            peer.Provider.Dispose();
        }

        try
        {
            Directory.Delete(packageRoot, true);
        }
        catch (IOException)
        {
            // A temporary folder that cannot go now goes with the machine's next clean-up.
        }
    }

    private bool AwaitBackgroundWork(Stopwatch waiting)
    {
        var waited = false;
        waiting.Start();
        try
        {
            while (true)
            {
                var pending = new BackgroundWorkWait();
                foreach (var peer in Peers) pending.Add(peer.Name, peer.Provider.Live!.PendingBackgroundWork(saves: true));
                if (pending.Count == 0) return waited;
                pending.Wait(waiting, BackgroundWorkBound);
                waited = true;
            }
        }
        finally
        {
            waiting.Stop();
        }
    }
}
