extern alias provider;

using System.Runtime.CompilerServices;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The solo provider: the session engine hosting this process's session with no links. These port the loopback
/// provider's tests for their intent; a tick is now time, not a command counter.
/// </summary>
internal static class SoloProviderTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly JsonTestCodec<MutableValue> MutableCodec = new();
    private static readonly JsonTestCodec<TestEvent> EventCodec = new();

    internal static void SoloSessionIsReadyWithTheLocalPlayerHosting()
    {
        using var solo = new Provider("solo.contract-test", clockMicros: () => 0);
        var snapshot = solo.Snapshot;
        Assert(snapshot.State == MultiplayerSessionState.Ready && snapshot.ProcessKind == MultiplayerProcessKind.Interactive &&
               snapshot.ExecutionSides == (MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server),
            "a solo session is Ready at construction with both sides");
        Assert(snapshot.LocalParticipantId.HasValue && snapshot.HostParticipantId.Equals(snapshot.LocalParticipantId) &&
               snapshot.TryGetParticipant(snapshot.LocalParticipantId.Value, out var local) &&
               local!.IsLocal && local.IsConnected && local.IsHost,
            "the local player is connected and hosts the session");
        Assert(solo.Players.Players.Count == 1 && solo.Players.Local != null, "single-player play has exactly one player");
    }

    internal static void SoloCommandsConfirmInlineAfterTheirEvents()
    {
        using var solo = new Provider("solo.inline-test", clockMicros: () => 0);
        var state = RegisterState(solo, "counter", 0);
        var eventType = new PresentationEventType<TestEvent>("counted", EventCodec);
        var presented = 0;
        Require(solo.RegisterPresentation(new PresentationEventDefinition<TestEvent>(eventType, _ => presented++)), "register event");
        Require(solo.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("count"),
            RequestCodec,
            CounterCodec,
            (context, request) =>
            {
                Require(context.Emit(eventType, new TestEvent("counted")), "emit");
                return state.Update(value => OperationResult<CounterValue>.Success(new CounterValue(value.Value + request.Amount)));
            },
            PredictionMode.Owner)), "register count");

        var submission = solo.SubmitAsync(CommandType("count"), new AddRequest(2));
        Assert(submission.IsCompleted, "a solo command confirms inside SubmitAsync");
        var confirmation = submission.GetAwaiter().GetResult();
        Assert(confirmation.Result.Succeeded && !confirmation.WasPredicted && state.Value.Value == 2,
            "canonical work runs inline, once, without a prediction pass");
        Assert(presented == 1, "the command's event is presented before its confirmation can be observed");
    }

    internal static void RateLimitsUseASlidingWindowOnProviderTime()
    {
        long now = 100_000;
        using var solo = new Provider("solo.rate-test", clockMicros: () => now);
        var executions = 0;
        Require(solo.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("limited"),
            RequestCodec,
            CounterCodec,
            (_, request) =>
            {
                executions++;
                return OperationResult<CounterValue>.Success(new CounterValue(request.Amount));
            },
            maximumPerSecond: 2)), "register limited command");

        Assert(Submit(solo, "limited").Result.Succeeded && Submit(solo, "limited").Result.Succeeded, "two commands fit the window");
        var rejected = Submit(solo, "limited");
        Assert(rejected.Result.ErrorCode == ModErrorCode.RateLimited && executions == 2,
            "an excess command is rejected with the stable code and never reaches the handler");
        now = 1_050_000;
        solo.Pump();
        Assert(Submit(solo, "limited").Result.ErrorCode == ModErrorCode.RateLimited,
            "the window slides: a second boundary alone does not reset it");
        now = 1_100_000;
        solo.Pump();
        Assert(Submit(solo, "limited").Result.Succeeded && executions == 3, "a full second after the first command, it fits again");
    }

    internal static void RejectedSubmissionsLeaveNoTrace()
    {
        using var solo = new Provider("solo.rejection-test", clockMicros: () => 0);
        var state = RegisterState(solo, "kept", 5);
        var stateChanges = 0;
        var sessionChanges = 0;
        using var stateObserver = state.SubscribeChanged(_ => stateChanges++);
        using var sessionObserver = solo.SubscribeChanged(_ => sessionChanges++);

        Assert(Submit(solo, "missing").Result.ErrorCode == ModErrorCode.NotFound, "an unregistered command is refused");
        Require(solo.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("limited"),
            RequestCodec,
            CounterCodec,
            (_, request) => state.Update(value => OperationResult<CounterValue>.Success(new CounterValue(value.Value + request.Amount))),
            maximumPerSecond: 1)), "register limited command");
        var wrongTypes = solo.SubmitAsync(new MultiplayerCommandType<MutableValue, CounterValue>("limited"), new MutableValue(1))
            .GetAwaiter().GetResult();
        Assert(wrongTypes.Result.ErrorCode == ModErrorCode.NotFound, "mismatched generated types are refused");
        Assert(Submit(solo, "limited").Result.Succeeded, "the admitted command runs");
        Assert(Submit(solo, "limited").Result.ErrorCode == ModErrorCode.RateLimited, "the excess command is refused");
        Require(solo.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("canonical-rejection"),
            RequestCodec,
            CounterCodec,
            (_, _) =>
            {
                Require(state.Update(value => OperationResult<CounterValue>.Success(new CounterValue(99))), "staged change");
                return OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "rejected");
            })), "register rejecting command");
        Assert(Submit(solo, "canonical-rejection").Result.ErrorCode == ModErrorCode.InvalidArgument, "the handler rejects");
        Assert(state.Value.Value == 7 && state.Version == 1 && stateChanges == 1,
            "only the admitted command changed state, and observers saw that change once");
        Assert(sessionChanges == 0 && solo.Snapshot.Tick.Value == 0, "submissions never move canonical time; only the clock does");
    }

    internal static void ObserversRunAfterSettlementAndMaySubmitAgain()
    {
        using var solo = new Provider("solo.observer-test", clockMicros: () => 0);
        var state = RegisterState(solo, "counter", 1);
        Require(solo.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("increment"),
            RequestCodec,
            CounterCodec,
            (_, request) => state.Update(value => OperationResult<CounterValue>.Success(new CounterValue(value.Value + request.Amount))))),
            "register increment");
        var seen = new List<string>();
        MultiplayerCommandConfirmation<CounterValue>? reentrant = null;
        using var first = state.SubscribeChanged(value =>
        {
            seen.Add("first:" + value.Value);
            if (value.Value == 3) reentrant = Submit(solo, "increment");
        });
        using var second = state.SubscribeChanged(value => seen.Add("second:" + value.Value));

        var outer = Submit(solo, "increment");
        Assert(outer.Result.Succeeded && outer.Result.Value!.Value == 3, "the outer command commits");
        Assert(reentrant != null && reentrant.Result.Succeeded && state.Value.Value == 5,
            "an observer's submission runs as its own transaction once the outer one has settled");
        Assert(seen.SequenceEqual(new[] { "first:3", "second:3", "first:5", "second:5" }),
            "every observer in a round sees the same settled value, and the nested change gets its own round");
    }

    internal static void RejectedAndThrowingCommandsRollBackStateAndEvents()
    {
        using var solo = new Provider("solo.transaction-test", clockMicros: () => 0);
        var first = RegisterState(solo, "first", 1);
        var second = RegisterState(solo, "second", 10);
        var changes = 0;
        var presentations = 0;
        var eventType = new PresentationEventType<TestEvent>("accepted", EventCodec);
        using var firstChanged = first.SubscribeChanged(_ => changes++);
        using var secondChanged = second.SubscribeChanged(_ => changes++);
        using var presented = Require(
            solo.RegisterPresentation(new PresentationEventDefinition<TestEvent>(eventType, _ => presentations++)),
            "register presentation");
        var scenarios = new (string Id, IMultiplayerCodec<CounterValue> Codec, Func<MultiplayerCommandContext, AddRequest, OperationResult<CounterValue>> Finish, ModErrorCode Code)[]
        {
            ("handler-rejects", CounterCodec, (_, _) => OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "rejected"), ModErrorCode.InvalidArgument),
            ("response-rejects", new RejectingCounterCodec(), (_, _) => OperationResult<CounterValue>.Success(new CounterValue(99)), ModErrorCode.InvalidArgument),
            ("handler-throws", CounterCodec, (_, _) => throw new InvalidOperationException("synthetic failure"), ModErrorCode.Unknown),
            ("response-throws", new ThrowingCounterCodec(), (_, _) => OperationResult<CounterValue>.Success(new CounterValue(99)), ModErrorCode.Unknown)
        };
        foreach (var scenario in scenarios)
        {
            RegisterMutatingCommand(solo, scenario.Id, first, second, eventType, scenario.Codec, scenario.Finish);
            var result = Submit(solo, scenario.Id);
            Assert(!result.Result.Succeeded && result.Result.ErrorCode == scenario.Code, scenario.Id + " is a structured rejection");
            Assert(first.Value.Value == 1 && second.Value.Value == 10 && first.Version == 0 && second.Version == 0,
                scenario.Id + " restores every state and version");
            Assert(changes == 0 && presentations == 0, scenario.Id + " publishes no state change and no event");
        }
    }

    internal static void ObjectInputsEnforceCodecBounds()
    {
        using var solo = new Provider("solo.object-bounds-test", clockMicros: () => 0);
        bool? senderOwnedTarget = null;
        var ownershipType = new ReplicatedObjectType<CounterValue, AddRequest>("ownership-context");
        Require(solo.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            ownershipType,
            CounterCodec,
            RequestCodec,
            (context, state, input) =>
            {
                senderOwnedTarget = context.SenderOwnsTarget;
                return OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount));
            })), "register ownership-context type");
        var ownershipObject = Require(solo.SpawnObject(ownershipType, new CounterValue(0)), "spawn server-owned object");
        Assert(ownershipObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult().Result.Succeeded && senderOwnedTarget == false,
            "a server-owned object never claims the sender owns it");
        Assert(ownershipObject.TransferOwnership(solo.Snapshot.LocalParticipantId).Succeeded, "transfer to the local player");
        Assert(ownershipObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult().Result.Succeeded && senderOwnedTarget == true,
            "an exact matching owner is reported to the handler");

        var oversizedInputCodec = new OversizedInputCodec();
        var inputExecutions = 0;
        var inputType = new ReplicatedObjectType<CounterValue, AddRequest>("input-bound");
        Require(solo.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            inputType,
            CounterCodec,
            oversizedInputCodec,
            (_, state, input) =>
            {
                inputExecutions++;
                return OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount));
            })), "register input-bound type");
        var inputObject = Require(solo.SpawnObject(inputType, new CounterValue(0)), "spawn input-bound object");
        var inputResult = inputObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(inputResult.Result.ErrorCode == ModErrorCode.InvalidArgument && oversizedInputCodec.DecodeCalls == 0 &&
               inputExecutions == 0 && inputObject.State.Value == 0,
            "an oversized input is refused before it is decoded or handled");

        var stateType = new ReplicatedObjectType<CounterValue, AddRequest>("state-bound");
        Require(solo.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            stateType,
            new ConditionalStateCodec(),
            RequestCodec,
            (_, state, input) => OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount)))),
            "register state-bound type");
        var stateObject = Require(solo.SpawnObject(stateType, new CounterValue(0)), "spawn state-bound object");
        var stateResult = stateObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(stateResult.Result.ErrorCode == ModErrorCode.InvalidArgument && stateObject.Version == 0,
            "an oversized resulting state is refused and never installed");

        var throwingType = new ReplicatedObjectType<CounterValue, AddRequest>("throwing-input");
        Require(solo.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            throwingType,
            CounterCodec,
            new ThrowingInputCodec(),
            (_, state, input) => OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount)))),
            "register throwing-input type");
        var throwingObject = Require(solo.SpawnObject(throwingType, new CounterValue(0)), "spawn throwing-input object");
        var throwingResult = throwingObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(throwingResult.Result.ErrorCode == ModErrorCode.Unknown && throwingObject.State.Value == 0 && throwingObject.Version == 0,
            "a throwing input codec is a structured rejection that changes nothing");
    }

    internal static void HandlersDeferGraphChangesAndRefuseRegistration()
    {
        using var solo = new Provider("solo.reentrancy-test", clockMicros: () => 0);
        var objectType = new ReplicatedObjectType<CounterValue, AddRequest>("existing-object");
        Require(solo.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            objectType,
            CounterCodec,
            RequestCodec,
            (_, state, input) => OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount)))),
            "register existing type");
        var existing = Require(solo.SpawnObject(objectType, new CounterValue(0)), "spawn existing object");
        var eventType = new PresentationEventType<TestEvent>("existing-event", EventCodec);
        var presented = new List<string>();
        Require(solo.RegisterPresentation(new PresentationEventDefinition<TestEvent>(eventType, value => presented.Add(value.Message))),
            "register existing event");
        var refusals = 0;
        IReplicatedObject<CounterValue, AddRequest>? spawnedInside = null;
        var spawnVisibleInside = false;
        var presentedInside = -1;
        Require(solo.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("graph"),
            RequestCodec,
            CounterCodec,
            (_, request) =>
            {
                CountRefused(solo.RegisterState(new ReplicatedStateDefinition<CounterValue>("late", new CounterValue(0), CounterCodec)), ref refusals);
                CountRefused(solo.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                    CommandType("late-command"), RequestCodec, CounterCodec,
                    (__, late) => OperationResult<CounterValue>.Success(new CounterValue(late.Amount)))), ref refusals);
                CountRefused(solo.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                    new ReplicatedObjectType<CounterValue, AddRequest>("late-type"), CounterCodec, RequestCodec,
                    (__, state, input) => OperationResult<CounterValue>.Success(state))), ref refusals);
                CountRefused(solo.RegisterPresentation(new PresentationEventDefinition<TestEvent>(
                    new PresentationEventType<TestEvent>("late-event", EventCodec))), ref refusals);
                var nested = solo.SubmitAsync(CommandType("graph"), new AddRequest(1)).GetAwaiter().GetResult();
                if (nested.Result.ErrorCode == ModErrorCode.InvalidState) refusals++;

                spawnedInside = Require(solo.SpawnObject(objectType, new CounterValue(9)), "spawn inside the handler");
                spawnVisibleInside = spawnedInside.IsSpawned && solo.GetObjects(objectType).Count == 2;
                Require(solo.DespawnObject(existing.Id), "despawn inside the handler");
                Require(solo.PublishPresentation(eventType, new TestEvent("published-inside")), "publish inside the handler");
                presentedInside = presented.Count;
                return request.Amount == 0
                    ? OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "abort")
                    : OperationResult<CounterValue>.Success(new CounterValue(request.Amount));
            })), "register graph command");

        var aborted = Submit(solo, "graph", 0);
        Assert(aborted.Result.ErrorCode == ModErrorCode.InvalidArgument && refusals == 5,
            "registrations and nested submissions are refused inside a handler");
        Assert(spawnVisibleInside && presentedInside == 0, "a handler sees its own spawn, but nothing is presented before commit");
        Assert(!spawnedInside!.IsSpawned && existing.IsSpawned && presented.Count == 0 && solo.GetObjects(objectType).Count == 1,
            "an aborted handler's spawn, despawn and event never happen");

        refusals = 0;
        var committed = Submit(solo, "graph", 1);
        Assert(committed.Result.Succeeded && refusals == 5, "the committing handler is refused the same registrations");
        Assert(spawnedInside!.IsSpawned && !existing.IsSpawned && presented.SequenceEqual(new[] { "published-inside" }) &&
               solo.GetObjects(objectType).Count == 1,
            "a committed handler's spawn, despawn and event take effect together");
    }

    internal static void OwnerFacadesIsolateConsumersAndCloseWithThem()
    {
        long now = 0;
        using var solo = new Provider("solo.owner-test", clockMicros: () => now);
        using var firstLifetime = new FakeModLifetime();
        using var secondLifetime = new FakeModLifetime();
        var first = (IMultiplayerSession)solo.CreateOwnerFacade(typeof(IMultiplayerSession), "first.mod", firstLifetime);
        var second = (IMultiplayerSession)solo.CreateOwnerFacade(typeof(IMultiplayerSession), "second.mod", secondLifetime);
        Assert(!ReferenceEquals(first, second), "each consumer receives its own facade");
        var firstTicks = 0;
        var secondTicks = 0;
        first.SubscribeChanged(_ => firstTicks++);
        second.SubscribeChanged(_ => secondTicks++);

        const string sharedId = "identical-public-id";
        var firstState = RegisterState(first, sharedId, 1);
        var secondState = RegisterState(second, sharedId, 100);
        var firstPresentations = 0;
        var secondPresentations = 0;
        var sharedEvent = new PresentationEventType<TestEvent>(sharedId, EventCodec);
        Require(first.RegisterPresentation(new PresentationEventDefinition<TestEvent>(sharedEvent, value =>
        {
            Assert(value.Message.StartsWith("first", StringComparison.Ordinal), "first partition receives only first payloads");
            firstPresentations++;
        })), "register first event");
        Require(second.RegisterPresentation(new PresentationEventDefinition<TestEvent>(sharedEvent, value =>
        {
            Assert(value.Message.StartsWith("second", StringComparison.Ordinal), "second partition receives only second payloads");
            secondPresentations++;
        })), "register second event");
        Require(first.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType(sharedId), RequestCodec, CounterCodec,
            (context, request) =>
            {
                var updated = firstState.Update(value => OperationResult<CounterValue>.Success(new CounterValue(value.Value + request.Amount)));
                if (updated.Succeeded) Require(context.Emit(sharedEvent, new TestEvent("first-command")), "emit first");
                return updated;
            })), "register first command");
        Require(second.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType(sharedId), RequestCodec, CounterCodec,
            (context, request) =>
            {
                var updated = secondState.Update(value => OperationResult<CounterValue>.Success(new CounterValue(value.Value + (request.Amount * 10))));
                if (updated.Succeeded) Require(context.Emit(sharedEvent, new TestEvent("second-command")), "emit second");
                return updated;
            })), "register second command");

        Assert(Submit(first, sharedId).Result.Value!.Value == 3 && secondState.Value.Value == 100 &&
               firstPresentations == 1 && secondPresentations == 0,
            "a facade reaches only its own same-id command, state and event");
        Assert(Submit(second, sharedId).Result.Value!.Value == 120 && firstState.Value.Value == 3 && secondPresentations == 1,
            "the other facade is isolated the same way");
        Assert(Submit(solo, sharedId).Result.ErrorCode == ModErrorCode.NotFound, "the provider's own partition cannot reach mod commands");

        var sharedObjectType = new ReplicatedObjectType<CounterValue, AddRequest>(sharedId);
        foreach (var facade in new[] { first, second })
        {
            Require(facade.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                sharedObjectType, CounterCodec, RequestCodec,
                (_, state, input) => OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount)))),
                "register same-id object type");
        }

        var firstObject = Require(first.SpawnObject(sharedObjectType, new CounterValue(5)), "spawn first object");
        var secondObject = Require(second.SpawnObject(sharedObjectType, new CounterValue(50)), "spawn second object");
        Assert(first.GetObjects(sharedObjectType).Single().Id.Equals(firstObject.Id) &&
               second.GetObjects(sharedObjectType).Single().Id.Equals(secondObject.Id) &&
               !first.TryGetObject(sharedObjectType, secondObject.Id, out _) &&
               solo.GetObjects(sharedObjectType).Count == 0,
            "object discovery stays inside each owner partition");
        Assert(first.DespawnObject(secondObject.Id).ErrorCode == ModErrorCode.NotFound && secondObject.IsSpawned,
            "a facade cannot despawn another owner's object");

        now = 50_000;
        solo.Pump();
        var ticksBeforeStopping = secondTicks;
        firstLifetime.Dispose();
        Assert(!firstObject.IsSpawned && secondObject.IsSpawned, "stopping one consumer despawns only its objects");
        Assert(first.SubmitAsync(CommandType(sharedId), new AddRequest(2)).GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.Cancelled,
            "a stopped consumer's facade refuses new work");
        Assert(Submit(second, sharedId).Result.Succeeded && secondState.Value.Value == 140, "the other consumer keeps working");
        now = 100_000;
        solo.Pump();
        Assert(secondTicks > ticksBeforeStopping && firstTicks < secondTicks, "a stopped consumer's subscriptions are gone");
    }

    internal static void MutableValuesNeverEscapeCanonicalStorage()
    {
        using var solo = new Provider("solo.mutable-test", clockMicros: () => 0);
        var initial = new MutableValue(1);
        var state = Require(solo.RegisterState(new ReplicatedStateDefinition<MutableValue>("mutable-state", initial, MutableCodec)),
            "register mutable state");
        initial.Value = 100;
        var view = state.Value;
        view.Value = 200;
        Assert(state.Value.Value == 1 && state.Version == 0, "neither the initial value nor a read copy aliases storage");

        var secondObserverValue = -1;
        using var firstObserver = state.SubscribeChanged(value => value.Value = 400);
        using var secondObserver = state.SubscribeChanged(value => secondObserverValue = value.Value);
        var response = Require(state.Update(current =>
        {
            current.Value = 2;
            current.History.Add(2);
            return OperationResult<MutableValue>.Success(current);
        }), "update mutable state");
        response.Value = 500;
        Assert(secondObserverValue == 2 && state.Value.Value == 2 && state.Value.History.SequenceEqual(new[] { 1, 2 }),
            "results and observer payloads are independent copies");

        var objectType = new ReplicatedObjectType<MutableValue, AddRequest>("mutable-object");
        Require(solo.RegisterObjectType(new ReplicatedObjectTypeDefinition<MutableValue, AddRequest>(
            objectType, MutableCodec, RequestCodec,
            (_, current, input) =>
            {
                current.Value += input.Amount;
                current.History.Add(current.Value);
                return OperationResult<MutableValue>.Success(current);
            })), "register mutable object type");
        var replicated = Require(solo.SpawnObject(objectType, new MutableValue(4)), "spawn mutable object");
        var confirmed = Require(replicated.SubmitInputAsync(new AddRequest(2)).GetAwaiter().GetResult().Result, "confirm input");
        confirmed.Value = 1000;
        Assert(replicated.State.Value == 6 && replicated.State.History.SequenceEqual(new[] { 4, 6 }) && replicated.Version == 1,
            "object confirmations are detached from canonical storage");
    }

    internal static void SoloPumpAllocatesNothing()
    {
        long now = 0;
        using var solo = new Provider("solo.pump-test", clockMicros: () => now);
        RegisterState(solo, "idle", 0);
        var steps = 0;
        using var tick = solo.SubscribeTick(_ => steps++);
        var allocated = AllocationPasses.Fewest(() => PumpFrames(solo, ref now, 2_000));
        Assert(allocated == 0, "a warm solo pump allocates nothing (allocated " + allocated + " bytes)");
        Assert(steps > 0 && solo.Snapshot.Tick.Value == (ulong)steps, "fixed steps ran at 20 Hz while the game pumped per frame");
    }

    /// <summary>
    /// Pumps <paramref name="frames"/> game frames of 16.667 ms, moving <paramref name="now"/> on, and returns what this
    /// thread allocated meanwhile.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PumpFrames(Provider solo, ref long now, int frames)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < frames; frame++)
        {
            now += 16_667;
            solo.Pump();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void RegisterMutatingCommand(
        IMultiplayerSession session,
        string id,
        IReplicatedState<CounterValue> first,
        IReplicatedState<CounterValue> second,
        PresentationEventType<TestEvent> eventType,
        IMultiplayerCodec<CounterValue> responseCodec,
        Func<MultiplayerCommandContext, AddRequest, OperationResult<CounterValue>> finish) =>
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType(id),
            RequestCodec,
            responseCodec,
            (context, request) =>
            {
                Require(first.Update(value => OperationResult<CounterValue>.Success(new CounterValue(value.Value + request.Amount))), "mutate first");
                Require(second.Update(value => OperationResult<CounterValue>.Success(new CounterValue(value.Value + request.Amount))), "mutate second");
                Require(context.Emit(eventType, new TestEvent(id)), "buffer presentation");
                return finish(context, request);
            })), "register mutating command " + id);

    private static IReplicatedState<CounterValue> RegisterState(IMultiplayerSession session, string id, int value) =>
        Require(session.RegisterState(new ReplicatedStateDefinition<CounterValue>(id, new CounterValue(value), CounterCodec)),
            "register state " + id);

    private static MultiplayerCommandConfirmation<CounterValue> Submit(IMultiplayerSession session, string id, int amount = 2) =>
        session.SubmitAsync(CommandType(id), new AddRequest(amount)).GetAwaiter().GetResult();

    private static MultiplayerCommandType<AddRequest, CounterValue> CommandType(string id) => new(id);

    private static void CountRefused<T>(OperationResult<T> result, ref int count) where T : notnull
    {
        if (!result.Succeeded && result.ErrorCode == ModErrorCode.InvalidState) count++;
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private sealed class RejectingCounterCodec : IMultiplayerCodec<CounterValue>
    {
        public int MaximumEncodedBytes => 64;

        public OperationResult<byte[]> Encode(CounterValue value) =>
            OperationResult<byte[]>.Failure(ModErrorCode.InvalidArgument, "synthetic response rejection");

        public OperationResult<CounterValue> Decode(byte[] bytes) =>
            OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "unexpected decode");
    }

    private sealed class ThrowingCounterCodec : IMultiplayerCodec<CounterValue>
    {
        public int MaximumEncodedBytes => 64;

        public OperationResult<byte[]> Encode(CounterValue value) => throw new InvalidOperationException("synthetic codec failure");

        public OperationResult<CounterValue> Decode(byte[] bytes) => throw new InvalidOperationException("unexpected decode");
    }

    private sealed class ThrowingInputCodec : IMultiplayerCodec<AddRequest>
    {
        public int MaximumEncodedBytes => 64;

        public OperationResult<byte[]> Encode(AddRequest value) => throw new InvalidOperationException("synthetic input codec failure");

        public OperationResult<AddRequest> Decode(byte[] bytes) => throw new InvalidOperationException("unexpected decode");
    }

    private sealed class OversizedInputCodec : IMultiplayerCodec<AddRequest>
    {
        public int DecodeCalls { get; private set; }

        public int MaximumEncodedBytes => 1;

        public OperationResult<byte[]> Encode(AddRequest value) => OperationResult<byte[]>.Success(new byte[2]);

        public OperationResult<AddRequest> Decode(byte[] bytes)
        {
            DecodeCalls++;
            return OperationResult<AddRequest>.Success(new AddRequest(1));
        }
    }

    private sealed class ConditionalStateCodec : IMultiplayerCodec<CounterValue>
    {
        public int MaximumEncodedBytes => 1;

        public OperationResult<byte[]> Encode(CounterValue value) => OperationResult<byte[]>.Success(new byte[value.Value == 0 ? 1 : 2]);

        public OperationResult<CounterValue> Decode(byte[] bytes) => OperationResult<CounterValue>.Success(new CounterValue(0));
    }
}
