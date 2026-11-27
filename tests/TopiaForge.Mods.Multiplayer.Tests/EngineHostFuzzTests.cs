using System.Text;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Seeded hostile session frames into a host engine through its links: random bytes, mutated and spliced client
/// messages, and well-formed ones in the wrong order. A well-behaved client keeps playing throughout. Nothing throws out
/// of the host, the client never notices, every table stays within its bound, and a seed repeats exactly.
/// </summary>
internal static class EngineHostFuzzTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddType = new("add");
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> BoxType = new("box");
    private static readonly PresentationEventType<TestEvent> FlashType = new("flash", new JsonTestCodec<TestEvent>());

    internal static void HostileClientFramesEndOnlyTheirOwnConnection()
    {
        var first = Run(160, record: true);
        That(first == Run(160, record: false), "a seed repeats the host fuzz exactly");
    }

    private static string Run(int seed, bool record)
    {
        var fuzz = new FuzzMutator(seed);
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive, options =>
        {
            options.MaximumParticipants = 8;

            // A short grace frees a closed peer's slot soon, so fresh hostile peers keep getting in to be fuzzed.
            options.ParticipantGraceMicros = 200_000;
        });
        var hostScore = Register(harness.HostSession);
        var good = harness.AddClient("good");
        var goodScore = Register(good.Session);
        harness.Flush();
        var box = harness.HostSession.SpawnObject(BoxType, new CounterValue(0), new ParticipantId("good"));
        That(box.Succeeded, "the host spawns a box the good client owns");
        harness.Flush();

        var corpus = Corpus();
        var peers = new List<RawPeer>();
        var opened = 0;
        var joined = 0;
        var delivered = 0L;
        var attempted = 0L;
        var run = record ? FuzzLedger.Start("frames into a host engine") : null;
        try
        {
            for (var round = 0; round < FuzzMutator.Iterations(1_000); round++)
            {
                // Peers the host has closed carry no more frames, so drop them: a frame to a closed link is silently
                // dropped and would never reach the engine. Only open links count as reach.
                peers.RemoveAll(peer => peer.End.State == EngineLinkState.Closed);
                while (peers.Count < 6)
                {
                    var fresh = harness.AddRawPeer("hostile-" + ++opened);

                    // Most hostile peers join properly first, so their later mutations reach the post-join handlers,
                    // not only the join path. A valid request, then a ReadyAck for the host's epoch.
                    if (fuzz.Chance(85))
                    {
                        fresh.Join("Raw-" + opened);
                        harness.Advance(2);
                        fresh.Send((frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.ReadyAck, 0));
                        harness.Advance(1);
                        fresh.Receive();
                        if (harness.Host.Snapshot.TryGetParticipant(new ParticipantId("hostile-" + opened), out var record2) && record2!.IsConnected) joined++;
                    }

                    peers.Add(fresh);
                }

                var peer = fuzz.Pick(peers);
                for (var frame = 1 + fuzz.Random.Next(4); frame > 0; frame--)
                {
                    attempted++;
                    if (peer.End.State == EngineLinkState.Closed) continue;
                    peer.SendRaw(Hostile(fuzz, corpus));
                    delivered++;
                    if (run != null) run.Inputs++;
                }

                harness.Advance(1 + fuzz.Random.Next(3));
                foreach (var other in peers) other.Receive();
                if (fuzz.Chance(8))
                {
                    var leaving = fuzz.Pick(peers);
                    harness.Network.Sever(leaving.End);
                    peers.Remove(leaving);
                }

                if (round % 30 == 0) GoodClientPlays(harness, good.Session, goodScore, hostScore);
                var census = harness.Host.Census();
                That(census.Connections <= peers.Count + 1 + 6, "the host keeps no connection it was not given (" + census + ")");
                That(census.ParticipantRecords <= 1 + (2 * 8) && census.QueuedCallbacks == 0 && census.QueuedOutbound <= 64,
                    "and its records and queues stay within their bounds (" + census + ")");
                That(census.CommandRateLimiters <= census.ParticipantRecords && census.ObjectRateLimiters <= census.ParticipantRecords * census.Objects,
                    "and every rate limiter belongs to a participant it still knows (" + census + ")");
            }

            GoodClientPlays(harness, good.Session, goodScore, hostScore);
            That(good.Engine.Snapshot.State == MultiplayerSessionState.Ready && harness.Host.Snapshot.TryGetParticipant(new ParticipantId("good"), out var kept) &&
                 kept!.IsConnected, "the good client played through it all");

            // The fuzzer is worth running only if its frames actually reach an open link and its mutations reach the
            // post-join handlers: both were almost never true before closed peers were dropped and peers joined first.
            That(delivered >= attempted * 3 / 4, "most hostile frames reached an open link (" + delivered + " of " + attempted + ")");
            That(joined >= 100, "many hostile peers joined, so mutations reached the post-join handlers (" + joined + ")");
            var participants = string.Join(",", harness.Host.Snapshot.Participants.Select(participant => participant.Id + (participant.IsConnected ? "+" : "-")));
            return participants + "; delivered " + delivered + "/" + attempted + "; joined " + joined + "; score " + hostScore.Value.Value + "@" + hostScore.Version +
                   "; violations " + harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) + "; " + harness.Host.Census();
        }
        finally
        {
            run?.Dispose();
        }
    }

    private static void GoodClientPlays(EngineHarness harness, EngineSessionFacade session, IReplicatedState<CounterValue> clientScore, IReplicatedState<CounterValue> hostScore)
    {
        var submitted = session.SubmitAsync(AddType, new AddRequest(1));
        Assert(session.TryGetObject(BoxType, session.GetObjects(BoxType).Single().Id, out var box), "the good client sees its box");
        var input = box!.SubmitInputAsync(new AddRequest(2));
        harness.Flush();
        That(submitted.IsCompleted && submitted.Result.Result.Succeeded && input.IsCompleted && input.Result.Result.Succeeded,
            "the good client's command and input run (" + submitted.Result.Result.ErrorCode + ", " + input.Result.Result.ErrorCode + ")");
        That(clientScore.Value.Value == hostScore.Value.Value && clientScore.Version == hostScore.Version, "and it sees the host's score");
    }

    private static IReplicatedState<CounterValue> Register(EngineSessionFacade session)
    {
        var score = Require(session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "score");
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddType, RequestCodec, CounterCodec,
            (command, request) =>
            {
                var updated = score.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount)));
                command.Emit(FlashType, new TestEvent("added"));
                return updated;
            },
            PredictionMode.Owner)), "add");
        Require(session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            BoxType, CounterCodec, RequestCodec,
            (context, state, input) => context.SenderOwnsTarget
                ? OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount))
                : OperationResult<CounterValue>.Failure(ModErrorCode.NotAuthoritative, "Only the owner moves the box."),
            PredictionMode.Owner)), "box");
        Require(session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(FlashType, _ => { })), "flash");
        return score;
    }

    /// <summary>Frames a client sends, alone and together, as the mutations' starting points.</summary>
    private static List<byte[]> Corpus()
    {
        var payload = CounterCodec.Encode(new CounterValue(3)).Value!;
        var request = RequestCodec.Encode(new AddRequest(4)).Value!;
        var contract = new ContractEntry(EngineHarness.Owner, "white-box.contract", 1, new string('a', 64));
        Action<ProtocolWriter, ProtocolWriter>[] messages =
        {
            (frame, body) => JoinMessages.WriteJoinRequest(frame, body, new JoinRequestMessage(SessionEngine.ProtocolVersion, "Raw", Array.Empty<ContractEntry>(), Array.Empty<byte>())),
            (frame, body) => JoinMessages.WriteJoinRequest(frame, body, new JoinRequestMessage("1.9.0", "Raw player", new[] { contract }, payload)),
            (frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.ReadyAck, 0),
            (frame, body) => ReplicationMessages.WriteCommand(frame, body, new InternWriter(), 0, 1, EngineHarness.Owner, "add", request),
            (frame, body) => ReplicationMessages.WriteCommand(frame, body, new InternWriter(), 0, 9, EngineHarness.Owner, "missing", request),
            (frame, body) => ReplicationMessages.WriteObjectInput(frame, body, 0, 2, "box-1", request),
            (frame, body) => SessionMessages.WriteClockSyncReply(frame, body, 0),
            (frame, body) => SessionMessages.WriteEmpty(frame, body, MessageType.Leave),
            (frame, body) =>
            {
                body.Reset();
                body.WriteString("a future extension");
                frame.WriteEnvelope(0x50, body);
            }
        };

        var corpus = messages.Select(write => Frame(write)).ToList();
        corpus.Add(Frame((frame, body) =>
        {
            messages[0](frame, body);
            messages[2](frame, body);
            messages[3](frame, body);
            messages[5](frame, body);
        }));
        return corpus;
    }

    private static byte[] Hostile(FuzzMutator fuzz, List<byte[]> corpus)
    {
        switch (fuzz.Random.Next(6))
        {
            case 0:
                return fuzz.Bytes(fuzz.Random.Next(1, 64));
            case 1:
                return fuzz.Splice(fuzz.Pick(corpus), fuzz.Pick(corpus), 4096);
            case 2:
                // Well-formed messages in any order and number: a join twice, traffic before a join, submissions after a leave.
                return Enumerable.Range(0, 1 + fuzz.Random.Next(4)).SelectMany(_ => fuzz.Pick(corpus)).ToArray();
            case 3:
                // A known envelope type around a body of random bytes.
                var body = fuzz.Bytes(fuzz.Random.Next(48));
                return new[] { (byte)fuzz.Random.Next(0x80) }.Concat(fuzz.Varint((ulong)body.Length, minimal: true)).Concat(body).ToArray();
            default:
                return fuzz.Mutate(fuzz.Pick(corpus), 4096);
        }
    }

    private static byte[] Frame(Action<ProtocolWriter, ProtocolWriter> write)
    {
        var frame = new ProtocolWriter();
        write(frame, new ProtocolWriter());
        return frame.ToArray();
    }

    private static void Assert(bool condition, string message) => That(condition, message);

    private static T Require<T>(OperationResult<T> result, string what) where T : notnull =>
        result.TryGetValue(out var value) ? value : throw new InvalidOperationException("Assertion failed: " + what + " registers: " + result.ErrorMessage);
}
