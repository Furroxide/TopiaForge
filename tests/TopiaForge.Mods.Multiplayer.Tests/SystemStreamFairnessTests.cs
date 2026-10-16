using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// System channels between a host and several peers: a player that stops taking messages holds back only its own stream
/// and loses its connection at the stall bound, a briefly stalled one recovers, a headless host is heard, stale messages
/// after replacements never end an honest player, and an older peer credits a newer one's channels. White-box, on the
/// virtual engine transport.
/// </summary>
internal static class SystemStreamFairnessTests
{
    private const int TicksPerSecond = 60;
    private const byte NewerKind = 9;
    private static readonly ParticipantId SilentId = new("silent");
    private static readonly ParticipantId QuietId = new("quiet");
    private static readonly ParticipantId HonestId = new("honest");
    private static readonly ParticipantId AlphaId = new("alpha");
    private static readonly ParticipantId BetaId = new("beta");

    internal static void OneStalledPlayerNeverBlocksTheOthers()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);

        // Two players that acknowledge their snapshots and then never credit anything they are sent.
        var silent = ReadyRawPeer(harness, "silent");
        var quiet = ReadyRawPeer(harness, "quiet");
        var honest = harness.AddClient("honest");
        harness.Flush();
        That(harness.Host.IsParticipantReady(SilentId) && harness.Host.IsParticipantReady(QuietId) && harness.Host.IsParticipantReady(HonestId),
            "every player is Ready");
        var chat = Open(harness.Host, InternalSystemChannelKind.Chat);
        var mine = Open(honest.Engine, InternalSystemChannelKind.Chat);

        // Ten lines a frame to everyone but the quiet player, and ten to it alone. The honest player takes its lines as they
        // come; the others take nothing.
        var skipped = new List<ParticipantId>();
        var heard = 0;
        var direct = 0;
        var refused = 0;
        var started = harness.NowMicros;
        for (var frame = 0; frame < 110; frame++)
        {
            for (var index = 0; index < 10; index++)
            {
                chat.Send(Bytes("line"), InternalSystemTarget.AllExcept(QuietId), skipped);
                if (chat.Send(Bytes("direct"), InternalSystemTarget.Only(QuietId)).Succeeded) direct++;
                else refused++;
            }

            harness.Advance();
            heard += Drain(mine).Count;
            silent.Receive();
            quiet.Receive();
        }

        harness.Flush();
        heard += Drain(mine).Count;
        That(heard == 1_100, "the honest player hears every line (" + heard + ")");
        That(skipped.Count == 1_100 - InternalSystemLimits.HostWindowMessages && skipped.All(id => id.Equals(SilentId)),
            "past its window the silent player is skipped, and named (" + skipped.Count + ")");
        That(direct == InternalSystemLimits.HostWindowMessages && refused == 1_100 - direct, "and past the quiet one's, a message to it alone is refused");
        // Only refusals mark the quiet player's stream as waiting; nothing to everyone reaches it, so nothing skips it either.
        That(chat.CanSend(InternalSystemTarget.Only(HonestId), 1) && !chat.CanSend(InternalSystemTarget.Everyone, 1) &&
             chat.Send(Bytes("to the rest"), InternalSystemTarget.AllExcept(QuietId)).Value == 1, "everyone else is still reached");

        // Both leave their windows full: at the stall bound the host ends them as protocol violations, and nobody else.
        var stall = InternalSystemLimits.StallSeconds * 1_000_000L;
        while (harness.NowMicros - started < stall - 1_000_000) harness.Advance(TicksPerSecond);
        That(harness.Host.IsConnectedParticipant(SilentId) && harness.Host.IsConnectedParticipant(QuietId) &&
             harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0, "a full window is no violation before the bound");
        while (harness.NowMicros - started < stall + 3_000_000) harness.Advance(TicksPerSecond);
        That(!harness.Host.IsConnectedParticipant(SilentId) && !harness.Host.IsConnectedParticipant(QuietId) &&
             harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 2 && harness.Host.IsConnectedParticipant(HonestId),
            "past it the stalled players lose their connections, and the honest one keeps its own");
    }

    internal static void StallsCountOnlyOnceAPlayerIsReady()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var joining = SessionEngine.CreateClient(harness.Options, new MultiplayerSessionId("joining"), harness.NowMicros);
        harness.Track(joining);
        var hold = joining.HoldReadiness();
        harness.Join(joining, "alpha");
        harness.Flush();
        var world = Open(harness.Host, InternalSystemChannelKind.WorldReplication);
        var loading = Open(joining, InternalSystemChannelKind.WorldReplication);
        That(joining.IsAwaitingLocalReadiness, "the player has the snapshot and holds");

        // While the player loads, the host fills its window and keeps sending, well past the stall bound.
        var skipped = new List<ParticipantId>();
        for (var second = 0; second < InternalSystemLimits.StallSeconds + 90; second++)
        {
            for (var index = 0; index < 20; index++) world.Send(Bytes("chunk"), InternalSystemTarget.Everyone, skipped);
            harness.Advance(TicksPerSecond);
        }

        That(skipped.Count > 0 && harness.Host.IsConnectedParticipant(AlphaId), "a player still loading is skipped, never ended for its full window");

        // Ready at last, it takes what it holds: its stall counts from Ready, so it keeps its connection.
        hold.Dispose();
        harness.Advance();
        Drain(loading);
        harness.Flush();
        That(joining.Snapshot.State == MultiplayerSessionState.Ready && harness.Host.IsConnectedParticipant(AlphaId) &&
             harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0, "the player becomes Ready and keeps its connection");
    }

    internal static void ABrieflyStalledPlayerRecovers()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        var beta = harness.AddClient("beta");
        harness.Flush();
        var world = Open(harness.Host, InternalSystemChannelKind.WorldReplication);
        var steady = Open(alpha.Engine, InternalSystemChannelKind.WorldReplication);
        var stalled = Open(beta.Engine, InternalSystemChannelKind.WorldReplication);

        // Beta's game stalls for half a minute and takes nothing, while the host sends everyone a delta every frame.
        const int Frames = 30 * TicksPerSecond;
        var skipped = new List<ParticipantId>();
        var heard = 0;
        for (var frame = 0; frame < Frames; frame++)
        {
            world.Send(Bytes("delta " + frame), InternalSystemTarget.Everyone, skipped);
            harness.Advance();
            heard += Drain(steady).Count;
        }

        harness.Flush();
        heard += Drain(steady).Count;
        That(heard == Frames && skipped.Count == Frames - InternalSystemLimits.HostWindowMessages && skipped.All(id => id.Equals(BetaId)),
            "alpha hears every delta, and beta is skipped once its window is full (" + heard + " heard, " + skipped.Count + " skipped)");
        That(stalled.GetStatistics().Pending == InternalSystemLimits.HostWindowMessages && stalled.GetStatistics().Dropped == 0,
            "beta holds a full window, and nothing was dropped");

        // Beta's game comes back: it takes what it holds, its window opens, and its owner's baseline reaches it.
        var caughtUp = Drain(stalled);
        harness.Flush();
        That(caughtUp.Count == InternalSystemLimits.HostWindowMessages && caughtUp[0] == "host:delta 0", "beta takes what it was sent, in order");
        That(world.CanSend(InternalSystemTarget.Everyone, 1) && world.Send(Bytes("baseline"), InternalSystemTarget.Only(BetaId)).Value == 1,
            "its window is open again, for the baseline its owner sends it");
        harness.Flush();
        That(Drain(stalled).SequenceEqual(new[] { "host:baseline" }), "which arrives");

        // Taking messages again, beta keeps its connection however long the session runs on.
        for (var second = 0; second < InternalSystemLimits.StallSeconds + 30; second++)
        {
            world.Send(Bytes("tick " + second), InternalSystemTarget.Everyone);
            harness.Advance(TicksPerSecond);
            Drain(steady);
            Drain(stalled);
        }

        That(harness.Host.IsConnectedParticipant(BetaId) && harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0,
            "a player that stalled and recovered is never ended");
    }

    internal static void HeadlessHostsAreHeard()
    {
        // The harness's default host is headless: a dedicated server without a player of its own.
        using var harness = new EngineHarness();
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var scene = Open(harness.Host, InternalSystemChannelKind.SceneFollow);
        var follower = Open(alpha.Engine, InternalSystemChannelKind.SceneFollow);
        var server = InternalSystemMessage.HeadlessHostSender;
        That(follower.GetGeneration(server) != 0 && scene.GetGeneration(AlphaId) != 0, "the player has a stream with a host that has no player");

        var queued = 0;
        var heard = 0;
        for (var frame = 0; frame < 8; frame++)
        {
            for (var index = 0; index < 200; index++) queued += scene.Send(Bytes("scene " + queued), InternalSystemTarget.Everyone).Value;
            harness.Flush();
            while (follower.TryReceive(out var message)) heard += message.Sender.Equals(server) ? 1 : 0;
            harness.Flush();
        }

        That(queued == 1_600 && heard == queued, "the player hears all of it, from the headless host (" + heard + " of " + queued + ")");
        That(scene.CanSend(InternalSystemTarget.Everyone, InternalSystemLimits.MaximumPayloadBytes), "and taking it keeps the host's window open");

        // No player can take the headless host's name.
        var impostor = harness.AddRawPeer(server.Value);
        impostor.Join("Impostor");
        harness.Flush();
        That(!harness.Host.IsConnectedParticipant(server) && impostor.Receive().Any(message => message.Type == MessageType.JoinRejected),
            "a player whose id would be the headless host's is refused");
    }

    internal static void ReplacementsDuringAStallNeverEndAnHonestPlayer()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var kinds = new[]
        {
            InternalSystemChannelKind.WorldReplication, InternalSystemChannelKind.Avatars, InternalSystemChannelKind.Chat, InternalSystemChannelKind.SceneFollow
        };
        var hosts = kinds.Select(kind => Open(harness.Host, kind)).ToArray();
        var players = kinds.Select(kind => Open(alpha.Engine, kind)).ToArray();

        // The player's uplink stalls while the host replaces the session four times. Each replacement restarts the
        // player's streams, so it may send a window on every channel each time, and it sends only what CanSend allows.
        var uplink = alpha.Link.Outbound!;
        harness.Network.HoldNext(uplink, 1_000_000);
        var sent = 0;
        for (var round = 0; round <= 4; round++)
        {
            for (var frame = 0; frame < 110; frame++)
            {
                foreach (var channel in players)
                {
                    while (channel.CanSend(InternalSystemTarget.Host, 1) && channel.Send(Bytes("up"), InternalSystemTarget.Host).Succeeded) sent++;
                }

                harness.Advance();
            }

            if (round < 4) harness.Host.ReplaceSession(new MultiplayerSessionId("replaced-" + round), new SessionSeed((ulong)round + 10));
        }

        var window = kinds.Length * InternalSystemLimits.PlayerWindowMessages;
        That(sent == 5 * window, "the player sends a window on every channel for each of its five streams (" + sent + ")");

        // The stall ends and it all arrives at once: what belongs to the old sessions is dropped and counted, never charged
        // to the player's rate, so the player keeps its connection.
        harness.Network.Release(uplink);
        harness.Flush();
        var taken = hosts.Sum(channel => Drain(channel).Count);
        var dropped = hosts.Sum(channel => channel.GetStatistics().Dropped);
        That(harness.Host.IsConnectedParticipant(AlphaId) && harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0,
            "the honest player keeps its connection");
        That(taken == window && dropped == 4 * window, "the host takes the current streams and drops the old sessions' (" + taken + " taken, " + dropped + " dropped)");

        // Stale messages have an allowance of their own: past it the host discards them uncounted, and never ends the sender.
        var stale = harness.AddRawPeer("stale");
        stale.Join("Stale");
        harness.Flush();
        var join = Accepted(stale);
        var countedBefore = hosts[2].GetStatistics().Dropped;
        stale.Send((frame, body) =>
        {
            for (var index = 0; index < 3_000; index++)
            {
                SystemMessages.Write(frame, body, join.Epoch + 7, join.Generation, (byte)InternalSystemChannelKind.Chat, new byte[] { 1 });
            }
        });
        harness.Flush();
        var counted = hosts[2].GetStatistics().Dropped - countedBefore;
        That(harness.Host.IsConnectedParticipant(new ParticipantId("stale")) && harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0,
            "a flood of stale messages never ends its sender");
        That(counted >= SessionEngine.SystemIngressBurst && counted < SessionEngine.SystemIngressBurst + 64 && harness.Host.SystemMessagesDiscarded == 3_000 - counted,
            "the host counts them within their allowance and discards the rest (" + counted + " counted)");
    }

    internal static void UnknownKindsAreCreditedAsTaken()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);

        // A newer player sends on a channel this host has never heard of: the host leaves it unclaimed, and credits it.
        var newer = harness.AddRawPeer("newer");
        newer.Join("Newer");
        harness.Flush();
        var join = Accepted(newer);
        newer.Send((frame, body) =>
        {
            for (var index = 0; index < 40; index++) SystemMessages.Write(frame, body, join.Epoch, join.Generation, NewerKind, new byte[100]);
        });
        harness.Flush();
        var credit = LastCredit(newer.Receive(), NewerKind);
        That(harness.Host.SystemMessagesUnclaimed == 40 && credit.HasValue && credit.Value.Consumed == 40 && credit.Value.ConsumedBytes == 4_000 &&
             credit.Value.Generation == join.Generation, "the host credits all of them as taken, so the newer player's window opens");
        That(harness.Host.IsConnectedParticipant(new ParticipantId("newer")), "and keeps the player");

        // A newer host's channel never stalls an older player's streams either.
        var client = new RawHostClient();
        SystemStreamTests.JoinWithHost(client, generation: 3);
        for (var round = 1; round <= 3; round++)
        {
            client.Host.Send((frame, body) =>
            {
                for (var index = 0; index < InternalSystemLimits.HostWindowMessages; index++) SystemMessages.Write(frame, body, 0, 3, NewerKind, new byte[] { 1 });
            });
            client.Pump();
            client.Pump();
            var credited = LastCredit(client.Host.Receive(), NewerKind);
            That(credited.HasValue && credited.Value.Consumed == (ulong)(round * InternalSystemLimits.HostWindowMessages),
                "the player credits each window in full (round " + round + ")");
        }

        That(client.Engine.Snapshot.State == MultiplayerSessionState.Ready && client.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0,
            "and stays in its session");
    }

    /// <summary>Joins a raw peer and acknowledges its snapshot, so the host counts it Ready; it then credits nothing.</summary>
    private static RawPeer ReadyRawPeer(EngineHarness harness, string identity)
    {
        var peer = harness.AddRawPeer(identity);
        peer.Join(identity);
        harness.Flush();
        var accepted = Accepted(peer);
        peer.Send((frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.ReadyAck, accepted.Epoch));
        return peer;
    }

    /// <summary>Gets the <c>JoinAccepted</c> the host sent a raw peer.</summary>
    private static JoinAcceptedMessage Accepted(RawPeer peer)
    {
        foreach (var message in peer.Receive())
        {
            if (message.Type == MessageType.JoinAccepted) return JoinMessages.ReadJoinAccepted(message.Reader());
        }

        throw new InvalidOperationException("Assertion failed: the raw peer was accepted");
    }

    /// <summary>Gets the last credit for <paramref name="kind"/> among <paramref name="messages"/>, or null.</summary>
    private static SystemCreditMessage? LastCredit(List<ReceivedMessage> messages, byte kind)
    {
        SystemCreditMessage? last = null;
        foreach (var message in messages)
        {
            if (message.Type != MessageType.SystemCredit) continue;
            var credit = SystemMessages.ReadCredit(message.Reader());
            if (credit.Kind == kind) last = credit;
        }

        return last;
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
