using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Identifies the physical/logical role represented by a multiplayer test node.</summary>
    public enum MultiplayerTestRole
    {
        /// <summary>One interactive process contains both logical sides without a transport hop.</summary>
        Standalone = 0,

        /// <summary>An interactive process contains only the logical client side.</summary>
        RemoteClient = 1,

        /// <summary>An interactive process contains both the logical client and server sides.</summary>
        ListenServer = 2,

        /// <summary>A headless process contains only the logical server side.</summary>
        DedicatedServer = 3
    }

    /// <summary>
    /// Deterministic packet conditions applied by <see cref="MultiplayerTestRig"/>. The rig models the live session
    /// channel, which is reliable and ordered: a lost or delayed frame holds every later frame on the same link
    /// behind it, and duplicates are discarded.
    /// </summary>
    public sealed class MultiplayerNetworkConditions
    {
        /// <summary>Creates deterministic packet conditions.</summary>
        /// <param name="latencyTicks">Base one-way latency in virtual network ticks.</param>
        /// <param name="dropEvery">Loses the first attempt of every Nth frame, so it arrives after the retry delay; zero disables loss.</param>
        /// <param name="duplicateEvery">Duplicates every Nth frame; the receiver discards the copy. Zero disables duplication.</param>
        /// <param name="reorderEvery">Delays every Nth frame by one extra tick, holding later frames behind it; zero disables it.</param>
        /// <param name="retryDelayTicks">Delay before a deliberately lost frame is retried.</param>
        public MultiplayerNetworkConditions(
            int latencyTicks = 0,
            int dropEvery = 0,
            int duplicateEvery = 0,
            int reorderEvery = 0,
            int retryDelayTicks = 1)
        {
            if (latencyTicks < 0) throw new ArgumentOutOfRangeException(nameof(latencyTicks));
            if (dropEvery < 0) throw new ArgumentOutOfRangeException(nameof(dropEvery));
            if (duplicateEvery < 0) throw new ArgumentOutOfRangeException(nameof(duplicateEvery));
            if (reorderEvery < 0) throw new ArgumentOutOfRangeException(nameof(reorderEvery));
            if (retryDelayTicks < 1) throw new ArgumentOutOfRangeException(nameof(retryDelayTicks));
            LatencyTicks = latencyTicks;
            DropEvery = dropEvery;
            DuplicateEvery = duplicateEvery;
            ReorderEvery = reorderEvery;
            RetryDelayTicks = retryDelayTicks;
        }

        private MultiplayerNetworkConditions(MultiplayerNetworkConditions source, int downstreamRateHz, int bandwidthBytesPerSecond)
            : this(source.LatencyTicks, source.DropEvery, source.DuplicateEvery, source.ReorderEvery, source.RetryDelayTicks)
        {
            DownstreamRateHz = downstreamRateHz;
            BandwidthBytesPerSecond = bandwidthBytesPerSecond;
        }

        /// <summary>Gets base one-way latency in virtual network ticks.</summary>
        public int LatencyTicks { get; }

        /// <summary>Gets the frame-loss cadence, or zero when disabled.</summary>
        public int DropEvery { get; }

        /// <summary>Gets the frame-duplication cadence, or zero when disabled.</summary>
        public int DuplicateEvery { get; }

        /// <summary>Gets the frame-delay cadence, or zero when disabled.</summary>
        public int ReorderEvery { get; }

        /// <summary>Gets the retry delay after deliberate frame loss.</summary>
        public int RetryDelayTicks { get; }

        /// <summary>
        /// Gets how many times a second the host sends coalesced changes, or zero to send them at every tick. Live
        /// sessions use 10.
        /// </summary>
        public int DownstreamRateHz { get; }

        /// <summary>Gets each link direction's capacity in bytes per second, or zero for unlimited.</summary>
        public int BandwidthBytesPerSecond { get; }

        /// <summary>Returns a copy whose host sends coalesced changes at most <paramref name="hertz"/> times a second.</summary>
        /// <param name="hertz">The downstream rate; zero sends coalesced changes at every tick, the rig's default.</param>
        public MultiplayerNetworkConditions WithDownstreamRate(int hertz)
        {
            if (hertz < 0 || hertz > 1000) throw new ArgumentOutOfRangeException(nameof(hertz));
            return new MultiplayerNetworkConditions(this, hertz, BandwidthBytesPerSecond);
        }

        /// <summary>Returns a copy whose links carry at most <paramref name="bytesPerSecond"/> bytes a second each way.</summary>
        /// <param name="bytesPerSecond">The capacity; zero leaves links unlimited.</param>
        public MultiplayerNetworkConditions WithBandwidth(int bytesPerSecond)
        {
            if (bytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
            return new MultiplayerNetworkConditions(this, DownstreamRateHz, bytesPerSecond);
        }
    }

    /// <summary>A process-sized participant in a deterministic multiplayer test.</summary>
    public sealed class MultiplayerTestNode
    {
        internal MultiplayerTestNode(
            MultiplayerTestRole role,
            SessionEngine engine,
            EngineSessionFacade facade,
            ParticipantId? assignedParticipantId,
            string displayName)
        {
            Role = role;
            Engine = engine;
            Facade = facade;
            ParticipantId = assignedParticipantId;
            DisplayName = displayName;
        }

        /// <summary>Gets the physical/logical test role.</summary>
        public MultiplayerTestRole Role { get; }

        /// <summary>Gets the same transport-neutral session interface exposed to a mod.</summary>
        public IMultiplayerSession Session => Facade;

        /// <summary>
        /// Gets the participant the rig assigned to this node, or null for a headless dedicated server. A client knows
        /// it from the moment it is added and keeps it while disconnected; read
        /// <see cref="MultiplayerSessionSnapshot.LocalParticipantId"/> for the session's live view.
        /// </summary>
        public ParticipantId? ParticipantId { get; }

        /// <summary>Gets whether admission and the initial canonical snapshot are complete.</summary>
        public bool IsReady => Session.Snapshot.State == MultiplayerSessionState.Ready;

        internal SessionEngine Engine { get; }

        internal EngineSessionFacade Facade { get; }

        internal string DisplayName { get; }

        internal VirtualLinkEnd? Link { get; set; }

        internal List<VirtualLinkPair> LinkHistory { get; } = new List<VirtualLinkPair>();

        internal ulong StalledUntilTick { get; set; }
    }

    /// <summary>
    /// Runs deterministic standalone, listen-server, and dedicated-server multiplayer simulations on the same session
    /// engine the live provider uses, connected by a virtual transport instead of sockets. Tests advance virtual ticks
    /// explicitly, so latency and packet faults are reproducible.
    /// </summary>
    public sealed class MultiplayerTestRig : IDisposable
    {
        /// <summary>Gets the deterministic number of network ticks in one virtual rate-limit second.</summary>
        public const ulong TicksPerSecond = 60;

        internal const string DefaultOwnerModId = "topiaforge.multiplayer-test";
        private const string SessionId = "topiaforge-test-session";
        private const ulong InitialSeed = 0x544f504941544553UL;

        private readonly List<MultiplayerTestNode> nodes = new List<MultiplayerTestNode>();
        private readonly RigDiagnostics diagnostics = new RigDiagnostics();
        private readonly FixedStepClock clock = new FixedStepClock((int)TicksPerSecond);
        private readonly EngineOptions options;
        private readonly VirtualEngineTransport network;
        private ulong currentTick;
        private bool disposed;

        private MultiplayerTestRig(MultiplayerTestRole serverRole, MultiplayerNetworkConditions conditions)
        {
            Conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
            options = new EngineOptions
            {
                StepRateHz = (int)TicksPerSecond,
                DownstreamRateHz = conditions.DownstreamRateHz,
                ClockSyncIntervalMicros = 0,
                MaximumParticipants = 256,
                ObjectIdPrefix = "test-object-",
                CallbackFailures = EngineCallbackFailureMode.Propagate,
                Diagnostics = diagnostics,
                ParticipantIdForIdentity = identity => new ParticipantId(identity)
            };
            network = new VirtualEngineTransport(new VirtualLinkConditions
            {
                LatencyTicks = conditions.LatencyTicks,
                DropEvery = conditions.DropEvery,
                DuplicateEvery = conditions.DuplicateEvery,
                ReorderEvery = conditions.ReorderEvery,
                RetryDelayTicks = conditions.RetryDelayTicks,
                BandwidthBytesPerSecond = conditions.BandwidthBytesPerSecond,
                TicksPerSecond = (int)TicksPerSecond
            });
            Network = new MultiplayerTestNetwork(this, network);
            var isDedicated = serverRole == MultiplayerTestRole.DedicatedServer;
            ParticipantId? hostId = isDedicated ? (ParticipantId?)null : new ParticipantId("host");
            var engine = SessionEngine.CreateHost(
                options,
                isDedicated ? MultiplayerProcessKind.Headless : MultiplayerProcessKind.Interactive,
                new MultiplayerSessionId(SessionId),
                new SessionSeed(InitialSeed),
                hostId,
                "Host",
                0);
            Server = new MultiplayerTestNode(serverRole, engine, engine.CreateFacade(DefaultOwnerModId), hostId, "Host");
            nodes.Add(Server);
        }

        /// <summary>Gets the deterministic transport conditions.</summary>
        public MultiplayerNetworkConditions Conditions { get; }

        /// <summary>Gets the process containing the canonical logical server.</summary>
        public MultiplayerTestNode Server { get; }

        /// <summary>Gets all current process-sized nodes in creation order.</summary>
        public IReadOnlyList<MultiplayerTestNode> Nodes => nodes;

        /// <summary>Gets the current canonical virtual network tick.</summary>
        public NetworkTick Tick => new NetworkTick(currentTick);

        /// <summary>Gets scripted link faults: holds, partitions, process stalls, permanent loss and replays.</summary>
        public MultiplayerTestNetwork Network { get; }

        private long NowMicros => clock.StepStartMicros(currentTick);

        /// <summary>Creates an interactive standalone session: one process with both sides and no transport hop.</summary>
        public static MultiplayerTestRig CreateStandalone(MultiplayerNetworkConditions? conditions = null) =>
            new MultiplayerTestRig(MultiplayerTestRole.Standalone, conditions ?? new MultiplayerNetworkConditions());

        /// <summary>Creates an interactive listen server to which remote clients may be added.</summary>
        public static MultiplayerTestRig CreateListenServer(MultiplayerNetworkConditions? conditions = null) =>
            new MultiplayerTestRig(MultiplayerTestRole.ListenServer, conditions ?? new MultiplayerNetworkConditions());

        /// <summary>Creates a headless dedicated server to which remote clients may be added.</summary>
        public static MultiplayerTestRig CreateDedicatedServer(MultiplayerNetworkConditions? conditions = null) =>
            new MultiplayerTestRig(MultiplayerTestRole.DedicatedServer, conditions ?? new MultiplayerNetworkConditions());

        /// <summary>
        /// Adds a remote client that joins over the virtual transport. It is not ready until the transport is
        /// advanced: the host admits it, installs its canonical state/object snapshot, and only then reports Ready.
        /// </summary>
        public MultiplayerTestNode AddRemoteClient(string participantId, string? displayName = null)
        {
            ThrowIfDisposed();
            var id = new ParticipantId(participantId);
            foreach (var existing in nodes)
            {
                if (existing.ParticipantId.HasValue && existing.ParticipantId.Value.Equals(id))
                {
                    throw new InvalidOperationException("Participant '" + id + "' is already admitted.");
                }
            }

            var engine = SessionEngine.CreateClient(options, new MultiplayerSessionId(SessionId), NowMicros);
            var node = new MultiplayerTestNode(
                MultiplayerTestRole.RemoteClient,
                engine,
                engine.CreateFacade(DefaultOwnerModId),
                id,
                displayName ?? participantId);
            nodes.Add(node);
            Join(node);
            ThrowIfFaulted();
            return node;
        }

        /// <summary>
        /// Disconnects a remote client. Both sides observe it at once: pending work is cancelled, participant
        /// snapshots are refreshed, and canonical ownership held by that participant is released.
        /// </summary>
        public void Disconnect(MultiplayerTestNode client)
        {
            ValidateRemoteClient(client);
            var link = client.Link;
            if (link == null) return;
            client.Link = null;
            network.Sever(link);
            PumpAll();
            ThrowIfFaulted();
        }

        /// <summary>
        /// Rejoins an existing remote client over a new connection, disconnecting it first if needed. Pending
        /// predicted work is cancelled, and the latest canonical snapshot is installed before the next Ready.
        /// </summary>
        public void Reconnect(MultiplayerTestNode client)
        {
            ValidateRemoteClient(client);
            if (client.Link != null)
            {
                network.Sever(client.Link);
                client.Link = null;
                PumpAll();
            }

            Join(client);
            ThrowIfFaulted();
        }

        /// <summary>
        /// Interrupts a client's path without closing it, as a brief outage does. Frames are held both ways, the
        /// client reports <see cref="MultiplayerSessionState.Reconnecting"/>, and the host reports the participant's
        /// connection as poor until <see cref="Resume"/>.
        /// </summary>
        public void Interrupt(MultiplayerTestNode client)
        {
            var link = RequireLink(client);
            SetPath(link, EngineLinkState.Stalled, held: true);
            PumpAll();
            ThrowIfFaulted();
        }

        /// <summary>Restores an interrupted path: held frames arrive in order and the client returns to Ready.</summary>
        public void Resume(MultiplayerTestNode client)
        {
            var link = RequireLink(client);
            SetPath(link, EngineLinkState.Open, held: false);
            PumpAll();
            ThrowIfFaulted();
        }

        /// <summary>
        /// Removes a client from the session as its host would. The client's session ends with
        /// <see cref="MultiplayerSessionEndReason.Kicked"/> once the notice reaches it.
        /// </summary>
        public void Kick(MultiplayerTestNode client)
        {
            RequireLink(client);
            Server.Engine.Kick(client.ParticipantId!.Value, MultiplayerSessionEndReason.Kicked, "The host removed this player from the session.");
            ThrowIfFaulted();
        }

        /// <summary>Gets the bytes and frames a node has sent and received so far.</summary>
        public MultiplayerTestTraffic Traffic(MultiplayerTestNode node)
        {
            RequireNode(node);
            long bytesSent = 0, framesSent = 0, bytesReceived = 0, framesReceived = 0;
            foreach (var client in nodes)
            {
                if (client.Role != MultiplayerTestRole.RemoteClient) continue;
                if (!ReferenceEquals(node, Server) && !ReferenceEquals(node, client)) continue;
                foreach (var pair in client.LinkHistory)
                {
                    var outbound = ReferenceEquals(node, Server) ? pair.HostEnd.Outbound! : pair.ClientEnd.Outbound!;
                    var inbound = ReferenceEquals(node, Server) ? pair.ClientEnd.Outbound! : pair.HostEnd.Outbound!;
                    bytesSent += outbound.BytesSent;
                    framesSent += outbound.FramesSent;
                    bytesReceived += inbound.BytesDelivered;
                    framesReceived += inbound.FramesDelivered;
                }
            }

            return new MultiplayerTestTraffic(bytesSent, framesSent, bytesReceived, framesReceived);
        }

        /// <summary>
        /// Creates another session facade on <paramref name="node"/> owned by <paramref name="modId"/>, as each mod in a
        /// process gets its own. Registrations are keyed by owner, so two mods may use the same wire ids.
        /// </summary>
        public IMultiplayerSession CreateOwnerSession(MultiplayerTestNode node, string modId)
        {
            RequireNode(node);
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            return node.Engine.CreateFacade(modId);
        }

        /// <summary>
        /// Replaces the current simulated match while preserving each node's stable session facade and generated
        /// registrations. Session-scoped state resets to declared defaults and connected clients resynchronize.
        /// </summary>
        public void StartNewSession(string sessionId)
        {
            ThrowIfDisposed();
            var id = new MultiplayerSessionId(sessionId);
            if (id.Equals(Server.Session.Snapshot.Id))
            {
                throw new ArgumentException("The replacement session id must differ from the current session id.", nameof(sessionId));
            }

            Server.Engine.ReplaceSession(id, new SessionSeed(Server.Session.Snapshot.Seed.Value + 1));
            ThrowIfFaulted();
        }

        /// <summary>Advances the virtual transport and canonical clock by an exact number of ticks.</summary>
        public void Advance(int ticks = 1)
        {
            ThrowIfDisposed();
            if (ticks < 1) throw new ArgumentOutOfRangeException(nameof(ticks));
            for (var index = 0; index < ticks; index++)
            {
                PumpAll();
                currentTick++;
                network.Now = currentTick;
                network.DeliverDue(currentTick);
                PumpAll();
                ThrowIfFaulted();
            }
        }

        /// <summary>Advances deterministic virtual time, including rate-limit windows, without using wall-clock time.</summary>
        public void AdvanceTime(TimeSpan duration)
        {
            ThrowIfDisposed();
            if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
            var exactTicks = duration.TotalSeconds * TicksPerSecond;
            if (exactTicks > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(duration));
            Advance(Math.Max(1, checked((int)Math.Ceiling(exactTicks))));
        }

        /// <summary>Advances until no frame is in flight and no node holds unsent or undelivered work.</summary>
        public void Flush(int maximumTicks = 256)
        {
            ThrowIfDisposed();
            if (maximumTicks < 1) throw new ArgumentOutOfRangeException(nameof(maximumTicks));
            PumpAll();
            ThrowIfFaulted();
            var count = 0;
            while (HasPendingWork() && count++ < maximumTicks) Advance();
            if (HasPendingWork())
            {
                throw new InvalidOperationException("The multiplayer test transport did not settle within the requested tick budget.");
            }
        }

        /// <summary>Finds a replicated object handle on one node after its spawn/snapshot packet arrives.</summary>
        public IReplicatedObject<TState, TInput> GetObject<TState, TInput>(MultiplayerTestNode node, NetworkObjectId id)
            where TState : class
            where TInput : class
        {
            RequireNode(node);
            return node.Engine.FindObject<TState, TInput>(id) ??
                throw new KeyNotFoundException("Replicated object '" + id + "' is not present with the requested generated types.");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (var index = nodes.Count - 1; index >= 0; index--)
            {
                var engine = nodes[index].Engine;
                engine.Shutdown(engine.Role == EngineRole.Host
                    ? MultiplayerSessionEndReason.HostEnded
                    : MultiplayerSessionEndReason.Left);
            }

            nodes.Clear();
        }

        internal void RequireNode(MultiplayerTestNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            ThrowIfDisposed();
            if (!nodes.Contains(node)) throw new ArgumentException("The node does not belong to this rig.", nameof(node));
        }

        private VirtualLinkEnd RequireLink(MultiplayerTestNode client)
        {
            ValidateRemoteClient(client);
            return client.Link ?? throw new InvalidOperationException("Participant '" + client.ParticipantId + "' is not connected.");
        }

        private static void SetPath(VirtualLinkEnd clientEnd, EngineLinkState state, bool held)
        {
            foreach (var end in new[] { clientEnd, clientEnd.Peer! })
            {
                if (end.State != EngineLinkState.Closed) end.State = state;
                end.Outbound!.Held = held;
            }
        }

        private void Join(MultiplayerTestNode client)
        {
            var pair = network.Connect(client.ParticipantId!.Value.Value);
            client.LinkHistory.Add(pair);
            client.Link = pair.ClientEnd;
            Server.Engine.Accept(pair.HostEnd);
            client.Engine.Connect(pair.ClientEnd, client.DisplayName);
        }

        private void PumpAll()
        {
            network.Now = currentTick;
            var now = NowMicros;
            foreach (var node in nodes.ToArray())
            {
                if (!node.Engine.IsEnded && currentTick >= node.StalledUntilTick) node.Engine.Pump(now);
            }
        }

        private bool HasPendingWork()
        {
            if (network.HasInFlight) return true;
            foreach (var node in nodes)
            {
                if (node.Engine.IsEnded) continue;
                if (currentTick < node.StalledUntilTick || node.Engine.HasPendingWork) return true;
                if (node.Link != null && node.Link.PendingFrames > 0) return true;
            }

            return false;
        }

        private void ThrowIfFaulted()
        {
            var fault = diagnostics.TakeFault();
            if (fault != null) throw new InvalidOperationException("The multiplayer session engine reported a fault: " + fault);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(MultiplayerTestRig));
        }

        private void ValidateRemoteClient(MultiplayerTestNode client)
        {
            RequireNode(client);
            if (client.Role != MultiplayerTestRole.RemoteClient || !nodes.Contains(client))
            {
                throw new ArgumentException("Only a remote client owned by this rig can change connection state.", nameof(client));
            }
        }

        /// <summary>Collects engine faults: protocol violations and values a local codec could not decode.</summary>
        private sealed class RigDiagnostics : IEngineDiagnostics
        {
            private readonly Queue<EngineDiagnostic> faults = new Queue<EngineDiagnostic>();

            public void Report(EngineDiagnostic diagnostic) => faults.Enqueue(diagnostic);

            internal EngineDiagnostic? TakeFault() => faults.Count == 0 ? null : faults.Dequeue();
        }
    }
}
