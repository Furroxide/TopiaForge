extern alias provider;

using System.Security.Cryptography;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Link conditions every datagram on a <see cref="VirtualDatagramNetwork"/> meets.</summary>
internal sealed class NetworkConditions
{
    internal long LatencyMicros { get; set; } = 10_000;

    /// <summary>Gets or sets the extra delay drawn uniformly per datagram; any jitter reorders datagrams.</summary>
    internal long JitterMicros { get; set; }

    internal double LossRate { get; set; }

    internal double DuplicateRate { get; set; }
}

/// <summary>
/// One attachment point: a transport endpoint, a network stack around one, or a raw inbox a test reads and writes by hand. A node is reachable
/// three ways, as in the field: directly, through a relay, and on the LAN. A datagram sent to one of its addresses
/// arrives as if from the sender's address of the same kind, the way a relay shows each side only its channel.
/// </summary>
internal sealed class VirtualNode
{
    private readonly int index;
    private int rebinds;

    internal VirtualNode(string name, int index, TransportEndpoint? endpoint, NetworkStack? stack = null)
    {
        if (index < 1 || index > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(index));
        Name = name;
        Stack = stack;
        Endpoint = stack?.Endpoint ?? endpoint;
        this.index = index;
    }

    internal string Name { get; }

    internal TransportEndpoint? Endpoint { get; }

    /// <summary>Gets the network stack the node runs, which polls its endpoint and moves frames to its links.</summary>
    internal NetworkStack? Stack { get; }

    /// <summary>Gets the direct address; a NAT rebinding changes it.</summary>
    internal byte[] Address => AddressOf(TransportPathKind.Direct);

    internal Queue<(VirtualNode From, byte[] Bytes)> Inbox { get; } = new();

    /// <summary>Gets whether the node left the network: nothing reaches it any more, as with a closed socket.</summary>
    internal bool Removed { get; set; }

    /// <summary>
    /// Gets an address of the node. The node's index takes two bytes, so a long run that opens a network for every join
    /// never hands two nodes the same address; the first 255 nodes keep the addresses they always had.
    /// </summary>
    internal byte[] AddressOf(TransportPathKind kind) => kind switch
    {
        TransportPathKind.Relayed => new byte[] { 100, 64, (byte)(index >> 8), (byte)index, 0x0D, 0x96 },
        TransportPathKind.Lan => new byte[] { 192, 168, (byte)(index >> 8), (byte)index, 0x6A, 0x72 },
        _ => new byte[] { 10, (byte)rebinds, (byte)(index >> 8), (byte)index, 0x6A, 0x72 }
    };

    /// <summary>Gets whether <paramref name="key"/> is this node's address of <paramref name="kind"/>, without allocating.</summary>
    internal bool Owns(byte[] key, TransportPathKind kind)
    {
        if (key.Length != 6 || key[2] != (byte)(index >> 8) || key[3] != (byte)index) return false;
        return kind switch
        {
            TransportPathKind.Relayed => key[0] == 100 && key[1] == 64 && key[4] == 0x0D && key[5] == 0x96,
            TransportPathKind.Lan => key[0] == 192 && key[1] == 168 && key[4] == 0x6A && key[5] == 0x72,
            _ => key[0] == 10 && key[1] == (byte)rebinds && key[4] == 0x6A && key[5] == 0x72
        };
    }

    /// <summary>Moves the node to a new direct address, as a NAT does when its mapping expires; the old one goes dead.</summary>
    internal void Rebind() => rebinds++;
}

/// <summary>
/// A deterministic datagram network for transport tests. It carries the endpoints' own bytes with seeded latency,
/// jitter, loss and duplication, enforces the 1,200-byte limit, and polls each endpoint exactly when its next timer
/// falls due. With the same seeds, two runs exchange identical datagrams at identical times.
/// </summary>
internal sealed class VirtualDatagramNetwork
{
    private const int SpinLimit = 10_000;

    private static readonly TransportPathKind[] Kinds = { TransportPathKind.Direct, TransportPathKind.Relayed, TransportPathKind.Lan };

    private readonly Random random;
    private readonly List<VirtualNode> nodes = new();
    private readonly List<InFlight> inFlight = new();
    private readonly List<(VirtualNode Node, long At)> retiring = new();
    private readonly HashSet<(VirtualNode, VirtualNode)> partitions = new();
    private readonly IncrementalHash transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long ordinal;
    private int spinStreak;
    private int nodeCount;

    internal VirtualDatagramNetwork(int seed, long startMicros = 1_000_000)
    {
        random = new Random(seed);
        Now = startMicros;
    }

    internal long Now { get; private set; }

    internal NetworkConditions Conditions { get; } = new();

    /// <summary>Gets or sets a filter that loses the datagrams it returns true for, on top of the random loss.</summary>
    internal Func<VirtualNode, VirtualNode, byte[], bool>? Drop { get; set; }

    /// <summary>Gets or sets an observer of every datagram an endpoint sends, before the network acts on it.</summary>
    internal Action<VirtualNode, VirtualNode?, byte[]>? Observe { get; set; }

    /// <summary>Gets or sets an observer that also sees the path each datagram was addressed to, and so its kind.</summary>
    internal Action<VirtualNode, TransportPath, byte[]>? ObservePath { get; set; }

    /// <summary>Gets or sets a filter on the destination path, which can make one kind of path unreachable.</summary>
    internal Func<VirtualNode, TransportPath, bool>? Unreachable { get; set; }

    internal long Sent { get; private set; }

    internal long Delivered { get; private set; }

    internal long Lost { get; private set; }

    internal long Duplicated { get; private set; }

    /// <summary>Gets how often an endpoint reported a timer already due right after it was polled: a busy loop.</summary>
    internal long Spins { get; private set; }

    internal VirtualNode AddEndpoint(string name, TransportEndpoint endpoint) => Add(name, endpoint);

    internal VirtualNode AddRaw(string name) => Add(name, null);

    internal VirtualNode AddStack(string name, NetworkStack stack)
    {
        var node = new VirtualNode(name, ++nodeCount, null, stack);
        nodes.Add(node);
        return node;
    }

    /// <summary>Gets how many nodes are attached now; retired ones no longer count.</summary>
    internal int NodeCount => nodes.Count;

    /// <summary>
    /// Takes a node off the network at <paramref name="atMicros"/>, the way a network thread disposes its stack once a
    /// stop's grace period ends: the stack is disposed, which wipes its keys, and datagrams still on their way to it are
    /// lost. Until then the node keeps running, so graceful closes still go out.
    /// </summary>
    internal void Retire(VirtualNode node, long atMicros)
    {
        if (node.Removed || retiring.Exists(entry => ReferenceEquals(entry.Node, node))) return;
        retiring.Add((node, Math.Max(atMicros, Now)));
    }

    /// <summary>Gets the path a node uses to reach <paramref name="target"/>: its address, as the I/O layer would key it.</summary>
    internal static TransportPath PathTo(VirtualNode target, TransportPathKind kind = TransportPathKind.Direct) =>
        new(target.AddressOf(kind), kind);

    /// <summary>Sends bytes from a node as if its socket had, through the same conditions as endpoint traffic.</summary>
    internal void Inject(VirtualNode from, VirtualNode to, byte[] bytes, TransportPathKind kind = TransportPathKind.Direct) =>
        Schedule(from, to, kind, (byte[])bytes.Clone());

    /// <summary>Cuts or restores both directions between two nodes. Datagrams already in flight still arrive.</summary>
    internal void Partition(VirtualNode left, VirtualNode right, bool cut)
    {
        if (cut)
        {
            partitions.Add((left, right));
            partitions.Add((right, left));
        }
        else
        {
            partitions.Remove((left, right));
            partitions.Remove((right, left));
        }
    }

    internal void RunFor(long micros) => RunUntil(Now + micros);

    /// <summary>Delivers datagrams and runs timers in time order until <paramref name="time"/>.</summary>
    internal void RunUntil(long time)
    {
        while (true)
        {
            Settle();
            var next = NextEvent();
            if (next > time) break;
            Now = Advance(next);
        }

        Now = Math.Max(Now, time);
        Settle();
    }

    /// <summary>Runs until <paramref name="condition"/> holds, checking after every event; false if time ran out first.</summary>
    internal bool RunUntil(Func<bool> condition, long maximumMicros)
    {
        var deadline = Now + maximumMicros;
        while (true)
        {
            Settle();
            if (condition()) return true;

            // The condition may read frames or queue sends, as an application would; endpoints answer at once.
            Settle();
            var next = NextEvent();
            if (next > deadline) return false;
            Now = Advance(next);
        }
    }

    /// <summary>
    /// Gets the time to move to. A timer already due right after a poll is a busy loop: it is counted, the clock
    /// creeps forward a microsecond, and a long streak fails the test rather than crawling through virtual time.
    /// </summary>
    private long Advance(long next)
    {
        if (next > Now)
        {
            spinStreak = 0;
            return next;
        }

        Spins++;
        if (++spinStreak > SpinLimit) throw new InvalidOperationException("An endpoint keeps reporting a timer that is already due, so it would never sleep.");
        return Now + 1;
    }

    /// <summary>Gets a hash of every datagram sent so far, with its time, sender and receiver.</summary>
    internal string TranscriptHash() => Convert.ToHexString(transcript.GetCurrentHash());

    private VirtualNode Add(string name, TransportEndpoint? endpoint)
    {
        var node = new VirtualNode(name, ++nodeCount, endpoint);
        nodes.Add(node);
        return node;
    }

    private void Settle()
    {
        RetireDue();
        while (inFlight.Count > 0 && inFlight[0].DeliverAt <= Now)
        {
            var datagram = inFlight[0];
            inFlight.RemoveAt(0);
            var to = datagram.To;
            if (to.Removed)
            {
                Lost++;
                continue;
            }

            Delivered++;
            if (to.Stack != null) to.Stack.Receive(datagram.Source, datagram.Bytes, 0, datagram.Bytes.Length, Now);
            else if (to.Endpoint != null) to.Endpoint.Receive(datagram.Source, datagram.Bytes, 0, datagram.Bytes.Length, Now);
            else to.Inbox.Enqueue((datagram.From, datagram.Bytes));
        }

        foreach (var node in nodes)
        {
            if (node.Endpoint == null) continue;
            if (node.Stack != null) node.Stack.Poll(Now);
            else node.Endpoint.Poll(Now);
            while (TryDequeue(node, out var outgoing))
            {
                var bytes = new byte[outgoing.Length];
                Buffer.BlockCopy(outgoing.Buffer, 0, bytes, 0, outgoing.Length);
                var (target, kind) = Resolve(outgoing.Path.KeyBytes);
                node.Endpoint.Release(outgoing);
                Observe?.Invoke(node, target, bytes);
                ObservePath?.Invoke(node, outgoing.Path, bytes);
                if (target == null || (Unreachable?.Invoke(node, outgoing.Path) ?? false)) Lost++;
                else Schedule(node, target, kind, bytes);
            }
        }
    }

    private static bool TryDequeue(VirtualNode node, out OutgoingDatagram datagram) =>
        node.Stack != null ? node.Stack.TryDequeueDatagram(out datagram) : node.Endpoint!.TryDequeueDatagram(out datagram);

    /// <summary>Finds the node and kind an address belongs to; an address a rebinding retired belongs to nobody.</summary>
    private (VirtualNode? Node, TransportPathKind Kind) Resolve(byte[] key)
    {
        foreach (var node in nodes)
        {
            foreach (var kind in Kinds)
            {
                if (node.Owns(key, kind)) return (node, kind);
            }
        }

        return (null, TransportPathKind.Direct);
    }

    private void RetireDue()
    {
        for (var index = retiring.Count - 1; index >= 0; index--)
        {
            var (node, at) = retiring[index];
            if (at > Now) continue;
            retiring.RemoveAt(index);
            node.Removed = true;
            nodes.Remove(node);
            node.Stack?.Dispose();
        }
    }

    private long NextEvent()
    {
        var next = inFlight.Count > 0 ? inFlight[0].DeliverAt : long.MaxValue;
        foreach (var (_, at) in retiring) next = Math.Min(next, at);
        foreach (var node in nodes)
        {
            if (node.Stack != null) next = Math.Min(next, node.Stack.NextTimeoutMicros());
            else if (node.Endpoint != null) next = Math.Min(next, node.Endpoint.NextTimeoutMicros());
        }

        return next;
    }

    private void Schedule(VirtualNode from, VirtualNode to, TransportPathKind kind, byte[] bytes)
    {
        if (bytes.Length > TransportOptions.MaximumDatagramBytes) throw new InvalidOperationException("A datagram exceeded 1,200 bytes.");
        Sent++;
        transcript.AppendData(BitConverter.GetBytes(Now));
        transcript.AppendData(new[] { from.Address[3], to.Address[3], (byte)kind });
        transcript.AppendData(BitConverter.GetBytes(bytes.Length));
        transcript.AppendData(bytes);

        // The receiver sees the sender's address of the same kind, as it stands when the datagram leaves.
        var source = new TransportPath(from.AddressOf(kind), kind);
        var lost = random.NextDouble() < Conditions.LossRate;
        var copies = random.NextDouble() < Conditions.DuplicateRate ? 2 : 1;
        if (lost || to.Removed || partitions.Contains((from, to)) || (Drop?.Invoke(from, to, bytes) ?? false))
        {
            Lost++;
            return;
        }

        if (copies == 2) Duplicated++;
        for (var copy = 0; copy < copies; copy++)
        {
            var jitter = Conditions.JitterMicros > 0 ? random.NextInt64(0, Conditions.JitterMicros + 1) : 0;
            Insert(new InFlight(Now + Math.Max(1, Conditions.LatencyMicros) + jitter, ++ordinal, from, to, source, bytes));
        }
    }

    private void Insert(InFlight datagram)
    {
        var index = inFlight.Count;
        while (index > 0 && inFlight[index - 1].DeliverAt > datagram.DeliverAt) index--;
        inFlight.Insert(index, datagram);
    }

    private sealed record InFlight(long DeliverAt, long Ordinal, VirtualNode From, VirtualNode To, TransportPath Source, byte[] Bytes);
}
