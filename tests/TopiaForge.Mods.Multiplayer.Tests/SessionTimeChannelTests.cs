using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The session's time channel (WS3.13) in the engine: the host's alone. The host sends it to its players; a joined player's
/// engine refuses to send it as not authoritative; and a player whose connection sends it anyway is ended as a protocol
/// violation, as for any other message a player may never send, while the players that remain go on hearing the host.
/// White-box, on the virtual engine transport.
/// </summary>
internal static class SessionTimeChannelTests
{
    internal static void OnlyTheHostSendsTheSessionsTime()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.Time);
        var player = Open(alpha.Engine, InternalSystemChannelKind.Time);
        var time = new byte[] { 1, 1, 0, 0, 0, 0x3F, 1 };

        That(host.Send(new ArraySegment<byte>(time), InternalSystemTarget.Everyone).Value == 1, "the host sends its time to its player");
        harness.Flush();
        That(player.TryReceive(out var heard) && heard.Sender.Value == "host" && heard.Payload.SequenceEqual(time) && !player.TryReceive(out _),
            "which hears it, once, from the host");

        var refused = player.Send(new ArraySegment<byte>(time), InternalSystemTarget.Host);
        That(refused.ErrorCode == ModErrorCode.NotAuthoritative && player.GetStatistics().Sent == 0,
            "a joined player's engine refuses to send the session's time, as not authoritative (" + refused.ErrorMessage + ")");
        That(Open(alpha.Engine, InternalSystemChannelKind.Chat).Send(new ArraySegment<byte>(time), InternalSystemTarget.Host).Succeeded,
            "while the same player sends on its other channels as before");

        // A peer that sends the time anyway, with its own connection's generation, is ended; nothing reaches the host's channel.
        var raw = harness.AddRawPeer("raw");
        raw.Join("Raw");
        harness.Flush();
        var generation = SystemStreamTests.JoinedGeneration(raw);
        var violations = harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation);
        That(harness.Host.IsConnectedParticipant(new ParticipantId("raw")), "the raw peer has joined");
        raw.Send((frame, body) => SystemMessages.Write(frame, body, 0, generation, (byte)InternalSystemChannelKind.Time, time));
        harness.Flush();
        That(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == violations + 1 && !harness.Host.IsConnectedParticipant(new ParticipantId("raw")),
            "a player that sends the session's time anyway is ended as a protocol violation");
        That(!host.TryReceive(out _) && host.GetStatistics().Received == 0, "and nothing it sent reaches the host's channel");

        // Whatever stream the message claims, another connection's generation or another session's epoch, which an honest
        // player's stale message could carry: a player never sends the session's time, so it is ended all the same.
        foreach (var (name, epoch, offset) in new[] { ("stale", 0UL, 1UL), ("other", 7UL, 0UL) })
        {
            var peer = harness.AddRawPeer(name);
            peer.Join(name);
            harness.Flush();
            var joined = SystemStreamTests.JoinedGeneration(peer);
            var before = harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation);
            That(harness.Host.IsConnectedParticipant(new ParticipantId(name)), "the " + name + " peer has joined");
            peer.Send((frame, body) => SystemMessages.Write(frame, body, epoch, joined + offset, (byte)InternalSystemChannelKind.Time, time));
            harness.Flush();
            That(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == before + 1 && !harness.Host.IsConnectedParticipant(new ParticipantId(name)),
                "a player's time stamped with epoch " + epoch + " and generation " + (joined + offset) + " is a protocol violation too");
        }

        That(!host.TryReceive(out _) && host.GetStatistics().Received == 0, "and none of it reaches the host's channel");

        That(host.Send(new ArraySegment<byte>(time), InternalSystemTarget.Everyone).Value == 1, "the host goes on sending its time");
        harness.Flush();
        That(player.TryReceive(out _) && alpha.Engine.Snapshot.Id.Equals(harness.Host.Snapshot.Id), "to the player that remains, still in the session");
    }

    private static IInternalSystemChannel Open(SessionEngine engine, InternalSystemChannelKind kind)
    {
        var opened = engine.OpenSystemChannel(kind);
        if (!opened.TryGetValue(out var channel)) throw new InvalidOperationException("Assertion failed: the channel opens (" + opened.ErrorMessage + ")");
        return channel;
    }
}
