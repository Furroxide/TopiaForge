using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The rig's virtual transport now models both channel contracts of the real one. Session stays reliable and ordered;
/// Realtime and Voice are unreliable and sequenced: loss is final, older datagrams are dropped, and nothing waits behind
/// the session stream. The bytes are markers here, so delivery order is visible.
/// </summary>
internal static class VirtualVoiceChannelTests
{
    internal static void VoiceLossIsFinalAndNeverWaitsForTheSessionStream()
    {
        var network = new VirtualEngineTransport(new VirtualLinkConditions { LatencyTicks = 1, DropEvery = 3, RetryDelayTicks = 5 });
        var pair = network.Connect("client");
        var arrivals = new List<(EngineChannel Channel, int Marker, ulong Tick)>();
        for (ulong tick = 0; tick < 40; tick++)
        {
            network.Now = tick;
            if (tick < 30)
            {
                pair.ClientEnd.Send(EngineChannel.Session, Marker((int)tick));
                pair.ClientEnd.Send(EngineChannel.Voice, Marker((int)tick));
            }

            network.Now = tick + 1;
            network.DeliverDue(tick + 1);
            while (pair.HostEnd.TryReceive(out var channel, out var bytes)) arrivals.Add((channel, bytes[0], tick + 1));
        }

        var session = arrivals.Where(arrival => arrival.Channel == EngineChannel.Session).ToList();
        var voice = arrivals.Where(arrival => arrival.Channel == EngineChannel.Voice).ToList();
        Assert(session.Select(arrival => arrival.Marker).SequenceEqual(Enumerable.Range(0, 30)), "every session frame arrives, in order");
        Assert(session.Any(arrival => arrival.Tick >= (ulong)arrival.Marker + 6), "some waited for a retry, holding later frames behind them");
        Assert(voice.All(arrival => arrival.Tick == (ulong)arrival.Marker + 1), "every voice datagram that arrives does so after the latency alone");
        var lost = pair.ClientEnd.Outbound!.DatagramsLost[(int)EngineChannel.Voice];
        Assert(lost > 0 && voice.Count + lost == 30, "a lost voice datagram is never delivered (" + lost + " lost)");
        Assert(voice.Select(arrival => arrival.Marker).Distinct().Count() == voice.Count, "and nothing arrives twice");
        Assert(!network.HasInFlight, "nothing is left in flight");
    }

    internal static void OlderAndDuplicateDatagramsAreDropped()
    {
        // A session frame first, so the second frame sent is the one the cadence delays.
        var network = new VirtualEngineTransport(new VirtualLinkConditions { LatencyTicks = 1, ReorderEvery = 2 });
        var pair = network.Connect("client");
        pair.ClientEnd.Send(EngineChannel.Session, Marker(0));
        pair.ClientEnd.Send(EngineChannel.Voice, Marker(1));
        pair.ClientEnd.Send(EngineChannel.Voice, Marker(2));
        var heard = Deliver(network, pair, 4);
        Assert(heard.Where(item => item.Channel == EngineChannel.Voice).Select(item => item.Marker).SequenceEqual(new[] { 2 }),
            "datagram 1, delayed past datagram 2, is dropped on arrival");
        Assert(pair.ClientEnd.Outbound!.DatagramsStale[(int)EngineChannel.Voice] == 1, "and counted as stale");

        network = new VirtualEngineTransport(new VirtualLinkConditions { LatencyTicks = 1, DuplicateEvery = 1 });
        pair = network.Connect("client");
        for (var marker = 0; marker < 5; marker++) pair.ClientEnd.Send(EngineChannel.Voice, Marker(marker));
        heard = Deliver(network, pair, 4);
        Assert(heard.Select(item => item.Marker).SequenceEqual(Enumerable.Range(0, 5)), "duplicated datagrams arrive once each");
        Assert(pair.ClientEnd.Outbound!.DatagramsStale[(int)EngineChannel.Voice] == 5, "every copy is dropped");
    }

    internal static void APathThatIsDownLosesDatagramsButHoldsTheStream()
    {
        var network = new VirtualEngineTransport(new VirtualLinkConditions { LatencyTicks = 1 });
        var pair = network.Connect("client");
        var direction = pair.ClientEnd.Outbound!;
        direction.Held = true;
        pair.ClientEnd.Send(EngineChannel.Session, Marker(1));
        pair.ClientEnd.Send(EngineChannel.Voice, Marker(2));
        Assert(network.HasInFlight, "a datagram counts as in flight until its tick comes");
        var heard = Deliver(network, pair, 3);
        Assert(heard.Count == 0 && direction.DatagramsLost[(int)EngineChannel.Voice] == 1, "while the path is down, voice is lost");
        direction.Held = false;
        pair.ClientEnd.Send(EngineChannel.Voice, Marker(3));
        heard = Deliver(network, pair, 3);
        Assert(heard.Select(item => (item.Channel, item.Marker)).SequenceEqual(new[] { (EngineChannel.Session, 1), (EngineChannel.Voice, 3) }),
            "once it is back, the held session frame arrives, and new voice with it");

        // A partition behaves the same way: the stream waits, datagrams are lost.
        direction.HeldUntilTick = network.Now + 3;
        pair.ClientEnd.Send(EngineChannel.Session, Marker(4));
        pair.ClientEnd.Send(EngineChannel.Voice, Marker(5));
        heard = Deliver(network, pair, 5);
        Assert(heard.Select(item => item.Marker).SequenceEqual(new[] { 4 }), "a partition delays the stream and loses the datagram");

        // A test hold applies to the session stream alone.
        network.HoldNext(direction, 1);
        pair.ClientEnd.Send(EngineChannel.Session, Marker(6));
        pair.ClientEnd.Send(EngineChannel.Voice, Marker(7));
        heard = Deliver(network, pair, 3);
        Assert(heard.Select(item => item.Marker).SequenceEqual(new[] { 7 }), "voice passes a held session frame");
        Assert(!network.HasInFlight, "a frame held by the test does not count as in flight");
        network.Release(direction);
        heard = Deliver(network, pair, 2);
        Assert(heard.Select(item => item.Marker).SequenceEqual(new[] { 6 }), "and the held frame follows on release");
    }

    internal static void EveryChannelSharesTheLinkRate()
    {
        // 6,000 bytes a second at 60 ticks a second: a 1,000-byte session frame takes ten ticks to serialize.
        var network = new VirtualEngineTransport(new VirtualLinkConditions { LatencyTicks = 1, BandwidthBytesPerSecond = 6_000 });
        var pair = network.Connect("client");
        pair.ClientEnd.Send(EngineChannel.Session, new byte[1_000]);
        pair.ClientEnd.Send(EngineChannel.Voice, Marker(9));
        var heard = Deliver(network, pair, 20);
        var voiceTick = heard.Single(item => item.Channel == EngineChannel.Voice).Tick;
        Assert(voiceTick >= 11, "a datagram queued behind a large frame waits for the link, as on a real path (tick " + voiceTick + ")");
        var direction = pair.ClientEnd.Outbound!;
        Assert(direction.ChannelBytesSent[(int)EngineChannel.Session] == 1_000 && direction.ChannelBytesSent[(int)EngineChannel.Voice] == 1,
            "bytes are counted per channel");
        Assert(direction.BytesSent == 1_001 && direction.BytesDelivered == 1_001, "and together");
    }

    private static List<(EngineChannel Channel, int Marker, ulong Tick)> Deliver(VirtualEngineTransport network, VirtualLinkPair pair, int ticks)
    {
        var heard = new List<(EngineChannel, int, ulong)>();
        for (var index = 0; index < ticks; index++)
        {
            var tick = network.Now + 1;
            network.Now = tick;
            network.DeliverDue(tick);
            while (pair.HostEnd.TryReceive(out var channel, out var bytes)) heard.Add((channel, bytes.Length > 0 ? bytes[0] : -1, tick));
        }

        return heard;
    }

    private static ArraySegment<byte> Marker(int marker) => new(new[] { (byte)marker });

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
