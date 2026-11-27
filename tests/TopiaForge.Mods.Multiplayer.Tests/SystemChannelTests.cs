using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The engine's runtime support: system channels between peers, readiness holds, the sources that name entities and
/// avatars, the local player's name and the end-of-frame flush. White-box, on the virtual engine transport.
/// </summary>
internal static class SystemChannelTests
{
    internal static void SystemMessagesTravelBothWaysInOrder()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        var beta = harness.AddClient("beta");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.WorldReplication);
        var first = Open(alpha.Engine, InternalSystemChannelKind.WorldReplication);
        var second = Open(beta.Engine, InternalSystemChannelKind.WorldReplication);

        That(host.Send(Bytes("one"), InternalSystemTarget.Everyone).Value == 2, "the host queues one copy for each joined player");
        That(host.Send(Bytes("two"), InternalSystemTarget.Only(new ParticipantId("alpha"))).Value == 1, "or for one of them");
        That(host.Send(Bytes("three"), InternalSystemTarget.AllExcept(new ParticipantId("alpha"))).Value == 1, "or for everyone but one");
        harness.Flush();
        That(Drain(first).SequenceEqual(new[] { "host:one", "host:two" }), "each player hears what was addressed to it, in order");
        That(Drain(second).SequenceEqual(new[] { "host:one", "host:three" }), "and nothing else");

        That(first.Send(Bytes("up"), InternalSystemTarget.Host).Value == 1 && second.Send(Bytes("also"), InternalSystemTarget.Host).Value == 1, "players send to the host");
        harness.Flush();
        That(Drain(host).OrderBy(line => line, StringComparer.Ordinal).SequenceEqual(new[] { "alpha:up", "beta:also" }), "which hears who sent each");
        That(first.Send(Bytes("x"), InternalSystemTarget.Everyone).ErrorCode == ModErrorCode.NotAuthoritative, "a player cannot address other players");
        That(host.Send(Bytes("x"), InternalSystemTarget.Host).ErrorCode == ModErrorCode.InvalidArgument, "the host does not send to itself");

        var stats = host.GetStatistics();
        That(stats.Kind == InternalSystemChannelKind.WorldReplication && stats.Sent == 4 && stats.Received == 2 && stats.Dropped == 0 && stats.Pending == 0,
            "the host's counters add up (" + stats.Sent + " sent, " + stats.Received + " received)");

        // Another kind is another channel: nothing crosses between them.
        var chat = Open(harness.Host, InternalSystemChannelKind.Chat);
        That(chat.Send(Bytes("hi"), InternalSystemTarget.Everyone).Value == 2, "the host chats");
        harness.Flush();
        That(!first.TryReceive(out _) && harness.Host.SystemMessagesUnclaimed == 0 && alpha.Engine.SystemMessagesUnclaimed == 1,
            "a player with no chat channel open leaves the message unclaimed");
    }

    internal static void MessagesFromAnEarlierSessionAreDropped()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive, latencyTicks: 3);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.SceneFollow);
        var player = Open(alpha.Engine, InternalSystemChannelKind.SceneFollow);

        That(player.Send(Bytes("stale"), InternalSystemTarget.Host).Succeeded, "the player sends in the first session");
        alpha.Engine.Pump(harness.NowMicros);
        harness.Host.ReplaceSession(new MultiplayerSessionId("second-session"), new SessionSeed(7));
        That(host.Send(Bytes("after"), InternalSystemTarget.Everyone).Value == 1, "the host speaks in the new session");
        harness.Flush();
        That(!host.TryReceive(out _) && host.GetStatistics().Dropped == 1, "the host drops what the player sent before it saw the replacement");
        That(Drain(player).SequenceEqual(new[] { "host:after" }) && alpha.Engine.Snapshot.Id.Equals(new MultiplayerSessionId("second-session")),
            "the player hears the new session's message");

        That(host.Send(Bytes("before"), InternalSystemTarget.Everyone).Value == 1, "a message queued just before another replacement");
        harness.Host.ReplaceSession(new MultiplayerSessionId("third-session"), new SessionSeed(8));
        harness.Flush();
        That(!player.TryReceive(out _) && player.GetStatistics().Dropped == 1, "is dropped with the session it belonged to");
        That(player.Send(Bytes("fresh"), InternalSystemTarget.Host).Succeeded, "the player speaks in the third session");
        harness.Flush();
        That(Drain(host).SequenceEqual(new[] { "alpha:fresh" }), "and the host hears it");
    }

    internal static void ChannelsAreBoundedAndOwnedOnce()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.Avatars);
        That(harness.Host.OpenSystemChannel(InternalSystemChannelKind.Avatars).ErrorCode == ModErrorCode.Conflict, "a channel has one owner");
        Throws<ArgumentOutOfRangeException>(() => harness.Host.OpenSystemChannel((InternalSystemChannelKind)9), "an unknown kind cannot be opened");
        Throws<ArgumentNullException>(() => host.Send(default, InternalSystemTarget.Everyone), "a message needs a payload");
        That(host.Send(new ArraySegment<byte>(new byte[InternalSystemLimits.MaximumPayloadBytes + 1]), InternalSystemTarget.Everyone).ErrorCode == ModErrorCode.InvalidArgument,
            "a payload past 32 KiB is refused");
        var player = Open(alpha.Engine, InternalSystemChannelKind.Avatars);

        // A player's window from its host is 1,024 messages and 1 MiB on each channel: past it the player is skipped there
        // and then, and named, so nothing the host was told it sent is ever dropped later.
        var alphaId = new ParticipantId("alpha");
        var accepted = 0;
        var skipped = new List<ParticipantId>();
        for (var index = 0; index < InternalSystemLimits.HostWindowMessages + 6; index++)
        {
            accepted += host.Send(Bytes("m" + index), InternalSystemTarget.Everyone, skipped).Value;
        }

        That(accepted == InternalSystemLimits.HostWindowMessages && host.GetStatistics().Refused == 6 && skipped.Count == 6 && skipped.All(id => id.Equals(alphaId)) &&
             !host.CanSend(InternalSystemTarget.Everyone, 1), "a full window skips the player for the host's next sends, and names it (" + accepted + " queued)");
        That(host.Send(Bytes("only"), InternalSystemTarget.Only(alphaId)).ErrorCode == ModErrorCode.RateLimited && host.GetStatistics().Refused == 7,
            "and a message to that player alone is refused whole");
        harness.Flush();
        That(player.GetStatistics().Pending == InternalSystemLimits.HostWindowMessages && player.GetStatistics().Dropped == 0, "the player holds all it was sent");
        That(Drain(player).Count == InternalSystemLimits.HostWindowMessages, "for its owner to take");
        harness.Flush();
        That(host.CanSend(InternalSystemTarget.Everyone, InternalSystemLimits.MaximumPayloadBytes), "and taking them opens the window again");
        var large = 0;
        for (var index = 0; index < 40; index++)
        {
            large += host.Send(new ArraySegment<byte>(new byte[InternalSystemLimits.MaximumPayloadBytes]), InternalSystemTarget.Everyone).Value;
        }

        harness.Flush();
        That(large == 32 && player.GetStatistics().Pending == 32 && player.GetStatistics().Dropped == 0,
            "the window also holds at most 1 MiB of payload (" + large + " accepted)");

        player.Dispose();
        That(!player.IsOpen && player.Send(Bytes("x"), InternalSystemTarget.Host).ErrorCode == ModErrorCode.InvalidState, "a closed channel sends nothing");
        harness.Flush();
        That(player.GetStatistics().Dropped == 32 && host.CanSend(InternalSystemTarget.Everyone, InternalSystemLimits.MaximumPayloadBytes),
            "what it held is dropped, which opens the host's window as taking it would");
        That(Open(alpha.Engine, InternalSystemChannelKind.Avatars).IsOpen, "and its kind can be opened again");

        // Sending from inside a transaction would outlive an abort, so it is refused.
        OperationResult<int>? inside = null;
        using (harness.HostSession.SubscribeTick(_ => inside ??= host.Send(Bytes("tick"), InternalSystemTarget.Everyone)))
        {
            harness.Advance(2);
        }

        That(inside != null && inside.ErrorCode == ModErrorCode.InvalidState, "a tick handler cannot send system messages");

        var solo = SessionEngine.CreateHost(harness.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("solo"), new SessionSeed(1),
            new ParticipantId("me"), "Me", 0);
        That(Open(solo, InternalSystemChannelKind.Chat).Send(Bytes("alone"), InternalSystemTarget.Everyone).Value == 0, "in single player nobody hears it");
        var waiting = SessionEngine.CreateClient(harness.Options, new MultiplayerSessionId("waiting"), 0);
        That(Open(waiting, InternalSystemChannelKind.Chat).Send(Bytes("early"), InternalSystemTarget.Host).ErrorCode == ModErrorCode.Unavailable,
            "a player not joined to a host cannot send");
    }

    internal static void HostilePeersCannotAbuseSystemMessages()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var early = harness.AddRawPeer("early");
        early.Send((frame, body) => SystemMessages.Write(frame, body, 0, 0, (byte)InternalSystemChannelKind.Chat, new byte[] { 1 }));
        harness.Flush();
        That(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 1, "a system message before joining is a protocol violation");

        var raw = harness.AddRawPeer("raw");
        raw.Join("Raw");
        harness.Flush();
        var generation = SystemStreamTests.JoinedGeneration(raw);
        var chat = Open(harness.Host, InternalSystemChannelKind.Chat);
        raw.Send((frame, body) =>
        {
            SystemMessages.Write(frame, body, 0, generation, 0, new byte[] { 1 });
            SystemMessages.Write(frame, body, 0, generation, 200, new byte[] { 2 });
            SystemMessages.Write(frame, body, 0, generation + 1, (byte)InternalSystemChannelKind.Chat, new byte[] { 4 });
            SystemMessages.Write(frame, body, 0, generation, (byte)InternalSystemChannelKind.Chat, new byte[] { 3 });
        });
        harness.Flush();
        That(harness.Host.SystemMessagesUnclaimed == 2 && chat.TryReceive(out var kept) && kept.Payload.Single() == 3 && !chat.TryReceive(out _) &&
             chat.GetStatistics().Dropped == 1, "unknown kinds are left unclaimed, another connection's generation is dropped, and the rest arrive");
        raw.Send((frame, body) => SystemMessages.Write(frame, body, 0, generation, (byte)InternalSystemChannelKind.Chat, new byte[InternalSystemLimits.MaximumPayloadBytes + 1]));
        harness.Flush();
        That(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 2 && !chat.TryReceive(out _), "an oversized payload ends that connection");
    }

    internal static void MapFollowCanHoldAfterAReplacementArrives()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var guest = harness.AddClient("alpha");
        harness.Flush();
        harness.Host.ReplaceSession(new MultiplayerSessionId("new-map"), new SessionSeed(7));
        harness.Host.FlushOutbound();
        harness.Network.DeliverDue(harness.Tick + 1);
        guest.Engine.Pump(harness.NowMicros);
        That(guest.Engine.SessionReplacementsReceived == 1 && guest.Engine.SessionState == MultiplayerSessionState.Synchronizing,
            "a small replacement cannot become Ready before runtime follow observes its new stream");
        var hold = guest.Engine.HoldReadiness();
        guest.Engine.FlushOutbound();
        harness.Flush();
        That(guest.Engine.IsAwaitingLocalReadiness && !harness.Host.IsParticipantReady(new ParticipantId("alpha")),
            "map acquisition holds the replacement on both peers");
        hold.Dispose(); harness.Flush();
        That(harness.Host.IsParticipantReady(new ParticipantId("alpha")), "validated map readiness completes the replacement");
    }

    internal static void ReadinessHoldsDelayTheAcknowledgement()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var held = SessionEngine.CreateClient(harness.Options, new MultiplayerSessionId("joining"), harness.NowMicros);
        harness.Track(held);
        var hold = held.HoldReadiness();
        harness.Join(held, "alpha");
        harness.Flush();
        That(held.Snapshot.State == MultiplayerSessionState.Synchronizing && held.IsAwaitingLocalReadiness, "a held client has the session but is not Ready");
        That(!harness.Host.IsParticipantReady(new ParticipantId("alpha")) && harness.Host.IsParticipantReady(new ParticipantId("host")),
            "the host is not told it is ready, and is ready itself");
        var heldSince = held.ReadinessHeldSinceMicros;
        That(heldSince > 0 && held.ReadinessProgressMicros == heldSince, "the hold's bounds start when the snapshot arrives");

        // Progress is the host's world arriving, or the runtime saying it is still at work.
        var world = Open(harness.Host, InternalSystemChannelKind.WorldReplication);
        var arriving = Open(held, InternalSystemChannelKind.WorldReplication);
        harness.Advance(3);
        That(world.Send(Bytes("baseline"), InternalSystemTarget.Everyone).Value == 1, "the host sends the world to a player still synchronizing");
        harness.Flush();
        That(Drain(arriving).SequenceEqual(new[] { "host:baseline" }), "who receives it before Ready");
        var afterBaseline = held.ReadinessProgressMicros;
        That(afterBaseline > heldSince && held.ReadinessHeldSinceMicros == heldSince, "which counts as progress");
        harness.Advance(3);
        hold.ReportProgress();
        That(held.ReadinessProgressMicros > afterBaseline, "and so does the runtime's own report");

        hold.Dispose();
        hold.Dispose();
        harness.Flush();
        That(held.Snapshot.State == MultiplayerSessionState.Ready && harness.Host.IsParticipantReady(new ParticipantId("alpha")) && held.ReadinessHolds == 0,
            "releasing the hold, once however often it is disposed, makes the client Ready on both sides");

        // A hold taken while Ready waits for the next snapshot, as when the host replaces the session for a new world.
        var later = held.HoldReadiness();
        That(held.Snapshot.State == MultiplayerSessionState.Ready, "a hold changes nothing while Ready");
        harness.Host.ReplaceSession(new MultiplayerSessionId("next-world"), new SessionSeed(3));
        harness.Flush();
        That(held.Snapshot.State == MultiplayerSessionState.Synchronizing && !harness.Host.IsParticipantReady(new ParticipantId("alpha")),
            "the replacement waits for the runtime");
        later.Dispose();
        harness.Flush();
        That(held.Snapshot.State == MultiplayerSessionState.Ready, "and completes when it lets go");

        var unheld = harness.AddClient("beta");
        var early = unheld.Engine.HoldReadiness();
        early.Dispose();
        harness.Flush();
        That(unheld.Engine.Snapshot.State == MultiplayerSessionState.Ready, "a hold released before the snapshot never delays it");
    }

    internal static void HostsEndPlayersThatNeverBecomeReady()
    {
        // One second here; in production the host waits longer than the most a player's own holds may take.
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive, options => options.ReadyTimeoutMicros = 1_000_000);
        var stalled = harness.AddRawPeer("stalled");
        stalled.Join("Stalled");
        var steady = harness.AddClient("steady");
        harness.Flush();
        harness.Advance(70);
        var ended = stalled.Receive().LastOrDefault(message => message.Type == MessageType.SessionEnded);
        That(ended.Body != null && JoinMessages.ReadEndReason(ended.Reader()) == MultiplayerSessionEndReason.TimedOut && stalled.End.State == EngineLinkState.Closed,
            "a player that takes the snapshot and never acknowledges it is timed out");
        That(!harness.Host.IsConnectedParticipant(new ParticipantId("stalled")) && steady.Engine.Snapshot.State == MultiplayerSessionState.Ready &&
             harness.Host.IsParticipantReady(new ParticipantId("steady")), "and its slot is free, while a player that acknowledged stays");

        // After a replacement the same bound applies: a player whose runtime holds on for ever is timed out too.
        var forever = steady.Engine.HoldReadiness();
        harness.Host.ReplaceSession(new MultiplayerSessionId("next-world"), new SessionSeed(9));
        harness.Advance(20);
        That(steady.Engine.Snapshot.State == MultiplayerSessionState.Synchronizing, "the held player resynchronizes");
        harness.Advance(60);
        That(steady.Engine.Snapshot.State == MultiplayerSessionState.Ended && steady.Engine.Snapshot.EndReason == MultiplayerSessionEndReason.TimedOut,
            "and is timed out when it never acknowledges the new snapshot");
        forever.Dispose();
    }

    internal static void SourcesAnswerTheSessionsLookups()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive, options => options.CallbackFailures = EngineCallbackFailureMode.Report);
        var session = harness.HostSession;
        var crate = new TestEntity("crate");
        That(!session.TryGetEntity(new NetworkObjectId("net-1"), out _) && !session.TryGetNetworkObjectId(crate, out _), "with no source the session knows no entities");
        var source = new TestSources(crate, new NetworkObjectId("net-1"));
        var entities = harness.Host.SetEntityIdentitySource(source);
        var avatars = harness.Host.SetAvatarSource(source);
        Throws<InvalidOperationException>(() => harness.Host.SetEntityIdentitySource(new TestSources(crate, default)), "one entity source at a time");
        Throws<InvalidOperationException>(() => harness.Host.SetAvatarSource(new TestSources(crate, default)), "one avatar source at a time");
        That(session.TryGetNetworkObjectId(crate, out var id) && id.Equals(new NetworkObjectId("net-1")), "the source names the entity's network object");
        That(session.TryGetEntity(new NetworkObjectId("net-1"), out var found) && ReferenceEquals(found, crate), "and the network object's entity");
        That(session.TryGetAvatar(new ParticipantId("host"), out var avatar) && ReferenceEquals(avatar, crate) &&
             session.TryGetPose(new ParticipantId("host"), out var pose) && pose.Health == 50, "the avatar source answers for players");
        var answered = false;
        var thread = new Thread(() => answered = session.TryGetEntity(new NetworkObjectId("net-1"), out _));
        thread.Start();
        thread.Join();
        That(!answered, "another thread is never answered by a main-thread source");

        source.Throws = true;
        That(!session.TryGetEntity(new NetworkObjectId("net-1"), out _) && !session.TryGetAvatar(new ParticipantId("host"), out _) &&
             harness.Diagnostics.Count(EngineDiagnosticKind.CallbackFailed) == 2, "a failing source answers no and is reported");
        entities.Dispose();
        avatars.Dispose();
        That(!session.TryGetEntity(new NetworkObjectId("net-1"), out _) && !session.TryGetPose(new ParticipantId("host"), out _), "a removed source answers nothing");
        harness.Host.SetEntityIdentitySource(source).Dispose();
    }

    internal static void TheFrameFlushSendsWithoutReceiving()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var alpha = harness.AddClient("alpha");
        harness.Flush();
        var host = Open(harness.Host, InternalSystemChannelKind.Avatars);
        var player = Open(alpha.Engine, InternalSystemChannelKind.Avatars);
        host.Send(Bytes("pose"), InternalSystemTarget.Everyone);
        harness.Host.FlushOutbound();

        // The virtual link takes at least a tick; the host does not pump again before the player receives.
        harness.Network.DeliverDue(harness.Tick + 1);
        alpha.Engine.Pump(harness.NowMicros);
        That(Drain(player).SequenceEqual(new[] { "host:pose" }), "the flush sends what the frame queued, without waiting for the next pump");

        harness.Host.SetLocalDisplayName("Renamed host");
        harness.Flush();
        That(alpha.Engine.Snapshot.Participants.Any(participant => participant.DisplayName == "Renamed host"), "renaming the host's player reaches every player");
        alpha.Engine.SetLocalDisplayName("Ignored");
        That(alpha.Engine.Snapshot.Participants.All(participant => participant.DisplayName != "Ignored"), "a joined player's name is its host's to keep");
        Throws<ArgumentException>(() => harness.Host.SetLocalDisplayName(" "), "a blank name is refused");

        Exception? nested = null;
        using (harness.HostSession.SubscribeChanged(_ =>
        {
            try
            {
                harness.Host.FlushOutbound();
            }
            catch (InvalidOperationException exception)
            {
                nested ??= exception;
            }
        }))
        {
            harness.Host.SetLocalDisplayName("Again");
        }

        That(nested != null, "a flush from inside a callback is refused");
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

    private sealed class TestEntity : IEntity
    {
        internal TestEntity(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public string Name => Id;

        public bool IsAlive => true;

        public Vec3 Position => new(1, 2, 3);
    }

    private sealed class TestSources : IInternalEntityIdentitySource, IInternalAvatarSource
    {
        private readonly IEntity entity;
        private readonly NetworkObjectId id;

        internal TestSources(IEntity entity, NetworkObjectId id)
        {
            this.entity = entity;
            this.id = id;
        }

        internal bool Throws { get; set; }

        public bool TryGetNetworkObjectId(IEntity candidate, out NetworkObjectId found)
        {
            if (Throws) throw new InvalidOperationException("broken source");
            found = ReferenceEquals(candidate, entity) ? id : default;
            return found.IsValid;
        }

        public bool TryGetEntity(NetworkObjectId candidate, out IEntity? found)
        {
            if (Throws) throw new InvalidOperationException("broken source");
            found = candidate.Equals(id) ? entity : null;
            return found != null;
        }

        public bool TryGetAvatar(ParticipantId participant, out IEntity? avatar)
        {
            if (Throws) throw new InvalidOperationException("broken source");
            avatar = participant.Value == "host" ? entity : null;
            return avatar != null;
        }

        public bool TryGetPose(ParticipantId participant, out MultiplayerPose pose)
        {
            if (Throws) throw new InvalidOperationException("broken source");
            pose = new MultiplayerPose(new NetworkTick(1), new Vec3(0, 0, 0), new Ray(new Vec3(0, 0, 0), new Vec3(0, 0, 1)), new Vec3(0, 0, 0), 50, 100);
            return participant.Value == "host";
        }
    }
}
