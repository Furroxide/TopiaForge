extern alias provider;

using System.Security.Cryptography;
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

/// <summary>One attachment point: a transport endpoint, or a raw inbox a test reads and writes by hand.</summary>
internal sealed class VirtualNode
{
    internal VirtualNode(string name, int index, TransportEndpoint? endpoint)
    {
        Name = name;
        Endpoint = endpoint;
        Address = new byte[] { 10, 0, 0, (byte)index, 0x6A, 0x72 };
    }

    internal string Name { get; }

    internal TransportEndpoint? Endpoint { get; }

    internal byte[] Address { get; }

    internal Queue<(VirtualNode From, byte[] Bytes)> Inbox { get; } = new();
}

/// <summary>
/// A deterministic datagram network for transport tests. It carries the endpoints' own bytes with seeded latency,
/// jitter, loss and duplication, enforces the 1,200-byte limit, and polls each endpoint exactly when its next timer
/// falls due. With the same seeds, two runs exchange identical datagrams at identical times.
/// </summary>
internal sealed class VirtualDatagramNetwork
{
    private const int SpinLimit = 10_000;

    private readonly Random random;
    private readonly List<VirtualNode> nodes = new();
    private readonly List<InFlight> inFlight = new();
    private readonly HashSet<(VirtualNode, VirtualNode)> partitions = new();
    private readonly IncrementalHash transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long ordinal;
    private int spinStreak;

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

    internal long Sent { get; private set; }

    internal long Delivered { get; private set; }

    internal long Lost { get; private set; }

    internal long Duplicated { get; private set; }

    /// <summary>Gets how often an endpoint reported a timer already due right after it was polled: a busy loop.</summary>
    internal long Spins { get; private set; }

    internal VirtualNode AddEndpoint(string name, TransportEndpoint endpoint) => Add(name, endpoint);

    internal VirtualNode AddRaw(string name) => Add(name, null);

    /// <summary>Gets the path a node uses to reach <paramref name="target"/>: its address, as the I/O layer would key it.</summary>
    internal static TransportPath PathTo(VirtualNode target) => new(target.Address, TransportPathKind.Direct);

    /// <summary>Sends bytes from a node as if its socket had, through the same conditions as endpoint traffic.</summary>
    internal void Inject(VirtualNode from, VirtualNode to, byte[] bytes) => Schedule(from, to, (byte[])bytes.Clone());

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
        var node = new VirtualNode(name, nodes.Count + 1, endpoint);
        nodes.Add(node);
        return node;
    }

    private void Settle()
    {
        while (inFlight.Count > 0 && inFlight[0].DeliverAt <= Now)
        {
            var datagram = inFlight[0];
            inFlight.RemoveAt(0);
            Delivered++;
            var to = datagram.To;
            if (to.Endpoint != null) to.Endpoint.Receive(PathTo(datagram.From), datagram.Bytes, 0, datagram.Bytes.Length, Now);
            else to.Inbox.Enqueue((datagram.From, datagram.Bytes));
        }

        foreach (var node in nodes)
        {
            if (node.Endpoint == null) continue;
            node.Endpoint.Poll(Now);
            while (node.Endpoint.TryDequeueDatagram(out var outgoing))
            {
                var bytes = new byte[outgoing.Length];
                Buffer.BlockCopy(outgoing.Buffer, 0, bytes, 0, outgoing.Length);
                var target = nodes.FirstOrDefault(candidate => candidate.Address.AsSpan().SequenceEqual(outgoing.Path.KeyBytes));
                node.Endpoint.Release(outgoing);
                Observe?.Invoke(node, target, bytes);
                if (target == null) Lost++;
                else Schedule(node, target, bytes);
            }
        }
    }

    private long NextEvent()
    {
        var next = inFlight.Count > 0 ? inFlight[0].DeliverAt : long.MaxValue;
        foreach (var node in nodes)
        {
            if (node.Endpoint != null) next = Math.Min(next, node.Endpoint.NextTimeoutMicros());
        }

        return next;
    }

    private void Schedule(VirtualNode from, VirtualNode to, byte[] bytes)
    {
        if (bytes.Length > TransportOptions.MaximumDatagramBytes) throw new InvalidOperationException("A datagram exceeded 1,200 bytes.");
        Sent++;
        transcript.AppendData(BitConverter.GetBytes(Now));
        transcript.AppendData(new[] { from.Address[3], to.Address[3] });
        transcript.AppendData(BitConverter.GetBytes(bytes.Length));
        transcript.AppendData(bytes);
        var lost = random.NextDouble() < Conditions.LossRate;
        var copies = random.NextDouble() < Conditions.DuplicateRate ? 2 : 1;
        if (lost || partitions.Contains((from, to)) || (Drop?.Invoke(from, to, bytes) ?? false))
        {
            Lost++;
            return;
        }

        if (copies == 2) Duplicated++;
        for (var copy = 0; copy < copies; copy++)
        {
            var jitter = Conditions.JitterMicros > 0 ? random.NextInt64(0, Conditions.JitterMicros + 1) : 0;
            Insert(new InFlight(Now + Math.Max(1, Conditions.LatencyMicros) + jitter, ++ordinal, from, to, bytes));
        }
    }

    private void Insert(InFlight datagram)
    {
        var index = inFlight.Count;
        while (index > 0 && inFlight[index - 1].DeliverAt > datagram.DeliverAt) index--;
        inFlight.Insert(index, datagram);
    }

    private sealed record InFlight(long DeliverAt, long Ordinal, VirtualNode From, VirtualNode To, byte[] Bytes);
}
