using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// System-channel streams: a rejoin restarts them and says so, each sender has its own window so nobody starves anyone
/// else, credits pace an honest sender so it is never dropped, and a peer that breaks its window or its rate loses its
/// connection. White-box, on the virtual engine transport.
/// </summary>
internal static class SystemStreamTests
{
    private static readonly ParticipantId HostId = new("host");
    private static readonly ParticipantId AlphaId = new("alpha");

    internal static void StreamsRestartWhenAPlayerRejoins()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.WorldReplication);
        var player = Open(alpha.Engine, InternalSystemChannelKind.WorldReplication);
        var hostView = host.GetGeneration(AlphaId);
        var playerView = player.GetGeneration(HostId);
        var playerChannel = player.Generation;
        var hostChannel = host.Generation;
        That(hostView != 0 && playerView != 0 && host.GetGeneration(new ParticipantId("nobody")) == 0 && player.GetGeneration(AlphaId) == 0,
            "each side has a stream with the other once the player joined, and none with anyone else");

        host.Send(Bytes("delta-1"), InternalSystemTarget.Only(AlphaId));
        harness.Flush();
        That(Drain(player).SequenceEqual(new[] { "host:delta-1" }), "the first delta arrives");

        // What each owner has not taken yet, what is in flight, and what was never sent when the link goes.
        player.Send(Bytes("up-early"), InternalSystemTarget.Host);
        host.Send(Bytes("undrained"), InternalSystemTarget.Only(AlphaId));
        harness.Flush();
        host.Send(Bytes("delta-2"), InternalSystemTarget.Only(AlphaId));
        harness.Host.FlushOutbound();
        player.Send(Bytes("up-lost"), InternalSystemTarget.Host);
        harness.Network.Sever(alpha.Link);
        harness.Advance();
        That(player.GetGeneration(HostId) == 0 && player.Generation != playerChannel, "a lost link ends the player's stream and says so");

        // The player comes back within the grace period: the same session, the same epoch, new streams.
        harness.Join(alpha.Engine, "alpha");
        harness.Flush();
        host.Send(Bytes("delta-3"), InternalSystemTarget.Only(AlphaId));
        harness.Flush();
        That(alpha.Engine.Snapshot.State == MultiplayerSessionState.Ready && alpha.Engine.Snapshot.Id.Equals(new MultiplayerSessionId("white-box-session")),
            "the player is back in the same session");
        That(player.GetGeneration(HostId) != 0 && player.GetGeneration(HostId) != playerView && host.GetGeneration(AlphaId) != hostView &&
             host.Generation != hostChannel, "both sides see a new generation, so their owners know to start again from a baseline");
        That(Drain(player).SequenceEqual(new[] { "host:delta-3" }), "the player hears only the new stream");
        That(player.GetStatistics().Dropped == 2, "what it held from the old stream, and what it never sent, are dropped and counted (" + player.GetStatistics().Dropped + ")");
        That(!host.TryReceive(out _) && host.GetStatistics().Dropped == 1, "the host drops what it held from the player's old stream");
    }

    internal static void OneSenderCannotStarveTheOthers()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var hostile = harness.AddRawPeer("hostile");
        hostile.Join("Hostile");
        harness.Flush();
        var generation = JoinedGeneration(hostile);
        var honest = harness.AddClient("honest");
        harness.Flush();
        var chat = Open(harness.Host, InternalSystemChannelKind.Chat);
        var mine = Open(honest.Engine, InternalSystemChannelKind.Chat);

        // The hostile player joined first and floods every frame, just inside its window; the owner drains every frame.
        var heard = 0;
        var frames = 0;
        while (harness.Host.IsConnectedParticipant(new ParticipantId("hostile")) && frames < 60)
        {
            hostile.Send((frame, body) =>
            {
                for (var index = 0; index < 60; index++) SystemMessages.Write(frame, body, 0, generation, (byte)InternalSystemChannelKind.Chat, new byte[] { 1 });
            });
            That(mine.Send(Bytes("hello " + frames), InternalSystemTarget.Host).Succeeded, "the honest player speaks in frame " + frames);
            harness.Advance();
            heard += Drain(chat).Count(line => line.StartsWith("honest:", StringComparison.Ordinal));
            frames++;
            if (frames == 5) That(heard == 5, "the honest player is heard in each of the first five frames (" + heard + ")");
        }

        That(!harness.Host.IsConnectedParticipant(new ParticipantId("hostile")) && harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 1 &&
             frames > 5 && frames < 30, "a player that keeps sending faster than the host allows loses its connection (after " + frames + " frames)");
        That(heard == frames, "and the honest player was heard every frame (" + heard + " of " + frames + ")");

        // A burst past the window in one go ends the connection at once; the honest player in the same frame is heard.
        var flooder = harness.AddRawPeer("flooder");
        flooder.Join("Flooder");
        harness.Flush();
        var floodGeneration = JoinedGeneration(flooder);
        flooder.Send((frame, body) =>
        {
            for (var index = 0; index < 1_100; index++) SystemMessages.Write(frame, body, 0, floodGeneration, (byte)InternalSystemChannelKind.Chat, new byte[] { 2 });
        });
        mine.Send(Bytes("still here"), InternalSystemTarget.Host);
        harness.Advance();
        var lines = Drain(chat);
        That(!harness.Host.IsConnectedParticipant(new ParticipantId("flooder")) && harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 2,
            "a burst past the window breaks the protocol");
        That(lines.Contains("honest:still here") && lines.Count(line => line.StartsWith("flooder:", StringComparison.Ordinal)) <= InternalSystemLimits.PlayerWindowMessages,
            "the honest player is heard, and the flooder never held more than its window (" + lines.Count + " lines)");
    }

    internal static void HonestSendersAreNeverDropped()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.WorldReplication);
        var player = Open(alpha.Engine, InternalSystemChannelKind.WorldReplication);

        // A 3 MiB baseline in 96 chunks while the player's game is still loading and its owner takes nothing.
        const int Chunks = 96;
        var sent = 0;
        while (sent < Chunks && host.Send(Chunk(sent), InternalSystemTarget.Only(AlphaId)).Succeeded) sent++;
        That(sent == InternalSystemLimits.HostWindowBytes / InternalSystemLimits.MaximumPayloadBytes, "the host fills one window, 1 MiB, and no more (" + sent + ")");
        That(!host.CanSend(InternalSystemTarget.Only(AlphaId), InternalSystemLimits.MaximumPayloadBytes), "the next chunk would not fit");
        for (var stall = 0; stall < 30; stall++)
        {
            // The player's game stalls: only the host runs, and it keeps trying; each try is refused, so nothing piles up.
            That(host.Send(Chunk(sent), InternalSystemTarget.Only(AlphaId)).ErrorCode == ModErrorCode.RateLimited, "a send during the stall is refused, not dropped later");
            harness.Host.Pump(harness.NowMicros);
        }

        harness.Flush();
        That(player.GetStatistics().Pending == sent && player.GetStatistics().Dropped == 0, "after the stall the player holds everything it was sent");

        var received = new List<int>();
        for (var round = 0; round < 64 && received.Count < Chunks; round++)
        {
            while (player.TryReceive(out var message)) received.Add(BitConverter.ToInt32(message.Payload, 0));
            harness.Flush();
            while (sent < Chunks && host.Send(Chunk(sent), InternalSystemTarget.Only(AlphaId)).Succeeded) sent++;
            harness.Flush();
        }

        That(received.SequenceEqual(Enumerable.Range(0, Chunks)) && player.GetStatistics().Dropped == 0 && host.GetStatistics().Dropped == 0,
            "every chunk arrives once, in order, and nothing is dropped (" + received.Count + " received)");

        // A player keeps to its window towards the host, and to its rate, so the host never has to refuse it.
        var up = 0;
        while (player.Send(Bytes("u" + up), InternalSystemTarget.Host).Succeeded) up++;
        That(up == InternalSystemLimits.PlayerWindowMessages, "a player fills its window to the host (" + up + ")");
        harness.Flush();
        That(Drain(host).Count == up, "the host takes them");
        harness.Flush();
        That(player.CanSend(InternalSystemTarget.Host, 1), "and the player's window opens again");

        // Sending as fast as the windows allow, the player meets its own rate, so its host never has to end it.
        var startTick = harness.Tick;
        var total = 0;
        var refused = 0;
        for (var frame = 0; frame < 12; frame++)
        {
            for (var index = 0; index < InternalSystemLimits.PlayerWindowMessages; index++)
            {
                if (player.Send(Bytes("r" + index), InternalSystemTarget.Host).Succeeded) total++;
                else refused++;
            }

            harness.Flush();
            Drain(host);
            harness.Flush();
        }

        var seconds = (harness.Tick - startTick) / 60.0;
        var allowed = InternalSystemLimits.PlayerMessageBurst + (InternalSystemLimits.PlayerMessagesPerSecond * seconds) + 1;
        That(refused > 0 && total >= InternalSystemLimits.PlayerMessageBurst && total <= allowed,
            "past its burst the player is held to its rate (" + total + " sent in " + seconds.ToString("0.00") + " s, " + refused + " refused)");
        That(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0 && alpha.Engine.Snapshot.State == MultiplayerSessionState.Ready,
            "and its host never ends it");
    }

    internal static void PeersThatBreakTheirWindowsLoseTheirConnection()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var liar = harness.AddRawPeer("liar");
        liar.Join("Liar");
        harness.Flush();
        var generation = JoinedGeneration(liar);
        Open(harness.Host, InternalSystemChannelKind.Chat);

        // A credit for another connection is ignored; a credit for more than was sent breaks the protocol.
        liar.Send((frame, body) => SystemMessages.WriteCredit(frame, body, 0, generation + 1, (byte)InternalSystemChannelKind.Chat, 5, 5));
        harness.Flush();
        That(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0 && harness.Host.IsConnectedParticipant(new ParticipantId("liar")),
            "a stale credit is ignored");
        liar.Send((frame, body) => SystemMessages.WriteCredit(frame, body, 0, generation, (byte)InternalSystemChannelKind.Chat, 5, 5));
        harness.Flush();
        That(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 1 && !harness.Host.IsConnectedParticipant(new ParticipantId("liar")),
            "a credit for messages never sent breaks the protocol");

        // A host that sends past a player's window breaks the protocol too, and the player leaves its session.
        var client = new RawHostClient();
        JoinWithHost(client, generation: 7);
        var channel = Open(client.Engine, InternalSystemChannelKind.WorldReplication);
        client.Host.Send((frame, body) =>
        {
            SystemMessages.Write(frame, body, 0, 8, (byte)InternalSystemChannelKind.WorldReplication, new byte[] { 1 });
            SystemMessages.Write(frame, body, 0, 7, (byte)InternalSystemChannelKind.WorldReplication, new byte[] { 2 });
            SessionMessages.WriteCommit(frame, body, 0, 0);
        });
        client.Pump();
        That(channel.TryReceive(out var accepted) && accepted.Payload.Single() == 2 && !channel.TryReceive(out _) && channel.GetStatistics().Dropped == 1,
            "a player drops what is stamped with another connection's generation");
        client.Host.Send((frame, body) =>
        {
            for (var index = 0; index <= InternalSystemLimits.HostWindowMessages; index++)
            {
                SystemMessages.Write(frame, body, 0, 7, (byte)InternalSystemChannelKind.WorldReplication, new byte[] { 3 });
            }
        });
        client.Pump();
        That(client.Engine.Snapshot.State == MultiplayerSessionState.Ended && client.Engine.Snapshot.EndReason == MultiplayerSessionEndReason.ProtocolMismatch &&
             client.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 1, "a host past the player's window ends the player's session");
    }

    /// <summary>Gets the join generation the host gave a raw peer in its <c>JoinAccepted</c>.</summary>
    internal static ulong JoinedGeneration(RawPeer peer)
    {
        foreach (var message in peer.Receive())
        {
            if (message.Type == MessageType.JoinAccepted) return JoinMessages.ReadJoinAccepted(message.Reader()).Generation;
        }

        throw new InvalidOperationException("Assertion failed: the raw peer was accepted");
    }

    /// <summary>Accepts the client's join as a host would, naming the host's player so system messages have a sender.</summary>
    internal static void JoinWithHost(RawHostClient client, ulong generation)
    {
        client.Engine.Connect(client.ClientEnd, "Client");
        client.Host.Send((frame, body) =>
        {
            JoinMessages.WriteJoinAccepted(frame, body, new JoinAcceptedMessage
            {
                SessionId = "raw-session",
                Seed = 1,
                ParticipantId = "client",
                HostParticipantId = "host",
                StepRateHz = 60,
                NextSequence = 1,
                Generation = generation
            });
            SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, 0);
            SessionMessages.WriteEmpty(frame, body, MessageType.SnapshotEnd);
            SessionMessages.WriteCommit(frame, body, 0, 0);
        });
        client.Pump();
        client.Pump();
        client.Host.Receive();
    }

    private static ArraySegment<byte> Chunk(int index)
    {
        var bytes = new byte[InternalSystemLimits.MaximumPayloadBytes];
        BitConverter.GetBytes(index).CopyTo(bytes, 0);
        return new ArraySegment<byte>(bytes);
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
        while (channel.TryReceive(out var message)) lines.Add(message.Sender + ":" + System.Text.Encoding.UTF8.GetString(message.Payload));
        return lines;
    }
}
