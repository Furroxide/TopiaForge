using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Host-side bounds: what one peer can make the host send everyone else, the limits that keep a long session from
/// growing without end, and the operations a handler may not run inside a transaction.
/// </summary>
internal static class EngineHardeningTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> BoxType = new("box");

    internal static void UnsolicitedClockRepliesAreIgnored()
    {
        using var harness = new EngineHarness();
        var flooder = JoinRaw(harness, "flooder");
        var watcher = JoinRaw(harness, "watcher");
        flooder.Receive();
        flooder.Send((frame, body) =>
        {
            for (var index = 0; index < 2000; index++)
            {
                SessionMessages.WriteClockSyncReply(frame, body, index % 2 == 0 ? 0UL : 1_000_000UL);
            }
        });
        harness.Flush();
        Assert(Ping(harness, "flooder") == null, "replies to probes the host never sent take no round-trip sample");
        Assert(watcher.Receive().All(message => message.Type != MessageType.ParticipantsChanged),
            "so they cannot make the host resend the participant list to everyone");
    }

    internal static void OnlyTheLatestProbeIsTimed()
    {
        using var harness = new EngineHarness(configure: options => options.ClockSyncIntervalMicros = 1_000_000);
        var peer = harness.AddRawPeer("prober");
        peer.Join("Prober");
        harness.Flush();
        peer.Send((frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.ReadyAck, 0));
        harness.Flush();
        var probes = peer.Receive().Where(message => message.Type == MessageType.ClockSync).ToList();
        Assert(probes.Count == 1, "a ready connection receives one clock probe");
        var echoed = probes[0].Reader().ReadVarUInt();
        harness.Advance(6);
        peer.Send((frame, body) =>
        {
            SessionMessages.WriteClockSyncReply(frame, body, echoed + 1);
            SessionMessages.WriteClockSyncReply(frame, body, echoed);
        });
        harness.Flush();
        var measured = Ping(harness, "prober");
        Assert(measured.HasValue, "the echo of the latest probe yields a round-trip sample");
        harness.Advance(30);
        peer.Send((frame, body) =>
        {
            for (var index = 0; index < 50; index++) SessionMessages.WriteClockSyncReply(frame, body, echoed);
        });
        harness.Flush();
        Assert(Ping(harness, "prober") == measured, "repeating an answered probe's echo changes nothing");
    }

    internal static void ParticipantChangesReachEachClientOncePerFlush()
    {
        using var harness = new EngineHarness();
        var watcher = JoinRaw(harness, "watcher");
        var joiners = Enumerable.Range(1, 5).Select(index => harness.AddRawPeer("joiner-" + index)).ToList();
        foreach (var joiner in joiners) joiner.Join(joiner.End.PeerIdentity);
        harness.Flush();
        var lists = watcher.Receive().Where(message => message.Type == MessageType.ParticipantsChanged).ToList();
        Assert(lists.Count == 1 && SessionMessages.ReadParticipants(lists[0].Reader()).Count == 6,
            "five joins in one pump reach an existing client as a single participant list (" + lists.Count + ")");
    }

    internal static void DepartedParticipantsMakeRoomWithinTheRecordLimit()
    {
        // Two admitted players leave room for two departed records.
        using var harness = new EngineHarness(configure: options => options.MaximumParticipants = 2);
        var first = harness.AddClient("first");
        var second = harness.AddClient("second");
        harness.Flush();
        harness.Network.Sever(first.Link);
        harness.Advance();
        harness.Network.Sever(second.Link);
        harness.Advance();
        var third = harness.AddClient("third");
        harness.AddClient("fourth");
        harness.Flush();
        harness.Network.Sever(third.Link);
        harness.Advance();
        var fifth = harness.AddClient("fifth");
        harness.Flush();
        var participants = harness.Host.Snapshot.Participants;
        Assert(fifth.Engine.Snapshot.State == MultiplayerSessionState.Ready, "a new player is admitted while a departed record can make room");
        Assert(participants.Count == 4 && !harness.Host.Snapshot.TryGetParticipant(new ParticipantId("first"), out _),
            "the longest-departed record makes room, so the list never exceeds its limit");
        Assert(harness.Host.Snapshot.TryGetParticipant(new ParticipantId("second"), out var kept) && !kept!.IsConnected,
            "more recent departures keep their grace records");
    }

    internal static void AJoinBeyondTheRecordLimitIsRefused()
    {
        // A permissive admission policy cannot push the participant records past their limit.
        using var harness = new EngineHarness(configure: options =>
        {
            options.MaximumParticipants = 1;
            options.JoinPolicy = new AdmitEveryone();
        });
        harness.AddClient("first");
        harness.AddClient("second");
        harness.Flush();
        var third = harness.AddClient("third");
        harness.Flush();
        Assert(third.Engine.Snapshot.State == MultiplayerSessionState.Ended &&
               third.Engine.Snapshot.EndReason == MultiplayerSessionEndReason.AdmissionRejected,
            "a join that would need a third record is refused as full");
        Assert(harness.Host.Snapshot.Participants.Count == 2, "and the host keeps two records");
    }

    internal static void KicksAndShutdownWaitForTheTransactionToEnd()
    {
        using var harness = new EngineHarness();
        Require(harness.HostSession.RegisterObjectType(BoxDefinition()), "host box");
        var client = harness.AddClient("owner");
        Require(client.Session.RegisterObjectType(BoxDefinition()), "client box");
        harness.Flush();
        var owner = new ParticipantId("owner");
        var box = Require(harness.HostSession.SpawnObject(BoxType, new CounterValue(0), owner), "spawn");
        harness.Flush();
        var refusals = 0;
        var tick = harness.HostSession.SubscribeTick(_ =>
        {
            try
            {
                harness.Host.Kick(owner, MultiplayerSessionEndReason.Kicked, "Removed by a tick handler.");
            }
            catch (InvalidOperationException)
            {
                refusals++;
            }

            try
            {
                harness.Host.Shutdown(MultiplayerSessionEndReason.HostEnded);
            }
            catch (InvalidOperationException)
            {
                refusals++;
            }
        });
        harness.Advance();
        tick.Dispose();
        harness.Flush();
        Assert(refusals == 2, "a tick handler can neither kick a participant nor end the session");
        Assert(harness.Host.Snapshot.TryGetParticipant(owner, out var stillThere) && stillThere!.IsConnected &&
               box.OwnerId.Equals(owner) && client.Engine.Snapshot.State == MultiplayerSessionState.Ready,
            "a refused kick leaves no half-removed participant: still connected, still the owner");
        Assert(harness.Host.Kick(owner, MultiplayerSessionEndReason.Kicked, "Removed between steps."), "the same kick works outside a transaction");
        harness.Flush();
        Assert(box.OwnerId == null && client.Engine.Snapshot.EndReason == MultiplayerSessionEndReason.Kicked,
            "and returns the participant's objects to the server");
    }

    internal static void RateHistoryEndsWithItsValue()
    {
        using var harness = new EngineHarness();
        var limited = Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>(
            "limited", new CounterValue(0), CounterCodec, MultiplayerDelivery.Immediate, 5)), "limited state");
        Require(harness.HostSession.RegisterObjectType(BoxDefinition(maximumUpdatesPerSecond: 5)), "limited boxes");
        harness.AddClient("observer");
        harness.Flush();
        var boxes = Change(harness, limited, count: 20);
        Assert(harness.Host.RateHistoryEntries == 21, "each rate-limited change is timed per connection");
        foreach (var box in boxes) Require(harness.HostSession.DespawnObject(box.Id), "despawn");
        limited.Dispose();
        harness.Flush();
        Assert(harness.Host.RateHistoryEntries == 0, "despawned objects and unregistered states leave no timing behind");

        var again = Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>(
            "limited", new CounterValue(0), CounterCodec, MultiplayerDelivery.Immediate, 5)), "limited state again");
        Change(harness, again, count: 3);
        harness.Host.ReplaceSession(new MultiplayerSessionId("next-round"), new SessionSeed(2));
        harness.Flush();
        Assert(harness.Host.RateHistoryEntries == 0, "a session replacement starts every connection's timing afresh");
    }

    internal static void NamesBeyondTheInternBudgetTravelInline()
    {
        var writer = new InternWriter();
        var frame = new ProtocolWriter();
        var names = Enumerable.Range(0, ProtocolLimits.MaximumInternedStrings + 3).Select(index => "name-" + index).ToList();
        foreach (var name in names) writer.Write(frame, name);
        writer.Write(frame, names[0]);
        writer.Write(frame, names[^1]);
        var reader = new InternReader();
        var input = new ProtocolReader(frame.ToArray());
        foreach (var name in names) Assert(reader.Read(input) == name, "name round trip " + name);
        Assert(reader.Read(input) == names[0] && reader.Read(input) == names[^1] && input.AtEnd,
            "interned names keep their references and later names travel inline every time");

        using var harness = new EngineHarness();
        const int count = ProtocolLimits.MaximumInternedStrings + 4;
        for (var index = 0; index < count; index++)
        {
            Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>(
                "s-" + index, new CounterValue(index), CounterCodec)), "state " + index);
        }

        var client = harness.AddClient("late");
        harness.Flush();
        var last = Require(client.Session.RegisterState(new ReplicatedStateDefinition<CounterValue>(
            "s-" + (count - 1), new CounterValue(-1), CounterCodec)), "mirror the last state");
        Assert(client.Engine.Snapshot.State == MultiplayerSessionState.Ready && last.Value.Value == count - 1,
            "a host with more names than one connection's intern table still synchronizes a joining client");
    }

    internal static void DisposedObjectTypesRunNoInput()
    {
        using var harness = new EngineHarness();
        var executions = 0;
        var hostType = Require(harness.HostSession.RegisterObjectType(BoxDefinition(onInput: () => executions++)), "host box");
        var client = harness.AddClient("pilot");
        Require(client.Session.RegisterObjectType(BoxDefinition()), "client box");
        harness.Flush();
        var box = Require(harness.HostSession.SpawnObject(BoxType, new CounterValue(0), new ParticipantId("pilot")), "spawn");
        harness.Flush();
        hostType.Dispose();
        var input = client.Engine.FindObject<CounterValue, AddRequest>(box.Id)!.SubmitInputAsync(new AddRequest(1));
        harness.Flush();
        Assert(input.IsCompleted && input.Result.Result.ErrorCode == ModErrorCode.NotFound && executions == 0,
            "an input for a type whose registration was disposed never reaches the old handler");
    }

    private static RawPeer JoinRaw(EngineHarness harness, string identity)
    {
        var peer = harness.AddRawPeer(identity);
        peer.Join(identity);
        harness.Flush();
        peer.Send((frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.ReadyAck, 0));
        harness.Flush();
        peer.Receive();
        return peer;
    }

    private static int? Ping(EngineHarness harness, string participant) =>
        harness.Host.Snapshot.TryGetParticipant(new ParticipantId(participant), out var record) ? record!.PingMilliseconds : null;

    private static List<IReplicatedObject<CounterValue, AddRequest>> Change(EngineHarness harness, IReplicatedState<CounterValue> state, int count)
    {
        var boxes = Enumerable.Range(0, count)
            .Select(_ => Require(harness.HostSession.SpawnObject(BoxType, new CounterValue(0)), "spawn"))
            .ToList();
        harness.Flush();
        foreach (var box in boxes) Require(box.Apply(value => OperationResult<CounterValue>.Success(new CounterValue(value.Value + 1))), "change");
        Require(state.Update(value => OperationResult<CounterValue>.Success(new CounterValue(value.Value + 1))), "update");
        harness.Flush();
        return boxes;
    }

    private static ReplicatedObjectTypeDefinition<CounterValue, AddRequest> BoxDefinition(Action? onInput = null, int maximumUpdatesPerSecond = 0) =>
        new(
            BoxType,
            CounterCodec,
            RequestCodec,
            (_, state, input) =>
            {
                onInput?.Invoke();
                return OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount));
            },
            PredictionMode.None,
            30,
            16 * 1024,
            MultiplayerDelivery.Immediate,
            maximumUpdatesPerSecond);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }

    private sealed class AdmitEveryone : IJoinPolicy
    {
        public JoinDecision Evaluate(JoinRequestContext context) => JoinDecision.Accept();
    }
}
