using System;
using System.Collections.Concurrent;
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
        private readonly Thread thread;
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

        /// <summary>Starts a connection; the attempt fills in from the network thread.</summary>
        internal ConnectAttempt Connect(TransportPath path, byte[]? expectedKey)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var attempt = new ConnectAttempt();
            var key = expectedKey == null ? null : (byte[])expectedKey.Clone();
            Post((network, now) => network.Connect(attempt, path, key, now));
            return attempt;
        }

        internal bool TryTakeEvent(out NetworkEvent networkEvent) => events.TryDequeue(out networkEvent!);

        /// <summary>
        /// Closes every link gracefully and stops once they have gone or <paramref name="gracefulMicros"/> passes, then
        /// disposes the stack, which wipes the transport's keys, and the socket. Blocks until the thread has ended.
        /// </summary>
        internal void Stop(long gracefulMicros)
        {
            if (Interlocked.Exchange(ref stopRequested, 1) == 0)
            {
                // The deadline is set here, not by a command, so the loop ends on time even if its queue never drains.
                Interlocked.Exchange(ref stopDeadline, clock() + Math.Max(0, gracefulMicros));
                Post((network, _) => network.CloseAll());
            }

            if (Interlocked.CompareExchange(ref started, 2, 0) == 0)
            {
                // Never started: nothing runs the loop, so release its resources here, and Start now refuses.
                stack.Dispose();
                socket.Dispose();
                return;
            }

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

                    var wait = Math.Min(stack.NextTimeoutMicros() - clock(), MaximumWaitMicros);
                    if (wait > 0) socket.WaitReadable(wait);
                }
            }
            finally
            {
                stack.Dispose();
                socket.Dispose();
                events.Enqueue(NetworkEvent.Ended());
            }
        }

        private void Turn(long now)
        {
            for (var count = 0; count < MaximumCommandsPerTurn && commands.TryDequeue(out var command); count++) command(stack, now);
            for (var count = 0; count < MaximumDatagramsPerTurn && socket.TryReceive(buffer, out var length, out var path); count++)
            {
                stack.Receive(path!, buffer, 0, length, now);
            }

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
            while (stack.Endpoint.TryReceiveInfo(out var reply)) events.Enqueue(NetworkEvent.Received(reply));
        }
    }
}
