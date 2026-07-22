using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class LoopbackProviderTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly JsonTestCodec<MutableValue> MutableCodec = new();

    internal static void RateLimitsEachAuthenticatedSenderPerCommand()
    {
        long nowMilliseconds = 100;
        using var session = new global::TopiaForge.Multiplayer.LoopbackMultiplayerSession(
            "rate-limit-test",
            () => nowMilliseconds);
        var executions = 0;
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("limited"),
            RequestCodec,
            CounterCodec,
            (_, request) =>
            {
                executions++;
                return OperationResult<CounterValue>.Success(new CounterValue(request.Amount));
            },
            maximumPerSecond: 2)), "register limited command");

        Assert(Submit(session, "limited").Result.Succeeded, "first command is accepted");
        Assert(Submit(session, "limited").Result.Succeeded, "second command is accepted");
        var rejected = Submit(session, "limited");
        Assert(!rejected.Result.Succeeded, "excess command is rejected");
        Assert(rejected.Result.ErrorCode == ModErrorCode.RateLimited, "rate limit has a stable rejection code");
        Assert(executions == 2, "rate-limited command never reaches the handler");

        nowMilliseconds = 1000;
        Assert(Submit(session, "limited").Result.Succeeded, "new fixed window accepts commands");
        Assert(executions == 3, "new-window command executes once");

        var direct = new global::TopiaForge.Multiplayer.LoopbackCommand<AddRequest, CounterValue>(
            new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                CommandType("sender-scoped"),
                RequestCodec,
                CounterCodec,
                (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)),
                maximumPerSecond: 1),
            (_, _) => { });
        var firstSender = new ParticipantId("first");
        var secondSender = new ParticipantId("second");
        Assert(direct.TryAcquire(firstSender, 0).Succeeded, "first sender acquires its allowance");
        Assert(!direct.TryAcquire(firstSender, 0).Succeeded, "first sender exhausts its allowance");
        Assert(direct.TryAcquire(secondSender, 0).Succeeded, "second sender has an independent allowance");
        direct.Dispose();
    }

    internal static void PreAdmissionRejectionsDoNotAdvanceCanonicalTick()
    {
        long nowMilliseconds = 0;
        using var session = new global::TopiaForge.Multiplayer.LoopbackMultiplayerSession(
            "admission-tick-test",
            () => nowMilliseconds);
        var observedTicks = new List<ulong>();
        using var changed = session.SubscribeChanged(snapshot => observedTicks.Add(snapshot.Tick.Value));

        var missing = Submit(session, "missing");
        Assert(!missing.Result.Succeeded && missing.Result.ErrorCode == ModErrorCode.NotFound,
            "an unregistered command is rejected during admission");
        Assert(session.Snapshot.Tick.Value == 0 && observedTicks.Count == 0,
            "an unregistered command cannot advance or publish canonical time");

        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("limited-tick"),
            RequestCodec,
            CounterCodec,
            (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)),
            maximumPerSecond: 1)), "register admission command");

        var wrongPayloadType = session.SubmitAsync(
            new MultiplayerCommandType<MutableValue, CounterValue>("limited-tick"),
            new MutableValue(1)).GetAwaiter().GetResult();
        Assert(!wrongPayloadType.Result.Succeeded &&
               wrongPayloadType.Result.ErrorCode == ModErrorCode.InvalidArgument,
            "a command with mismatched generated payload types is rejected during admission");
        Assert(session.Snapshot.Tick.Value == 0 && observedTicks.Count == 0,
            "a payload-type mismatch cannot advance or publish canonical time");

        var accepted = Submit(session, "limited-tick");
        Assert(accepted.Result.Succeeded && accepted.SubmittedAt.Value == 0 && accepted.ConfirmedAt.Value == 1,
            "an admitted command resolves at its own canonical tick");
        Assert(session.Snapshot.Tick.Value == 1 && observedTicks.SequenceEqual(new ulong[] { 1 }),
            "an admitted command advances and publishes exactly one canonical tick");

        var rateLimited = Submit(session, "limited-tick");
        Assert(!rateLimited.Result.Succeeded && rateLimited.Result.ErrorCode == ModErrorCode.RateLimited,
            "an excess command is rejected before canonical processing");
        Assert(rateLimited.SubmittedAt.Value == 1 && rateLimited.ConfirmedAt.Value == 1 &&
               session.Snapshot.Tick.Value == 1 && observedTicks.SequenceEqual(new ulong[] { 1 }),
            "a rate-limited command cannot advance or publish canonical time");

        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("canonical-rejection"),
            RequestCodec,
            CounterCodec,
            (_, _) => OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "rejected"))),
            "register canonically rejecting command");
        var canonicalRejection = Submit(session, "canonical-rejection");
        Assert(!canonicalRejection.Result.Succeeded && canonicalRejection.ConfirmedAt.Value == 2,
            "a handler rejection still resolves at the admitted command's canonical tick");
        Assert(session.Snapshot.Tick.Value == 2 && observedTicks.SequenceEqual(new ulong[] { 1, 2 }),
            "canonical rollback publishes exactly one settled tick");

        var objectType = new ReplicatedObjectType<CounterValue, AddRequest>("limited-object");
        using var objectRegistration = Require(session.RegisterObjectType(
            new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                objectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) => OperationResult<CounterValue>.Success(
                    new CounterValue(state.Value + input.Amount)),
                maximumPerSecond: 1)),
            "register rate-limited object type");
        var replicatedObject = Require(session.SpawnObject(objectType, new CounterValue(0)),
            "spawn rate-limited object");
        var acceptedInput = replicatedObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(acceptedInput.Result.Succeeded && acceptedInput.ConfirmedAt.Value == 3,
            "an admitted object input advances canonical time");
        var rateLimitedInput = replicatedObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(!rateLimitedInput.Result.Succeeded && rateLimitedInput.Result.ErrorCode == ModErrorCode.RateLimited,
            "an excess object input is rejected during admission");
        Assert(rateLimitedInput.SubmittedAt.Value == 3 && rateLimitedInput.ConfirmedAt.Value == 3 &&
               session.Snapshot.Tick.Value == 3 && observedTicks.SequenceEqual(new ulong[] { 1, 2, 3 }),
            "a rate-limited object input cannot advance or publish canonical time");
    }

    internal static void SessionObserversRunAfterSettlementAndCannotReenterCommands()
    {
        using var session = new global::TopiaForge.Multiplayer.LoopbackMultiplayerSession(
            "observer-order-test",
            () => 0);
        var state = RegisterState(session, "counter", 1);
        var stateNotifications = 0;
        using var stateChanged = state.SubscribeChanged(_ => stateNotifications++);
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("increment"),
            RequestCodec,
            CounterCodec,
            (_, request) => state.Update(value => OperationResult<CounterValue>.Success(
                new CounterValue(value.Value + request.Amount))))), "register observer-order command");
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("rollback"),
            RequestCodec,
            CounterCodec,
            (_, request) =>
            {
                Require(state.Update(value => OperationResult<CounterValue>.Success(
                    new CounterValue(value.Value + request.Amount))), "stage rejected observer-order update");
                return OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "rejected");
            })), "register observer-order rollback command");

        var observations = new List<string>();
        MultiplayerCommandConfirmation<CounterValue>? reentrant = null;
        using var firstObserver = session.SubscribeChanged(snapshot =>
        {
            observations.Add(
                "first:" + snapshot.Tick.Value + ":" + state.Value.Value + ":" + state.Version + ":" + stateNotifications);
            if (snapshot.Tick.Value == 1)
            {
                reentrant = Submit(session, "increment");
            }
        });
        using var secondObserver = session.SubscribeChanged(snapshot =>
            observations.Add(
                "second:" + snapshot.Tick.Value + ":" + state.Value.Value + ":" + state.Version + ":" + stateNotifications));

        var first = Submit(session, "increment");
        Assert(first.Result.Succeeded && state.Value.Value == 3 && state.Version == 1,
            "the outer command commits before its session observers run");
        Assert(reentrant != null && !reentrant.Result.Succeeded &&
               reentrant.Result.ErrorCode == ModErrorCode.InvalidState,
            "a synchronous observer submission cannot reenter command processing");
        Assert(session.Snapshot.Tick.Value == 1 && observations.SequenceEqual(new[]
        {
            "first:1:3:1:1",
            "second:1:3:1:1",
        }), "every observer sees the same settled state for the published tick");

        var second = Submit(session, "increment");
        Assert(second.Result.Succeeded && second.ConfirmedAt.Value == 2 &&
               state.Value.Value == 5 && state.Version == 2,
            "normal command processing resumes after observer dispatch finishes");
        Assert(observations[^2] == "first:2:5:2:2" && observations[^1] == "second:2:5:2:2",
            "the next tick is published in order after its commit");

        var rolledBack = Submit(session, "rollback");
        Assert(!rolledBack.Result.Succeeded && rolledBack.ConfirmedAt.Value == 3,
            "the admitted rejection receives its own canonical resolution tick");
        Assert(state.Value.Value == 5 && state.Version == 2 && stateNotifications == 2,
            "the rejected command is fully rolled back before session observation");
        Assert(observations[^2] == "first:3:5:2:2" && observations[^1] == "second:3:5:2:2",
            "session observers see the settled rollback rather than transactional state");
    }

    internal static void RejectedAndThrowingCommandsRollbackAllStateAndPresentation()
    {
        using var session = new global::TopiaForge.Multiplayer.LoopbackMultiplayerSession(
            "transaction-test",
            () => 0);
        var first = RegisterState(session, "first", 1);
        var second = RegisterState(session, "second", 10);
        var changes = 0;
        var presentations = 0;
        var eventType = new PresentationEventType<TestEvent>("accepted", new JsonTestCodec<TestEvent>());
        using var firstChanged = first.SubscribeChanged(_ => changes++);
        using var secondChanged = second.SubscribeChanged(_ => changes++);
        using var presented = Require(
            session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(eventType, _ => presentations++)),
            "register presentation");

        RegisterMutatingCommand(
            session,
            "handler-rejects",
            first,
            second,
            eventType,
            CounterCodec,
            (_, _) => OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "rejected"));
        var rejected = Submit(session, "handler-rejects");
        Assert(!rejected.Result.Succeeded, "handler rejection is returned");
        AssertRolledBack(first, second, changes, presentations, "handler rejection");

        RegisterMutatingCommand(
            session,
            "response-rejects",
            first,
            second,
            eventType,
            new RejectingCounterCodec(),
            (_, _) => OperationResult<CounterValue>.Success(new CounterValue(99)));
        var responseRejected = Submit(session, "response-rejects");
        Assert(!responseRejected.Result.Succeeded, "response codec rejection is returned");
        AssertRolledBack(first, second, changes, presentations, "response codec rejection");

        RegisterMutatingCommand(
            session,
            "handler-throws",
            first,
            second,
            eventType,
            CounterCodec,
            (_, _) => throw new InvalidOperationException("synthetic failure"));
        var threw = Submit(session, "handler-throws");
        Assert(!threw.Result.Succeeded && threw.Result.ErrorCode == ModErrorCode.Unknown,
            "handler exception becomes a structured rejection");
        AssertRolledBack(first, second, changes, presentations, "handler exception");

        RegisterMutatingCommand(
            session,
            "response-throws",
            first,
            second,
            eventType,
            new ThrowingCounterCodec(),
            (_, _) => OperationResult<CounterValue>.Success(new CounterValue(99)));
        var codecThrew = Submit(session, "response-throws");
        Assert(!codecThrew.Result.Succeeded && codecThrew.Result.ErrorCode == ModErrorCode.Unknown,
            "codec exception becomes a structured rejection");
        AssertRolledBack(first, second, changes, presentations, "codec exception");
    }

    internal static void ReplicatedObjectsEnforceInputAndStateCodecBounds()
    {
        using var session = new global::TopiaForge.Multiplayer.LoopbackMultiplayerSession(
            "object-bounds-test",
            () => 0);
        bool? senderOwnedTarget = null;
        var ownershipType = new ReplicatedObjectType<CounterValue, AddRequest>("ownership-context");
        using var ownershipRegistration = Require(session.RegisterObjectType(
            new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                ownershipType,
                CounterCodec,
                RequestCodec,
                (context, state, input) =>
                {
                    senderOwnedTarget = context.SenderOwnsTarget;
                    return OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount));
                })),
            "register ownership-context object type");
        var ownershipObject = Require(
            session.SpawnObject(ownershipType, new CounterValue(0)),
            "spawn server-owned ownership-context object");
        var serverOwnedInput = ownershipObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(serverOwnedInput.Result.Succeeded && senderOwnedTarget == false,
            "a server-owned object never claims that the authenticated sender owns the target");
        Assert(ownershipObject.TransferOwnership(session.Snapshot.LocalParticipantId).Succeeded,
            "the provider can transfer an object to its local participant");
        var participantOwnedInput = ownershipObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(participantOwnedInput.Result.Succeeded && senderOwnedTarget == true,
            "an exact matching participant owner is reported to the object handler");

        var oversizedInputCodec = new OversizedInputCodec();
        var inputExecutions = 0;
        var inputType = new ReplicatedObjectType<CounterValue, AddRequest>("input-bound");
        using var inputRegistration = Require(session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            inputType,
            CounterCodec,
            oversizedInputCodec,
            (_, state, input) =>
            {
                inputExecutions++;
                return OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount));
            })), "register input-bound object type");
        var inputObject = Require(
            session.SpawnObject(inputType, new CounterValue(0)),
            "spawn input-bound object");
        var inputResult = inputObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(!inputResult.Result.Succeeded && inputResult.Result.ErrorCode == ModErrorCode.InvalidArgument,
            "oversized input is rejected");
        Assert(oversizedInputCodec.DecodeCalls == 0, "oversized input is rejected before decode");
        Assert(inputExecutions == 0 && inputObject.State.Value == 0, "oversized input cannot mutate object state");

        var boundedStateCodec = new ConditionalStateCodec();
        var stateType = new ReplicatedObjectType<CounterValue, AddRequest>("state-bound");
        using var stateRegistration = Require(session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            stateType,
            boundedStateCodec,
            RequestCodec,
            (_, state, input) => OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount)))),
            "register state-bound object type");
        var stateObject = Require(
            session.SpawnObject(stateType, new CounterValue(0)),
            "spawn state-bound object");
        var stateResult = stateObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(!stateResult.Result.Succeeded && stateResult.Result.ErrorCode == ModErrorCode.InvalidArgument,
            "oversized resulting state is rejected");
        Assert(stateObject.State.Value == 0, "rejected resulting state is not installed");

        var throwingType = new ReplicatedObjectType<CounterValue, AddRequest>("throwing-input");
        using var throwingRegistration = Require(session.RegisterObjectType(
            new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                throwingType,
                CounterCodec,
                new ThrowingInputCodec(),
                (_, state, input) => OperationResult<CounterValue>.Success(
                    new CounterValue(state.Value + input.Amount)))),
            "register throwing-input object type");
        var throwingObject = Require(
            session.SpawnObject(throwingType, new CounterValue(0)),
            "spawn throwing-input object");
        var throwingResult = throwingObject.SubmitInputAsync(new AddRequest(1)).GetAwaiter().GetResult();
        Assert(!throwingResult.Result.Succeeded && throwingResult.Result.ErrorCode == ModErrorCode.Unknown,
            "throwing input codec becomes a structured rejection");
        Assert(throwingObject.State.Value == 0 && throwingObject.Version == 0,
            "throwing input codec cannot partially mutate object state");
    }

    internal static void CommandTransactionsRejectReentrantGraphMutation()
    {
        using var session = new global::TopiaForge.Multiplayer.LoopbackMultiplayerSession(
            "reentrancy-test",
            () => 0);
        var objectType = new ReplicatedObjectType<CounterValue, AddRequest>("existing-object");
        using var objectRegistration = Require(session.RegisterObjectType(
            new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                objectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) => OperationResult<CounterValue>.Success(
                    new CounterValue(state.Value + input.Amount)))),
            "register existing object type");
        var existingObject = Require(
            session.SpawnObject(objectType, new CounterValue(0)),
            "spawn existing object");
        var eventType = new PresentationEventType<TestEvent>("existing-event", new JsonTestCodec<TestEvent>());
        using var eventRegistration = Require(
            session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(eventType)),
            "register existing event");
        var rejectedMutations = 0;

        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("reentrant"),
            RequestCodec,
            CounterCodec,
            (_, _) =>
            {
                CountRejected(session.RegisterState(new ReplicatedStateDefinition<CounterValue>(
                    "illegal-state", new CounterValue(0), CounterCodec)), ref rejectedMutations);
                CountRejected(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                    CommandType("illegal-command"), RequestCodec, CounterCodec,
                    (__, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)))),
                    ref rejectedMutations);
                var illegalType = new ReplicatedObjectType<CounterValue, AddRequest>("illegal-type");
                CountRejected(session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                    illegalType, CounterCodec, RequestCodec,
                    (__, state, input) => OperationResult<CounterValue>.Success(
                        new CounterValue(state.Value + input.Amount)))), ref rejectedMutations);
                CountRejected(session.SpawnObject(objectType, new CounterValue(0)), ref rejectedMutations);
                CountRejected(session.DespawnObject(existingObject.Id), ref rejectedMutations);
                CountRejected(existingObject.TransferOwnership(session.Snapshot.LocalParticipantId), ref rejectedMutations);
                var illegalEvent = new PresentationEventType<TestEvent>(
                    "illegal-event", new JsonTestCodec<TestEvent>());
                CountRejected(session.RegisterPresentation(
                    new PresentationEventDefinition<TestEvent>(illegalEvent)), ref rejectedMutations);
                CountRejected(session.PublishPresentation(eventType, new TestEvent("illegal")), ref rejectedMutations);
                var nested = session.SubmitAsync<AddRequest, CounterValue>(
                    CommandType("reentrant"), new AddRequest(1)).GetAwaiter().GetResult();
                if (!nested.Result.Succeeded && nested.Result.ErrorCode == ModErrorCode.InvalidState)
                {
                    rejectedMutations++;
                }

                return OperationResult<CounterValue>.Success(new CounterValue(1));
            })), "register reentrant command");

        var result = Submit(session, "reentrant");
        Assert(result.Result.Succeeded, "outer command remains valid after rejected reentrant calls");
        Assert(rejectedMutations == 9, "every reentrant graph mutation is rejected");
        Assert(existingObject.IsSpawned, "rejected despawn leaves the object discoverable");
        Assert(!existingObject.OwnerId.HasValue, "rejected ownership transfer leaves ownership unchanged");
    }

    internal static void OwnerFacadesIsolateConsumerLifetimes()
    {
        using var provider = new global::TopiaForge.Multiplayer.LoopbackMultiplayerSession(
            "owner-facade-test",
            () => 0);
        using var firstLifetime = new FakeModLifetime();
        using var secondLifetime = new FakeModLifetime();
        var first = (IMultiplayerSession)provider.CreateOwnerFacade(
            typeof(IMultiplayerSession), "first.mod", firstLifetime);
        var second = (IMultiplayerSession)provider.CreateOwnerFacade(
            typeof(IMultiplayerSession), "second.mod", secondLifetime);
        Assert(!ReferenceEquals(first, second), "each consumer receives a distinct owner facade");

        var firstTicks = 0;
        var secondTicks = 0;
        first.SubscribeChanged(_ => firstTicks++);
        second.SubscribeChanged(_ => secondTicks++);

        const string sharedId = "identical-public-id";
        var firstState = Require(first.RegisterState(new ReplicatedStateDefinition<CounterValue>(
            sharedId, new CounterValue(1), CounterCodec)), "register first same-id state");
        var secondState = Require(second.RegisterState(new ReplicatedStateDefinition<CounterValue>(
            sharedId, new CounterValue(100), CounterCodec)), "register second same-id state");
        var firstPresentations = 0;
        var secondPresentations = 0;
        var sharedEvent = new PresentationEventType<TestEvent>(sharedId, new JsonTestCodec<TestEvent>());
        Require(first.RegisterPresentation(new PresentationEventDefinition<TestEvent>(
            sharedEvent,
            value =>
            {
                Assert(value.Message.StartsWith("first", StringComparison.Ordinal),
                    "first event partition receives only first payloads");
                firstPresentations++;
            })), "register first same-id presentation");
        Require(second.RegisterPresentation(new PresentationEventDefinition<TestEvent>(
            sharedEvent,
            value =>
            {
                Assert(value.Message.StartsWith("second", StringComparison.Ordinal),
                    "second event partition receives only second payloads");
                secondPresentations++;
            })), "register second same-id presentation");

        Require(first.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType(sharedId),
            RequestCodec,
            CounterCodec,
            (context, request) =>
            {
                var updated = firstState.Update(value => OperationResult<CounterValue>.Success(
                    new CounterValue(value.Value + request.Amount)));
                if (updated.Succeeded) Require(context.Emit(sharedEvent, new TestEvent("first-command")),
                    "buffer first owner presentation");
                return updated;
            })), "register first same-id command");
        Require(second.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType(sharedId),
            RequestCodec,
            CounterCodec,
            (context, request) =>
            {
                var updated = secondState.Update(value => OperationResult<CounterValue>.Success(
                    new CounterValue(value.Value + (request.Amount * 10))));
                if (updated.Succeeded) Require(context.Emit(sharedEvent, new TestEvent("second-command")),
                    "buffer second owner presentation");
                return updated;
            })), "register second same-id command");

        var firstCommand = Submit(first, sharedId);
        Assert(firstCommand.Result.Succeeded && firstCommand.Result.Value!.Value == 3,
            "first facade submits only to its same-id handler");
        Assert(firstState.Value.Value == 3 && secondState.Value.Value == 100,
            "first command transaction cannot reach second same-id state");
        Assert(firstPresentations == 1 && secondPresentations == 0,
            "first command dispatches only its owner-scoped event");

        var secondCommand = Submit(second, sharedId);
        Assert(secondCommand.Result.Succeeded && secondCommand.Result.Value!.Value == 120,
            "second facade submits only to its same-id handler");
        Assert(firstState.Value.Value == 3 && secondState.Value.Value == 120,
            "second command transaction cannot reach first same-id state");
        Assert(firstPresentations == 1 && secondPresentations == 1,
            "second command dispatches only its owner-scoped event");

        Require(second.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType("second-only"),
            RequestCodec,
            CounterCodec,
            (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)))),
            "register second-only command");
        var crossOwnerSubmission = Submit(first, "second-only");
        Assert(!crossOwnerSubmission.Result.Succeeded && crossOwnerSubmission.Result.ErrorCode == ModErrorCode.NotFound,
            "a facade cannot submit a command registered by another owner");
        var providerSubmission = Submit(provider, sharedId);
        Assert(!providerSubmission.Result.Succeeded && providerSubmission.Result.ErrorCode == ModErrorCode.NotFound,
            "the reserved provider partition cannot submit mod commands");

        var sharedObjectType = new ReplicatedObjectType<CounterValue, AddRequest>(sharedId);
        Require(first.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            sharedObjectType,
            CounterCodec,
            RequestCodec,
            (_, state, input) => OperationResult<CounterValue>.Success(
                new CounterValue(state.Value + input.Amount)))), "register first same-id object type");
        Require(second.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            sharedObjectType,
            CounterCodec,
            RequestCodec,
            (_, state, input) => OperationResult<CounterValue>.Success(
                new CounterValue(state.Value + (input.Amount * 10))))), "register second same-id object type");
        var firstObjectChanges = 0;
        var secondObjectChanges = 0;
        first.SubscribeObjects(sharedObjectType, _ => firstObjectChanges++);
        second.SubscribeObjects(sharedObjectType, _ => secondObjectChanges++);
        var firstObject = Require(first.SpawnObject(sharedObjectType, new CounterValue(5)),
            "spawn first same-id object");
        var secondObject = Require(second.SpawnObject(sharedObjectType, new CounterValue(50)),
            "spawn second same-id object");
        Assert(firstObjectChanges == 1 && secondObjectChanges == 1,
            "same-id object discovery dispatch stays inside each owner partition");
        Assert(first.GetObjects(sharedObjectType).Count == 1 &&
               first.GetObjects(sharedObjectType)[0].Id.Equals(firstObject.Id),
            "first discovery returns only first objects");
        Assert(second.GetObjects(sharedObjectType).Count == 1 &&
               second.GetObjects(sharedObjectType)[0].Id.Equals(secondObject.Id),
            "second discovery returns only second objects");
        Assert(!first.TryGetObject(sharedObjectType, secondObject.Id, out _) &&
               !second.TryGetObject(sharedObjectType, firstObject.Id, out _),
            "cross-owner object lookup is blocked even when ids are known");
        var crossOwnerDespawn = first.DespawnObject(secondObject.Id);
        Assert(!crossOwnerDespawn.Succeeded && crossOwnerDespawn.ErrorCode == ModErrorCode.NotFound && secondObject.IsSpawned,
            "cross-owner object despawn is blocked without revealing ownership");
        Assert(provider.GetObjects(sharedObjectType).Count == 0 &&
               !provider.TryGetObject(sharedObjectType, firstObject.Id, out _),
            "the reserved provider partition cannot discover mod objects");
        var providerDespawn = provider.DespawnObject(firstObject.Id);
        Assert(!providerDespawn.Succeeded && providerDespawn.ErrorCode == ModErrorCode.NotFound,
            "the reserved provider partition cannot despawn mod objects");

        var firstInput = firstObject.SubmitInputAsync(new AddRequest(2)).GetAwaiter().GetResult();
        var secondInput = secondObject.SubmitInputAsync(new AddRequest(2)).GetAwaiter().GetResult();
        Assert(firstInput.Result.Succeeded && firstObject.State.Value == 7,
            "first same-id object uses its owner-scoped input handler");
        Assert(secondInput.Result.Succeeded && secondObject.State.Value == 70,
            "second same-id object uses its owner-scoped input handler");
        Assert(firstObject.TransferOwnership(first.Snapshot.LocalParticipantId).Succeeded &&
               firstObject.OwnerId.Equals(first.Snapshot.LocalParticipantId) &&
               !secondObject.OwnerId.HasValue,
            "ownership changes remain attached to the originating owner's object");
        Assert(first.PublishPresentation(sharedEvent, new TestEvent("first-direct")).Succeeded,
            "first facade publishes its event");
        Assert(firstPresentations == 2 && secondPresentations == 1,
            "direct presentation publishing cannot dispatch across owners");

        var ticksBeforeStopping = secondTicks;
        firstLifetime.Dispose();
        Assert(!firstObject.IsSpawned && secondObject.IsSpawned,
            "stopping one lifetime despawns only its replicated objects");
        Assert(second.GetObjects(sharedObjectType).Count == 1 && secondState.Value.Value == 120,
            "stopping one lifetime preserves the other owner's state and discovery");
        Assert(Submit(second, sharedId).Result.Succeeded && secondState.Value.Value == 140,
            "the other owner's same-id command remains active");
        Assert(second.PublishPresentation(sharedEvent, new TestEvent("second-after-stop")).Succeeded &&
               secondPresentations == 3,
            "the other owner's same-id presentation remains active");
        Assert(firstTicks < secondTicks && secondTicks > ticksBeforeStopping,
            "stopping one facade removes only its session subscription");
    }

    internal static void MutableValuesNeverEscapeCanonicalStorage()
    {
        using var session = new global::TopiaForge.Multiplayer.LoopbackMultiplayerSession(
            "mutable-boundary-test",
            () => 0);
        var initial = new MutableValue(1);
        var state = Require(
            session.RegisterState(new ReplicatedStateDefinition<MutableValue>(
                "mutable-state",
                initial,
                MutableCodec)),
            "register mutable state");
        initial.Value = 100;
        initial.History.Add(100);

        var view = state.Value;
        view.Value = 200;
        view.History.Add(200);
        Assert(state.Value.Value == 1 && state.Value.History.SequenceEqual(new[] { 1 }) && state.Version == 0,
            "mutating a state Value copy cannot alter storage or advance its version");

        var rejected = state.Update(current =>
        {
            current.Value = 300;
            current.History.Add(300);
            return OperationResult<MutableValue>.Failure(ModErrorCode.InvalidArgument, "reject mutation");
        });
        Assert(!rejected.Succeeded && state.Value.Value == 1 && state.Version == 0,
            "a rejected updater cannot mutate canonical state through its input reference");

        var secondObserverValue = -1;
        using var firstObserver = state.SubscribeChanged(value =>
        {
            value.Value = 400;
            value.History.Add(400);
        });
        using var secondObserver = state.SubscribeChanged(value => secondObserverValue = value.Value);
        var updateResult = Require(state.Update(current =>
        {
            current.Value = 2;
            current.History.Add(2);
            return OperationResult<MutableValue>.Success(current);
        }), "update mutable state");
        updateResult.Value = 500;
        updateResult.History.Add(500);
        Assert(secondObserverValue == 2 && state.Value.Value == 2 &&
               state.Value.History.SequenceEqual(new[] { 1, 2 }) && state.Version == 1,
            "update results and subscriber payloads are independent codec copies");

        var commandType = new MultiplayerCommandType<AddRequest, MutableValue>("mutable-command");
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, MutableValue>(
            commandType,
            RequestCodec,
            MutableCodec,
            (_, request) => state.Update(current =>
            {
                current.Value += request.Amount;
                current.History.Add(current.Value);
                return OperationResult<MutableValue>.Success(current);
            }))), "register mutable response command");
        var commandConfirmation = session.SubmitAsync(commandType, new AddRequest(1)).GetAwaiter().GetResult();
        var commandResponse = Require(commandConfirmation.Result, "confirm mutable command");
        commandResponse.Value = 600;
        commandResponse.History.Add(600);
        Assert(state.Value.Value == 3 && state.Value.History.SequenceEqual(new[] { 1, 2, 3 }) && state.Version == 2,
            "mutating a command confirmation cannot mutate the state returned by its handler");

        var objectType = new ReplicatedObjectType<MutableValue, AddRequest>("mutable-object");
        Require(session.RegisterObjectType(new ReplicatedObjectTypeDefinition<MutableValue, AddRequest>(
            objectType,
            MutableCodec,
            RequestCodec,
            (_, current, input) =>
            {
                current.Value += input.Amount;
                current.History.Add(current.Value);
                return OperationResult<MutableValue>.Success(current);
            })), "register mutable object type");
        var secondChangeValue = -1;
        using var firstChanges = session.SubscribeObjects<MutableValue, AddRequest>(objectType, change =>
        {
            change.State.Value = 700;
            change.State.History.Add(700);
        });
        using var secondChanges = session.SubscribeObjects<MutableValue, AddRequest>(
            objectType,
            change => secondChangeValue = change.State.Value);
        var objectInitial = new MutableValue(4);
        var replicatedObject = Require(session.SpawnObject(objectType, objectInitial), "spawn mutable object");
        objectInitial.Value = 800;
        objectInitial.History.Add(800);
        var objectView = replicatedObject.State;
        objectView.Value = 900;
        objectView.History.Add(900);
        Assert(secondChangeValue == 4 && replicatedObject.State.Value == 4 && replicatedObject.Version == 0,
            "object snapshots and State views cannot mutate canonical object storage");

        var objectConfirmation = replicatedObject.SubmitInputAsync(new AddRequest(2)).GetAwaiter().GetResult();
        var confirmedObject = Require(objectConfirmation.Result, "confirm mutable object input");
        confirmedObject.Value = 1000;
        confirmedObject.History.Add(1000);
        Assert(secondChangeValue == 6 && replicatedObject.State.Value == 6 &&
               replicatedObject.State.History.SequenceEqual(new[] { 4, 6 }) && replicatedObject.Version == 1,
            "object confirmations and per-subscriber snapshots remain detached from canonical storage");
    }

    private static void RegisterMutatingCommand(
        IMultiplayerSession session,
        string id,
        IReplicatedState<CounterValue> first,
        IReplicatedState<CounterValue> second,
        PresentationEventType<TestEvent> eventType,
        IMultiplayerCodec<CounterValue> responseCodec,
        Func<MultiplayerCommandContext, AddRequest, OperationResult<CounterValue>> finish)
    {
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            CommandType(id),
            RequestCodec,
            responseCodec,
            (context, request) =>
            {
                Require(first.Update(value => OperationResult<CounterValue>.Success(
                    new CounterValue(value.Value + request.Amount))), "mutate first state");
                Require(second.Update(value => OperationResult<CounterValue>.Success(
                    new CounterValue(value.Value + request.Amount))), "mutate second state");
                Require(context.Emit(eventType, new TestEvent(id)), "buffer presentation");
                return finish(context, request);
            })), "register mutating command");
    }

    private static IReplicatedState<CounterValue> RegisterState(
        IMultiplayerSession session,
        string id,
        int value) =>
        Require(
            session.RegisterState(new ReplicatedStateDefinition<CounterValue>(id, new CounterValue(value), CounterCodec)),
            "register state " + id);

    private static MultiplayerCommandConfirmation<CounterValue> Submit(IMultiplayerSession session, string id) =>
        session.SubmitAsync(CommandType(id), new AddRequest(2)).GetAwaiter().GetResult();

    private static MultiplayerCommandType<AddRequest, CounterValue> CommandType(string id) => new(id);

    private static void AssertRolledBack(
        IReplicatedState<CounterValue> first,
        IReplicatedState<CounterValue> second,
        int changes,
        int presentations,
        string scenario)
    {
        Assert(first.Value.Value == 1 && second.Value.Value == 10, scenario + " restores every state");
        Assert(first.Version == 0 && second.Version == 0, scenario + " restores state versions");
        Assert(changes == 0, scenario + " does not publish transient state notifications");
        Assert(presentations == 0, scenario + " discards buffered presentation");
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value))
        {
            throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        }

        return value;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static void CountRejected<T>(OperationResult<T> result, ref int count) where T : notnull
    {
        if (!result.Succeeded && result.ErrorCode == ModErrorCode.InvalidState) count++;
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

        public OperationResult<byte[]> Encode(CounterValue value) =>
            throw new InvalidOperationException("synthetic codec failure");

        public OperationResult<CounterValue> Decode(byte[] bytes) =>
            throw new InvalidOperationException("unexpected decode");
    }

    private sealed class ThrowingInputCodec : IMultiplayerCodec<AddRequest>
    {
        public int MaximumEncodedBytes => 64;

        public OperationResult<byte[]> Encode(AddRequest value) =>
            throw new InvalidOperationException("synthetic input codec failure");

        public OperationResult<AddRequest> Decode(byte[] bytes) =>
            throw new InvalidOperationException("unexpected decode");
    }

    private sealed class OversizedInputCodec : IMultiplayerCodec<AddRequest>
    {
        public int DecodeCalls { get; private set; }

        public int MaximumEncodedBytes => 1;

        public OperationResult<byte[]> Encode(AddRequest value) =>
            OperationResult<byte[]>.Success(new byte[2]);

        public OperationResult<AddRequest> Decode(byte[] bytes)
        {
            DecodeCalls++;
            return OperationResult<AddRequest>.Success(new AddRequest(1));
        }
    }

    private sealed class ConditionalStateCodec : IMultiplayerCodec<CounterValue>
    {
        public int MaximumEncodedBytes => 1;

        public OperationResult<byte[]> Encode(CounterValue value) =>
            OperationResult<byte[]>.Success(new byte[value.Value == 0 ? 1 : 2]);

        public OperationResult<CounterValue> Decode(byte[] bytes) =>
            OperationResult<CounterValue>.Success(new CounterValue(0));
    }
}
