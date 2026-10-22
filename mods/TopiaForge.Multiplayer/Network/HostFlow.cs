using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.ControlPlane;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.NatTraversal;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>A network whose game socket the NAT layer shares: the network the flows command, and the agent on its socket.</summary>
    internal sealed class TraversalNetwork
    {
        internal TraversalNetwork(INetworkControl network, NatAgent agent)
        {
            Network = network ?? throw new ArgumentNullException(nameof(network));
            Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        }

        internal INetworkControl Network { get; }

        internal NatAgent Agent { get; }
    }

    /// <summary>Creates the networks live sessions run on: real sockets and threads in the game, a virtual network in tests.</summary>
    internal interface INetworkFactory
    {
        /// <summary>Starts a network that owns <paramref name="stack"/>, on <paramref name="port"/> or any free port when zero.</summary>
        INetworkControl Start(int port, NetworkStack stack);

        /// <summary>
        /// Starts a network as <see cref="Start"/> does, with the NAT layer on its game socket, for a session found through
        /// the TopiaForge service. The agent contacts nothing until a session starts on it with a plan made from the
        /// service's answers.
        /// </summary>
        TraversalNetwork StartTraversal(int port, NetworkStack stack, NatAgentOptions options);

        /// <summary>Creates the host's descriptor endpoint on <paramref name="port"/>, or null where the network serves none.</summary>
        DescriptorEndpoint? CreateDescriptorEndpoint(int port, bool relayOnly);

        /// <summary>Creates the host's LAN announcer, or null where the network has no LAN.</summary>
        LanAnnouncer? CreateAnnouncer(LanBeacon beacon);

        /// <summary>Creates a LAN browser listening on <paramref name="discoveryPort"/>, or null where the network has no LAN.</summary>
        LanBrowser? CreateBrowser(int discoveryPort);

        /// <summary>Names the host a path from this network reaches, without its port, or null where sources cannot be told apart.</summary>
        string? SourceOf(TransportPath path);

        /// <summary>
        /// Names the network a path from this network comes from, as password limits count it: an IPv4 address or an
        /// IPv6 /64, never the port. Null where it cannot be told, as through a relay. Only the network layer reads it.
        /// </summary>
        byte[]? NetworkOf(TransportPath path);

        /// <summary>
        /// Gets whether a game socket this factory opened runs over IPv4 alone, because the system has no IPv6: the fallback
        /// <see cref="UdpSocketDriver.IPv4Only"/> records. Once true it stays true. It names no address.
        /// </summary>
        bool OpenedIPv4Only { get; }
    }

    /// <summary>The game's networks: a <see cref="NetworkThread"/> over a <see cref="UdpSocketDriver"/>, with TCP and LAN services.</summary>
    internal sealed class SocketNetworkFactory : INetworkFactory
    {
        private readonly Func<long> clock;
        private readonly IPAddress? bindAddress;
        private readonly bool withoutIPv6;

        /// <param name="bindAddress">
        /// The address the game socket binds, every local address by default. Tests bind a loopback address, as every test
        /// socket does.
        /// </param>
        /// <param name="withoutIPv6">Opens the game socket a system without IPv6 gets, on any system; tests use it.</param>
        internal SocketNetworkFactory(Func<long> clock, IPAddress? bindAddress = null, bool withoutIPv6 = false)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.bindAddress = bindAddress;
            this.withoutIPv6 = withoutIPv6;
        }

        public bool OpenedIPv4Only { get; private set; }

        public INetworkControl Start(int port, NetworkStack stack)
        {
            var thread = new NetworkThread("TopiaForge multiplayer network", OpenGameSocket(port), stack, clock);
            thread.Start();
            return thread;
        }

        public TraversalNetwork StartTraversal(int port, NetworkStack stack, NatAgentOptions options)
        {
            var nat = NatPlumbing.Wrap(OpenGameSocket(port), options, clock);
            var thread = new NetworkThread("TopiaForge multiplayer network", nat.Socket, stack, clock);
            thread.Attach(nat.Service);
            thread.Start();
            return new TraversalNetwork(thread, nat.Agent);
        }

        public DescriptorEndpoint? CreateDescriptorEndpoint(int port, bool relayOnly) => new DescriptorEndpoint(port, relayOnly: relayOnly);

        public LanAnnouncer? CreateAnnouncer(LanBeacon beacon) => new LanAnnouncer(beacon);

        public LanBrowser? CreateBrowser(int discoveryPort) => new LanBrowser(discoveryPort);

        public string? SourceOf(TransportPath path) => UdpPaths.SourceOf(path);

        public byte[]? NetworkOf(TransportPath path) => UdpPaths.NetworkOf(path);

        /// <summary>Opens a game socket, and notes when it is the IPv4 socket a system without IPv6 gets.</summary>
        private UdpSocketDriver OpenGameSocket(int port)
        {
            var socket = new UdpSocketDriver(port, bindAddress, withoutIPv6);
            if (socket.IPv4Only) OpenedIPv4Only = true;
            return socket;
        }
    }

    /// <summary>
    /// A listen host: this process's session, open to other players. Starting binds the game port, the descriptor
    /// endpoint on the same port number and, for LAN or public visibility, the LAN announcer; it signs and serves the
    /// descriptor, answers info queries, and admits players through <see cref="HostPolicy"/>. The engine keeps running
    /// the same session throughout: hosting adds links to it, and stopping closes them while the host plays on. A public
    /// or invite-only host with the TopiaForge service configured shares its game socket with the NAT layer and keeps an
    /// <see cref="InternetHost"/>: registered, listed, holding an invite, and answering joiners through signaling. A LAN
    /// host never contacts the service.
    /// </summary>
    internal sealed class LiveHost : IDisposable
    {
        private const int PortAttempts = 4;
        private const long StopGraceMicros = 250_000;

        /// <summary>How many links one address may hold at once: a household of players behind one router.</summary>
        internal const int MaximumLinksPerSource = 4;

        private readonly SessionEngine engine;
        private readonly LocalIdentity identity;
        private readonly HostSettings settings;
        private readonly ILiveSessionContent content;
        private readonly Func<long> clock;
        private readonly Func<long> unixSeconds;
        private readonly Func<TransportOptions> transportOptions;
        private readonly InternetServices? services;
        private readonly Func<NatAgentOptions> natOptions;

        /// <summary>Whether the session offers voice chat, taken once as hosting starts and fixed for the whole run.</summary>
        private readonly bool offersVoice;
        private INetworkControl? network;
        private TraversalNetwork? traversal;
        private IReadOnlyList<DescriptorPackage>? packages;
        private DescriptorEndpoint? descriptorEndpoint;
        private int lastPlayers = -1;
        private bool stopped;

        internal LiveHost(
            SessionEngine engine,
            LocalIdentity identity,
            BanList bans,
            HostSettings settings,
            ILiveSessionContent content,
            Func<long> clock,
            Func<long> unixSeconds,
            byte[]? passwordKey,
            Func<TransportOptions>? transportOptions = null,
            InternetServices? services = null,
            Func<NatAgentOptions>? natOptions = null)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.unixSeconds = unixSeconds ?? throw new ArgumentNullException(nameof(unixSeconds));
            this.transportOptions = transportOptions ?? (() => new TransportOptions());
            this.services = settings.Visibility == ServerVisibility.Lan ? null : services;
            this.natOptions = natOptions ?? (() => new NatAgentOptions());

            // The descriptor is signed with it once and the engine holds it while anyone is connected, so a later change to
            // the settings object reaches neither: hosting again is how the setting changes.
            offersVoice = settings.Voice;
            Bans = bans ?? throw new ArgumentNullException(nameof(bans));
            Policy = new HostPolicy(bans, passwordKey, content, clock);
        }

        internal BanList Bans { get; }

        internal HostPolicy Policy { get; }

        /// <summary>Gets the game port the host is reachable on.</summary>
        internal int Port { get; private set; }

        /// <summary>Gets the signed descriptor the host serves.</summary>
        internal SignedServerDescriptor? Descriptor { get; private set; }

        internal bool IsRunning => network != null && !stopped;

        /// <summary>Gets why the host stopped on its own, when its network failed; otherwise none.</summary>
        internal LiveFailure Failure { get; private set; }

        /// <summary>Gets the session's life with the TopiaForge service, for a public or invite-only host that has one.</summary>
        internal InternetHost? Internet { get; private set; }

        /// <summary>
        /// Binds the network and starts serving. Returns <see cref="LiveFailure.ContentUnreadable"/> when this game's
        /// packages cannot be read, before anything binds, and <see cref="LiveFailure.PortInUse"/> when no port could be
        /// bound; the session is then untouched.
        /// </summary>
        internal LiveFailure Start(INetworkFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (network != null) throw new InvalidOperationException("The host has already started.");
            packages = content.HostPackages();
            if (packages == null) return LiveFailure.ContentUnreadable;
            var port = settings.Port;
            for (var attempt = 0; attempt < PortAttempts && network == null; attempt++)
            {
                if (TryBind(factory, port)) break;
                if (!settings.FallBackToAnyPort) return LiveFailure.PortInUse;
                port = 0;
            }

            if (network == null) return LiveFailure.PortInUse;
            Descriptor = Sign(packages);
            descriptorEndpoint?.SetDescriptor(Descriptor);
            if (settings.Visibility != ServerVisibility.Invite)
            {
                var announcer = factory.CreateAnnouncer(new LanBeacon(identity.ServerId, Port));
                if (announcer != null) network.Attach(announcer);
            }

            engine.Options.MaximumParticipants = settings.MaxPlayers;
            engine.Options.JoinPolicy = Policy;

            // Before the first player can join: a session that offers no voice carries none, and every player hears so in
            // JoinAccepted. Stopping closes every connection, which ends the setting with the run.
            engine.OfferVoice(offersVoice);
            RefreshInfo(force: true);
            if (traversal != null) Internet = new InternetHost(services!, identity, traversal, Descriptor, settings, ConnectedPlayers, clock);
            return LiveFailure.None;
        }

        /// <summary>
        /// Changes the gamemode and world the session announces, as a new world does: the descriptor is signed again and
        /// served, the session info follows, and the TopiaForge service gets the newer descriptor.
        /// </summary>
        internal void UpdateWorld(string? gamemode, string? world)
        {
            if (network == null || stopped || packages == null) throw new InvalidOperationException("The host is not running.");
            settings.Gamemode = gamemode;
            settings.World = world;
            Descriptor = Sign(packages);
            descriptorEndpoint?.SetDescriptor(Descriptor);
            RefreshInfo(force: true);
            Internet?.Republish(Descriptor);
        }

        /// <summary>Main thread, every frame: admits new links to the engine and keeps the session info current.</summary>
        internal void Pump()
        {
            if (network == null || stopped) return;
            while (network.TryTakeEvent(out var networkEvent))
            {
                if (networkEvent.Kind == NetworkEventKind.LinkAccepted)
                {
                    engine.Accept(networkEvent.Link!);
                }
                else if (networkEvent.Kind == NetworkEventKind.Stopped)
                {
                    // The network gave up after failing too often: every player's session ends and the host plays on.
                    Failure = LiveFailure.NetworkStopped;
                    Stop();
                    return;
                }
            }

            RefreshInfo(force: false);
            Internet?.Pump();
        }

        /// <summary>Ends one player's connection with a message.</summary>
        internal bool Kick(ParticipantId participant, string message) =>
            engine.Kick(participant, MultiplayerSessionEndReason.Kicked, message);

        /// <summary>Bans the identity behind <paramref name="participant"/> and ends its connection; false for the host itself.</summary>
        internal bool Ban(ParticipantId participant, string displayName)
        {
            var peer = engine.ParticipantIdentityOf(participant);
            if (peer == null) return false;
            Bans.Add(peer, displayName, unixSeconds());
            engine.Kick(participant, MultiplayerSessionEndReason.Banned, "You are banned from this session.");
            return true;
        }

        /// <summary>Stops hosting: every player's session ends, and this process plays on alone in the same session.</summary>
        internal void Stop()
        {
            if (stopped) return;
            stopped = true;
            if (engine.Role == EngineRole.Host && !engine.IsEnded) engine.CloseConnections(MultiplayerSessionEndReason.HostEnded, "The host stopped hosting.");
            if (ReferenceEquals(engine.Options.JoinPolicy, Policy)) engine.Options.JoinPolicy = null;
            Policy.Wipe();

            // The listing is deleted. The NAT layer is left to the network, which closes the links first, so the message
            // ending each player's session still leaves through the relay, and then says goodbye and releases the relay.
            Internet?.Stop();
            network?.Stop(StopGraceMicros);
        }

        public void Dispose() => Stop();

        private bool TryBind(INetworkFactory factory, int port)
        {
            var options = transportOptions();
            options.RelayOnly = !settings.AllowDirect;
            options.MaximumConnections = Math.Max(4, settings.MaxPlayers * 2);
            var endpoint = new TransportEndpoint(identity.CopyTransportPrivateKey(), options, clock());
            var stack = new NetworkStack(endpoint, key => new MultiplayerPlayerKey(identity.PlayerKeyFor(key)), MaximumLinksPerSource, factory.SourceOf, factory.NetworkOf);
            INetworkControl started;
            TraversalNetwork? shared = null;
            try
            {
                if (services != null)
                {
                    // Reached through the service: the NAT layer shares the game socket, relay-only unless the player allowed direct play.
                    var nat = natOptions();
                    nat.RelayOnly = !settings.AllowDirect;
                    shared = factory.StartTraversal(port, stack, nat);
                    started = shared.Network;
                }
                else
                {
                    started = factory.Start(port, stack);
                }
            }
            catch (SocketException)
            {
                stack.Dispose();
                return false;
            }

            DescriptorEndpoint? tcp;
            try
            {
                tcp = factory.CreateDescriptorEndpoint(started.LocalPort, !settings.AllowDirect);
            }
            catch (SocketException)
            {
                // The game port's number is taken for TCP; try another pair.
                started.Stop(0);
                return false;
            }

            if (tcp != null) started.Attach(tcp);
            network = started;
            traversal = shared;
            descriptorEndpoint = tcp;
            Port = started.LocalPort;
            return true;
        }

        private SignedServerDescriptor Sign(IReadOnlyList<DescriptorPackage> packages)
        {
            var descriptor = new ServerDescriptor(
                identity.SigningPublicKey,
                identity.TransportPublicKey,
                unixSeconds(),
                settings.Name,
                content.GameBuild,
                content.RuntimeVersion,
                SessionEngine.ProtocolVersion,
                settings.MaxPlayers,
                settings.Password != null,
                offersVoice,
                settings.Visibility,
                false,
                settings.Gamemode,
                settings.World,
                packages);
            var seed = identity.CopySigningSeed();
            try
            {
                return SignedServerDescriptor.Sign(descriptor, seed);
            }
            finally
            {
                Array.Clear(seed, 0, seed.Length);
            }
        }

        /// <summary>Counts the players connected now, this host's own included.</summary>
        private int ConnectedPlayers()
        {
            var players = 0;
            foreach (var participant in engine.Snapshot.Participants)
            {
                if (participant.IsConnected) players++;
            }

            return players;
        }

        private void RefreshInfo(bool force)
        {
            var players = ConnectedPlayers();
            if (!force && players == lastPlayers) return;
            lastPlayers = players;
            var info = new SessionInfo(
                identity.ServerId,
                Descriptor!.Sha256,
                settings.Name,
                content.GameBuild,
                content.RuntimeVersion,
                SessionEngine.ProtocolVersion,
                settings.Gamemode,
                settings.World,
                Math.Min(players, settings.MaxPlayers),
                settings.MaxPlayers,
                settings.Password != null,
                offersVoice).Encode();
            network!.Post((stack, _) => stack.Endpoint.SetInfo(info));
        }
    }
}
