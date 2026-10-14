extern alias provider;

using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using JoinContent = provider::TopiaForge.Multiplayer.Network.JoinContent;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Defects the soak and the fuzzers found, each pinned at the smallest scale that shows it.</summary>
internal static class HardeningRegressionTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddType = new("add");
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> BoxType = new("box");

    /// <summary>
    /// Found by the soak: a host kept a rate limiter for every participant that ever submitted, so players coming and
    /// going over a long session grew it without end. A participant whose record is gone never returns under that id,
    /// so its limiters go with the record: when its grace period ends, when a newer player takes its place, and when the
    /// host closes every connection.
    /// </summary>
    internal static void ForgottenParticipantsTakeTheirRateLimitersWithThem()
    {
        using (var expiry = new EngineHarness(configure: options => options.ParticipantGraceMicros = 1_000_000))
        {
            var box = Prepare(expiry);
            var leaver = Join(expiry, "leaver", box);
            Assert(Limiters(expiry) == (1, 1), "a player that submitted has a limiter on the command and on the object it moved");
            expiry.Network.Sever(leaver.Link);
            expiry.Advance(70);
            Assert(!expiry.Host.Snapshot.TryGetParticipant(new ParticipantId("leaver"), out _) && Limiters(expiry) == (0, 0),
                "once its grace period ends, the record and its limiters are gone (" + Limiters(expiry) + ")");
        }

        using (var eviction = new EngineHarness(configure: options => options.MaximumParticipants = 1))
        {
            var box = Prepare(eviction);
            eviction.Network.Sever(Join(eviction, "first", box).Link);
            eviction.Advance();
            eviction.Network.Sever(Join(eviction, "second", box).Link);
            eviction.Advance();
            Assert(Limiters(eviction) == (2, 2), "two departed players keep their places, and their limiters, for their grace period");
            eviction.AddClient("third");
            eviction.Flush();
            Assert(!eviction.Host.Snapshot.TryGetParticipant(new ParticipantId("first"), out _) && Limiters(eviction) == (1, 1),
                "a newer player takes the longest-departed place, and that player's limiters go with it (" + Limiters(eviction) + ")");
        }

        using (var closing = new EngineHarness())
        {
            var box = Prepare(closing);
            Join(closing, "one", box);
            Join(closing, "two", box);
            Assert(Limiters(closing) == (2, 2), "two players submitted");
            closing.Host.CloseConnections(MultiplayerSessionEndReason.HostEnded, "The host stopped hosting.");
            Assert(Limiters(closing) == (0, 0), "closing every connection forgets every player's limiters (" + Limiters(closing) + ")");
        }
    }

    /// <summary>
    /// Found by fuzzing join content (seed 154): the length was read into 32 bits, so the five-byte length
    /// <c>BC 80 80 80 40</c>, which is 2^34 + 60, read as 60, and 60 bytes of content passed under a second spelling. A
    /// length past 64 KiB is refused however it is spelled, so join content has exactly one.
    /// </summary>
    internal static void JoinContentLengthsCannotWrapAround()
    {
        var body = Enumerable.Range(0, 60).Select(index => (byte)(index * 7)).ToArray();
        var pinned = new byte[] { 1, 0, 0xBC, 0x80, 0x80, 0x80, 0x40 }.Concat(body).ToArray();
        Assert(!JoinContent.TryDecode(pinned, out _, out _), "the length the fuzzer found is refused");
        for (var wraps = 1UL; wraps < 8; wraps++)
        {
            var length = 60 + (wraps << 32);
            var field = new List<byte>();
            do
            {
                field.Add((byte)((length & 0x7F) | (length > 0x7F ? 0x80UL : 0UL)));
                length >>= 7;
            }
            while (length != 0);
            Assert(!JoinContent.TryDecode(new byte[] { 1, 0 }.Concat(field).Concat(body).ToArray(), out _, out _),
                "60 bytes behind a length of 60 + " + wraps + " x 2^32 are refused");
        }

        Assert(JoinContent.TryDecode(JoinContent.Encode(null, body), out _, out var content) && content.SequenceEqual(body),
            "and 60 bytes behind a length of 60 still read");
    }

    /// <summary>
    /// Found by fuzzing a client (seed 161): when an object in a host's snapshot did not decode, the session ended and the
    /// client went back to solo play, but the objects after it were still created, in the solo session, where no host
    /// would ever despawn them. The same held for objects a late registration adopted. The host's objects now stop at
    /// the one that ended the session.
    /// </summary>
    internal static void ASessionThatEndsMidSnapshotLeavesNothingBehind()
    {
        var crateType = new ReplicatedObjectType<CounterValue, AddRequest>("crate");
        var good = CounterCodec.Encode(new CounterValue(1)).Value!;
        var bad = new byte[] { (byte)'{' };

        var snapshot = new SoloReturningClient(BoxType);
        snapshot.Join(intern => new[] { Box(BoxType, "box-1", bad), Box(BoxType, "box-2", good), Box(BoxType, "box-3", good) });
        Assert(snapshot.Engine.Role == EngineRole.Host && snapshot.Session.GetObjects(BoxType).Count == 0,
            "an object that does not decode ends the session, and the objects after it never reach the solo session (" +
            snapshot.Session.GetObjects(BoxType).Count + ")");

        var adoption = new SoloReturningClient(BoxType);
        adoption.Join(intern => new[] { Box(crateType, "crate-1", bad), Box(crateType, "crate-2", good) });
        Assert(adoption.Engine.Role == EngineRole.Client && adoption.Engine.Snapshot.State == MultiplayerSessionState.Ready,
            "objects of a type the client has not registered wait for it");
        Require(adoption.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            crateType, CounterCodec, RequestCodec, (_, state, _) => OperationResult<CounterValue>.Success(state))), "register crate");
        Assert(adoption.Engine.Role == EngineRole.Host && adoption.Session.GetObjects(crateType).Count == 0,
            "and adopting one that does not decode ends the session without adopting the rest into solo play (" +
            adoption.Session.GetObjects(crateType).Count + ")");
    }

    private static ObjectWireMessage Box(ReplicatedObjectType<CounterValue, AddRequest> type, string id, byte[] bytes) =>
        new() { Owner = EngineHarness.Owner, TypeId = type.Id, ObjectId = id, Bytes = bytes };

    /// <summary>A client that returns to solo play when its session ends, as the provider's does, joined by a raw host.</summary>
    private sealed class SoloReturningClient
    {
        private readonly VirtualEngineTransport network = new(new VirtualLinkConditions());
        private readonly RawPeer host;

        internal SoloReturningClient(ReplicatedObjectType<CounterValue, AddRequest> type)
        {
            var pair = network.Connect("client");
            Engine = SessionEngine.CreateClient(
                new EngineOptions
                {
                    StepRateHz = 60,
                    AfterClientSessionEnds = _ => new SoloSessionStart(new MultiplayerSessionId("solo"), new SessionSeed(1), new ParticipantId("local"), "Local player")
                },
                new MultiplayerSessionId("placeholder"),
                0);
            Session = Engine.CreateFacade(EngineHarness.Owner);
            Require(Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                type, CounterCodec, RequestCodec, (_, state, _) => OperationResult<CounterValue>.Success(state))), "register " + type.Id);
            Engine.Connect(pair.ClientEnd, "Client");
            host = new RawPeer(pair.HostEnd);
        }

        internal SessionEngine Engine { get; }

        internal EngineSessionFacade Session { get; }

        /// <summary>Accepts the join with a snapshot of <paramref name="objects"/>, then lets the client read it.</summary>
        internal void Join(Func<InternWriter, ObjectWireMessage[]> objects)
        {
            host.Send((frame, body) =>
            {
                var intern = new InternWriter();
                JoinMessages.WriteJoinAccepted(frame, body, new JoinAcceptedMessage
                {
                    SessionId = "raw-session",
                    Seed = 1,
                    ParticipantId = "client",
                    StepRateHz = 60,
                    NextSequence = 1
                });
                SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, 0);
                foreach (var item in objects(intern)) ReplicationMessages.WriteObjectSpawned(frame, body, intern, item);
                SessionMessages.WriteEmpty(frame, body, MessageType.SnapshotEnd);
                SessionMessages.WriteCommit(frame, body, 0, 0);
            });
            network.Now = 1;
            network.DeliverDue(1);
            Engine.Pump(20_000);
        }
    }

    private static IReplicatedObject<CounterValue, AddRequest> Prepare(EngineHarness harness)
    {
        Register(harness.HostSession);
        return Require(harness.HostSession.SpawnObject(BoxType, new CounterValue(0)), "spawn a box the server owns");
    }

    /// <summary>Joins a client, which sends one command and one input to <paramref name="box"/>, and waits for both.</summary>
    private static (SessionEngine Engine, EngineSessionFacade Session, VirtualLinkEnd Link) Join(
        EngineHarness harness,
        string identity,
        IReplicatedObject<CounterValue, AddRequest> box)
    {
        var client = harness.AddClient(identity);
        Register(client.Session);
        harness.Flush();
        var command = client.Session.SubmitAsync(AddType, new AddRequest(1));
        Assert(client.Session.TryGetObject(BoxType, box.Id, out var mirrored), identity + " sees the box");
        var input = mirrored!.SubmitInputAsync(new AddRequest(1));
        harness.Flush();
        Assert(command.IsCompleted && command.Result.Result.Succeeded && input.IsCompleted && input.Result.Result.Succeeded,
            identity + "'s command and input run");
        return client;
    }

    private static void Register(EngineSessionFacade session)
    {
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddType, RequestCodec, CounterCodec, (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)))), "register add");
        Require(session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            BoxType, CounterCodec, RequestCodec, (_, state, input) => OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount)))),
            "register box");
    }

    private static (int Commands, int Objects) Limiters(EngineHarness harness)
    {
        var census = harness.Host.Census();
        return (census.CommandRateLimiters, census.ObjectRateLimiters);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }
}
