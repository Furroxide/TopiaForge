extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Handshake;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using provider::TopiaForge.Multiplayer.Transport.Recovery;
using provider::TopiaForge.Multiplayer.Transport.Streams;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The transport's parts on their own: acknowledgement, RTT, loss, streams, pacing, channels and guards.</summary>
internal static class TransportComponentTests
{
    internal static void AckTrackerKeepsTheNewestRangesAndDeadlines()
    {
        var acks = new AckTracker();
        acks.OnPacketReceived(0, true, 1_000);
        That(!acks.IsAckDue(1_000, 25_000) && acks.AckDeadline(25_000) == 26_000, "one packet waits up to the ack delay");
        acks.OnPacketReceived(2, true, 2_000);
        That(acks.IsAckDue(2_000, 25_000) && acks.AckDeadline(25_000) == 1_000, "two packets make an ACK due at once");
        acks.OnPacketReceived(1, false, 3_000);
        That(acks.Ranges.Count == 1 && acks.Ranges[0].Smallest == 0 && acks.Ranges[0].Largest == 2, "a filled gap merges its neighbours");
        That(acks.AckDelay(5_000) == 3_000, "the delay is measured from the newest packet's arrival");
        acks.OnAckSent();
        That(!acks.HasNewPackets && acks.AckDeadline(25_000) == long.MaxValue, "an ACK settles the deadline");
        acks.OnPacketReceived(5, false, 6_000);
        That(acks.HasNewPackets && !acks.IsAckDue(1_000_000_000, 25_000), "packets that ask for nothing never make an ACK due");
        for (var index = 0; index <= 40; index++) acks.OnPacketReceived(100 + (2UL * (ulong)index), false, 7_000);
        That(acks.Ranges.Count == AckTracker.MaximumRanges && acks.Ranges[0].Largest == 180 && acks.Ranges[31].Smallest == 118,
            "only the newest 32 ranges are kept");
        acks.OnPacketReceived(181, true, 8_000);
        acks.OnPacketReceived(179, true, 8_000);
        That(acks.Ranges[0].Smallest == 178 && acks.Ranges[0].Largest == 181, "late packets join the ranges they touch");
    }

    internal static void RttFollowsRfc9002()
    {
        var rtt = new RttEstimator(250_000);
        That(rtt.SmoothedRtt == 250_000 && rtt.RttVariation == 125_000 && rtt.ProbeTimeout(25_000) == 775_000,
            "before a sample, the initial RTT sets the probe timeout");
        rtt.OnSample(100_000, 10_000, 25_000);
        That(rtt.MinimumRtt == 100_000 && rtt.SmoothedRtt == 100_000 && rtt.RttVariation == 50_000, "the first sample is taken whole");
        rtt.OnSample(120_000, 30_000, 25_000);
        That(rtt.SmoothedRtt == 102_500 && rtt.RttVariation == 42_500, "an ack delay that would cross the minimum is ignored");
        rtt.OnSample(200_000, 20_000, 25_000);
        That(rtt.SmoothedRtt == 112_187 && rtt.RttVariation == 51_250, "the ack delay, capped, comes off the sample");
        That(rtt.LossDelay() == 225_000 && rtt.ProbeTimeout(25_000) == 112_187 + 205_000 + 25_000,
            "the loss delay is 9/8 of the larger RTT and the probe timeout adds 4 variations and the ack delay");
        rtt.OnSample(-1, 0, 25_000);
        That(rtt.LatestRtt == 200_000, "a negative sample is ignored");
    }

    internal static void LossDetectorDeclaresLossByThresholdAndTimer()
    {
        var loss = new LossDetector();
        var rtt = new RttEstimator(100_000);
        for (var number = 0UL; number < 6; number++)
        {
            var packet = loss.Rent(number, (long)number * 10_000);
            packet.AckEliciting = true;
            packet.Streams.Add(new StreamRange(number * 100, 100));
            loss.OnSent(packet);
        }

        var acknowledged = new List<ulong>();
        var lost = new List<ulong>();
        loss.OnAck(new List<PacketRange> { new(5, 5) }, 0, 150_000, rtt, 25_000, packet => acknowledged.Add(packet.Number), packet => lost.Add(packet.Number));
        That(acknowledged.SequenceEqual(new[] { 5UL }) && rtt.SmoothedRtt == 100_000, "the newest packet is acknowledged and timed");
        That(lost.SequenceEqual(new[] { 0UL, 1UL, 2UL, 3UL }), "three newer packets or 9/8 of the RTT declare a packet lost");
        That(loss.LossTime == 152_500, "the loss timer covers the packet not yet old enough");
        That(!loss.OnTimer(152_499, rtt, 25_000, packet => lost.Add(packet.Number)) && lost.Count == 4, "the loss timer waits for its deadline");
        That(!loss.OnTimer(152_500, rtt, 25_000, packet => lost.Add(packet.Number)) && lost.Last() == 4, "then declares the packet lost");
        That(loss.TimerDeadline(rtt, 25_000) == long.MaxValue && loss.InFlightCount == 0, "nothing in flight arms no timer");

        var probe = loss.Rent(6, 200_000);
        probe.AckEliciting = true;
        loss.OnSent(probe);
        That(loss.TimerDeadline(rtt, 25_000) == 200_000 + 325_000, "the probe timeout runs from the last packet that asked for an ACK");
        That(!loss.OnTimer(524_999, rtt, 25_000, _ => { }) && loss.OnTimer(525_000, rtt, 25_000, _ => { }) && loss.ProbeCount == 1,
            "the probe timeout fires on time");
        That(loss.TimerDeadline(rtt, 25_000) == 200_000 + 650_000, "each expiry doubles the next timeout");
        loss.OnAck(new List<PacketRange> { new(6, 6) }, 0, 600_000, rtt, 25_000, _ => { }, _ => { });
        That(loss.ProbeCount == 0 && loss.InFlightCount == 0, "an acknowledgement resets the backoff");
    }

    internal static void SessionStreamsReassembleWithinTheirWindows()
    {
        var frameA = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();
        var frameB = Enumerable.Range(0, 150).Select(value => (byte)(value * 3)).ToArray();
        var send = new SessionSendStream(0);
        send.AppendFrame(frameA, 0, frameA.Length);
        send.AppendFrame(frameB, 0, frameB.Length);
        That(send.WriteOffset == 302 + 152 && !send.HasDataToSend && send.IsBlockedByWindow, "without a window nothing may be sent");
        send.OnWindow(1_000);
        send.OnWindow(10);
        That(send.TryTake(100, out var first) && first.Offset == 0 && first.Length == 100, "the window opens and a smaller one is ignored");
        That(send.TryTake(1_000, out var second) && second.Offset == 100 && second.Length == 354 && !send.TryTake(10, out _), "the rest goes next");
        send.OnLost(first);
        That(send.TryTake(60, out var resent) && resent.Offset == 0 && resent.Length == 60, "lost bytes go first, split to fit");
        send.OnAcknowledged(second);
        send.OnAcknowledged(resent);
        That(send.AcknowledgedFloor == 60 && !send.IsFullyAcknowledged, "the floor stops at the first unacknowledged byte");
        That(send.TryTake(100, out var rest) && rest.Offset == 60 && rest.Length == 40, "the rest of the lost range follows");
        send.OnAcknowledged(rest);
        That(send.IsFullyAcknowledged && send.Backlog == 0 && !send.HasDataToSend, "every byte is settled");

        var bytes = new byte[send.WriteOffset];
        var copy = new SessionSendStream(1_000);
        copy.AppendFrame(frameA, 0, frameA.Length);
        copy.AppendFrame(frameB, 0, frameB.Length);
        copy.CopyTo(new StreamRange(0, bytes.Length), bytes, 0);
        copy.TryTake(bytes.Length, out _);
        copy.QueueProbe(10);
        That(copy.TryTake(50, out var probe) && probe.Offset == 0 && probe.Length == 10, "a probe resends the oldest unacknowledged bytes");

        var receive = new SessionReceiveStream(1_000, 400);
        That(receive.Accept(200, bytes.Skip(200).ToArray()) == StreamAcceptResult.Accepted && receive.FrameCount == 0, "a later range waits");
        That(receive.Accept(0, bytes.Take(250).ToArray()) == StreamAcceptResult.Accepted && receive.FrameCount == 2, "the gap fills and both frames complete");
        That(receive.TryDequeue(out var readA) && readA.SequenceEqual(frameA) && receive.TryDequeue(out var readB) && readB.SequenceEqual(frameB),
            "frames come out whole and in order");
        That(receive.IsWindowUpdateDue && receive.NextWindow() == (ulong)bytes.Length + 1_000 && !receive.IsWindowUpdateDue,
            "consuming a quarter of the window earns an update");
        That(receive.Accept(1_500, new byte[10]) == StreamAcceptResult.FlowControlViolation, "bytes past the window are a violation");
        That(new SessionReceiveStream(1_000, 400).Accept(0, new byte[] { 0x91, 0x03 }) == StreamAcceptResult.FrameTooLarge,
            "a frame longer than the largest frame is refused");
        That(new SessionReceiveStream(1_000, 400).Accept(0, new byte[] { 0x80, 0x00 }) == StreamAcceptResult.MalformedLength &&
             new SessionReceiveStream(1_000, 400).Accept(0, new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }) == StreamAcceptResult.MalformedLength,
            "a length must be a minimal varint of at most five bytes");
        var duplicate = new SessionReceiveStream(1_000, 400);
        duplicate.Accept(0, bytes.Take(100).ToArray());
        That(duplicate.NewBytes(50, new byte[100]) == 50 && duplicate.NewBytes(0, new byte[100]) == 0, "only bytes past the delivered end are new");
        That(duplicate.BufferedBytes == 100, "a frame's announced length reserves nothing ahead of its bytes (" + duplicate.BufferedBytes + ")");
        var growth = duplicate.NewBytes(100, bytes.Skip(100).Take(50).ToArray());
        duplicate.Accept(100, bytes.Skip(100).Take(50).ToArray());
        That(duplicate.BufferedBytes == 150 && growth == 50, "so what admission is told is what the stream then holds");
    }

    internal static void PacerHoldsTheBurstThenTheSustainedRate()
    {
        var pacer = new Pacer(32 * 1024, 64 * 1024, 2_000_000, 0);
        That(pacer.CanSend(1200, 0), "a fresh pacer sends at once");
        pacer.OnSent(1200);
        pacer.OnSent(1200);
        That(!pacer.CanSend(1200, 0) && pacer.NextAvailable(1200, 0) == 18_311, "the peak bucket spaces packets at the burst rate");
        long time = 0;
        long early = 2 * 1200;
        long late = 0;
        while (time < 10_000_000)
        {
            if (pacer.CanSend(1200, time))
            {
                pacer.OnSent(1200);
                if (time < 2_000_000) early += 1200;
                if (time >= 8_000_000) late += 1200;
                continue;
            }

            var next = pacer.NextAvailable(1200, time);
            That(next > time, "the pacer always names a later time when it holds a packet back");
            time = next;
        }

        That(early >= 128 * 1024 && early <= (128 * 1024) + 4 * 1200, "the first two seconds burst at 512 kbit/s (" + early + ")");
        That(Math.Abs(late - (64 * 1024)) <= 2 * 1200, "later traffic holds 256 kbit/s (" + late + ")");
    }

    internal static void DatagramChannelsKeepOnlyTheNewest()
    {
        var channel = new DatagramChannel(1);
        for (var index = 0; index < 70; index++) channel.Enqueue(new[] { (byte)index });
        That(channel.QueuedCount == DatagramChannel.MaximumQueued && channel.PeekQueued()[0] == 6, "a full queue drops its oldest frames");
        That(channel.TakeQueued() == 0 && channel.TakeQueued() == 1 && channel.NextSequence == 2, "sequences count frames sent");
        var receiver = new DatagramChannel(1);
        That(receiver.ShouldDeliver(5) && !receiver.ShouldDeliver(5) && !receiver.ShouldDeliver(3) && receiver.ShouldDeliver(9),
            "only frames newer than the last delivered one are delivered");
        That(new DatagramChannel(2).ShouldDeliver(0), "sequence zero is deliverable first");
    }

    internal static void CookiesBindPathIdAndTime()
    {
        var guard = new CookieGuard(new SeededTransportRandom(5), 0);
        var firstSecret = (byte[])TransportSecrets.Field(guard, "currentSecret");
        var pathA = new TransportPath(new byte[] { 1, 2, 3 }, TransportPathKind.Direct);
        var pathB = new TransportPath(new byte[] { 1, 2, 4 }, TransportPathKind.Direct);
        var cookie = guard.Issue(pathA, 77, 1_000);
        That(cookie.Length == 16 && guard.Verify(cookie, pathA, 77, 5_000), "a cookie verifies for its path and id");
        That(!guard.Verify(cookie, pathB, 77, 5_000) && !guard.Verify(cookie, pathA, 78, 5_000), "another path or id fails");
        var tampered = (byte[])cookie.Clone();
        tampered[0] ^= 1;
        That(!guard.Verify(tampered, pathA, 77, 5_000) && !guard.Verify(new byte[15], pathA, 77, 5_000), "tampered or short cookies fail");
        That(guard.Verify(cookie, pathA, 77, CookieGuard.SlotMicros + 10), "the previous slot's secret still verifies");
        That(!guard.Verify(cookie, pathA, 77, (2 * CookieGuard.SlotMicros) + 10), "two rotations later it does not");
        That(firstSecret.All(value => value == 0), "and the secret that issued it has been zeroed");
        That(new TransportPath(new byte[] { 1, 2, 3 }, TransportPathKind.Relayed).ToString() == "Relayed path", "a path never prints its key");
    }

    internal static void HandshakeLimiterSpendsPerSourceAndGlobalBudgets()
    {
        var limiter = new HandshakeLimiter(2, 3);
        var pathA = new TransportPath(new byte[] { 1 }, TransportPathKind.Direct);
        var pathB = new TransportPath(new byte[] { 2 }, TransportPathKind.Direct);
        var pathC = new TransportPath(new byte[] { 3 }, TransportPathKind.Direct);
        That(limiter.TryAdmit(pathA, 0) && limiter.TryAdmit(pathA, 0) && !limiter.TryAdmit(pathA, 0), "a source gets its own budget");
        That(limiter.TryAdmit(pathB, 0) && !limiter.TryAdmit(pathC, 0), "the endpoint's budget caps all sources together");
        That(limiter.TryAdmit(pathA, 1_000_000), "the budget is a sliding second");
        That(!limiter.MeterInitiation(0) && limiter.MeterInitiation(0), "more than half the global rate counts as load");

        var crowded = new HandshakeLimiter(10, 100_000);
        for (var index = 0; index < HandshakeLimiter.MaximumSources; index++)
        {
            That(crowded.TryAdmit(new TransportPath(BitConverter.GetBytes(index), TransportPathKind.Direct), 0), "sources fill the table");
        }

        var newcomer = new TransportPath(new byte[] { 9, 9, 9, 9, 9 }, TransportPathKind.Direct);
        That(!crowded.TryAdmit(newcomer, 999_999) && crowded.TryAdmit(newcomer, 1_000_000), "a full table admits newcomers once entries go stale");
    }
}
