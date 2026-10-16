using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.PortMapping;
using TopiaForge.Multiplayer.NatTraversal.Stun;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Transport;
using TopiaForge.Multiplayer.Transport.Packets;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>How a datagram that reached the shared socket is split by its first byte (RFC 7983).</summary>
    internal enum NatDatagramKind
    {
        /// <summary>Anything that is not STUN or ChannelData: the transport's own types, which it validates and drops if unknown.</summary>
        Transport = 0,

        /// <summary>First byte 0 to 3.</summary>
        Stun = 1,

        /// <summary>First byte 64 to 79: channels 0x4000 to 0x4FFF.</summary>
        ChannelData = 2
    }

    /// <summary>The first-byte split. The transport's own types are 0xE0 to 0xE5 and 0xE8, so the three can never overlap.</summary>
    internal static class NatDemux
    {
        internal static NatDatagramKind Classify(byte first)
        {
            if (first <= 0x03) return NatDatagramKind.Stun;
            if (first >= 0x40 && first <= 0x4F) return NatDatagramKind.ChannelData;
            return NatDatagramKind.Transport;
        }

        /// <summary>Gets whether the transport's own classification agrees that a byte belongs to the NAT layer.</summary>
        internal static bool AgreesWithTransport(byte first) => (Classify(first) != NatDatagramKind.Transport) == PacketType.IsForeign(first);
    }

    /// <summary>What the socket owner does with a datagram after the NAT layer saw it.</summary>
    internal enum NatReceiveResult
    {
        /// <summary>Hand the datagram to the transport unchanged.</summary>
        Transport = 0,

        /// <summary>Hand the unwrapped datagram to the transport, from the relayed path given.</summary>
        Relayed = 1,

        /// <summary>The NAT layer used it.</summary>
        Consumed = 2,

        /// <summary>It was malformed, unexpected or not allowed; nothing more happens.</summary>
        Dropped = 3
    }

    internal enum NatRole
    {
        None = 0,
        Joiner = 1,
        Host = 2
    }

    /// <summary>
    /// The NAT layer for one game socket, sans-I/O, on the network thread: it splits what the socket receives by first
    /// byte, runs STUN, the TURN allocation, candidate gathering, signaling and the connectivity checks of every session,
    /// wraps transport datagrams bound for relayed paths, and moves connections between paths through the transport's
    /// own validation. Whoever owns the socket (the network thread, or the virtual network in tests) hands it datagrams
    /// and the time, sends what it yields, and calls <see cref="Turn"/> once per turn. An agent with no session started
    /// contacts nothing at all, and nothing a peer or server sends makes it throw.
    /// </summary>
    internal sealed partial class NatAgent : IDisposable
    {
        internal const int MaximumQueuedDatagrams = 512;
        internal const int MaximumSessions = 32;

        private readonly NatAgentOptions options;
        private readonly StunClient stun;
        private readonly Queue<OutgoingDatagram> outbound = new Queue<OutgoingDatagram>();
        private readonly Stack<byte[]> pool = new Stack<byte[]>();
        private readonly StunMessageWriter writer = new StunMessageWriter();
        private readonly byte[] scratch = new byte[StunMessage.MaximumBytes];
        private readonly List<NatSession> sessions = new List<NatSession>();
        private readonly Dictionary<string, NatSession> byUfrag = new Dictionary<string, NatSession>(StringComparer.Ordinal);
        private readonly object gate = new object();
        private readonly List<ConnectAttempt> adopted = new List<ConnectAttempt>();
        private NatStatus status = NatStatus.Idle;
        private PortMapper? portMapper;
        private long currentTime;
        private bool forcedRelayOnly;
        private bool disposed;

        internal NatAgent(NatAgentOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            options.Validate();
            stun = new StunClient(options.Random, SendRaw);
        }

        internal NatAgentOptions Options => options;

        internal NatRole Role { get; private set; }

        /// <summary>
        /// Gets whether this session is relay-only: the agent's own option, or the transport's when the session started
        /// on a relay-only stack. Either one hides the player; neither can expose a player the other hides.
        /// </summary>
        internal bool RelayOnly => options.RelayOnly || forcedRelayOnly;

        /// <summary>Gets the latest status. The main thread may read it at any time.</summary>
        internal NatStatus Status => Volatile.Read(ref status);

        internal IReadOnlyList<NatSession> Sessions => sessions;

        internal StunClient Stun => stun;

        /// <summary>Gets how many datagrams this agent dropped because its own queue was full.</summary>
        internal long Overflow { get; private set; }

        /// <summary>Gets the port mapping of a host that shows its address; off for everything else.</summary>
        internal PortMappingState PortMapping => portMapper?.State ?? PortMappingState.Off;

        /// <summary>
        /// Takes one datagram from the socket. Transport datagrams pass straight through. STUN is answered or matched
        /// here; ChannelData and Data indications from this side's TURN server are unwrapped, and their contents go to the
        /// transport from the relayed path, or back through here when they are STUN themselves. A datagram from a source
        /// that cannot send (port 0, an unspecified, multicast or broadcast address) is dropped first. Never throws on input.
        /// </summary>
        internal NatReceiveResult Receive(TransportPath from, byte[] buffer, int offset, int count, long now, out TransportPath relayed, out int innerOffset, out int innerCount)
        {
            relayed = null!;
            innerOffset = offset;
            innerCount = count;
            if (from == null || buffer == null || count <= 0 || offset < 0 || offset > buffer.Length - count) return NatReceiveResult.Dropped;
            if (!NatAddresses.IsPlausibleSource(from)) return NatReceiveResult.Dropped;
            var kind = NatDemux.Classify(buffer[offset]);
            if (kind == NatDatagramKind.Transport) return NatReceiveResult.Transport;
            if (disposed) return NatReceiveResult.Dropped;
            if (Role == NatRole.None) return Draining(kind, from, buffer, offset, count, now);
            currentTime = now;
            var relay = RelayAt(from);
            if (kind == NatDatagramKind.ChannelData)
            {
                if (relay == null || !relay.TryUnwrap(buffer, offset, count, now, out var path, out var dataOffset, out var dataLength) || dataLength == 0) return NatReceiveResult.Dropped;
                return Inner(path, buffer, dataOffset, dataLength, buffer, offset, now, out relayed, out innerOffset, out innerCount);
            }

            // A peer's data through the relay, when no channel carries it: read in place, so the fallback allocates nothing.
            if (relay != null && StunMessage.IsDataIndication(buffer, offset, count))
            {
                if (!relay.TryReadData(buffer, offset, count, now, out var path, out var dataOffset, out var dataLength) || dataLength == 0) return NatReceiveResult.Dropped;
                return Inner(path, buffer, dataOffset, dataLength, buffer, offset, now, out relayed, out innerOffset, out innerCount);
            }

            if (!StunMessage.TryParse(buffer, offset, count, out var message)) return NatReceiveResult.Dropped;
            if (relay == null)
            {
                OnStun(from, message, now);
                return NatReceiveResult.Consumed;
            }

            if (message.Class == StunClass.Success || message.Class == StunClass.Error) stun.TryComplete(message, from, now);
            return NatReceiveResult.Consumed;
        }

        /// <summary>
        /// Wraps a transport datagram bound for a relayed path into <paramref name="output"/> for this side's TURN server.
        /// Returns the length, or 0 when it cannot go yet. Over a bound channel it allocates nothing.
        /// </summary>
        internal int Wrap(TransportPath path, byte[] data, int offset, int count, byte[] output, long now, out TransportPath server)
        {
            server = null!;
            if (disposed || !TurnRelayPath.TryGetAllocation(path, out var allocation)) return 0;
            var relay = RelayNumbered(allocation);
            if (relay == null) return 0;
            currentTime = now;
            var length = relay.Wrap(path, data, offset, count, output, now);
            if (length > 0) server = relay.Server;
            return length;
        }

        /// <summary>Runs every timer and decision for this turn; the transport's stack is there to query and move connections.</summary>
        internal void Turn(NetworkStack stack, long now)
        {
            if (stack == null) throw new ArgumentNullException(nameof(stack));
            if (disposed) return;
            if (Role == NatRole.None)
            {
                // Stopped: only releases still settling run, and only until they do.
                if (!ReleasesInFlight()) return;
                currentTime = now;
                stun.Poll(now);
                PollReleases();
                return;
            }

            currentTime = now;
            PollGathering(now);
            PollSignaling(now);
            stun.Poll(now);
            PollRelays(now);
            for (var index = 0; index < sessions.Count; index++) sessions[index].Turn(stack, now);
            PollRotation(stack, now);
            ExpireSessions(now);
            Publish();
        }

        internal long NextTimeoutMicros()
        {
            if (disposed) return long.MaxValue;
            if (Role == NatRole.None) return ReleasesInFlight() ? stun.NextTimeoutMicros() : long.MaxValue;
            var next = Math.Min(stun.NextTimeoutMicros(), GatheringTimeout());
            next = Math.Min(next, RelayTimeout());
            next = Math.Min(next, SignalingTimeout());
            foreach (var session in sessions) next = Math.Min(next, session.NextTimeoutMicros());
            return next;
        }

        /// <summary>Yields the next datagram the NAT layer itself sends: STUN, TURN and checks, already wrapped for relays.</summary>
        internal bool TryDequeueDatagram(out OutgoingDatagram datagram)
        {
            if (outbound.Count > 0)
            {
                datagram = outbound.Dequeue();
                return true;
            }

            datagram = default;
            return false;
        }

        internal void Release(OutgoingDatagram datagram)
        {
            if (datagram.Buffer != null && datagram.Buffer.Length == StunMessage.MaximumBytes && pool.Count < 64) pool.Push(datagram.Buffer);
        }

        /// <summary>
        /// Ends every session with a goodbye, closes signaling, stops the port mapping and releases the relay. What it
        /// queues still needs sending; the releases retransmit until the server answers, for as long as turns still run.
        /// </summary>
        internal void Stop(long now)
        {
            if (disposed || Role == NatRole.None) return;
            currentTime = now;
            foreach (var session in sessions) session.End(NatFailure.Cancelled, now, true);
            CloseSignaling();
            StopPortMapping();
            if (bindingOwner != null) stun.CancelAll(bindingOwner);
            ReleaseRelays(now);
            Role = NatRole.None;
            Publish();
        }

        /// <summary>
        /// Starts mapping the game port for a host that is hosting and shows its address. The mapping is the agent's from
        /// here: <see cref="Stop"/> and <see cref="Dispose"/> delete it, however hosting ends.
        /// </summary>
        internal void MapPort(PortMapper mapper, int port, Action<IPEndPoint> mapped)
        {
            if (mapper == null) throw new ArgumentNullException(nameof(mapper));
            if (mapped == null) throw new ArgumentNullException(nameof(mapped));
            if (disposed || Role != NatRole.Host || RelayOnly || portMapper != null || port < 1 || port > 65535) return;
            portMapper = mapper;
            mapper.Start(port, mapped);
        }

        /// <summary>
        /// Takes responsibility for an attempt from any thread before the command that starts it runs: if the agent is
        /// disposed first, as when the thread stops with the command still queued, the attempt fails rather than hang.
        /// </summary>
        internal void Adopt(ConnectAttempt attempt)
        {
            if (attempt == null) throw new ArgumentNullException(nameof(attempt));
            lock (gate)
            {
                if (!disposed)
                {
                    adopted.Add(attempt);
                    return;
                }
            }

            attempt.Fail(TransportCloseReason.Local);
        }

        /// <summary>Stops if still running; the socket owner flushes the releases this queues before closing the socket.</summary>
        public void Dispose()
        {
            if (disposed) return;
            Stop(currentTime);
            StopPortMapping();
            ConnectAttempt[] attempts;
            lock (gate)
            {
                disposed = true;
                attempts = adopted.ToArray();
                adopted.Clear();
            }

            foreach (var attempt in attempts)
            {
                if (attempt.State == ConnectAttemptState.Pending) attempt.Fail(TransportCloseReason.Local);
            }

            stun.Clear();
        }

        internal void Report(NatDiagnosticKind kind, string message) => options.Diagnostics?.Report(kind, message);

        /// <summary>Gets the time of the last call into the agent, for callbacks that carry none.</summary>
        internal long CurrentTime => currentTime;

        /// <summary>
        /// Sends a datagram the NAT layer made: STUN requests and responses, checks and keepalives. One bound for a
        /// relayed path goes through its allocation. Relay-only sends to nothing but its own TURN servers, whatever asks.
        /// </summary>
        internal void SendRaw(TransportPath destination, byte[] bytes, int length)
        {
            if (disposed || length <= 0) return;
            if (TurnRelayPath.TryGetAllocation(destination, out var allocation))
            {
                var relay = RelayNumbered(allocation);
                if (relay == null) return;
                var wrapped = Rent();
                var size = relay.Wrap(destination, bytes, 0, length, wrapped, currentTime);
                if (size == 0)
                {
                    pool.Push(wrapped);
                    return;
                }

                Enqueue(relay.Server, wrapped, size);
                return;
            }

            if (RelayOnly && !IsRelayServer(destination)) return;
            var copy = Rent();
            Buffer.BlockCopy(bytes, 0, copy, 0, length);
            Enqueue(destination, copy, length);
        }

        /// <summary>A stopped agent hears only the answers to its releases; anything else is dropped, as by an agent never started.</summary>
        private NatReceiveResult Draining(NatDatagramKind kind, TransportPath from, byte[] buffer, int offset, int count, long now)
        {
            if (kind != NatDatagramKind.Stun || !ReleasesInFlight() || !StunMessage.TryParse(buffer, offset, count, out var message)) return NatReceiveResult.Dropped;
            if (message.Class == StunClass.Success || message.Class == StunClass.Error) stun.TryComplete(message, from, now);
            return NatReceiveResult.Consumed;
        }

        private NatReceiveResult Inner(TransportPath path, byte[] source, int dataOffset, int dataLength, byte[] buffer, int offset, long now,
            out TransportPath relayed, out int innerOffset, out int innerCount)
        {
            relayed = null!;
            innerOffset = offset;
            innerCount = 0;
            var inner = NatDemux.Classify(source[dataOffset]);
            if (inner == NatDatagramKind.ChannelData || dataLength > TransportOptions.MaximumDatagramBytes) return NatReceiveResult.Dropped;
            if (inner == NatDatagramKind.Stun)
            {
                if (StunMessage.TryParse(source, dataOffset, dataLength, out var message)) OnStun(path, message, now);
                return NatReceiveResult.Consumed;
            }

            if (!ReferenceEquals(source, buffer) || dataOffset != offset) Buffer.BlockCopy(source, dataOffset, buffer, offset, dataLength);
            relayed = path;
            innerCount = dataLength;
            return NatReceiveResult.Relayed;
        }

        /// <summary>Responses complete their transactions; requests are connectivity checks; indications from peers are ignored.</summary>
        private void OnStun(TransportPath from, StunMessage message, long now)
        {
            if (message.Class == StunClass.Success || message.Class == StunClass.Error) stun.TryComplete(message, from, now);
            else if (message.Class == StunClass.Request && message.Method == StunMethod.Binding) OnCheck(from, message, now);
        }

        private void Enqueue(TransportPath destination, byte[] buffer, int length)
        {
            if (outbound.Count >= MaximumQueuedDatagrams)
            {
                pool.Push(buffer);
                Overflow++;
                return;
            }

            outbound.Enqueue(new OutgoingDatagram(destination, buffer, length));
        }

        private byte[] Rent() => pool.Count > 0 ? pool.Pop() : new byte[StunMessage.MaximumBytes];

        private void StopPortMapping()
        {
            var mapper = portMapper;
            portMapper = null;
            if (mapper == null) return;

            // Deleting the mapping runs on a pool thread; its outcome is best effort, and a failure is observed, never read.
            mapper.Stop().ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        private void ExpireSessions(long now)
        {
            for (var index = sessions.Count - 1; index >= 0; index--)
            {
                var session = sessions[index];
                if (!session.CanForget(now)) continue;
                ForgetPermissions(session);
                byUfrag.Remove(session.Checker.Local.Ufrag);
                sessions.RemoveAt(index);
            }
        }

        /// <summary>Replaces the status only when something in it changed, so a quiet turn allocates nothing.</summary>
        private void Publish()
        {
            var direct = 0;
            var relayedCount = 0;
            var live = 0;
            NatSession? join = null;
            foreach (var session in sessions)
            {
                if (session.IsJoin) join = session;
                if (session.State == NatSessionState.Ended || session.State == NatSessionState.Failed) continue;
                live++;
                if (session.Route == NatRoute.Direct) direct++;
                else if (session.Route == NatRoute.Relayed) relayedCount++;
            }

            var stage = join?.Stage ?? (Role == NatRole.Joiner ? lastJoinStage : NatJoinStage.Idle);
            var failure = join?.Failure ?? (Role == NatRole.Joiner ? lastJoinFailure : NatFailure.None);
            var route = join?.Route ?? NatRoute.None;
            var relayReady = RelayUp;
            var current = Volatile.Read(ref status);
            if (current.Gathering == gatheringOutcome && current.Stage == stage && current.Failure == failure && current.Route == route &&
                current.RelayReady == relayReady && current.Sessions == live && current.DirectSessions == direct &&
                current.RelayedSessions == relayedCount && current.Rotations == rotations && current.WithheldByService == withheldByService)
            {
                return;
            }

            Volatile.Write(ref status, new NatStatus(gatheringOutcome, stage, failure, route, relayReady, live, direct, relayedCount, rotations, withheldByService));
        }
    }
}
