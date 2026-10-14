using System;
using System.Net.Sockets;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>Creates the networks live sessions run on: real sockets and threads in the game, a virtual network in tests.</summary>
    internal interface INetworkFactory
    {
        /// <summary>Starts a network that owns <paramref name="stack"/>, on <paramref name="port"/> or any free port when zero.</summary>
        INetworkControl Start(int port, NetworkStack stack);

        /// <summary>Creates the host's descriptor endpoint on <paramref name="port"/>, or null where the network serves none.</summary>
        DescriptorEndpoint? CreateDescriptorEndpoint(int port, bool relayOnly);

        /// <summary>Creates the host's LAN announcer, or null where the network has no LAN.</summary>
        LanAnnouncer? CreateAnnouncer(LanBeacon beacon);
    }

    /// <summary>The game's networks: a <see cref="NetworkThread"/> over a <see cref="UdpSocketDriver"/>, with TCP and LAN services.</summary>
    internal sealed class SocketNetworkFactory : INetworkFactory
    {
        private readonly Func<long> clock;

        internal SocketNetworkFactory(Func<long> clock)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public INetworkControl Start(int port, NetworkStack stack)
        {
            var thread = new NetworkThread("TopiaForge multiplayer network", new UdpSocketDriver(port), stack, clock);
            thread.Start();
            return thread;
        }

        public DescriptorEndpoint? CreateDescriptorEndpoint(int port, bool relayOnly) => new DescriptorEndpoint(port, relayOnly: relayOnly);

        public LanAnnouncer? CreateAnnouncer(LanBeacon beacon) => new LanAnnouncer(beacon);
    }

    /// <summary>
    /// A listen host: this process's session, open to other players. Starting binds the game port, the descriptor
    /// endpoint on the same port number and, for LAN or public visibility, the LAN announcer; it signs and serves the
    /// descriptor, answers info queries, and admits players through <see cref="HostPolicy"/>. The engine keeps running
    /// the same session throughout: hosting adds links to it, and stopping closes them while the host plays on.
    /// </summary>
    internal sealed class LiveHost : IDisposable
    {
        private const int PortAttempts = 4;
        private const long StopGraceMicros = 250_000;

        private readonly SessionEngine engine;
        private readonly LocalIdentity identity;
        private readonly HostSettings settings;
        private readonly ILiveSessionContent content;
        private readonly Func<long> clock;
        private readonly Func<long> unixSeconds;
        private INetworkControl? network;
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
            byte[]? passwordKey)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.unixSeconds = unixSeconds ?? throw new ArgumentNullException(nameof(unixSeconds));
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

        /// <summary>
        /// Binds the network and starts serving. Returns <see cref="LiveFailure.PortInUse"/> when no port could be bound;
        /// the session is then untouched.
        /// </summary>
        internal LiveFailure Start(INetworkFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (network != null) throw new InvalidOperationException("The host has already started.");
            var port = settings.Port;
            for (var attempt = 0; attempt < PortAttempts && network == null; attempt++)
            {
                if (TryBind(factory, port)) break;
                if (!settings.FallBackToAnyPort) return LiveFailure.PortInUse;
                port = 0;
            }

            if (network == null) return LiveFailure.PortInUse;
            Descriptor = Sign();
            descriptorEndpoint?.SetDescriptor(Descriptor);
            if (settings.Visibility != ServerVisibility.Invite)
            {
                var announcer = factory.CreateAnnouncer(new LanBeacon(identity.ServerId, Port));
                if (announcer != null) network.Attach(announcer);
            }

            engine.Options.MaximumParticipants = settings.MaxPlayers;
            engine.Options.JoinPolicy = Policy;
            RefreshInfo(force: true);
            return LiveFailure.None;
        }

        /// <summary>Main thread, every frame: admits new links to the engine and keeps the session info current.</summary>
        internal void Pump()
        {
            if (network == null || stopped) return;
            while (network.TryTakeEvent(out var networkEvent))
            {
                if (networkEvent.Kind == NetworkEventKind.LinkAccepted) engine.Accept(networkEvent.Link!);
            }

            RefreshInfo(force: false);
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
            network?.Stop(StopGraceMicros);
        }

        public void Dispose() => Stop();

        private bool TryBind(INetworkFactory factory, int port)
        {
            var endpoint = new TransportEndpoint(identity.CopyTransportPrivateKey(), new TransportOptions
            {
                RelayOnly = !settings.AllowDirect,
                MaximumConnections = Math.Max(4, settings.MaxPlayers * 2)
            }, clock());
            var stack = new NetworkStack(endpoint, key => new MultiplayerPlayerKey(identity.PlayerKeyFor(key)));
            INetworkControl started;
            try
            {
                started = factory.Start(port, stack);
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
            descriptorEndpoint = tcp;
            Port = started.LocalPort;
            return true;
        }

        private SignedServerDescriptor Sign()
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
                settings.Voice,
                settings.Visibility,
                false,
                settings.Gamemode,
                settings.World,
                content.HostPackages());
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

        private void RefreshInfo(bool force)
        {
            var players = 0;
            foreach (var participant in engine.Snapshot.Participants)
            {
                if (participant.IsConnected) players++;
            }

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
                settings.Voice).Encode();
            network!.Post((stack, _) => stack.Endpoint.SetInfo(info));
        }
    }
}
