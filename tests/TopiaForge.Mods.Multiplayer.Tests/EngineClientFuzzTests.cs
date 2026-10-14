using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Seeded hostile session frames from a host into client engines through their links: joins and snapshots mutated,
/// spliced or cut, values that do not decode, objects of types the client registers only later, and every other message
/// in any order. The client returns to solo play when its session ends, as the provider does. Nothing throws out of the
/// client or its snapshot, what the client shows is always readable, a session that ended leaves nothing of the host's
/// behind in the solo session that follows it, every table stays within its bound, and a seed repeats exactly.
/// </summary>
internal static class EngineClientFuzzTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddType = new("add");
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> BoxType = new("box");
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> CrateType = new("crate");
    private static readonly PresentationEventType<TestEvent> FlashType = new("flash", new JsonTestCodec<TestEvent>());

    internal static void HostileHostFramesStayInsideTheClient()
    {
        var first = Run(161, record: true);
        That(first.SequenceEqual(Run(161, record: false)), "a seed repeats the client fuzz exactly");
    }

    private static List<string> Run(int seed, bool record)
    {
        var fuzz = new FuzzMutator(seed);
        var outcomes = new List<string>();
        var reached = 0L;
        var delivered = 0L;
        var run = record ? FuzzLedger.Start("frames into client engines") : null;
        try
        {
            for (var round = 0; round < FuzzMutator.Iterations(1_000); round++)
            {
                var client = new FuzzedClient(fuzz, round);
                outcomes.Add(client.Play());
                delivered += client.Frames;
                reached += client.Reach;
                if (run != null) run.Inputs += client.Frames;
            }

            // Frames that arrive after a round's session has ended only exercise the solo session the client returned
            // to. Most frames must reach the joined session's handlers, which they almost never did before most rounds
            // started Ready and stopped once the session ended.
            That(reached >= delivered * 3 / 4, "most frames reached the joined session (" + reached + " of " + delivered + ")");
            outcomes.Add("reached " + reached + "/" + delivered);
            return outcomes;
        }
        finally
        {
            run?.Dispose();
        }
    }

    /// <summary>One client engine whose host is a raw peer that writes what the fuzzer chooses.</summary>
    private sealed class FuzzedClient
    {
        private readonly FuzzMutator fuzz;
        private readonly VirtualEngineTransport network = new(new VirtualLinkConditions());
        private readonly RecordingDiagnostics diagnostics = new();
        private readonly SessionEngine engine;
        private readonly EngineSessionFacade session;
        private readonly IReplicatedState<CounterValue> score;
        private readonly RawPeer host;
        private readonly InternWriter intern = new();
        private readonly HashSet<string> sentIds = new(StringComparer.Ordinal);
        private ulong tick;
        private bool solo;
        private bool crateRegistered;

        internal FuzzedClient(FuzzMutator fuzz, int round)
        {
            this.fuzz = fuzz;
            var pair = network.Connect("client");
            engine = SessionEngine.CreateClient(
                new EngineOptions
                {
                    StepRateHz = 60,
                    Diagnostics = diagnostics,
                    CallbackFailures = EngineCallbackFailureMode.Propagate,
                    AfterClientSessionEnds = _ =>
                    {
                        solo = true;
                        return new SoloSessionStart(new MultiplayerSessionId("solo-" + round), new SessionSeed(7), new ParticipantId("local"), "Local player");
                    }
                },
                new MultiplayerSessionId("placeholder"),
                0);
            session = engine.CreateFacade(EngineHarness.Owner);
            score = Require(session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "score");
            Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                AddType, RequestCodec, CounterCodec,
                (_, request) => score.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount))),
                PredictionMode.Owner)), "add");
            RegisterObjects(BoxType);
            Require(session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(FlashType, _ => { })), "flash");
            session.SubscribeObjects(BoxType, change => _ = change.State.Value);
            session.SubscribeChanged(snapshot => _ = snapshot.Participants.Count);
            engine.Connect(pair.ClientEnd, "Client");
            host = new RawPeer(pair.HostEnd);
        }

        internal int Frames { get; private set; }

        /// <summary>Gets how many frames arrived while the client was still in the joined session, not the solo one after it.</summary>
        internal int Reach { get; private set; }

        internal string Play()
        {
            // Most rounds join cleanly to Ready first, so the hostile frames that follow reach the joined session's
            // handlers; the rest start from a mutated or arbitrary frame, to fuzz the join path too.
            Send(fuzz.Chance(80) ? Join() : fuzz.Chance(60) ? Join() : Message());
            for (var frame = 1 + fuzz.Random.Next(6); frame > 0; frame--)
            {
                // Once a frame has ended the session and returned the client to solo play, nothing more reaches the
                // joined session, so stop rather than fuzzing the solo session the client fell back to.
                if (solo) break;
                if (!crateRegistered && fuzz.Chance(15))
                {
                    // The client adopts the crates the host already sent; one that does not decode ends the session.
                    var wasSolo = solo;
                    crateRegistered = true;
                    RegisterObjects(CrateType);
                    Inspect("registering a type the host already sent");
                    if (solo && !wasSolo) RequireEmptySolo("adopting objects");
                }

                if (engine.Role == EngineRole.Client && engine.Snapshot.State == MultiplayerSessionState.Ready && fuzz.Chance(30)) Submit();
                Send(fuzz.Random.Next(5) switch
                {
                    0 => fuzz.Mutate(Join(), 8192),
                    1 => fuzz.Bytes(fuzz.Random.Next(1, 48)),
                    2 => fuzz.Splice(Message(), Join(), 8192),
                    _ => fuzz.Chance(30) ? fuzz.Mutate(Message(), 4096) : Message()
                });
            }

            var census = engine.Census();
            That(census.Tombstones <= 8_192 && census.UnboundStates + census.UnboundObjects <= sentIds.Count + 4 && census.QueuedCallbacks == 0,
                "the client's tables hold only what the host sent (" + census + ")");
            var outcome = engine.Role + " " + engine.Snapshot.State + " " + engine.Snapshot.Id + " score " + score.Value.Value + "@" + score.Version +
                          " objects " + string.Join(",", session.GetObjects(BoxType).Select(item => item.Id + "@" + item.Version)) +
                          " violations " + diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) + " codec " + diagnostics.Count(EngineDiagnosticKind.CodecFailed);
            if (!engine.IsEnded) engine.Shutdown(MultiplayerSessionEndReason.Left);
            return outcome;
        }

        private void RegisterObjects(ReplicatedObjectType<CounterValue, AddRequest> type) =>
            Require(session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                type, CounterCodec, RequestCodec, (_, state, input) => OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount)),
                PredictionMode.Owner)), type.Id);

        private void Submit()
        {
            if (fuzz.Chance(50))
            {
                session.SubmitAsync(AddType, new AddRequest(fuzz.Random.Next(1, 5)));
                return;
            }

            var boxes = session.GetObjects(BoxType);
            if (boxes.Count > 0) fuzz.Pick(boxes).SubmitInputAsync(new AddRequest(1));
        }

        /// <summary>Delivers one frame and pumps, then reads everything a mod could read; none of it may throw.</summary>
        private void Send(byte[] frame)
        {
            var wasSolo = solo;
            Frames++;
            if (engine.Role == EngineRole.Client) Reach++;
            host.SendRaw(frame);
            tick++;
            network.Now = tick;
            network.DeliverDue(tick);
            engine.Pump((long)(tick * 1_000_000UL / 60));
            Inspect("a frame");
            if (solo && !wasSolo) RequireEmptySolo("a frame");
        }

        /// <summary>The joined session just ended: the solo session after it starts empty, whatever the host sent.</summary>
        private void RequireEmptySolo(string after) =>
            That(session.GetObjects(BoxType).Count == 0 && session.GetObjects(CrateType).Count == 0 && score.Version == 0,
                "a session that ended during " + after + " leaves none of the host's objects or values in the solo session (" +
                session.GetObjects(BoxType).Count + " boxes, " + session.GetObjects(CrateType).Count + " crates, score version " + score.Version + ")");

        private void Inspect(string after)
        {
            var snapshot = engine.Snapshot;
            That(snapshot.Participants.Count <= MultiplayerSessionSnapshot.MaximumParticipantCount, "the snapshot after " + after + " is readable");
            _ = session.Players.Players.Count;
            _ = score.Value.Value;
            foreach (var type in new[] { BoxType, CrateType })
            {
                foreach (var item in session.GetObjects(type)) _ = (item.State.Value, item.Version, item.OwnerId);
            }
        }

        /// <summary>An accepted join with a snapshot of states and objects, some of which may not decode.</summary>
        private byte[] Join()
        {
            var frame = new ProtocolWriter();
            var body = new ProtocolWriter();
            JoinMessages.WriteJoinAccepted(frame, body, new JoinAcceptedMessage
            {
                SessionId = "raw-session",
                Seed = (ulong)fuzz.Random.Next(),
                ParticipantId = "client",
                HostParticipantId = fuzz.Chance(80) ? "host" : null,
                Tick = (ulong)fuzz.Random.Next(1_000),
                StepRateHz = fuzz.Chance(90) ? 60 : 1 + fuzz.Random.Next(1_000),
                NextSequence = (ulong)fuzz.Random.Next(1, 100)
            });
            SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, 0);
            if (fuzz.Chance(70)) ReplicationMessages.WriteStateDelta(frame, body, intern, EngineHarness.Owner, "score", 1 + (ulong)fuzz.Random.Next(9), Value());
            for (var count = fuzz.Random.Next(5); count > 0; count--) ReplicationMessages.WriteObjectSpawned(frame, body, intern, Object());
            SessionMessages.WriteEmpty(frame, body, MessageType.SnapshotEnd);
            SessionMessages.WriteParticipants(frame, body, Participants());
            SessionMessages.WriteCommit(frame, body, tick, (ulong)fuzz.Random.Next(3));
            return frame.ToArray();
        }

        /// <summary>One or a few well-formed host messages with fuzzed fields, ending in a commit more often than not.</summary>
        private byte[] Message()
        {
            var frame = new ProtocolWriter();
            var body = new ProtocolWriter();
            for (var count = 1 + fuzz.Random.Next(3); count > 0; count--)
            {
                switch (fuzz.Random.Next(13))
                {
                    case 0:
                        ReplicationMessages.WriteStateDelta(frame, body, intern, EngineHarness.Owner, fuzz.Chance(80) ? "score" : "unknown-" + fuzz.Random.Next(4),
                            (ulong)fuzz.Random.Next(20), Value());
                        break;
                    case 1:
                        ReplicationMessages.WriteObjectSpawned(frame, body, intern, Object());
                        break;
                    case 2:
                        ReplicationMessages.WriteObjectChanged(frame, body, Object());
                        break;
                    case 3:
                        var gone = Object();
                        ReplicationMessages.WriteObjectDespawned(frame, body, gone.ObjectId, gone.Version, gone.Bytes);
                        break;
                    case 4:
                        ReplicationMessages.WriteResult(frame, body, MessageType.CommandResult, (ulong)fuzz.Random.Next(1, 100), tick,
                            fuzz.Chance(50) ? WireOutcome.Success(Value()) : WireOutcome.Failure((ModErrorCode)fuzz.Random.Next(1, 40), "refused"));
                        break;
                    case 5:
                        ReplicationMessages.WritePresentation(frame, body, intern, EngineHarness.Owner, "flash", fuzz.Chance(80) ? new JsonTestCodec<TestEvent>().Encode(new TestEvent("x")).Value! : fuzz.Bytes(4));
                        break;
                    case 6:
                        SessionMessages.WriteParticipants(frame, body, Participants());
                        break;
                    case 7:
                        SessionMessages.WriteClockSync(frame, body, fuzz.Random.Next(), tick, fuzz.Random.Next(100_000));
                        break;
                    case 8:
                        SessionMessages.WriteSessionReplaced(frame, body, new SessionReplacedMessage("raw-session-" + fuzz.Random.Next(3), 9, (ulong)fuzz.Random.Next(3)));
                        break;
                    case 9 when fuzz.Chance(30):
                        SessionMessages.WriteSessionEnded(frame, body, (MultiplayerSessionEndReason)fuzz.Random.Next(10), "The host ended it.");
                        break;
                    case 10:
                        SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, (ulong)fuzz.Random.Next(2));
                        break;
                    case 11:
                        SessionMessages.WriteEmpty(frame, body, MessageType.SnapshotEnd);
                        break;
                    default:
                        break;
                }
            }

            if (fuzz.Chance(70)) SessionMessages.WriteCommit(frame, body, tick, (ulong)fuzz.Random.Next(100));
            return frame.ToArray();
        }

        private ObjectWireMessage Object()
        {
            var id = (fuzz.Chance(80) ? "box-" : "crate-") + fuzz.Random.Next(1, 6);
            sentIds.Add(id);
            return new ObjectWireMessage
            {
                Owner = EngineHarness.Owner,
                TypeId = id.StartsWith("box-", StringComparison.Ordinal) ? "box" : "crate",
                ObjectId = id,
                OwnerParticipant = fuzz.Chance(50) ? null : fuzz.Chance(70) ? "client" : "host",
                Version = (ulong)fuzz.Random.Next(20),
                Bytes = Value()
            };
        }

        /// <summary>A counter as the codec writes it, or, now and then, bytes that do not decode.</summary>
        private byte[] Value() => fuzz.Chance(85) ? CounterCodec.Encode(new CounterValue(fuzz.Random.Next(100))).Value! : fuzz.Bytes(1 + fuzz.Random.Next(6));

        private ParticipantEntry[] Participants()
        {
            var entries = new List<ParticipantEntry> { new("client", "Client", true, false, MultiplayerConnectionQuality.Unknown, null, null) };
            if (fuzz.Chance(80)) entries.Add(new ParticipantEntry("host", "Host", true, true, MultiplayerConnectionQuality.Excellent, fuzz.Random.Next(200), "key"));
            for (var count = fuzz.Random.Next(3); count > 0; count--)
            {
                entries.Add(new ParticipantEntry("p" + fuzz.Random.Next(9), "Player", fuzz.Chance(50), fuzz.Chance(10),
                    (MultiplayerConnectionQuality)fuzz.Random.Next(5), fuzz.Chance(50) ? null : fuzz.Random.Next(60_001), null));
            }

            return entries.ToArray();
        }
    }

    private static T Require<T>(OperationResult<T> result, string what) where T : notnull =>
        result.TryGetValue(out var value) ? value : throw new InvalidOperationException("Assertion failed: " + what + " registers: " + result.ErrorMessage);
}
