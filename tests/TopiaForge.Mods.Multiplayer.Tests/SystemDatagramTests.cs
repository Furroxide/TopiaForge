using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The system channels' realtime lane (WS3.10): datagrams beside each channel's stream, on the engine's realtime channel.
/// They go both ways at once and only where a stream may go, a player keeps to its rate, the host takes twice that and
/// drops the rest, what belongs to an earlier session or connection is dropped, a full queue lets its oldest go, and
/// nothing a hostile peer sends on the lane ends its connection. White-box, on the virtual engine transport.
/// </summary>
internal static class SystemDatagramTests
{
    internal static void DatagramsTravelBothWays()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        var beta = harness.AddClient("beta");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.Avatars);
        var first = Open(alpha.Engine, InternalSystemChannelKind.Avatars);
        var second = Open(beta.Engine, InternalSystemChannelKind.Avatars);
        var alphaId = new ParticipantId("alpha");

        That(host.SendDatagram(Bytes("one"), InternalSystemTarget.Everyone).Value == 2, "the host sends one datagram to each joined player");
        That(host.SendDatagram(Bytes("two"), InternalSystemTarget.Only(alphaId)).Value == 1, "or to one of them");
        That(host.SendDatagram(Bytes("three"), InternalSystemTarget.AllExcept(alphaId)).Value == 1, "or to everyone but one");
        harness.Flush();
        That(Drain(first).SequenceEqual(new[] { "host:one", "host:two" }), "each player takes what was addressed to it, in order");
        That(Drain(second).SequenceEqual(new[] { "host:one", "host:three" }), "and nothing else");

        That(first.SendDatagram(Bytes("up"), InternalSystemTarget.Host).Value == 1 && second.SendDatagram(Bytes("also"), InternalSystemTarget.Host).Value == 1, "players send to the host");
        harness.Flush();
        That(Drain(host).OrderBy(line => line, StringComparer.Ordinal).SequenceEqual(new[] { "alpha:up", "beta:also" }), "which hears who sent each, from the connection");

        // The lane and the stream are apart: a message and a datagram each arrive where they belong.
        host.Send(Bytes("message"), InternalSystemTarget.Everyone);
        host.SendDatagram(Bytes("datagram"), InternalSystemTarget.Everyone);
        harness.Flush();
        That(first.TryReceive(out var message) && System.Text.Encoding.UTF8.GetString(message.Payload) == "message" && !first.TryReceive(out _), "the message on the stream");
        That(Drain(first).SequenceEqual(new[] { "host:datagram" }), "and the datagram on the lane");

        var stats = host.GetStatistics();
        That(stats.DatagramsSent == 6 && stats.DatagramsReceived == 2 && stats.DatagramsDropped == 0 && stats.Sent == 2,
            "the counters add up (" + stats.DatagramsSent + " sent, " + stats.DatagramsReceived + " received)");

        // Another kind is another channel: a datagram for a channel nobody opened is left unclaimed.
        var chat = Open(harness.Host, InternalSystemChannelKind.Chat);
        That(chat.SendDatagram(Bytes("hi"), InternalSystemTarget.Everyone).Value == 2, "the host sends on its chat channel's lane");
        harness.Flush();
        That(!first.TryReceiveDatagram(out _) && alpha.Engine.SystemDatagramsUnclaimed == 1, "a player with no chat channel open leaves it unclaimed");
    }

    internal static void DatagramsGoOnlyWhereTheyMay()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.Avatars);
        var player = Open(alpha.Engine, InternalSystemChannelKind.Avatars);
        That(Open(harness.Host, InternalSystemChannelKind.Time).SendDatagram(Bytes("t"), InternalSystemTarget.Everyone).ErrorCode == ModErrorCode.InvalidArgument,
            "the session's time has no lane: the host sends it as messages");
        That(host.SendDatagram(new ArraySegment<byte>(new byte[InternalSystemLimits.MaximumDatagramBytes + 1]), InternalSystemTarget.Everyone).ErrorCode == ModErrorCode.InvalidArgument,
            "a datagram past 1 KiB is refused");
        That(host.SendDatagram(new ArraySegment<byte>(new byte[InternalSystemLimits.MaximumDatagramBytes]), InternalSystemTarget.Everyone).Value == 1, "and one of 1 KiB goes");
        That(host.SendDatagram(Bytes("x"), InternalSystemTarget.Host).ErrorCode == ModErrorCode.InvalidArgument, "the host does not send to itself");
        That(player.SendDatagram(Bytes("x"), InternalSystemTarget.Everyone).ErrorCode == ModErrorCode.NotAuthoritative, "a player cannot address other players");
        Throws<ArgumentNullException>(() => host.SendDatagram(default, InternalSystemTarget.Everyone), "a datagram needs a payload");

        OperationResult<int>? inside = null;
        using (harness.HostSession.SubscribeTick(_ => inside ??= host.SendDatagram(Bytes("tick"), InternalSystemTarget.Everyone)))
        {
            harness.Advance(2);
        }

        That(inside != null && inside.ErrorCode == ModErrorCode.InvalidState, "nothing sent from inside a transaction may outlive an abort");
        player.Dispose();
        That(player.SendDatagram(Bytes("x"), InternalSystemTarget.Host).ErrorCode == ModErrorCode.InvalidState, "a closed channel sends nothing");
        host.SendDatagram(Bytes("late"), InternalSystemTarget.Everyone);
        harness.Flush();
        That(alpha.Engine.SystemDatagramsUnclaimed == 1, "and what arrives for it once closed is nobody's");

        var solo = SessionEngine.CreateHost(harness.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("solo"), new SessionSeed(1), new ParticipantId("me"), "Me", 0);
        That(Open(solo, InternalSystemChannelKind.Avatars).SendDatagram(Bytes("alone"), InternalSystemTarget.Everyone).Value == 0, "in single player nobody hears it");
        var waiting = SessionEngine.CreateClient(harness.Options, new MultiplayerSessionId("waiting"), 0);
        That(Open(waiting, InternalSystemChannelKind.Avatars).SendDatagram(Bytes("early"), InternalSystemTarget.Host).ErrorCode == ModErrorCode.Unavailable,
            "a player not joined to a host cannot send");
    }

    internal static void PlayersKeepToTheirRate()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.Avatars);
        var player = Open(alpha.Engine, InternalSystemChannelKind.Avatars);
        var sent = 0;
        while (sent < 200 && player.SendDatagram(Bytes("p"), InternalSystemTarget.Host).Succeeded) sent++;
        var refused = player.SendDatagram(Bytes("p"), InternalSystemTarget.Host);
        That(sent == InternalSystemLimits.PlayerDatagramBurst && refused.ErrorCode == ModErrorCode.RateLimited, "a player sends 64 at once and is told to wait (" + sent + ")");
        That(player.GetStatistics().Refused >= 1, "and the refusal is counted");
        harness.Flush();
        That(Drain(host).Count == sent && host.GetStatistics().DatagramsDropped == 0, "the host takes all it sent: twice the player's rate");

        // A second brings 64 more.
        harness.Advance(60);
        var again = 0;
        while (again < 200 && player.SendDatagram(Bytes("p"), InternalSystemTarget.Host).Succeeded) again++;
        That(again >= 60 && again <= InternalSystemLimits.PlayerDatagramBurst, "a second later about 64 more go (" + again + ")");
    }

    internal static void HostsDropWhatTheyCannotTake()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var early = harness.AddRawPeer("early");
        early.End.Send(EngineChannel.Realtime, Datagram(0, 0, (byte)InternalSystemChannelKind.Avatars, new byte[] { 1 }));
        harness.Flush();
        That(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0, "a datagram before joining is dropped, not a violation: it may be late");

        var raw = harness.AddRawPeer("raw");
        raw.Join("Raw");
        harness.Flush();
        var generation = SystemStreamTests.JoinedGeneration(raw);
        var avatars = Open(harness.Host, InternalSystemChannelKind.Avatars);
        var avatarsKind = (byte)InternalSystemChannelKind.Avatars;

        // A flood past the host's burst of 128 is dropped and counted; the rest arrive. It comes 100 a tick, within what a
        // link holds between two reads, so every datagram reaches the host's check.
        for (var burst = 0; burst < 3; burst++)
        {
            for (var index = 0; index < 100; index++) raw.End.Send(EngineChannel.Realtime, Datagram(0, generation, avatarsKind, new byte[] { (byte)index }));
            harness.Advance(1);
        }

        harness.Flush();
        var taken = Drain(avatars).Count;
        var flood = avatars.GetStatistics();
        That(taken >= 2 * InternalSystemLimits.PlayerDatagramBurst && taken < 140 && flood.DatagramsDropped == 300 - taken && harness.Network.ShedDatagrams == 0,
            "past twice a player's burst the host drops the flood (" + taken + " of 300 taken, " + flood.DatagramsDropped + " dropped)");

        // What belongs to another session or connection is dropped; a header that does not parse, or a payload past the
        // bound, is malformed; a kind nobody opened, or the time channel's, is unclaimed. None of it is a violation.
        harness.Advance(120);
        var dropped = avatars.GetStatistics().DatagramsDropped;
        var malformed = harness.Host.SystemDatagramsMalformed;
        var unclaimed = harness.Host.SystemDatagramsUnclaimed;
        raw.End.Send(EngineChannel.Realtime, Datagram(1, generation, avatarsKind, new byte[] { 1 }));
        raw.End.Send(EngineChannel.Realtime, Datagram(0, generation + 1, avatarsKind, new byte[] { 2 }));
        raw.End.Send(EngineChannel.Realtime, new ArraySegment<byte>(new byte[] { 2, 0, 0, avatarsKind, 9 }));
        raw.End.Send(EngineChannel.Realtime, new ArraySegment<byte>(new byte[] { 1, 0x80, 0x00, 0, avatarsKind }));
        raw.End.Send(EngineChannel.Realtime, Datagram(0, generation, avatarsKind, new byte[InternalSystemLimits.MaximumDatagramBytes + 1]));
        raw.End.Send(EngineChannel.Realtime, Datagram(0, generation, (byte)InternalSystemChannelKind.Chat, new byte[] { 3 }));
        raw.End.Send(EngineChannel.Realtime, Datagram(0, generation, (byte)InternalSystemChannelKind.Time, new byte[] { 4 }));
        raw.End.Send(EngineChannel.Realtime, Datagram(0, generation, 200, new byte[] { 5 }));
        raw.End.Send(EngineChannel.Realtime, Datagram(0, generation, avatarsKind, new byte[] { 6 }));
        harness.Flush();
        That(avatars.GetStatistics().DatagramsDropped == dropped + 2, "another session's and another connection's are dropped");
        That(harness.Host.SystemDatagramsMalformed == malformed + 3, "a datagram of another type, a varint spelt twice and a payload past the bound are malformed");
        That(harness.Host.SystemDatagramsUnclaimed == unclaimed + 3, "and one for a closed channel, the time channel or no kind at all is unclaimed");
        That(Drain(avatars).SequenceEqual(new[] { "raw:\u0006" }), "the one good datagram arrives");
        That(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0 && harness.Host.IsConnectedParticipant(new ParticipantId("raw")),
            "and nothing on the lane ends the connection");
    }

    internal static void QueuesAreBoundedAndRestartsDropWhatWaits()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.Avatars);
        var player = Open(alpha.Engine, InternalSystemChannelKind.Avatars);
        // 300 arrive, 100 a tick, and the player's owner takes none of them.
        for (var index = 0; index < 300; index++)
        {
            host.SendDatagram(new ArraySegment<byte>(new[] { (byte)index }), InternalSystemTarget.Everyone);
            if (index % 100 == 99) harness.Advance(1);
        }

        harness.Flush();
        var stats = player.GetStatistics();
        That(stats.DatagramsReceived == 300 && stats.DatagramsDropped == 300 - InternalSystemLimits.DatagramQueueLength, "a full queue lets go of its oldest (" + stats.DatagramsDropped + " dropped)");
        That(player.TryReceiveDatagram(out var oldest) && oldest.Payload.Array![oldest.Payload.Offset] == 300 - InternalSystemLimits.DatagramQueueLength, "and keeps the newest");

        // A replaced session drops what waits from the earlier one, on both sides.
        That(player.SendDatagram(Bytes("before"), InternalSystemTarget.Host).Succeeded, "the player sends in the first session");
        harness.Flush();
        harness.Host.ReplaceSession(new MultiplayerSessionId("second-session"), new SessionSeed(7));
        harness.Flush();
        That(!host.TryReceiveDatagram(out _), "the host drops what waited from the session it replaced");
        That(!player.TryReceiveDatagram(out _), "and so does the player");
        That(player.SendDatagram(Bytes("after"), InternalSystemTarget.Host).Succeeded, "the player sends in the new session");
        harness.Flush();
        That(Drain(host).SequenceEqual(new[] { "alpha:after" }), "which the host takes");
    }

    private static ArraySegment<byte> Datagram(ulong epoch, ulong generation, byte kind, byte[] payload)
    {
        var buffer = new byte[SystemDatagrams.MaximumHeaderBytes + payload.Length];
        var length = SystemDatagrams.Write(buffer, epoch, generation, kind, new ArraySegment<byte>(payload));
        return new ArraySegment<byte>(buffer, 0, length);
    }

    private static IInternalSystemChannel Open(SessionEngine engine, InternalSystemChannelKind kind)
    {
        var opened = engine.OpenSystemChannel(kind);
        if (!opened.TryGetValue(out var channel)) throw new InvalidOperationException("Assertion failed: the channel opens (" + opened.ErrorMessage + ")");
        return channel;
    }

    private static ArraySegment<byte> Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    private static List<string> Drain(IInternalSystemChannel channel)
    {
        var lines = new List<string>();
        while (channel.TryReceiveDatagram(out var datagram))
        {
            lines.Add(datagram.Sender + ":" + System.Text.Encoding.UTF8.GetString(datagram.Payload.Array!, datagram.Payload.Offset, datagram.Payload.Count));
        }

        return lines;
    }
}
