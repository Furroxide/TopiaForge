using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.CounterSample;
using TopiaForge.Multiplayer.DroneSample;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class MultiplayerRigTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly JsonTestCodec<TestEvent> EventCodec = new();
    private static readonly JsonTestCodec<MutableValue> MutableCodec = new();
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> CounterObjectType =
        new("counter-object");
    private static readonly PresentationEventType<TestEvent> AcceptedEventType =
        new("accepted", EventCodec);
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddCommandType =
        new("add");
    private static readonly ReplicatedObjectType<MutableValue, AddRequest> MutableObjectType =
        new("mutable-object");

    internal static void RolesExposeCorrectSides()
    {
        using var standalone = MultiplayerTestRig.CreateStandalone();
        Assert(standalone.Server.Role == MultiplayerTestRole.Standalone, "standalone role");
        Assert(standalone.Server.Session.Snapshot.HasWorldAuthority, "standalone server side");
        Assert(standalone.Server.Session.Snapshot.HasPresentation, "standalone client side");

        using var listen = MultiplayerTestRig.CreateListenServer();
        Assert(listen.Server.Role == MultiplayerTestRole.ListenServer, "listen role");
        Assert(listen.Server.Session.Snapshot.ExecutionSides ==
            (MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server), "listen logical sides");

        using var dedicated = MultiplayerTestRig.CreateDedicatedServer();
        Assert(dedicated.Server.Role == MultiplayerTestRole.DedicatedServer, "dedicated role");
        Assert(dedicated.Server.Session.Snapshot.ProcessKind == MultiplayerProcessKind.Headless, "dedicated headless");
        Assert(!dedicated.Server.Session.Snapshot.LocalParticipantId.HasValue, "dedicated has no fabricated player");
        var headlessPresentation = dedicated.Server.Session.RegisterPresentation(
            new PresentationEventDefinition<TestEvent>(AcceptedEventType, _ => { }));
        Assert(!headlessPresentation.Succeeded && headlessPresentation.ErrorCode == ModErrorCode.Unavailable,
            "dedicated hosts reject local presentation handlers without fabricating presentation access");

        var client = dedicated.AddRemoteClient("client-a");
        Assert(client.Role == MultiplayerTestRole.RemoteClient, "remote role");
        Assert(!client.IsReady, "client must synchronize before Ready");
        dedicated.Flush();
        Assert(client.IsReady, "client becomes Ready after snapshot");
    }

    internal static void ServerAndTwoClientsConverge()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var serverState = RegisterState(rig.Server.Session, "score", 0);
        var first = rig.AddRemoteClient("first");
        var second = rig.AddRemoteClient("second");
        var firstState = RegisterState(first.Session, "score", -1);
        var secondState = RegisterState(second.Session, "score", -1);
        var firstReadyValue = int.MinValue;
        first.Session.SubscribeChanged(snapshot =>
        {
            if (snapshot.State == MultiplayerSessionState.Ready && firstReadyValue == int.MinValue)
                firstReadyValue = firstState.Value.Value;
        });
        rig.Flush();
        Assert(firstReadyValue == 0, "snapshot must be installed before Ready callback");

        RegisterAddCommand(rig.Server.Session, serverState, rejectLarge: false);
        RegisterAddCommand(first.Session, firstState, rejectLarge: false);
        RegisterAddCommand(second.Session, secondState, rejectLarge: false);
        var submission = first.Session.SubmitAsync(AddCommandType, new AddRequest(2));
        Assert(firstState.Value.Value == 2, "owning client predicts immediately");
        Assert(serverState.Value.Value == 0, "prediction is not canonical");
        rig.Flush();

        var confirmation = submission.GetAwaiter().GetResult();
        Assert(confirmation.Result.Succeeded, "canonical command accepted");
        Assert(confirmation.WasPredicted, "remote owner prediction reported");
        Assert(serverState.Value.Value == 2, "server converged");
        Assert(firstState.Value.Value == 2, "sender converged");
        Assert(secondState.Value.Value == 2, "other client converged");
    }

    internal static void RejectedPredictionRollsBack()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var serverState = RegisterState(rig.Server.Session, "score", 2);
        var client = rig.AddRemoteClient("owner");
        var clientState = RegisterState(client.Session, "score", 0);
        rig.Flush();
        RegisterAddCommand(rig.Server.Session, serverState, rejectLarge: true);
        RegisterAddCommand(client.Session, clientState, rejectLarge: false);

        var submission = client.Session.SubmitAsync(AddCommandType, new AddRequest(100));
        Assert(clientState.Value.Value == 102, "client predicts before validation");
        rig.Flush();
        var confirmation = submission.GetAwaiter().GetResult();
        Assert(!confirmation.Result.Succeeded, "server validation rejects command");
        Assert(confirmation.Result.ErrorCode == ModErrorCode.InvalidArgument, "stable rejection code");
        Assert(clientState.Value.Value == 2, "rejected prediction rolls back");
        Assert(serverState.Value.Value == 2, "rejected prediction never mutates canonical state");
    }

    internal static void ReorderedInputRejectsStaleAndReplays()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(
            new MultiplayerNetworkConditions(reorderEvery: 2));
        var serverState = RegisterState(rig.Server.Session, "score", 0);
        var client = rig.AddRemoteClient("owner");
        var clientState = RegisterState(client.Session, "score", 0);
        rig.Flush(); // Synchronization is packet one; command packet two is deliberately delayed.
        RegisterAddCommand(rig.Server.Session, serverState, rejectLarge: false);
        RegisterAddCommand(client.Session, clientState, rejectLarge: false);

        var first = client.Session.SubmitAsync(AddCommandType, new AddRequest(100));
        var second = client.Session.SubmitAsync(AddCommandType, new AddRequest(3));
        Assert(clientState.Value.Value == 103, "both pending inputs are predicted in order");
        rig.Flush();

        var firstResult = first.GetAwaiter().GetResult();
        var secondResult = second.GetAwaiter().GetResult();
        Assert(!firstResult.Result.Succeeded && firstResult.Result.ErrorCode == ModErrorCode.Conflict,
            "late lower sequence is rejected as stale");
        Assert(secondResult.Result.Succeeded, "newer input is accepted");
        Assert(serverState.Value.Value == 3, "server applies only accepted input");
        Assert(clientState.Value.Value == 3, "rollback/replay converges after out-of-order confirmations");
    }

    internal static void OwnerPredictionAndOwnershipTransfer()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var first = rig.AddRemoteClient("first");
        var second = rig.AddRemoteClient("second");
        RegisterObjectType(rig.Server.Session);
        RegisterObjectType(first.Session);
        RegisterObjectType(second.Session);
        rig.Flush();
        var spawned = rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(0), first.ParticipantId);
        var serverObject = Require(spawned, "server spawns object");
        rig.Flush();
        var firstObject = rig.GetObject<CounterValue, AddRequest>(first, serverObject.Id);
        var secondObject = rig.GetObject<CounterValue, AddRequest>(second, serverObject.Id);

        var accepted = firstObject.SubmitInputAsync(new AddRequest(5));
        Assert(firstObject.State.Value == 5, "owner predicts object input");
        rig.Flush();
        Assert(accepted.GetAwaiter().GetResult().Result.Succeeded, "owner input accepted");
        Assert(serverObject.State.Value == 5 && secondObject.State.Value == 5, "object converges on all peers");

        var rejected = firstObject.SubmitInputAsync(new AddRequest(10));
        Assert(firstObject.State.Value == 15, "pending input predicted before transfer");
        Assert(serverObject.TransferOwnership(second.ParticipantId).Succeeded, "server transfers ownership");
        rig.Flush();
        var rejectedResult = rejected.GetAwaiter().GetResult();
        Assert(!rejectedResult.Result.Succeeded && rejectedResult.Result.ErrorCode == ModErrorCode.NotAuthoritative,
            "server rejects former owner");
        Assert(firstObject.State.Value == 5, "former owner prediction rolls back");
        Assert(firstObject.OwnerId.HasValue && firstObject.OwnerId.Value.Equals(second.ParticipantId!.Value),
            "ownership converges");
    }

    internal static void ObjectInputRejectsStaleAfterReordering()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(
            new MultiplayerNetworkConditions(reorderEvery: 3));
        var client = rig.AddRemoteClient("owner");
        RegisterObjectType(rig.Server.Session);
        RegisterObjectType(client.Session);
        rig.Flush(); // packet one
        var spawned = rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(0), client.ParticipantId);
        var serverObject = Require(spawned, "spawn object for stale-input test");
        rig.Flush(); // packet two
        var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);
        var first = clientObject.SubmitInputAsync(new AddRequest(1)); // packet three delayed
        var second = clientObject.SubmitInputAsync(new AddRequest(2));
        Assert(clientObject.State.Value == 3, "pending object inputs predict in order");
        rig.Flush();
        Assert(!first.GetAwaiter().GetResult().Result.Succeeded, "stale object input rejected");
        Assert(second.GetAwaiter().GetResult().Result.Succeeded, "newer object input accepted");
        Assert(serverObject.State.Value == 2 && clientObject.State.Value == 2, "object rollback/replay converges");
    }

    internal static void ReorderedObjectConfirmationsDoNotDoubleApplyPrediction()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(
            new MultiplayerNetworkConditions(reorderEvery: 5));
        var serverSideState = RegisterState(rig.Server.Session, "response-order-state", 0);
        var client = rig.AddRemoteClient("response-order-owner");
        var clientSideState = RegisterState(client.Session, "response-order-state", 0);
        var objectType = new ReplicatedObjectType<CounterValue, AddRequest>("response-order-object");
        Require(
            rig.Server.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                objectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) =>
                {
                    var updated = serverSideState.Update(current => OperationResult<CounterValue>.Success(
                        new CounterValue(current.Value + input.Amount)));
                    return updated.Succeeded
                        ? OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount))
                        : OperationResult<CounterValue>.Failure(updated.ErrorCode, updated.ErrorMessage);
                },
                PredictionMode.Owner)),
            "register canonical response-order object");
        Require(
            client.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                objectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) =>
                {
                    var updated = clientSideState.Update(current => OperationResult<CounterValue>.Success(
                        new CounterValue(current.Value + input.Amount)));
                    return updated.Succeeded
                        ? OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount))
                        : OperationResult<CounterValue>.Failure(updated.ErrorCode, updated.ErrorMessage);
                },
                PredictionMode.Owner)),
            "register predicted response-order object");
        rig.Flush(); // synchronization is packet one

        var serverObject = Require(
            rig.Server.Session.SpawnObject(objectType, new CounterValue(0), client.ParticipantId),
            "spawn response-order object");
        rig.Flush(); // spawn is packet two
        var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);
        var first = clientObject.SubmitInputAsync(new AddRequest(1)); // request packet three
        var second = clientObject.SubmitInputAsync(new AddRequest(2)); // request packet four
        Assert(clientObject.State.Value == 3 && clientSideState.Value.Value == 3,
            "both object transactions predict before either confirmation");

        rig.Advance(); // both requests execute canonically; response packet five is delayed
        rig.Advance(); // response packet six confirms the second input first
        Assert(!first.IsCompleted && second.IsCompleted,
            "the deterministic transport delivers the higher confirmation first");
        Assert(clientObject.State.Value == 3 && clientSideState.Value.Value == 3,
            "the cumulative confirmation does not replay an already-incorporated lower prediction");

        rig.Advance();
        Assert(first.GetAwaiter().GetResult().Result.Succeeded &&
               second.GetAwaiter().GetResult().Result.Succeeded &&
               serverObject.State.Value == 3 && clientObject.State.Value == 3 &&
               serverSideState.Value.Value == 3 && clientSideState.Value.Value == 3,
            "the delayed lower confirmation remains idempotent across object and related state");
    }

    internal static void ObjectTombstonesDominateDelayedSpawnAndChangeSnapshots()
    {
        using (var rig = MultiplayerTestRig.CreateDedicatedServer(
                   new MultiplayerNetworkConditions(reorderEvery: 2)))
        {
            var client = rig.AddRemoteClient("delayed-spawn-observer");
            RegisterObjectType(rig.Server.Session);
            RegisterObjectType(client.Session);
            rig.Flush(); // synchronization is packet one
            var changes = new List<ReplicatedObjectChange<CounterValue, AddRequest>>();
            using var subscription = client.Session.SubscribeObjects(CounterObjectType, changes.Add);

            var serverObject = Require(
                rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(4), client.ParticipantId),
                "spawn object whose packet is delayed"); // packet two is delayed
            Assert(rig.Server.Session.DespawnObject(serverObject.Id).Succeeded,
                "despawn before the delayed spawn packet arrives"); // packet three arrives first

            rig.Advance();
            Assert(client.Session.GetObjects(CounterObjectType).Count == 0,
                "an early tombstone records absence before spawn discovery");
            rig.Advance();
            Assert(client.Session.GetObjects(CounterObjectType).Count == 0 && changes.Count == 0,
                "a delayed spawn snapshot cannot resurrect a tombstoned network id");
        }

        using (var rig = MultiplayerTestRig.CreateDedicatedServer(
                   new MultiplayerNetworkConditions(reorderEvery: 3)))
        {
            var client = rig.AddRemoteClient("delayed-change-observer");
            RegisterObjectType(rig.Server.Session);
            RegisterObjectType(client.Session);
            rig.Flush(); // synchronization is packet one
            var serverObject = Require(
                rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(7), client.ParticipantId),
                "spawn object before delayed change test"); // packet two arrives normally
            rig.Flush();
            var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);
            var changes = new List<ReplicatedObjectChange<CounterValue, AddRequest>>();
            using var subscription = client.Session.SubscribeObjects(CounterObjectType, changes.Add);

            Assert(serverObject.TransferOwnership(null).Succeeded,
                "schedule a canonical change packet"); // packet three is delayed
            Assert(rig.Server.Session.DespawnObject(serverObject.Id).Succeeded,
                "schedule a tombstone after the delayed change"); // packet four arrives first

            rig.Advance();
            var changesAfterTombstone = changes.Count;
            Assert(!clientObject.IsSpawned && client.Session.GetObjects(CounterObjectType).Count == 0 &&
                   changesAfterTombstone > 0 && changes[^1].Kind == ReplicatedObjectChangeKind.Despawned,
                "the tombstone retires the existing handle before the delayed change " +
                "(spawned=" + clientObject.IsSpawned +
                ", objects=" + client.Session.GetObjects(CounterObjectType).Count +
                ", changes=" + changes.Count + ")");
            rig.Advance();
            Assert(!clientObject.IsSpawned && client.Session.GetObjects(CounterObjectType).Count == 0 &&
                   changes.Count == changesAfterTombstone,
                "a delayed change snapshot cannot recreate a tombstoned object");
        }
    }

    internal static void LateJoinAndReconnectSnapshotPrecedesReady()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var serverState = RegisterState(rig.Server.Session, "world", 0);
        RegisterObjectType(rig.Server.Session);
        Assert(serverState.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(7))).Succeeded,
            "server establishes canonical state");
        var spawned = rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(11));
        var serverObject = Require(spawned, "server establishes canonical object");

        var client = rig.AddRemoteClient("late");
        RegisterObjectType(client.Session);
        var clientState = RegisterState(client.Session, "world", -1);
        var readyChecks = 0;
        client.Session.SubscribeChanged(snapshot =>
        {
            if (snapshot.State != MultiplayerSessionState.Ready || readyChecks != 0) return;
            Assert(clientState.Value.Value == 7, "late-join state precedes Ready");
            Assert(rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id).State.Value == 11,
                "late-join object precedes Ready");
            readyChecks++;
        });
        rig.Flush();
        Assert(readyChecks == 1, "late join emits Ready after complete snapshot");

        Assert(serverState.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(9))).Succeeded,
            "server advances state before reconnect");
        var reconnectReady = false;
        client.Session.SubscribeChanged(snapshot =>
        {
            if (snapshot.State != MultiplayerSessionState.Ready || reconnectReady) return;
            Assert(clientState.Value.Value == 9, "reconnect snapshot precedes Ready");
            reconnectReady = true;
        });
        rig.Reconnect(client);
        rig.Flush();
        Assert(reconnectReady, "reconnect returns to Ready");
    }

    internal static void PacketFaultsConvergeWithoutDoubleExecution()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(
            new MultiplayerNetworkConditions(
                latencyTicks: 2,
                dropEvery: 2,
                duplicateEvery: 3,
                reorderEvery: 4,
                retryDelayTicks: 2));
        var serverState = RegisterState(rig.Server.Session, "score", 0);
        var client = rig.AddRemoteClient("faulty-link");
        var observer = rig.AddRemoteClient("observer");
        var clientState = RegisterState(client.Session, "score", 0);
        var observerState = RegisterState(observer.Session, "score", 0);
        rig.Flush();
        var executions = 0;
        var observerNotifications = new List<int>();
        observerState.SubscribeChanged(value => observerNotifications.Add(value.Value));
        RegisterAddCommand(rig.Server.Session, serverState, rejectLarge: false, () => executions++);
        RegisterAddCommand(client.Session, clientState, rejectLarge: false);
        RegisterAddCommand(observer.Session, observerState, rejectLarge: false);

        for (var index = 0; index < 4; index++)
        {
            var submission = client.Session.SubmitAsync(AddCommandType, new AddRequest(1));
            rig.Flush();
            Assert(submission.GetAwaiter().GetResult().Result.Succeeded, "reliable retry confirms packet-fault command");
        }

        Assert(executions == 4, "duplicates never double-execute canonical handlers");
        Assert(serverState.Value.Value == 4, "faulted server state");
        Assert(clientState.Value.Value == 4, "faulted sender convergence");
        Assert(observerState.Value.Value == 4, "faulted observer convergence");
        Assert(observerNotifications.SequenceEqual(new[] { 1, 2, 3, 4 }),
            "duplicated and reordered equal-version snapshots notify canonical observers exactly once per version");
    }

    internal static void ListenHostExecutesCanonicalHandlerOnce()
    {
        using var rig = MultiplayerTestRig.CreateListenServer();
        var state = RegisterState(rig.Server.Session, "score", 0);
        var executions = 0;
        var presentations = 0;
        Require(
            rig.Server.Session.RegisterPresentation(
                new PresentationEventDefinition<TestEvent>(AcceptedEventType, _ => presentations++)),
            "register listen-host presentation event");
        RegisterAddCommand(
            rig.Server.Session,
            state,
            rejectLarge: false,
            () => executions++,
            emitEvent: AcceptedEventType);
        var submission = rig.Server.Session.SubmitAsync(AddCommandType, new AddRequest(1));
        rig.Flush();
        var result = submission.GetAwaiter().GetResult();
        Assert(result.Result.Succeeded, "listen-host command accepted");
        Assert(!result.WasPredicted, "combined logical sides do not run a second prediction pass");
        Assert(executions == 1, "listen host executes canonical handler once");
        Assert(state.Value.Value == 1, "listen host mutates once");
        Assert(presentations == 1, "accepted presentation dispatches once");
    }

    internal static void GeneratedCounterSampleConverges()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var serverContract = new CounterSampleMod();
        using var serverBinding = Require(
            serverContract.BindMultiplayer(rig.Server.Session),
            "bind generated counter contract on server");
        var client = rig.AddRemoteClient("counter-owner");
        var clientContract = new CounterSampleMod();
        using var clientBinding = Require(
            clientContract.BindMultiplayer(client.Session),
            "bind generated counter contract on client");
        rig.Flush();

        var submission = clientContract.SubmitIncrementAsync(
            new CounterRequest { Amount = 3 });
        rig.Flush();
        var confirmation = submission.GetAwaiter().GetResult();
        Assert(confirmation.Result.TryGetValue(out var response) && response.Value == 3,
            "generated counter command returns canonical state");
        Assert(confirmation.WasPredicted, "generated counter command uses owner prediction");
        Assert(serverContract.LastPresentedValue == 0,
            "headless server never receives presentation callbacks");
        Assert(clientContract.LastPresentedValue == 3,
            "generated accepted presentation proxy reaches the interactive client");
    }

    internal static void GeneratedDroneSamplePredicts()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var serverContract = new DroneSampleMod();
        using var serverBinding = Require(
            serverContract.BindMultiplayer(rig.Server.Session),
            "bind generated drone codecs on server");
        var client = rig.AddRemoteClient("drone-owner");
        var clientContract = new DroneSampleMod();
        using var clientBinding = Require(
            clientContract.BindMultiplayer(client.Session),
            "bind generated drone codecs on client");
        rig.Flush();

        var spawned = serverContract.SpawnDroneObject(
            new DroneState { Callsign = "TF-1" },
            client.ParticipantId);
        var serverObject = Require(spawned, "spawn generated drone definition");
        rig.Flush();
        var clientObject = rig.GetObject<DroneState, DroneInput>(client, serverObject.Id);
        var submission = clientObject.SubmitInputAsync(
            new DroneInput { Horizontal = 2, Vertical = -1 });
        Assert(clientObject.State.X == 2 && clientObject.State.Y == -1,
            "generated drone codecs support immediate owner prediction");
        rig.Flush();
        Assert(submission.GetAwaiter().GetResult().Result.Succeeded,
            "generated drone input is accepted canonically");
        Assert(serverObject.State.X == 2 && serverObject.State.Y == -1,
            "generated replicated object converges on the server");
    }

    internal static void ObjectDiscoveryChangeAndDespawnAreTyped()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var client = rig.AddRemoteClient("observer");
        RegisterObjectType(rig.Server.Session);
        RegisterObjectType(client.Session);
        rig.Flush();
        var changes = new List<ReplicatedObjectChange<CounterValue, AddRequest>>();
        using var subscription = client.Session.SubscribeObjects(CounterObjectType, changes.Add);

        var serverObject = Require(
            rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(4), client.ParticipantId),
            "spawn discoverable object");
        rig.Flush();
        Assert(changes.Count == 1 && changes[0].Kind == ReplicatedObjectChangeKind.Spawned,
            "client receives a typed discovery notification");
        var clientObject = changes[0].Object!;
        Assert(!ReferenceEquals(serverObject, clientObject) && clientObject.State.Value == 4,
            "client reconstructs a distinct local handle from its registered codec");

        var update = clientObject.SubmitInputAsync(new AddRequest(2));
        rig.Flush();
        Assert(update.GetAwaiter().GetResult().Result.Succeeded &&
               changes.Any(item => item.Kind == ReplicatedObjectChangeKind.Changed && item.State.Value == 6),
            "canonical state changes notify typed subscribers");

        Assert(rig.Server.Session.DespawnObject(serverObject.Id).Succeeded, "server despawns canonically");
        rig.Flush();
        Assert(changes[^1].Kind == ReplicatedObjectChangeKind.Despawned && changes[^1].Object == null,
            "client receives an explicit typed despawn notification");
        Assert(!clientObject.IsSpawned, "retained handles expose their despawned state");
        Assert(!client.Session.TryGetObject(CounterObjectType, serverObject.Id, out _) &&
               client.Session.GetObjects(CounterObjectType).Count == 0,
            "despawned objects leave client discovery queries");
    }

    internal static void PresentationEventsRoundTripBoundedBytes()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var client = rig.AddRemoteClient("viewer");
        var serverType = new PresentationEventType<TestEvent>("notice", EventCodec);
        var clientType = new PresentationEventType<TestEvent>("notice", new JsonTestCodec<TestEvent>());
        TestEvent? received = null;
        Require(
            rig.Server.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(serverType)),
            "register server presentation codec");
        Require(
            client.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(clientType, value => received = value)),
            "register client presentation codec");
        rig.Flush();

        var sent = new TestEvent("wire-copy");
        Assert(rig.Server.Session.PublishPresentation(serverType, sent).Succeeded,
            "canonical server publishes a bounded event");
        rig.Flush();
        Assert(received != null && received.Message == "wire-copy" && !ReferenceEquals(sent, received),
            "receiver reconstructs presentation payload from its local codec");

        var oversizedType = new PresentationEventType<TestEvent>(
            "oversized",
            new OversizedTestCodec<TestEvent>());
        Require(
            rig.Server.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(oversizedType)),
            "register deliberately dishonest event codec");
        var rejected = rig.Server.Session.PublishPresentation(oversizedType, sent);
        Assert(!rejected.Succeeded && rejected.ErrorCode == ModErrorCode.InvalidArgument,
            "providers reject codecs that return bytes beyond their declared bound");
        var callbackInvoked = false;
        var context = new MultiplayerCommandContext(
            new ParticipantId("viewer"),
            rig.Tick,
            CancellationToken.None,
            (_, _, _) =>
            {
                callbackInvoked = true;
                return OperationResult<bool>.Success(true);
            });
        Assert(!context.Emit(oversizedType, sent).Succeeded && !callbackInvoked,
            "buffered command events reject oversized bytes before reaching a transport");
    }

    internal static void FailingHandlersAndCodecsRollbackTransactionally()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var serverState = RegisterState(rig.Server.Session, "failure-state", 0);
        var client = rig.AddRemoteClient("failure-client");
        var clientState = RegisterState(client.Session, "failure-state", 0);
        var commandType = new MultiplayerCommandType<AddRequest, CounterValue>("failure-command");
        var eventType = new PresentationEventType<TestEvent>("failure-event", EventCodec);
        var presentations = 0;
        Require(
            rig.Server.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(eventType)),
            "register failure event on the server");
        Require(
            client.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(eventType, _ => presentations++)),
            "register failure event on the client");
        rig.Flush();

        var canonicalObservations = new List<int>();
        serverState.SubscribeChanged(value => canonicalObservations.Add(value.Value));
        Require(
            rig.Server.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                commandType,
                RequestCodec,
                CounterCodec,
                (context, request) =>
                {
                    Require(
                        serverState.Update(current => OperationResult<CounterValue>.Success(
                            new CounterValue(current.Value + request.Amount))),
                        "mutate inside failing canonical command");
                    Require(context.Emit(eventType, new TestEvent("must-not-deliver")), "buffer rejected presentation");
                    if (request.Amount == 2) throw new OperationCanceledException("synthetic cancellation");
                    throw new InvalidOperationException("synthetic handler failure");
                },
                PredictionMode.Owner)),
            "register failing canonical command");
        Require(
            client.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                commandType,
                RequestCodec,
                CounterCodec,
                (_, request) => clientState.Update(current =>
                    OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount))),
                PredictionMode.Owner)),
            "register predicting client command");

        var throwing = client.Session.SubmitAsync(commandType, new AddRequest(1));
        Assert(clientState.Value.Value == 1, "failing command predicts locally");
        rig.Flush();
        var throwingResult = throwing.GetAwaiter().GetResult();
        Assert(!throwingResult.Result.Succeeded && throwingResult.Result.ErrorCode == ModErrorCode.Unknown,
            "thrown handlers become structured failures");
        Assert(serverState.Value.Value == 0 && clientState.Value.Value == 0,
            "thrown handlers roll back canonical and predicted state");
        Assert(canonicalObservations.Count == 0 && presentations == 0,
            "rejected canonical state and buffered presentation effects are never observed");

        var cancelled = client.Session.SubmitAsync(commandType, new AddRequest(2));
        rig.Flush();
        var cancelledResult = cancelled.GetAwaiter().GetResult();
        Assert(!cancelledResult.Result.Succeeded && cancelledResult.Result.ErrorCode == ModErrorCode.Cancelled,
            "handler cancellation becomes the stable cancellation result");
        Assert(serverState.Value.Value == 0 && clientState.Value.Value == 0 && canonicalObservations.Count == 0,
            "cancelled handlers also roll back without canonical notifications");

        using (var cancellation = new CancellationTokenSource())
        {
            var externallyCancelled = client.Session.SubmitAsync(
                commandType,
                new AddRequest(3),
                cancellation.Token);
            Assert(clientState.Value.Value == 3, "cancellable command predicts before transport delivery");
            cancellation.Cancel();
            var externalResult = externallyCancelled.GetAwaiter().GetResult();
            Assert(externalResult.Result.ErrorCode == ModErrorCode.Cancelled && clientState.Value.Value == 0,
                "caller cancellation completes pending work and rolls back prediction");
            rig.Flush();
            Assert(serverState.Value.Value == 0 && canonicalObservations.Count == 0,
                "a cancelled queued request never reaches canonical execution");
        }

        var codecType = new MultiplayerCommandType<AddRequest, CounterValue>("throwing-codec-command");
        Require(
            client.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                codecType,
                new ThrowingTestCodec<AddRequest>(),
                CounterCodec,
                (_, _) => OperationResult<CounterValue>.Success(new CounterValue(99)))),
            "register throwing request codec");
        var codecFailure = client.Session.SubmitAsync(codecType, new AddRequest(1)).GetAwaiter().GetResult();
        Assert(!codecFailure.Result.Succeeded && codecFailure.Result.ErrorCode == ModErrorCode.Unknown,
            "codec exceptions become structured failures without stranding a pending task");
    }

    internal static void ObjectInputsTransactionallyIncludeReplicatedState()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var serverSideState = RegisterState(rig.Server.Session, "object-side-state", 0);
        var client = rig.AddRemoteClient("object-transaction-owner");
        var clientSideState = RegisterState(client.Session, "object-side-state", 0);
        var objectType = new ReplicatedObjectType<CounterValue, AddRequest>("transaction-object");
        var canonicalObservations = new List<int>();
        serverSideState.SubscribeChanged(value => canonicalObservations.Add(value.Value));

        Require(
            rig.Server.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                objectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) =>
                {
                    var updated = serverSideState.Update(current => OperationResult<CounterValue>.Success(
                        new CounterValue(current.Value + input.Amount)));
                    if (!updated.Succeeded) return updated;
                    if (input.Amount == 10)
                        return OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "synthetic reject");
                    if (input.Amount == 99) throw new InvalidOperationException("synthetic object failure");
                    return OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount));
                },
                PredictionMode.Owner)),
            "register canonical transactional object");
        Require(
            client.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                objectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) =>
                {
                    var updated = clientSideState.Update(current => OperationResult<CounterValue>.Success(
                        new CounterValue(current.Value + input.Amount)));
                    return updated.Succeeded
                        ? OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount))
                        : OperationResult<CounterValue>.Failure(updated.ErrorCode, updated.ErrorMessage);
                },
                PredictionMode.Owner)),
            "register predicted transactional object");
        rig.Flush();

        var serverObject = Require(
            rig.Server.Session.SpawnObject(objectType, new CounterValue(0), client.ParticipantId),
            "spawn transactional object");
        rig.Flush();
        var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);

        var accepted = clientObject.SubmitInputAsync(new AddRequest(2));
        Assert(clientObject.State.Value == 2 && clientSideState.Value.Value == 2,
            "object and related state predict in one transaction");
        rig.Flush();
        Assert(accepted.GetAwaiter().GetResult().Result.Succeeded &&
               serverObject.State.Value == 2 && serverSideState.Value.Value == 2 &&
               clientObject.State.Value == 2 && clientSideState.Value.Value == 2,
            "accepted object and related state converge together");
        Assert(canonicalObservations.SequenceEqual(new[] { 2 }),
            "accepted canonical state publishes exactly once");

        var rejected = clientObject.SubmitInputAsync(new AddRequest(10));
        Assert(clientObject.State.Value == 12 && clientSideState.Value.Value == 12,
            "rejected object input is initially predicted across both values");
        rig.Flush();
        Assert(rejected.GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.InvalidArgument &&
               serverObject.State.Value == 2 && serverSideState.Value.Value == 2 &&
               clientObject.State.Value == 2 && clientSideState.Value.Value == 2,
            "rejected object transaction rolls back both values");
        Assert(canonicalObservations.SequenceEqual(new[] { 2 }),
            "rejected canonical object-side state remains invisible to observers");

        var throwing = clientObject.SubmitInputAsync(new AddRequest(99));
        rig.Flush();
        Assert(throwing.GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.Unknown &&
               serverObject.State.Value == 2 && serverSideState.Value.Value == 2 &&
               clientObject.State.Value == 2 && clientSideState.Value.Value == 2,
            "throwing object transactions complete with rollback across object and state");
        Assert(canonicalObservations.SequenceEqual(new[] { 2 }),
            "throwing canonical state changes remain buffered and suppressed");

        var firstPending = clientObject.SubmitInputAsync(new AddRequest(1));
        var secondPending = clientObject.SubmitInputAsync(new AddRequest(1));
        Assert(clientObject.State.Value == 4 && clientSideState.Value.Value == 4,
            "multiple pending object transactions predict in order");
        rig.Flush();
        Assert(firstPending.GetAwaiter().GetResult().Result.Succeeded &&
               secondPending.GetAwaiter().GetResult().Result.Succeeded &&
               serverObject.State.Value == 4 && serverSideState.Value.Value == 4 &&
               clientObject.State.Value == 4 && clientSideState.Value.Value == 4,
            "canonical confirmation rolls back and replays remaining cross-state object predictions");
    }

    internal static void PartialStateDeltasReplayAllPendingPredictions()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(
            new MultiplayerNetworkConditions(latencyTicks: 3));
        var serverDelta = RegisterState(rig.Server.Session, "delta-source", 0);
        var serverShared = RegisterState(rig.Server.Session, "delta-shared", 0);
        var client = rig.AddRemoteClient("delta-owner");
        var clientDelta = RegisterState(client.Session, "delta-source", 0);
        var clientShared = RegisterState(client.Session, "delta-shared", 0);
        var commandType = new MultiplayerCommandType<AddRequest, CounterValue>("delta-command");
        var objectType = new ReplicatedObjectType<CounterValue, AddRequest>("delta-object");

        Require(
            rig.Server.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                commandType,
                RequestCodec,
                CounterCodec,
                (_, request) => serverShared.Update(current => OperationResult<CounterValue>.Success(
                    new CounterValue((current.Value * 10) + request.Amount))),
                PredictionMode.Owner)),
            "register canonical delta command");
        Require(
            client.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                commandType,
                RequestCodec,
                CounterCodec,
                (_, request) => clientShared.Update(current => OperationResult<CounterValue>.Success(
                    new CounterValue((current.Value * 10) + request.Amount))),
                PredictionMode.Owner)),
            "register predicted delta command");
        Require(
            rig.Server.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                objectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) =>
                {
                    var updated = serverShared.Update(current => OperationResult<CounterValue>.Success(
                        new CounterValue(current.Value + input.Amount)));
                    return updated.Succeeded
                        ? OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount))
                        : OperationResult<CounterValue>.Failure(updated.ErrorCode, updated.ErrorMessage);
                },
                PredictionMode.Owner)),
            "register canonical delta object");
        Require(
            client.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                objectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) =>
                {
                    var updated = clientShared.Update(current => OperationResult<CounterValue>.Success(
                        new CounterValue(current.Value + input.Amount)));
                    return updated.Succeeded
                        ? OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount))
                        : OperationResult<CounterValue>.Failure(updated.ErrorCode, updated.ErrorMessage);
                },
                PredictionMode.Owner)),
            "register predicted delta object");
        rig.Flush();

        var serverObject = Require(
            rig.Server.Session.SpawnObject(objectType, new CounterValue(0), client.ParticipantId),
            "spawn partial-delta object");
        rig.Flush();
        var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);
        var command = client.Session.SubmitAsync(commandType, new AddRequest(1));
        var objectInput = clientObject.SubmitInputAsync(new AddRequest(2));
        Assert(clientShared.Value.Value == 3 && clientObject.State.Value == 2,
            "ordinary and object-input predictions compose before the delta");

        Require(
            serverDelta.Update(current => OperationResult<CounterValue>.Success(
                new CounterValue(current.Value + 1))),
            "publish unrelated partial state delta");
        rig.Advance(3);

        Assert(!command.IsCompleted && !objectInput.IsCompleted,
            "the partial delta arrives while both predicted operations remain pending");
        Assert(clientDelta.Value.Value == 1,
            "the included state applies from the partial delta");
        Assert(clientShared.Value.Value == 3 && clientObject.State.Value == 2,
            "an absent state is rebuilt from both pending prediction kinds in submission order instead of reset");

        rig.Flush();
        Assert(command.GetAwaiter().GetResult().Result.Succeeded &&
               objectInput.GetAwaiter().GetResult().Result.Succeeded &&
               serverShared.Value.Value == 3 && clientShared.Value.Value == 3 &&
               serverObject.State.Value == 2 && clientObject.State.Value == 2,
            "full confirmations converge after partial-delta reconciliation");
    }

    internal static void PresentationDeliveryIsReadyBoundAtMostOnceAndConnectionScoped()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(
            new MultiplayerNetworkConditions(latencyTicks: 2, duplicateEvery: 1, reorderEvery: 2));
        var eventType = new PresentationEventType<TestEvent>("faulted-presentation", EventCodec);
        Require(
            rig.Server.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(eventType)),
            "register canonical presentation");
        var client = rig.AddRemoteClient("presentation-client");
        var deliveries = 0;
        Require(
            client.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(eventType, _ => deliveries++)),
            "register local presentation");

        Assert(rig.Server.Session.PublishPresentation(eventType, new TestEvent("before-ready")).Succeeded,
            "publishing while a client synchronizes is valid");
        rig.Flush();
        Assert(client.IsReady && deliveries == 0,
            "transient presentation is dropped rather than delivered before Ready");

        Assert(rig.Server.Session.PublishPresentation(eventType, new TestEvent("deduplicated")).Succeeded,
            "publish accepted transient under packet faults");
        rig.Flush();
        Assert(deliveries == 1, "duplicate and reordered packets invoke an accepted presentation exactly once");

        Assert(rig.Server.Session.PublishPresentation(eventType, new TestEvent("stale-generation")).Succeeded,
            "schedule an event on the old connection generation");
        rig.Disconnect(client);
        rig.Reconnect(client);
        rig.Flush();
        Assert(client.IsReady && deliveries == 1,
            "events scheduled before disconnect cannot leak into the reconnected Ready generation");
    }

    internal static void DisconnectReconnectRefreshesParticipantsOwnershipAndObjects()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(new MultiplayerNetworkConditions(latencyTicks: 2));
        var client = rig.AddRemoteClient("lifecycle-client");
        RegisterObjectType(rig.Server.Session);
        RegisterObjectType(client.Session);
        rig.Flush();
        var serverObject = Require(
            rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(5), client.ParticipantId),
            "spawn lifecycle object");
        rig.Flush();
        var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);
        var pending = clientObject.SubmitInputAsync(new AddRequest(1));

        rig.Disconnect(client);
        var disconnected = rig.Server.Session.Snapshot.Participants.Single(item => item.Id.Value == "lifecycle-client");
        Assert(!disconnected.IsConnected && !client.Session.Snapshot.LocalParticipantId.HasValue,
            "disconnect updates participant connectivity without fabricating a connected local participant");
        Assert(!serverObject.OwnerId.HasValue, "disconnect safely releases canonical object ownership");
        Assert(pending.GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.Cancelled,
            "disconnect completes pending predicted object input as cancelled");
        Assert(rig.Server.Session.DespawnObject(serverObject.Id).Succeeded,
            "server may despawn while the client is disconnected");

        rig.Reconnect(client);
        rig.Flush();
        var reconnected = rig.Server.Session.Snapshot.Participants.Single(item => item.Id.Value == "lifecycle-client");
        Assert(reconnected.IsConnected && client.IsReady,
            "reconnect restores connected participant state only after synchronization");
        Assert(!clientObject.IsSpawned && !client.Session.TryGetObject(CounterObjectType, serverObject.Id, out _),
            "reconnect exact-snapshot replacement retires objects despawned while disconnected");
    }

    internal static void SequentialSessionsReuseFacadeAndResetSessionState()
    {
        using var rig = MultiplayerTestRig.CreateStandalone();
        var facade = rig.Server.Session;
        var state = RegisterState(facade, "sequential-state", 0);
        RegisterAddCommand(facade, state, rejectLarge: false);
        RegisterObjectType(facade);
        var presentationCount = 0;
        Require(
            facade.RegisterPresentation(new PresentationEventDefinition<TestEvent>(
                AcceptedEventType,
                _ => presentationCount++)),
            "register sequential-session presentation");
        var objectNotifications = 0;
        using var oldObjectSubscription = facade.SubscribeObjects<CounterValue, AddRequest>(
            CounterObjectType,
            _ => objectNotifications++);

        var oldToken = facade.CurrentSessionToken;
        var oldSessionId = facade.Snapshot.Id;
        var oldObject = Require(
            facade.SpawnObject(CounterObjectType, new CounterValue(7), facade.Snapshot.LocalParticipantId),
            "spawn object in first sequential session");
        var first = facade.SubmitAsync(AddCommandType, new AddRequest(1)).GetAwaiter().GetResult();
        Assert(first.Result.Succeeded && state.Value.Value == 1 && objectNotifications == 1,
            "first session uses its registered state, command, and object type");

        rig.StartNewSession("topiaforge-test-session-two");
        Assert(oldToken.IsCancellationRequested && !facade.CurrentSessionToken.IsCancellationRequested,
            "session replacement cancels only the old current-session token");
        Assert(!facade.Snapshot.Id.Equals(oldSessionId) &&
               facade.Snapshot.Id.Equals(new MultiplayerSessionId("topiaforge-test-session-two")),
            "the stable facade reports the replacement session identity");
        Assert(state.Value.Value == 0 && state.Version == 0,
            "replicated state resets to its immutable declared default for the new session");
        Assert(!oldObject.IsSpawned && facade.GetObjects(CounterObjectType).Count == 0,
            "session-scoped object handles never leak into the replacement match");

        var second = facade.SubmitAsync(AddCommandType, new AddRequest(2)).GetAwaiter().GetResult();
        var newObject = Require(
            facade.SpawnObject(CounterObjectType, new CounterValue(3), facade.Snapshot.LocalParticipantId),
            "reuse generated object registration in replacement session");
        Assert(second.Result.Succeeded && state.Value.Value == 2 && newObject.IsSpawned,
            "command and object registrations survive without duplicate author registration");
        Assert(objectNotifications == 1,
            "object discovery subscriptions owned by the old session do not leak into its replacement");
        Assert(facade.PublishPresentation(AcceptedEventType, new TestEvent("new-session")).Succeeded &&
               presentationCount == 0,
            "standalone presentation remains transport-scheduled until the deterministic rig advances");
        rig.Flush();
        Assert(presentationCount == 1,
            "presentation registration is reapplied to the replacement session");
    }

    internal static void SequentialSessionResetObserversSeeCoherentMetadata()
    {
        using var rig = MultiplayerTestRig.CreateStandalone();
        var facade = rig.Server.Session;
        var state = RegisterState(facade, "observer-reset-state", 4);
        Require(
            state.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(9))),
            "advance state before observer reset");
        var oldToken = facade.CurrentSessionToken;
        var oldSessionId = facade.Snapshot.Id;
        var oldSeed = facade.Snapshot.Seed;
        var replacementId = new MultiplayerSessionId("topiaforge-observer-session-two");
        var stateObserverRan = false;
        var sessionObserverRan = false;

        using var stateSubscription = state.SubscribeChanged(value =>
        {
            if (value.Value != 4) return;
            stateObserverRan = true;
            Assert(oldToken.IsCancellationRequested,
                "state reset observers see the retired token cancelled");
            Assert(!facade.CurrentSessionToken.Equals(oldToken) &&
                   !facade.CurrentSessionToken.IsCancellationRequested,
                "state reset observers see the live replacement token");
            Assert(!facade.Snapshot.Id.Equals(oldSessionId) && facade.Snapshot.Id.Equals(replacementId),
                "state reset observers never see replacement token with old session metadata");
            Assert(!facade.Snapshot.Seed.Equals(oldSeed),
                "state reset observers see replacement session-scoped metadata");
        });
        using var sessionSubscription = facade.SubscribeChanged(snapshot =>
        {
            if (!snapshot.Id.Equals(replacementId)) return;
            sessionObserverRan = true;
            Assert(state.Value.Value == 4 && state.Version == 0,
                "replacement-session observers run after session state is reset");
            Assert(!facade.CurrentSessionToken.Equals(oldToken) &&
                   !facade.CurrentSessionToken.IsCancellationRequested,
                "replacement-session observers see the replacement token");
        });

        rig.StartNewSession(replacementId.Value);

        Assert(stateObserverRan && sessionObserverRan,
            "both reset and replacement-session observers receive a coherent transition");
    }

    internal static void CommandAndObjectRateLimitsUseVirtualTime()
    {
        using (var rig = MultiplayerTestRig.CreateDedicatedServer())
        {
            var serverState = RegisterState(rig.Server.Session, "score", 0);
            var client = rig.AddRemoteClient("limited-command");
            var clientState = RegisterState(client.Session, "score", 0);
            rig.Flush();
            RegisterAddCommand(rig.Server.Session, serverState, rejectLarge: false, maximumPerSecond: 1);
            RegisterAddCommand(client.Session, clientState, rejectLarge: false, maximumPerSecond: 1);
            var first = client.Session.SubmitAsync(AddCommandType, new AddRequest(1));
            var second = client.Session.SubmitAsync(AddCommandType, new AddRequest(1));
            rig.Flush();
            Assert(first.GetAwaiter().GetResult().Result.Succeeded, "first command fits the rate window");
            Assert(second.GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.RateLimited,
                "second command is rejected with the stable rate-limit code");
            rig.AdvanceTime(TimeSpan.FromSeconds(1));
            var nextWindow = client.Session.SubmitAsync(AddCommandType, new AddRequest(1));
            rig.Flush();
            Assert(nextWindow.GetAwaiter().GetResult().Result.Succeeded,
                "advancing virtual time deterministically opens a new command window");
        }

        using (var rig = MultiplayerTestRig.CreateDedicatedServer())
        {
            var client = rig.AddRemoteClient("limited-object");
            RegisterObjectType(rig.Server.Session, maximumPerSecond: 1);
            RegisterObjectType(client.Session, maximumPerSecond: 1);
            rig.Flush();
            var serverObject = Require(
                rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(0), client.ParticipantId),
                "spawn rate-limited object");
            rig.Flush();
            var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);
            var first = clientObject.SubmitInputAsync(new AddRequest(1));
            var second = clientObject.SubmitInputAsync(new AddRequest(1));
            rig.Flush();
            Assert(first.GetAwaiter().GetResult().Result.Succeeded, "first object input fits the rate window");
            Assert(second.GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.RateLimited,
                "second object input is rate limited");
            rig.AdvanceTime(TimeSpan.FromSeconds(1));
            var nextWindow = clientObject.SubmitInputAsync(new AddRequest(1));
            rig.Flush();
            Assert(nextWindow.GetAwaiter().GetResult().Result.Succeeded,
                "virtual time opens a new object-input rate window");
        }
    }

    internal static void MutableValuesAreIsolatedAcrossPeersAndConfirmations()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var serverState = RegisterMutableState(rig.Server.Session, 10);
        var first = rig.AddRemoteClient("mutable-first");
        var second = rig.AddRemoteClient("mutable-second");
        var firstState = RegisterMutableState(first.Session, -1);
        var secondState = RegisterMutableState(second.Session, -1);
        RegisterMutableObjectType(rig.Server.Session);
        RegisterMutableObjectType(first.Session);
        RegisterMutableObjectType(second.Session);
        rig.Flush();

        var serverView = serverState.Value;
        serverView.Value = 100;
        serverView.History.Add(100);
        var clientView = firstState.Value;
        clientView.Value = 200;
        clientView.History.Add(200);
        Assert(serverState.Value.Value == 10 && firstState.Value.Value == 10 && secondState.Value.Value == 10 &&
               serverState.Version == 0 && firstState.Version == 0 && secondState.Version == 0,
            "mutating Value copies cannot silently advance or diverge replicated state");

        var rejected = serverState.Update(current =>
        {
            current.Value = 300;
            current.History.Add(300);
            return OperationResult<MutableValue>.Failure(ModErrorCode.InvalidArgument, "reject mutation");
        });
        Assert(!rejected.Succeeded && serverState.Value.Value == 10 && serverState.Version == 0,
            "a rejected server updater cannot mutate through the DTO it receives");

        var secondObserverValue = -1;
        using var firstObserver = firstState.SubscribeChanged(value =>
        {
            value.Value = 400;
            value.History.Add(400);
        });
        using var secondObserver = firstState.SubscribeChanged(value => secondObserverValue = value.Value);
        var stateResponse = Require(serverState.Update(current =>
        {
            current.Value = 11;
            current.History.Add(11);
            return OperationResult<MutableValue>.Success(current);
        }), "update canonical mutable state");
        stateResponse.Value = 500;
        stateResponse.History.Add(500);
        rig.Flush();
        Assert(secondObserverValue == 11 && serverState.Value.Value == 11 && firstState.Value.Value == 11 &&
               secondState.Value.Value == 11 && secondState.Value.History.SequenceEqual(new[] { 10, 11 }),
            "state results and observer payloads cannot affect another observer or peer");

        var secondChangeValue = -1;
        using var firstChanges = first.Session.SubscribeObjects<MutableValue, AddRequest>(
            MutableObjectType,
            change =>
            {
                change.State.Value = 600;
                change.State.History.Add(600);
            });
        using var secondChanges = first.Session.SubscribeObjects<MutableValue, AddRequest>(
            MutableObjectType,
            change => secondChangeValue = change.State.Value);
        var initial = new MutableValue(3);
        var serverObject = Require(
            rig.Server.Session.SpawnObject(MutableObjectType, initial, first.ParticipantId),
            "spawn cross-peer mutable object");
        initial.Value = 700;
        initial.History.Add(700);
        rig.Flush();
        var firstObject = rig.GetObject<MutableValue, AddRequest>(first, serverObject.Id);
        var secondObject = rig.GetObject<MutableValue, AddRequest>(second, serverObject.Id);
        var objectView = firstObject.State;
        objectView.Value = 800;
        objectView.History.Add(800);
        Assert(secondChangeValue == 3 && serverObject.State.Value == 3 && firstObject.State.Value == 3 &&
               secondObject.State.Value == 3 && serverObject.Version == 0 && firstObject.Version == 0,
            "object State and discovery snapshots are detached without advancing canonical versions");

        var submission = firstObject.SubmitInputAsync(new AddRequest(2));
        rig.Flush();
        var confirmation = submission.GetAwaiter().GetResult();
        var confirmed = Require(confirmation.Result, "confirm cross-peer mutable object input");
        confirmed.Value = 900;
        confirmed.History.Add(900);
        Assert(secondChangeValue == 5 && serverObject.State.Value == 5 && firstObject.State.Value == 5 &&
               secondObject.State.Value == 5 && secondObject.State.History.SequenceEqual(new[] { 3, 5 }) &&
               serverObject.Version == 1 && firstObject.Version == 1 && secondObject.Version == 1,
            "mutating an object confirmation cannot affect canonical storage or another peer");
    }

    private static IReplicatedState<CounterValue> RegisterState(IMultiplayerSession session, string id, int initial)
    {
        var result = session.RegisterState(new ReplicatedStateDefinition<CounterValue>(
            id,
            new CounterValue(initial),
            CounterCodec));
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException(result.ErrorMessage);
        return value;
    }

    private static IReplicatedState<MutableValue> RegisterMutableState(IMultiplayerSession session, int initial)
    {
        var result = session.RegisterState(new ReplicatedStateDefinition<MutableValue>(
            "mutable-state",
            new MutableValue(initial),
            MutableCodec));
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException(result.ErrorMessage);
        return value;
    }

    private static void RegisterMutableObjectType(IMultiplayerSession session)
    {
        var result = session.RegisterObjectType(new ReplicatedObjectTypeDefinition<MutableValue, AddRequest>(
            MutableObjectType,
            MutableCodec,
            RequestCodec,
            (_, state, input) =>
            {
                state.Value += input.Amount;
                state.History.Add(state.Value);
                return OperationResult<MutableValue>.Success(state);
            },
            PredictionMode.Owner));
        if (!result.Succeeded) throw new InvalidOperationException(result.ErrorMessage);
    }

    private static void RegisterAddCommand(
        IMultiplayerSession session,
        IReplicatedState<CounterValue> state,
        bool rejectLarge,
        Action? executed = null,
        PresentationEventType<TestEvent>? emitEvent = null,
        int maximumPerSecond = 30)
    {
        var result = session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddCommandType,
            RequestCodec,
            CounterCodec,
            (context, request) =>
            {
                executed?.Invoke();
                if (rejectLarge && request.Amount > 10)
                {
                    return OperationResult<CounterValue>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The canonical server rejected an excessive increment.");
                }

                var updated = state.Update(current =>
                    OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount)));
                if (updated.Succeeded && emitEvent != null)
                    context.Emit(emitEvent, new TestEvent("accepted"));
                return updated;
            },
            PredictionMode.Owner,
            maximumPerSecond));
        if (!result.Succeeded) throw new InvalidOperationException(result.ErrorMessage);
    }

    private static void RegisterObjectType(IMultiplayerSession session, int maximumPerSecond = 30)
    {
        var result = session.RegisterObjectType(ObjectDefinition(maximumPerSecond));
        if (!result.Succeeded) throw new InvalidOperationException(result.ErrorMessage);
    }

    private static ReplicatedObjectTypeDefinition<CounterValue, AddRequest> ObjectDefinition(
        int maximumPerSecond = 30) =>
        new(
            CounterObjectType,
            CounterCodec,
            RequestCodec,
            (context, state, input) => context.SenderOwnsTarget
                ? OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount))
                : OperationResult<CounterValue>.Failure(ModErrorCode.NotAuthoritative, "Sender does not own object."),
            PredictionMode.Owner,
            maximumPerSecond);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value))
            throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }
}
