using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class ParticipantLifetimeTests
{
    internal static void Run()
    {
        SharedLifetimes();
        WireBounds();
        OldProtocolIsRefused();
        Console.WriteLine("Participant lifetimes: host, reconnect, replacement, wire bounds and old protocol refusal passed.");
    }

    private static void SharedLifetimes()
    {
        using var rig = new EngineHarness(MultiplayerProcessKind.Interactive);
        var (guest, _, link) = rig.AddClient("guest");
        rig.Flush();
        var host = Row(rig.Host, "host");
        var first = Row(rig.Host, "guest");
        That(!host.PlayerKey.IsValid && host.LifetimeId != Guid.Empty, "host identity needs no persistence key");
        That(Row(guest, "host").LifetimeId == host.LifetimeId && Row(guest, "guest").LifetimeId == first.LifetimeId,
            "the participant wire carries exactly the host-issued lifetimes to the guest");
        rig.Advance(30);
        That(Row(rig.Host, "guest").LifetimeId == first.LifetimeId, "ordinary pumping preserves the lifetime");
        rig.Network.Sever(link); rig.Advance(3);
        That(!Row(rig.Host, "guest").IsConnected, "disconnected record is retained during grace");
        rig.Join(guest, "guest"); rig.Flush();
        var rejoined = Row(rig.Host, "guest");
        That(rejoined.Id.Equals(first.Id) && rejoined.PlayerKey.Equals(first.PlayerKey), "rejoin retains slot and persistence identity");
        That(rejoined.LifetimeId != first.LifetimeId && Row(guest, "guest").LifetimeId == rejoined.LifetimeId,
            "same-player reconnect has a new lifetime shared across peers");
        That(Row(rig.Host, "host").LifetimeId == host.LifetimeId, "unrelated joins do not retire the host");
        rig.Host.ReplaceSession(new MultiplayerSessionId("replacement"), new SessionSeed(123)); rig.Flush();
        That(Row(rig.Host, "host").LifetimeId != host.LifetimeId && Row(rig.Host, "guest").LifetimeId != rejoined.LifetimeId,
            "replacement retires host and guest lifetimes");
        That(Row(guest, "host").LifetimeId == Row(rig.Host, "host").LifetimeId &&
             Row(guest, "guest").LifetimeId == Row(rig.Host, "guest").LifetimeId, "replacement lifetimes travel across the wire");
    }

    private static MultiplayerParticipant Row(SessionEngine engine, string id) =>
        engine.Snapshot.Participants.Single(row => row.Id.Value == id);

    private static void WireBounds()
    {
        var token = Guid.NewGuid();
        var entries = new[] { Entry("host", token), Entry("guest", Guid.NewGuid()) };
        var frame = new ProtocolWriter(); var body = new ProtocolWriter();
        SessionMessages.WriteParticipants(frame, body, entries);
        var decoded = SessionMessages.ReadParticipants(new ProtocolReader(body.Buffer, 0, body.Length));
        That(decoded[0].LifetimeId == token, "wire round trip preserves the opaque lifetime");
        foreach (var malformed in new[] { new[] { Entry("host", Guid.Empty) }, new[] { Entry("host", token), Entry("guest", token) } })
        {
            SessionMessages.WriteParticipants(frame, body, malformed);
            var rejected = false;
            try { SessionMessages.ReadParticipants(new ProtocolReader(body.Buffer, 0, body.Length)); }
            catch (ProtocolException) { rejected = true; }
            That(rejected, "empty and duplicate lifetimes are rejected");
        }
        SessionMessages.WriteParticipants(frame, body, entries);
        var truncated = false;
        try { SessionMessages.ReadParticipants(new ProtocolReader(body.Buffer, 0, body.Length - 1)); }
        catch (ProtocolException) { truncated = true; }
        That(truncated, "truncated lifetime is rejected");
    }

    private static ParticipantEntry Entry(string id, Guid lifetime) =>
        new(id, id, true, false, MultiplayerConnectionQuality.Unknown, null, null, lifetime);

    private static void OldProtocolIsRefused()
    {
        using var rig = new EngineHarness();
        var raw = rig.AddRawPeer("old-peer");
        raw.Join("Sam", protocolVersion: "1.0.0");
        rig.Flush();
        var rejected = raw.Receive().FirstOrDefault(message => message.Type == MessageType.JoinRejected);
        That(rejected.Body != null && JoinMessages.ReadEndReason(rejected.Reader()) == MultiplayerSessionEndReason.ProtocolMismatch,
            "the former participant format is refused at admission");
    }
}
