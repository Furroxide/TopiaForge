using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    internal enum NetworkEventKind
    {
        /// <summary>A peer opened a link to this host.</summary>
        LinkAccepted = 0,

        /// <summary>A host answered an info query.</summary>
        InfoReceived = 1,

        /// <summary>One turn of the loop failed; the thread carries on.</summary>
        Fault = 2,

        /// <summary>The loop failed too often in a row and stopped; every link is closed.</summary>
        Stopped = 3
    }

    /// <summary>
    /// Something the network thread reports to the main thread. A fault names only the exception type, never its
    /// message, because socket messages can include addresses.
    /// </summary>
    internal sealed class NetworkEvent
    {
        private NetworkEvent(NetworkEventKind kind, NetworkLink? link, TransportInfoReply? info, string? fault)
        {
            Kind = kind;
            Link = link;
            Info = info;
            Fault = fault;
        }

        internal NetworkEventKind Kind { get; }
        internal NetworkLink? Link { get; }
        internal TransportInfoReply? Info { get; }
        internal string? Fault { get; }

        internal static NetworkEvent Accepted(NetworkLink link) => new NetworkEvent(NetworkEventKind.LinkAccepted, link, null, null);

        internal static NetworkEvent Received(TransportInfoReply reply) => new NetworkEvent(NetworkEventKind.InfoReceived, null, reply, null);

        internal static NetworkEvent Failed(Exception exception) => new NetworkEvent(NetworkEventKind.Fault, null, null, exception.GetType().Name);

        internal static NetworkEvent Ended() => new NetworkEvent(NetworkEventKind.Stopped, null, null, null);
    }

    /// <summary>
    /// The thread that owns a socket, its <see cref="NetworkStack"/>, the transport's crypto and its timers, so live
    /// connections keep their keepalives and retransmissions going while the main thread stalls, as it does through a
    /// scene load. The main thread talks to it only through queues: commands in, events out, and each link's own
    /// bounded frame queues. The thread sleeps until its next timer or a datagram, and at most 2 ms, so frames the
    /// main thread queues go out within that.
    /// </summary>
    internal sealed class NetworkThread : IDisposable
    {
        internal const long MaximumWaitMicros = 2_000;
        private const int MaximumDatagramsPerTurn = 1024;
        private const int MaximumCommandsPerTurn = 256;
        private const int MaximumConsecutiveFaults = 64;

        private readonly IDatagramSocket socket;
        private readonly NetworkStack stack;
        private readonly Func<long> clock;
        private readonly ConcurrentQueue<Action<NetworkStack, long>> commands = new ConcurrentQueue<Action<NetworkStack, long>>();
        private readonly ConcurrentQueue<NetworkEvent> events = new ConcurrentQueue<NetworkEvent>();
        private readonly byte[] buffer = new byte[TransportOptions.MaximumDatagramBytes];
        private readonly List<INetworkService> services = new List<INetworkService>();
        private readonly object gate = new object();
        private readonly HashSet<INetworkService> owned = new HashSet<INetworkService>();
        private readonly HashSet<ConnectAttempt> unstarted = new HashSet<ConnectAttempt>();
        private readonly Thread thread;
        private bool closing;
        private long stopDeadline = long.MaxValue;
        private int stopRequested;
        private int started;

        internal NetworkThread(string name, IDatagramSocket socket, NetworkStack stack, Func<long> clock)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            this.stack = stack ?? throw new ArgumentNullException(nameof(stack));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            thread = new Thread(Run) { IsBackground = true, Name = name };
        }

        /// <summary>Gets the local port of the socket the thread owns.</summary>
        internal int LocalPort => socket.LocalPort;

        /// <summary>Gets whether the loop has ended.</summary>
        internal bool HasStopped => started != 0 && !thread.IsAlive;

        internal void Start()
        {
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) throw new InvalidOperationException("The network thread has already run or been stopped.");
            thread.Start();
        }

        /// <summary>Runs <paramref name="command"/> on the network thread, with the time it runs at.</summary>
        internal void Post(Action<NetworkStack, long> command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            commands.Enqueue(command);
        }

        /// <summary>
        /// Starts a connection; the attempt fills in from the network thread. Every attempt completes: one made once the
        /// thread is stopping, or still queued when it stops, fails.
        /// </summary>
        internal ConnectAttempt Connect(TransportPath path, byte[]? expectedKey)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var attempt = new ConnectAttempt();
            var key = expectedKey == null ? null : (byte[])expectedKey.Clone();
            lock (gate)
            {
                if (!closing)
                {
                    unstarted.Add(attempt);
                    Post((network, now) =>
                    {
                        if (Claim(attempt)) network.Connect(attempt, path, key, now);
                    });
                    return attempt;
                }
            }

            attempt.Fail(TransportCloseReason.Local);
            return attempt;
        }

        internal bool TryTakeEvent(out NetworkEvent networkEvent) => events.TryDequeue(out networkEvent!);

        /// <summary>
        /// Runs <paramref name="service"/> on the network thread from its next turn until it is detached. The thread owns
        /// it from this call: stopping disposes it even if it never ran, and a thread already stopping disposes it now.
        /// </summary>
        internal void Attach(INetworkService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            lock (gate)
            {
                if (!closing)
                {
                    owned.Add(service);
                    Post((_, _) => services.Add(service));
                    return;
                }
            }

            service.Dispose();
        }

        /// <summary>Stops running <paramref name="service"/> and disposes it, on the network thread.</summary>
        internal void Detach(INetworkService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            Post((_, _) =>
            {
                services.Remove(service);
                bool wasOwned;
                lock (gate) wasOwned = owned.Remove(service);
                if (wasOwned) service.Dispose();
            });
        }

        /// <summary>
        /// Closes every link gracefully and stops once they have gone or <paramref name="gracefulMicros"/> passes, then
        /// disposes the services, the stack, which wipes the transport's keys, and the socket. Nothing new is attached or
        /// connected from this call on. Blocks until the thread has ended.
        /// </summary>
        internal void Stop(long gracefulMicros)
        {
            if (Interlocked.Exchange(ref stopRequested, 1) == 0)
            {
                lock (gate) closing = true;

                // The deadline is set here, not by a command, so the loop ends on time even if its queue never drains.
                Interlocked.Exchange(ref stopDeadline, clock() + Math.Max(0, gracefulMicros));
                Post((network, _) => network.CloseAll());
            }

            var previous = Interlocked.CompareExchange(ref started, 2, 0);
            if (previous == 0)
            {
                // Never started: nothing runs the loop, so its resources are released here, and Start now refuses.
                ReleaseResources();
                return;
            }

            // A thread stopped before it started has nothing to wait for, however often it is stopped again.
            if (previous == 2) return;
            if (Thread.CurrentThread != thread) thread.Join();
        }

        public void Dispose() => Stop(0);

        private void Run()
        {
            var faults = 0;
            try
            {
                while (true)
                {
                    var now = clock();
                    try
                    {
                        Turn(now);
                        faults = 0;
                    }
                    catch (Exception exception) when (!(exception is ThreadAbortException))
                    {
                        events.Enqueue(NetworkEvent.Failed(exception));
                        if (++faults >= MaximumConsecutiveFaults) break;
                    }

                    if (Volatile.Read(ref stopRequested) != 0 &&
                        ((commands.IsEmpty && stack.LinkCount == 0) || clock() >= Interlocked.Read(ref stopDeadline)))
                    {
                        break;
                    }

                    var next = stack.NextTimeoutMicros();
                    foreach (var service in services) next = Math.Min(next, service.NextTimeoutMicros());
                    var wait = Math.Min(next - clock(), MaximumWaitMicros);
                    if (wait > 0) socket.WaitReadable(wait);
                }
            }
            finally
            {
                ReleaseResources();
                events.Enqueue(NetworkEvent.Ended());
            }
        }

        /// <summary>Takes an attempt's command off the books; false once shutdown has failed the attempt instead.</summary>
        private bool Claim(ConnectAttempt attempt)
        {
            lock (gate) return unstarted.Remove(attempt);
        }

        /// <summary>
        /// Releases what the thread owns without running anything still queued: every service, running or waiting to,
        /// every attempt whose command never ran, then the stack and the socket. Attaching or connecting is refused from now.
        /// </summary>
        private void ReleaseResources()
        {
            INetworkService[] remaining;
            ConnectAttempt[] abandoned;
            lock (gate)
            {
                closing = true;
                remaining = new INetworkService[owned.Count];
                owned.CopyTo(remaining);
                owned.Clear();
                abandoned = new ConnectAttempt[unstarted.Count];
                unstarted.CopyTo(abandoned);
                unstarted.Clear();
            }

            services.Clear();
            foreach (var service in remaining) service.Dispose();
            foreach (var attempt in abandoned) attempt.Fail(TransportCloseReason.Local);
            stack.Dispose();
            socket.Dispose();
        }

        private void Turn(long now)
        {
            for (var count = 0; count < MaximumCommandsPerTurn && commands.TryDequeue(out var command); count++) command(stack, now);
            for (var count = 0; count < MaximumDatagramsPerTurn && socket.TryReceive(buffer, out var length, out var path); count++)
            {
                // A datagram the socket dropped still counts against the turn, so a flood of them cannot starve the timers.
                if (path != null) stack.Receive(path, buffer, 0, length, now);
            }

            foreach (var service in services) service.Turn(stack, now);
            stack.Poll(now);
            while (stack.TryDequeueDatagram(out var datagram))
            {
                try
                {
                    socket.Send(datagram.Path, datagram.Buffer, 0, datagram.Length);
                }
                finally
                {
                    stack.Release(datagram);
                }
            }

            while (stack.TryAccept(out var link)) events.Enqueue(NetworkEvent.Accepted(link));
            while (stack.Endpoint.TryReceiveInfo(out var reply))
            {
                var claimed = false;
                foreach (var service in services) claimed = claimed || service.TryClaim(reply, now);
                if (!claimed) events.Enqueue(NetworkEvent.Received(reply));
            }
        }
    }
}
