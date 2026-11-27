using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// What voice costs on the wire and what it shares with the session stream: a four-speaker session against the
/// transport's class budgets, voice under session load, frame pairing, the bounded receive queue, and voice the link
/// cannot carry shed while the session stream keeps its pace.
/// </summary>
internal static class VoiceBudgetTests
{
    // What the secure transport adds to every datagram: the protected packet's 17-byte header and 16-byte tag, and the
    // DATAGRAM frame's type, channel, sequence and length (at most 7 bytes). See MultiplayerSecureTransport.md.
    private const int TransportOverheadBytes = 33 + 7;

    internal static void FourSpeakersFitTheirBudget()
    {
        // Five seconds of everyone talking at once: the host and three clients, 50-byte frames (20 kbit/s of codec data).
        using var rig = new VoiceSessionHarness();
        var names = new[] { "host", "a", "b", "c" };
        var engines = new Dictionary<string, SessionEngine> { ["host"] = rig.Host };
        var links = new Dictionary<string, VirtualLinkEnd>();
        foreach (var name in names.Skip(1))
        {
            var (engine, link) = rig.AddClient(name);
            engines[name] = engine;
            links[name] = link;
        }

        var ears = names.ToDictionary(name => name, name => rig.Listen(engines[name]));
        var bases = names.Select((name, index) => (name, (short)(1000 * (index + 1)))).ToDictionary(pair => pair.name, pair => pair.Item2);
        foreach (var name in names) rig.Talk(engines[name], bases[name], 250);
        rig.Advance(5 * 60 + 10);
        rig.Flush();

        foreach (var listener in names)
        {
            foreach (var speaker in names.Where(name => name != listener))
            {
                var frames = ears[listener].From(speaker);
                Assert(frames.Select(frame => frame.Id).SequenceEqual(Enumerable.Range(bases[speaker], 250).Select(id => (short)id)),
                    listener + " hears all 250 of " + speaker + "'s frames, in order");
                var (_, decoded, statistics) = ears[listener].Play(speaker);
                Assert(decoded.SequenceEqual(Enumerable.Range(bases[speaker], 250).Select(id => (short)id)),
                    listener + " plays every frame of " + speaker);
                Assert(statistics.Concealed == 0 && statistics.Faded == 0 && statistics.Late == 0,
                    listener + " loses nothing of " + speaker);
                Assert(statistics.DriftInserts == 0 && statistics.DriftDrops == 0, "and never corrects the delay it started with");
            }
        }

        foreach (var name in names.Skip(1))
        {
            var uplink = links[name].Outbound!;
            var datagrams = uplink.ChannelFramesSent[(int)EngineChannel.Voice];
            var kilobits = (uplink.ChannelBytesSent[(int)EngineChannel.Voice] + (datagrams * TransportOverheadBytes)) * 8.0 / 5 / 1000;
            Assert(datagrams == 125, name + " sends its 250 frames in 125 datagrams, 25 a second, under the 30 allowed");
            Assert(kilobits <= 32, name + "'s voice fits the transport's 32 kbit/s client class (" + kilobits.ToString("F1") + " kbit/s)");
            var downlink = links[name].Peer!.Outbound!;
            var relayed = downlink.ChannelFramesSent[(int)EngineChannel.Voice];
            var down = (downlink.ChannelBytesSent[(int)EngineChannel.Voice] + (relayed * TransportOverheadBytes)) * 8.0 / 5 / 1000;
            Assert(relayed == 375 && down <= 96, name + " receives three speakers within the 96 kbit/s voice class (" + down.ToString("F1") + " kbit/s)");
        }

        var host = rig.Host.GetVoiceStatistics();
        Assert(host.Relayed == 375 && host.TotalDropped == 0 && host.Strikes == 0, "the host relayed every client datagram and refused none");
    }

    internal static void VoiceIsNeverHeldBehindTheSessionStream()
    {
        // Every fourth frame of every channel is lost once. Session frames are retried after 100 ms and hold everything
        // behind them on their stream; a lost voice datagram is simply gone, and the rest arrive on time.
        var conditions = new VirtualLinkConditions { LatencyTicks = 1, DropEvery = 4, RetryDelayTicks = 6 };
        using var rig = new VoiceSessionHarness(conditions);
        var (a, aLink) = rig.AddClient("a");
        var hostEars = rig.Listen(rig.Host);
        var talker = rig.Talk(a, 2000, 150);
        var junk = IgnorableMessage(8 * 1024);
        var start = rig.NowMicros;
        var longestSessionBacklog = 0UL;
        for (var tick = 0; tick < 200; tick++)
        {
            // An 8 KiB session message every tick: ignorable, so the host skips it, but it rides the same stream.
            aLink.Send(EngineChannel.Session, new ArraySegment<byte>(junk));
            rig.Advance();
            var direction = aLink.Outbound!;
            if (direction.LastDeliverAt > rig.Tick) longestSessionBacklog = Math.Max(longestSessionBacklog, direction.LastDeliverAt - rig.Tick);
        }

        rig.Flush();
        Assert(longestSessionBacklog >= 6, "the session stream was held behind retries (" + longestSessionBacklog + " ticks)");
        var frames = hostEars.From("a");
        var uplink = aLink.Outbound!;
        Assert(uplink.DatagramsLost[(int)EngineChannel.Voice] > 0, "some voice datagrams were lost");
        Assert(frames.Count == (int)(talker.Next - (ulong)(2 * uplink.DatagramsLost[(int)EngineChannel.Voice])),
            "and only those: no voice was delayed into staleness");
        var worst = frames.Max(frame => frame.ArrivalMicros - (start + ((long)frame.Timestamp * VoiceSessionHarness.FrameMicros)));
        Assert(worst <= 70_000, "every voice frame that arrived did so within 70 ms of being spoken (" + worst / 1000 + " ms)");
        var (_, _, statistics) = hostEars.Play("a");
        Assert(statistics.Late == 0, "and none came too late to play");
    }

    internal static void FramesShareDatagramsInPairs()
    {
        using var rig = new VoiceSessionHarness();
        var (a, aLink) = rig.AddClient("a");
        var hostEars = rig.Listen(rig.Host);
        var uplink = aLink.Outbound!;
        long Sent() => uplink.ChannelFramesSent[(int)EngineChannel.Voice];
        byte[] Frame(int id) => rig.Codec.Packet((short)id);

        Assert(a.SubmitVoiceFrame(0, VoiceFrameFlags.None, Frame(1)) == VoiceSubmitResult.Held, "a first frame waits for a partner");
        Assert(a.SubmitVoiceFrame(1, VoiceFrameFlags.None, Frame(2)) == VoiceSubmitResult.Sent && Sent() == 1, "the next one sends both");
        Assert(a.SubmitVoiceFrame(2, VoiceFrameFlags.None, Frame(3)) == VoiceSubmitResult.Held, "a third waits again");
        rig.Advance(2);
        Assert(Sent() == 1, "for up to two frames' time");
        rig.Advance();
        Assert(Sent() == 2, "then goes alone");
        Assert(a.SubmitVoiceFrame(3, VoiceFrameFlags.EndOfTalk, Frame(4)) == VoiceSubmitResult.Sent && Sent() == 3, "the end of talk never waits");
        Assert(a.SubmitVoiceFrame(10, VoiceFrameFlags.None, Frame(11)) == VoiceSubmitResult.Held, "after a pause a frame waits");
        Assert(a.SubmitVoiceFrame(12, VoiceFrameFlags.None, Frame(13)) == VoiceSubmitResult.Held && Sent() == 4,
            "a frame that does not follow on sends the waiting one alone and waits itself");
        Assert(a.SubmitVoiceFrame(13, VoiceFrameFlags.None, Frame(14)) == VoiceSubmitResult.Sent && Sent() == 5, "and pairs with the next");

        Assert(a.SubmitVoiceFrame(13, VoiceFrameFlags.None, Frame(14)) == VoiceSubmitResult.Invalid, "time must move forward");
        Assert(a.SubmitVoiceFrame(20, VoiceFrameFlags.None, new byte[161]) == VoiceSubmitResult.Invalid, "a packet past 160 bytes is refused");
        Assert(a.SubmitVoiceFrame(20, VoiceFrameFlags.None, new byte[] { 0x00, 1, 2, 3, 4, 5 }) == VoiceSubmitResult.Invalid, "so is one the codec refuses");
        Assert(a.SubmitVoiceFrame(20, (VoiceFrameFlags)0x80, Frame(21)) == VoiceSubmitResult.Invalid, "and unknown flags");
        Assert(a.SubmitVoiceFrame(20, VoiceFrameFlags.None, ReadOnlySpan<byte>.Empty) == VoiceSubmitResult.Invalid, "and an empty packet");
        rig.Flush();

        var heard = hostEars.From("a");
        Assert(heard.Select(frame => frame.Timestamp).SequenceEqual(new ulong[] { 0, 1, 2, 3, 10, 12, 13 }), "the host hears every frame sent");
        Assert(heard.Select(frame => frame.Sequence).SequenceEqual(Enumerable.Range(1, 7).Select(value => (ulong)value)), "with one sequence per frame");

        var unvoiced = SessionEngine.CreateClient(new EngineOptions { StepRateHz = 60 }, new MultiplayerSessionId("unvoiced"), 0);
        Assert(unvoiced.SubmitVoiceFrame(0, VoiceFrameFlags.None, Frame(1)) == VoiceSubmitResult.Disabled, "an engine without voice options has no voice");
        unvoiced.Shutdown(MultiplayerSessionEndReason.Left);
        var waiting = SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("waiting"), 0);
        Assert(waiting.SubmitVoiceFrame(0, VoiceFrameFlags.None, Frame(1)) == VoiceSubmitResult.NotConnected, "nor does one that has not joined");
        waiting.Shutdown(MultiplayerSessionEndReason.Left);
    }

    internal static void AStalledTalkerIsPacedNotStruck()
    {
        // After a stall, the talker hands the engine three seconds of frames at once. The engine sends what the host's
        // limits allow and drops the rest itself, so the host relays every datagram it gets and strikes none.
        using var rig = new VoiceSessionHarness();
        var (a, _) = rig.AddClient("a");
        var (b, _) = rig.AddClient("b");
        var bEars = rig.Listen(b);
        var results = Enumerable.Range(0, 150).Select(ts => a.SubmitVoiceFrame((ulong)ts, VoiceFrameFlags.None, rig.Codec.Packet((short)(100 + ts)))).ToList();
        rig.Flush();
        Assert(results.Count(result => result == VoiceSubmitResult.Sent) == 30 && results.Count(result => result == VoiceSubmitResult.RateLimited) == 45,
            "thirty pairs leave, and the engine drops the other forty-five itself");
        var host = rig.Host.GetVoiceStatistics();
        Assert(host.Relayed == 30 && host.Strikes == 0 && host.TotalDropped == 0, "the host relays all thirty and strikes nothing");
        Assert(a.GetVoiceStatistics().Dropped(VoiceDropReason.RateLimited) == 45, "the talker counts what it dropped");
        Assert(bEars.From("a").Select(frame => frame.Id).SequenceEqual(Enumerable.Range(100, 60).Select(id => (short)id)), "b hears the first sixty frames");

        rig.Advance(61);
        Assert(a.SubmitVoiceFrame(200, VoiceFrameFlags.None, rig.Codec.Packet(300)) == VoiceSubmitResult.Held &&
               a.SubmitVoiceFrame(201, VoiceFrameFlags.None, rig.Codec.Packet(301)) == VoiceSubmitResult.Sent, "a second later the talker sends again");
        rig.Flush();
        Assert(bEars.From("a").Count == 62 && rig.Host.GetVoiceStatistics().Strikes == 0, "and is heard, never struck");
    }

    internal static void TheReceiveQueueIsBounded()
    {
        using var rig = new VoiceSessionHarness(tune: voice => voice.InboxCapacity = 4);
        var (a, _) = rig.AddClient("a");
        var (b, _) = rig.AddClient("b");
        rig.Talk(a, 2000, 10);
        rig.Advance(20);
        rig.Flush();
        var heard = new List<VoiceFrame>();
        while (b.TryReceiveVoiceFrame(out var frame)) heard.Add(frame);
        Assert(heard.Select(frame => frame.Timestamp).SequenceEqual(new ulong[] { 6, 7, 8, 9 }), "a full queue keeps the newest frames");
        Assert(b.GetVoiceStatistics().Dropped(VoiceDropReason.InboxOverflow) == 6, "and counts the six it dropped");
    }

    internal static void VoiceKeepsItsPaceOnARateLimitedLink()
    {
        // Three talkers and one listener on links of a million bytes a second, of which voice uses under 1%. Each datagram
        // costs the link its bytes, not a whole tick, so the 75 a second relayed to the listener never queue on a 60-tick
        // link, and the session stream waits behind nothing. A frame still arrives in the tick after its last byte, so
        // each of the two hops can add a tick to what a link with no rate at all takes, and no more, however long it runs.
        long WorstDelay(int bandwidth, out ulong sessionBacklog)
        {
            using var rig = new VoiceSessionHarness(new VirtualLinkConditions { LatencyTicks = 1, BandwidthBytesPerSecond = bandwidth });
            var talkers = new[] { "a", "b", "c" }.Select(name => rig.AddClient(name).Engine).ToList();
            var (listener, link) = rig.AddClient("d");
            var ears = rig.Listen(listener);
            var start = rig.NowMicros;
            for (var index = 0; index < talkers.Count; index++) rig.Talk(talkers[index], (short)(1000 * (index + 1)), 250);
            rig.Advance(5 * 60);
            var downlink = link.Peer!.Outbound!;
            sessionBacklog = downlink.BusyUntilTick > rig.Tick ? downlink.BusyUntilTick - rig.Tick : 0;
            rig.Flush();
            Assert(ears.Frames.Count == 750, "the listener hears all 750 frames (" + ears.Frames.Count + ")");
            return ears.Frames.Max(frame => frame.ArrivalMicros - (start + ((long)frame.Timestamp * VoiceSessionHarness.FrameMicros)));
        }

        var free = WorstDelay(0, out _);
        var limited = WorstDelay(1_000_000, out var backlog);
        Assert(limited <= free + 33_334,
            "voice on the rate-limited link is within a tick a hop of a free one (" + (limited / 1_000) + " ms against " + (free / 1_000) + " ms)");
        Assert(backlog <= 1, "and after five seconds the listener's session stream is not waiting behind voice (" + backlog + " ticks)");
    }

    internal static void AVoiceOverloadIsShedAndTheSessionLaneKeepsItsPace()
    {
        // Three talkers of 80-byte frames send one listener about 12,800 bytes a second over links of 8,000: more voice
        // than the link carries. The host also sends the listener a 100-byte session message ten times a second, as
        // snapshots would. The rig sheds the voice its link cannot carry in time, as the real transport drops voice
        // first, so the voice that arrives is fresh and the session lane keeps its pace. Before, every frame arrived but
        // the worst 6.2 s late, and the session lane and its messages fell 440 ticks behind in ten seconds.
        const ulong Bound = VirtualEngineTransport.DatagramBacklogTicks;
        var alone = Run(1);
        var overloaded = Run(3);
        var measured = " (worst frame " + (overloaded.WorstVoice / 1_000) + " ms against " + (alone.WorstVoice / 1_000) + " ms for one talker, session lane " +
                       overloaded.Backlog + " ticks behind, worst session message " + overloaded.WorstSession + " ticks, " + overloaded.Heard + " of 1,500 frames heard, " +
                       overloaded.Shed + " datagrams shed)";
        Assert(alone.Shed == 0 && alone.Heard == 500 && alone.UplinkShed == 0, "one talker fits the link, and nothing of it is shed");
        Assert(overloaded.Shed > 0 && overloaded.Heard + (2 * overloaded.Shed) == 1_500,
            "three do not: the listener hears every frame but those of the datagrams its link shed, two to each" + measured);
        Assert(overloaded.UplinkShed == 0, "each talker's own uplink, within the rate, sheds nothing");
        Assert(overloaded.WorstVoice <= alone.WorstVoice + ((long)(Bound + 2) * 16_667),
            "what arrives is at most the bound and a datagram later than one talker's voice on the same link" + measured);
        Assert(overloaded.WorstSession <= (2 * Bound) + 3 && overloaded.Backlog <= (2 * Bound) + 3,
            "every session message arrives within nine ticks, and the lane ends as close: it keeps its pace" + measured);

        (long WorstVoice, ulong Backlog, ulong WorstSession, int Heard, long Shed, long UplinkShed) Run(int talkerCount)
        {
            using var rig = new VoiceSessionHarness(new VirtualLinkConditions { LatencyTicks = 1, BandwidthBytesPerSecond = 8_000 }, packetBytes: 80);
            var talkers = new[] { "a", "b", "c" }.Take(talkerCount).Select(name => rig.AddClient(name)).ToList();
            var (listener, link) = rig.AddClient("d");
            var ears = rig.Listen(listener);
            var hostEnd = link.Peer!;
            var downlink = hostEnd.Outbound!;
            var message = IgnorableMessage(100);
            var start = rig.NowMicros;
            for (var index = 0; index < talkers.Count; index++) rig.Talk(talkers[index].Engine, (short)(1000 * (index + 1)), 500);
            var worstSession = 0UL;
            for (var tick = 0; tick < 10 * 60; tick++)
            {
                if (tick % 6 == 0)
                {
                    hostEnd.Send(EngineChannel.Session, new ArraySegment<byte>(message));
                    worstSession = Math.Max(worstSession, downlink.LastDeliverAt - rig.Tick);
                }

                rig.Advance();
            }

            var backlog = downlink.BusyUntilTick > rig.Tick ? downlink.BusyUntilTick - rig.Tick : 0;
            rig.Flush();
            var worstVoice = ears.Frames.Max(frame => frame.ArrivalMicros - (start + ((long)frame.Timestamp * VoiceSessionHarness.FrameMicros)));
            var uplinkShed = talkers.Sum(talker => talker.Link.Outbound!.DatagramsShed[(int)EngineChannel.Voice]);
            return (worstVoice, backlog, worstSession, ears.Frames.Count, downlink.DatagramsShed[(int)EngineChannel.Voice], uplinkShed);
        }
    }

    internal static void VoiceWithinTheRateIsNeverShed()
    {
        // The same three talkers over links of 16,000 bytes a second, which carry them: the listener hears every frame.
        using (var rig = new VoiceSessionHarness(new VirtualLinkConditions { LatencyTicks = 1, BandwidthBytesPerSecond = 16_000 }, packetBytes: 80))
        {
            var talkers = new[] { "a", "b", "c" }.Select(name => rig.AddClient(name).Engine).ToList();
            var (listener, _) = rig.AddClient("d");
            var ears = rig.Listen(listener);
            for (var index = 0; index < talkers.Count; index++) rig.Talk(talkers[index], (short)(1000 * (index + 1)), 500);
            rig.Advance(10 * 60);
            rig.Flush();
            Assert(ears.Frames.Count == 1_500, "a link with room for the voice carries all of it (" + ears.Frames.Count + " of 1,500 frames)");
            Assert(rig.Network.ShedDatagrams == 0, "and sheds none of it, on any link");
        }

        // One talker, while the host sends the listener an 8 KiB session message every second, which takes the link half a
        // second each. Voice stays within its share of the link meanwhile, so none of it is shed either.
        using (var rig = new VoiceSessionHarness(new VirtualLinkConditions { LatencyTicks = 1, BandwidthBytesPerSecond = 16_000 }, packetBytes: 80))
        {
            var talker = rig.AddClient("a").Engine;
            var (listener, link) = rig.AddClient("d");
            var ears = rig.Listen(listener);
            var message = IgnorableMessage(8 * 1_024);
            rig.Talk(talker, 1000, 500);
            for (var tick = 0; tick < 10 * 60; tick++)
            {
                if (tick % 60 == 0) link.Peer!.Send(EngineChannel.Session, new ArraySegment<byte>(message));
                rig.Advance();
            }

            rig.Flush();
            Assert(ears.Frames.Count == 500, "voice beside large session messages arrives in full (" + ears.Frames.Count + " of 500 frames)");
            Assert(rig.Network.ShedDatagrams == 0, "none of it shed while the session lane was behind");
        }
    }

    /// <summary>One session frame holding a single ignorable message of <paramref name="bytes"/> bytes.</summary>
    private static byte[] IgnorableMessage(int bytes)
    {
        var frame = new ProtocolWriter();
        var body = new ProtocolWriter();
        body.WriteRaw(new byte[bytes], 0, bytes);
        frame.WriteEnvelope(MessageType.LastIgnorable, body);
        return frame.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
