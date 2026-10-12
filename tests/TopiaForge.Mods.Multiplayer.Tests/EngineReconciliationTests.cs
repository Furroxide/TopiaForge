using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Client-side guarantees: predictions reconcile only at a commit, a confirmation reports what the host really did,
/// and cancellation and session replacement never misattribute an outcome.
/// </summary>
internal static class EngineReconciliationTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly JsonTestCodec<TestEvent> EventCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddCommandType = new("add");
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> BoxType = new("box");

    internal static void SplitFlushesReconcileOnlyAtTheirCommit()
    {
        var raw = new RawHostClient();
        var score = Require(raw.Session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "score");
        Require(raw.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            BoxType, CounterCodec, RequestCodec, (_, state, _) => OperationResult<CounterValue>.Success(state))), "box");
        RegisterAdd(raw.Session, (_, request) => Increment(score, request.Amount), PredictionMode.Owner);
        raw.Join();
        var seen = new List<int>();
        using var watch = score.SubscribeChanged(value => seen.Add(value.Value));
        var submission = raw.Session.SubmitAsync(AddCommandType, new AddRequest(1));
        raw.Pump();

        // The host's flush for the command arrives in two frames: a spawn and the result, then the state and the commit.
        raw.Host.Send((frame, body) =>
        {
            ReplicationMessages.WriteObjectSpawned(frame, body, raw.Intern, new ObjectWireMessage
            {
                Owner = EngineHarness.Owner,
                TypeId = "box",
                ObjectId = "box-1",
                Bytes = Encode(CounterCodec, new CounterValue(0))
            });
            ReplicationMessages.WriteResult(frame, body, MessageType.CommandResult, 1, 1, WireOutcome.Success(Encode(CounterCodec, new CounterValue(1))));
        });
        raw.Pump();
        Assert(seen.SequenceEqual(new[] { 1 }) && score.Value.Value == 1 && !submission.IsCompleted,
            "half a flush neither rolls the prediction back nor completes the command (" + string.Join(",", seen) + ")");

        raw.Host.Send((frame, body) =>
        {
            ReplicationMessages.WriteStateDelta(frame, body, raw.Intern, EngineHarness.Owner, "score", 1, Encode(CounterCodec, new CounterValue(1)));
            SessionMessages.WriteCommit(frame, body, 1, 1);
        });
        raw.Pump();
        Assert(seen.SequenceEqual(new[] { 1 }) && score.Value.Value == 1,
            "the commit confirms the predicted value without another notification (" + string.Join(",", seen) + ")");
        Assert(submission.IsCompleted && submission.Result.Result.Succeeded && submission.Result.WasPredicted, "and completes the command");
    }

    internal static void CancellingASentPredictionKeepsItApplied()
    {
        using var harness = new EngineHarness(latencyTicks: 2);
        var score = Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "host score");
        RegisterAdd(harness.HostSession, (_, request) => Increment(score, request.Amount), PredictionMode.Owner);
        var client = harness.AddClient("pilot");
        var mirror = Require(client.Session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "mirror");
        RegisterAdd(client.Session, (_, request) => Increment(mirror, request.Amount), PredictionMode.Owner);
        harness.Flush();
        var seen = new List<int>();
        using var watch = mirror.SubscribeChanged(value => seen.Add(value.Value));
        using var cancellation = new CancellationTokenSource();
        var submission = client.Session.SubmitAsync(AddCommandType, new AddRequest(1), cancellation.Token);
        harness.PumpAll();
        cancellation.Cancel();
        Assert(submission.IsCompleted && submission.Result.Result.ErrorCode == ModErrorCode.Cancelled,
            "the caller's task completes as cancelled at once");
        harness.Flush();
        Assert(seen.SequenceEqual(new[] { 1 }),
            "a sent submission still executes, so its prediction stays applied and never flickers (" + string.Join(",", seen) + ")");
        Assert(score.Value.Value == 1 && mirror.Value.Value == 1 && client.Engine.PendingSubmissionCount == 0,
            "the host applied it, and the client forgot it once its commit arrived");
    }

    internal static void ACancellationDuringAPumpWaitsForItsSettlePoint()
    {
        using var harness = new EngineHarness();
        var watched = Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>("watched", new CounterValue(0), CounterCodec)), "host state");
        RegisterAdd(harness.HostSession, (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)));
        var client = harness.AddClient("canceller");
        var codec = new CancellingCodec();
        Require(client.Session.RegisterState(new ReplicatedStateDefinition<CounterValue>("watched", new CounterValue(0), codec)), "client state");
        RegisterAdd(client.Session, (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)));
        harness.Flush();
        using var cancellation = new CancellationTokenSource();
        var submission = client.Session.SubmitAsync(AddCommandType, new AddRequest(1), cancellation.Token);
        codec.Arm(cancellation, () => submission.IsCompleted);
        Require(watched.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(1))), "change the watched state");
        harness.Flush();
        Assert(codec.Fired && !codec.CompletedWhileDecoding,
            "a token cancelled while the client decodes a frame does not complete the submission in the middle of the frame");
        Assert(submission.IsCompleted && submission.Result.Result.ErrorCode == ModErrorCode.Cancelled,
            "the cancellation completes at the settle point after the frame");
    }

    internal static void ResultsQueuedBeforeAReplacementStillComplete()
    {
        using var harness = new EngineHarness();
        var votes = Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>("votes", new CounterValue(0), CounterCodec)), "votes");
        RegisterAdd(harness.HostSession, (_, request) => Increment(votes, request.Amount));
        var client = harness.AddClient("voter");
        RegisterAdd(client.Session, (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)));
        harness.Flush();
        var replaced = false;
        using var restart = votes.SubscribeChanged(value =>
        {
            if (value.Value < 1 || replaced) return;
            replaced = true;
            harness.Host.ReplaceSession(new MultiplayerSessionId("restarted"), new SessionSeed(7));
        });
        var vote = client.Session.SubmitAsync(AddCommandType, new AddRequest(1));
        harness.Flush();
        Assert(replaced && vote.IsCompleted && vote.Result.Result.Succeeded,
            "a vote whose commit restarts the session reports the success it had, not a cancellation");
        Assert(client.Engine.Snapshot.Id.Value == "restarted" && client.Engine.Snapshot.State == MultiplayerSessionState.Ready,
            "and the client follows into the new session");
    }

    internal static void LateResultsFromAReplacedSessionMatchNothing()
    {
        using var harness = new EngineHarness(latencyTicks: 2);
        var executions = 0;
        RegisterAdd(harness.HostSession, (_, request) =>
        {
            executions++;
            return OperationResult<CounterValue>.Success(new CounterValue(request.Amount));
        });
        var client = harness.AddClient("racer");
        RegisterAdd(client.Session, (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)));
        harness.Flush();
        var first = client.Session.SubmitAsync(AddCommandType, new AddRequest(1));
        harness.PumpAll();
        harness.Host.ReplaceSession(new MultiplayerSessionId("second-round"), new SessionSeed(9));
        harness.PumpAll();
        harness.Advance(2);
        Assert(client.Engine.Snapshot.Id.Value == "second-round" && client.Engine.Snapshot.State == MultiplayerSessionState.Ready,
            "the client is ready in the new session while the host's answer to the old submission is still in flight");
        var second = client.Session.SubmitAsync(AddCommandType, new AddRequest(2));
        harness.Flush();
        Assert(first.Result.Result.ErrorCode == ModErrorCode.Cancelled, "the old session's submission is cancelled by the replacement");
        Assert(second.Result.Result.Succeeded && second.Result.Result.Value!.Value == 2 && executions == 1,
            "the host's late refusal of the old submission cannot complete the new one, which really executed");
    }

    internal static void AnInputThatDespawnsItsTargetStillSucceeds()
    {
        using var harness = new EngineHarness();
        Require(harness.HostSession.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            BoxType,
            CounterCodec,
            RequestCodec,
            (context, state, input) =>
            {
                var consumed = harness.HostSession.DespawnObject(context.TargetObjectId);
                return consumed.Succeeded
                    ? OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount))
                    : OperationResult<CounterValue>.Failure(consumed.ErrorCode, consumed.ErrorMessage);
            })), "host box");
        var client = harness.AddClient("eater");
        Require(client.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            BoxType, CounterCodec, RequestCodec, (_, state, _) => OperationResult<CounterValue>.Success(state))), "client box");
        harness.Flush();
        var box = Require(harness.HostSession.SpawnObject(BoxType, new CounterValue(1), new ParticipantId("eater")), "spawn");
        harness.Flush();
        var consume = client.Engine.FindObject<CounterValue, AddRequest>(box.Id)!.SubmitInputAsync(new AddRequest(1));
        harness.Flush();
        Assert(consume.IsCompleted && consume.Result.Result.Succeeded && consume.Result.Result.Value!.Value == 2,
            "an input that consumes its own target reports the host's success, not a cancellation");
        Assert(client.Session.GetObjects(BoxType).Count == 0 && client.Engine.PendingSubmissionCount == 0,
            "and the object is gone from the client");
    }

    internal static void CommandEventsAddressTheCommandsSender()
    {
        using var harness = new EngineHarness();
        var whisper = new PresentationEventType<TestEvent>("whisper", EventCodec);
        var received = new Dictionary<string, int>(StringComparer.Ordinal);
        var predictedPublishes = new List<OperationResult<bool>>();
        Require(harness.HostSession.RegisterPresentation(new PresentationEventDefinition<TestEvent>(whisper)), "host whisper");
        RegisterAdd(harness.HostSession, (_, request) => Publish(harness.HostSession, whisper, request, null), PredictionMode.Owner);
        var alice = harness.AddClient("alice").Session;
        var bob = harness.AddClient("bob").Session;
        foreach (var (name, session) in new[] { ("alice", alice), ("bob", bob) })
        {
            Require(session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(
                whisper, _ => received[name] = received.GetValueOrDefault(name) + 1)), "whisper on " + name);
            RegisterAdd(session, (_, request) => Publish(session, whisper, request, predictedPublishes), PredictionMode.Owner);
        }

        harness.Flush();
        var submitted = alice.SubmitAsync(AddCommandType, new AddRequest(1));
        harness.Flush();
        Assert(submitted.Result.Result.Succeeded && submitted.Result.WasPredicted, "the predicted command succeeds");
        Assert(received.GetValueOrDefault("alice") == 1 && !received.ContainsKey("bob"),
            "an event a command publishes to its Sender reaches the participant who sent it, even from a dedicated server");
        Assert(predictedPublishes.Count >= 1 && predictedPublishes.All(result => result.Succeeded),
            "a prediction's publish is dropped as a success, so it never mispredicts");
    }

    internal static void ReRegisteredStatesKeepTheirCanonicalValue()
    {
        using var harness = new EngineHarness();
        var score = Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "host score");
        var client = harness.AddClient("reloader");
        var mirror = Require(client.Session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "mirror");
        harness.Flush();
        Require(score.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(5))), "update");
        harness.Flush();
        Assert(mirror.Value.Value == 5, "the client mirrors the canonical value");
        mirror.Dispose();
        var again = Require(client.Session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "register again");
        Assert(again.Value.Value == 5 && again.Version == score.Version,
            "registering a state again adopts the canonical value the client already has");
    }

    internal static void AFrameHoldsOneMaximumMessageAfterTheSoftLimit()
    {
        var raw = new RawHostClient();
        raw.Join();
        var frame = new ProtocolWriter(ProtocolLimits.MaximumFrameBytes);
        var body = new ProtocolWriter(ProtocolLimits.MaximumPayloadBytes + 1024);

        // An ignorable filler leaves the frame one byte short of the soft limit, so a sender would not split yet.
        var filler = new byte[ProtocolLimits.SoftFrameBytes - 1 - 4];
        body.Reset();
        body.WriteRaw(filler, 0, filler.Length);
        frame.WriteEnvelope(0x50, body);
        Assert(frame.Length == ProtocolLimits.SoftFrameBytes - 1, "the filler stops one byte short of the soft limit");
        ReplicationMessages.WriteStateDelta(frame, body, raw.Intern, EngineHarness.Owner, "huge", 1, new byte[ProtocolLimits.MaximumPayloadBytes]);
        SessionMessages.WriteCommit(frame, body, 1, 0);
        Assert(frame.Length > (1024 * 1024) + (64 * 1024) && frame.Length <= ProtocolLimits.MaximumFrameBytes,
            "one maximum-size message past the soft limit fits the frame bound (" + frame.Length + " bytes)");

        raw.Host.SendRaw(frame.ToArray());
        raw.Pump();
        Assert(raw.Engine.Snapshot.State == MultiplayerSessionState.Ready && raw.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 0,
            "the client accepts the frame instead of ending the session");
    }

    private static OperationResult<CounterValue> Publish(
        EngineSessionFacade session,
        PresentationEventType<TestEvent> whisper,
        AddRequest request,
        List<OperationResult<bool>>? record)
    {
        var published = session.PublishPresentation(whisper, new TestEvent("for the sender"), PresentationTarget.Sender);
        record?.Add(published);
        return published.Succeeded
            ? OperationResult<CounterValue>.Success(new CounterValue(request.Amount))
            : OperationResult<CounterValue>.Failure(published.ErrorCode, published.ErrorMessage);
    }

    private static OperationResult<CounterValue> Increment(IReplicatedState<CounterValue> state, int amount) =>
        state.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + amount)));

    private static void RegisterAdd(
        EngineSessionFacade session,
        Func<MultiplayerCommandContext, AddRequest, OperationResult<CounterValue>> handler,
        PredictionMode prediction = PredictionMode.None) =>
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddCommandType, RequestCodec, CounterCodec, handler, prediction)), "register add");

    private static byte[] Encode<T>(IMultiplayerCodec<T> codec, T value) where T : class =>
        codec.Encode(value).Value ?? throw new InvalidOperationException("encode");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }

    /// <summary>A codec that, once armed, cancels a token the first time the engine decodes with it.</summary>
    private sealed class CancellingCodec : IMultiplayerCodec<CounterValue>
    {
        private CancellationTokenSource? cancellation;
        private Func<bool>? completed;

        public int MaximumEncodedBytes => CounterCodec.MaximumEncodedBytes;

        internal bool Fired { get; private set; }

        internal bool CompletedWhileDecoding { get; private set; }

        internal void Arm(CancellationTokenSource source, Func<bool> isCompleted)
        {
            cancellation = source;
            completed = isCompleted;
        }

        public OperationResult<byte[]> Encode(CounterValue value) => CounterCodec.Encode(value);

        public OperationResult<CounterValue> Decode(byte[] bytes)
        {
            if (cancellation != null && !Fired)
            {
                Fired = true;
                cancellation.Cancel();
                CompletedWhileDecoding = completed!();
            }

            return CounterCodec.Decode(bytes);
        }
    }
}
