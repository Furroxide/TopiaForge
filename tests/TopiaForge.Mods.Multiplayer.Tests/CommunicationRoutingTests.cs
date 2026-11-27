using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class CommunicationRoutingTests
{
    internal static void Run()
    {
        OrdinaryAcknowledgementOrdering();
        PolicyBounds();
        VoiceAudiences();
        LifecycleAndAuthority();
        HostileVoice();
        ReplacementDropsHeldVoice();
        VoicePacketTests.DatagramsRoundTripOneOrTwoFrames();
        VoicePacketTests.MalformedDatagramsAreRefusedWhole();
        Console.WriteLine("CommunicationRoutingTests: 8 groups passed (acknowledgement ordering, bounds, voice audiences, lifecycle/authority, hostile voice, held voice cleanup, packet roundtrip, malformed packets).");
    }

    private static void OrdinaryAcknowledgementOrdering()
    {
        foreach (var routed in new[] { false, true })
        {
            using var rig = new VoiceSessionHarness();
            var listener = rig.AddClient("listener");
            var hostHeard = rig.Listen(rig.Host);
            var remoteHeard = rig.Listen(listener.Engine);
            var pair = rig.Network.Connect("early");
            rig.Host.Accept(pair.HostEnd);
            var peer = new RawPeer(pair.ClientEnd);
            peer.Join("Sam");
            rig.Flush();
            That(!rig.Host.IsParticipantReady(new ParticipantId("early")), "fixture withholds snapshot acknowledgement");
            using var lifetime = new FakeModLifetime();
            using var routing = routed ? rig.Host.RouteCommunications(lifetime, new CommunicationPolicy(
                new[] { Member("host", 1), Member("listener", 1), Member("early", 1) },
                new[] { new CommunicationRoute(1, 1) })).Value! : null;
            pair.ClientEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceSessionTests.Datagram(rig.Codec, 0, 1, 1, 101)));
            rig.Flush();
            var expected = routed ? 0 : 1;
            That(hostHeard.From("early").Count == expected && remoteHeard.From("early").Count == expected,
                "ordinary admitted voice reaches host and remote before acknowledgement; routed voice waits");
            peer.Send((frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.ReadyAck, 0));
            rig.Flush();
            That(rig.Host.IsParticipantReady(new ParticipantId("early")), "acknowledgement makes admitted peer ready");
            pair.ClientEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceSessionTests.Datagram(rig.Codec, 0, 2, 2, 102)));
            rig.Flush();
            That(hostHeard.From("early").Count == expected + 1 && remoteHeard.From("early").Count == expected + 1,
                "both paths accept fresh authorized voice after acknowledgement");
            if (routing == null) continue;
            routing.SetPolicy(new CommunicationPolicy(new[] { Member("early", 2), Member("host", 1), Member("listener", 1) },
                new[] { new CommunicationRoute(1, 1) }));
            pair.ClientEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceSessionTests.Datagram(rig.Codec, 0, 3, 3, 103)));
            rig.Flush();
            That(hostHeard.From("early").Count == 1 && remoteHeard.From("early").Count == 1,
                "readiness never bypasses audience authorization");
        }
    }

    private static void PolicyBounds()
    {
        Reject(() => new CommunicationRoute(0, 1));
        Reject(() => new CommunicationRoute(1, 17));
        foreach (var range in new[] { float.NaN, float.PositiveInfinity, -1, 1001 }) Reject(() => new CommunicationRoute(1, 1, range));
        Reject(() => new CommunicationPolicy(new[] { default(CommunicationMember) }, Array.Empty<CommunicationRoute>()));
        Reject(() => new CommunicationPolicy(Array.Empty<CommunicationMember>(), new[] { default(CommunicationRoute) }));
        var members = new[] { new CommunicationMember(new ParticipantId("a"), 1) };
        var routes = new[] { new CommunicationRoute(1, 1, 10) };
        var policy = new CommunicationPolicy(members, routes);
        members[0] = new CommunicationMember(new ParticipantId("a"), 2);
        routes[0] = new CommunicationRoute(2, 2);
        That(policy.TryGetDistance(new ParticipantId("a"), new ParticipantId("a"), out var distance) && distance == 10, "policy copies author lists");
        Reject(() => new CommunicationPolicy(new[] { members[0], members[0] }, routes));
        Reject(() => new CommunicationPolicy(members, new[] { routes[0], routes[0] }));
        Reject(() => new CommunicationPolicy(Enumerable.Range(0, 65).Select(i => new CommunicationMember(new ParticipantId("p" + i), 1)).ToArray(), routes));
        Reject(() => new CommunicationPolicy(members, Enumerable.Repeat(routes[0], 257).ToArray()));
    }

    private static void VoiceAudiences()
    {
        using var rig = new VoiceSessionHarness();
        var alive = rig.AddClient("alive");
        var dead = rig.AddClient("dead");
        var unknown = rig.AddClient("unknown");
        using var lifetime = new FakeModLifetime();
        var positions = new Positions();
        positions.Values["host"] = new Vec3(0, 0, 0);
        positions.Values["alive"] = new Vec3(10, 0, 0);
        using var source = rig.Host.SetAvatarSource(positions);
        var members = new[] { Member("host", 1), Member("alive", 1), Member("dead", 2) };
        using var routing = rig.Host.RouteCommunications(lifetime, new CommunicationPolicy(members,
            new[] { new CommunicationRoute(1, 1, 10), new CommunicationRoute(1, 2), new CommunicationRoute(2, 2) })).Value!;
        var hostHeard = rig.Listen(rig.Host);
        var aliveHeard = rig.Listen(alive.Engine);
        var deadHeard = rig.Listen(dead.Engine);
        var unknownHeard = rig.Listen(unknown.Engine);
        Send(rig, alive.Engine, 1);
        That(hostHeard.From("alive").Count == 1 && deadHeard.From("alive").Count == 1 && unknownHeard.Frames.Count == 0, "proximity inclusive boundary and one-way observer delivery; unassigned gets no frame");
        That((hostHeard.Frames[^1].Flags & VoiceFrameFlags.NonSpatial) == 0 && (deadHeard.Frames[^1].Flags & VoiceFrameFlags.NonSpatial) != 0, "the same packet has recipient-specific presentation");
        Send(rig, dead.Engine, 1);
        Send(rig, unknown.Engine, 1);
        That(hostHeard.From("dead").Count == 0 && aliveHeard.From("dead").Count == 0 && deadHeard.From("unknown").Count == 0, "dead and unassigned uplinks never leak to living or observers");
        positions.Values["alive"] = new Vec3(10.01f, 0, 0);
        Send(rig, alive.Engine, 2);
        That(hostHeard.From("alive").Count == 1 && deadHeard.From("alive").Count == 2, "out-of-range cannot reach host, global observer still hears");
        positions.Values.Remove("alive");
        Send(rig, rig.Host, 1);
        That(aliveHeard.Frames.Count == 0 && deadHeard.From("host").Count == 1, "host microphone obeys routes; unavailable presence fails closed");
        routing.SetPolicy(new CommunicationPolicy(members, new[] { new CommunicationRoute(1, 1), new CommunicationRoute(1, 2), new CommunicationRoute(2, 2) }));
        Send(rig, rig.Host, 2);
        That(aliveHeard.From("host").Count == 1 && (aliveHeard.Frames[^1].Flags & VoiceFrameFlags.NonSpatial) != 0, "hearing broadcast works without proximity and does not spatially fade");
        dead.Engine.SetListenerMutes(new[] { new ParticipantId("host") });
        rig.Advance(5);
        Send(rig, rig.Host, 3);
        That(aliveHeard.From("host").Count == 2 && deadHeard.From("host").Count == 2, "listener mute wins over global routing");
        rig.Host.SetServerMute(new ParticipantId("alive"), true);
        Send(rig, alive.Engine, 3);
        That(hostHeard.From("alive").Count == 1, "server mute wins over hearing route");
        routing.SetPolicy(new CommunicationPolicy(members, Array.Empty<CommunicationRoute>()));
        Send(rig, rig.Host, 4);
        That(aliveHeard.From("host").Count == 2 && deadHeard.From("host").Count == 2, "empty route set silences the host too");
    }

    private static void LifecycleAndAuthority()
    {
        using var rig = new VoiceSessionHarness();
        var client = rig.AddClient("a");
        using var lifetime = new FakeModLifetime();
        var policy = new CommunicationPolicy(new[] { Member("host", 1), Member("a", 1) }, new[] { new CommunicationRoute(1, 1, 5) });
        That(client.Engine.RouteCommunications(lifetime, policy).ErrorCode == ModErrorCode.NotAuthoritative, "clients cannot install routing");
        var old = rig.Host.RouteCommunications(lifetime, policy).Value!;
        That(!rig.Host.CanCommunicate(new ParticipantId("a"), new ParticipantId("host")), "no validated source fails closed");
        That(rig.Host.RouteCommunications(lifetime, policy).ErrorCode == ModErrorCode.Conflict, "single routing owner");
        rig.Host.ReplaceSession(new MultiplayerSessionId("replacement"), new SessionSeed(99));
        rig.Flush();
        That(old.SetPolicy(policy).ErrorCode == ModErrorCode.Cancelled && !rig.Host.HasCommunicationRouting
            && lifetime.TrackedResourceCount == 0, "session replacement retires authority and its lifetime registration");
        var next = rig.Host.RouteCommunications(lifetime, policy).Value!;
        old.Dispose();
        That(rig.Host.HasCommunicationRouting && lifetime.TrackedResourceCount == 1, "old disposal untracks its resource without retiring new routing");
        lifetime.Dispose();
        That(!rig.Host.HasCommunicationRouting && next.SetPolicy(policy).ErrorCode == ModErrorCode.Cancelled, "scope stop restores ordinary routing and rejects retained handle");
        var heard = rig.Listen(rig.Host);
        Send(rig, client.Engine, 1);
        That(heard.From("a").Count == 1 && (heard.Frames[0].Flags & VoiceFrameFlags.NonSpatial) == 0, "ordinary voice remains compatible after cleanup");
    }

    private static void HostileVoice()
    {
        using var rig = new VoiceSessionHarness();
        var client = rig.AddClient("a");
        var heard = rig.Listen(rig.Host);
        var packet = VoiceSessionTests.Datagram(rig.Codec, 0, 1, 1, 1);
        That(VoicePacket.TryReadUplink(packet, rig.Voice.MaximumPacketBytes, out var parsed), "valid attack fixture");
        // One-byte payload length for this codec: the flags byte immediately precedes it.
        packet[parsed.First.PayloadOffset - 2] = (byte)VoiceFrameFlags.NonSpatial;
        client.Link.Send(EngineChannel.Voice, new ArraySegment<byte>(packet));
        rig.Advance(10);
        That(heard.Frames.Count == 0, "forged global flag is rejected on the uplink");
        ulong sequence = 100;
        That(rig.FloodUntilMuted(client.Link, ref sequence) > 0, "voice abuse remains rate bounded and auto-muted");
    }

    private static void ReplacementDropsHeldVoice()
    {
        using var rig = new VoiceSessionHarness();
        var client = rig.AddClient("a");
        using var lifetime = new FakeModLifetime();
        using var route = rig.Host.RouteCommunications(lifetime, new CommunicationPolicy(
            new[] { Member("host", 1), Member("a", 1) }, new[] { new CommunicationRoute(1, 1) })).Value!;
        client.Engine.SubmitVoiceFrame(1, VoiceFrameFlags.EndOfTalk, rig.Codec.Packet(1));
        rig.Advance(5);
        That(rig.Host.GetVoiceStatistics().Delivered > 0, "a frame waits in the host inbox");
        That(rig.Host.SubmitVoiceFrame(1, VoiceFrameFlags.None, rig.Codec.Packet(2)) == VoiceSubmitResult.Held, "a host frame waits to pair");
        rig.Host.ReplaceSession(new MultiplayerSessionId("after-private"), new SessionSeed(42));
        That(!rig.Host.TryReceiveVoiceFrame(out _), "replacement discards received old-generation audio");
        var heard = rig.Listen(client.Engine);
        rig.Advance(10);
        That(heard.Frames.Count == 0, "held private audio cannot flush under ordinary routing in the replacement");
    }

    private static void Send(VoiceSessionHarness rig, SessionEngine engine, ulong timestamp)
    {
        engine.SubmitVoiceFrame(timestamp, VoiceFrameFlags.EndOfTalk, rig.Codec.Packet(1));
        rig.Advance(5);
    }

    internal static CommunicationMember Member(string id, int group) => new(new ParticipantId(id), group);
    internal static void That(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("An invalid communication policy was accepted.");
    }

    internal sealed class Positions : IInternalAvatarSource, IInternalCommunicationPositionSource
    {
        internal Dictionary<string, Vec3> Values { get; } = new();
        public bool TryGetCommunicationPosition(ParticipantId participant, out Vec3 position) => Values.TryGetValue(participant.Value, out position);
        public bool TryGetAvatar(ParticipantId participant, out IEntity? avatar) { avatar = null; return false; }
        public bool TryGetPose(ParticipantId participant, out MultiplayerPose pose) { pose = default; return false; }
    }
}
