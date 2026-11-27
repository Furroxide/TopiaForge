using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Transaction rules of the session engine: spawns, despawns, transfers and events inside a handler take effect only
/// when it commits, while registrations and nested submissions are refused there.
/// </summary>
internal static partial class MultiplayerRigTests
{
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> SpawnCommandType = new("spawn-counter");
    private static readonly PresentationEventType<TestEvent> SpawnedEventType = new("counter-spawned", EventCodec);

    internal static void CommandsSpawnAndEmitOnlyWhenTheyCommit()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var client = rig.AddRemoteClient("spawner");
        var spawnedInsideHandlers = new List<IReplicatedObject<CounterValue, AddRequest>>();
        RegisterObjectType(rig.Server.Session);
        RegisterObjectType(client.Session);
        Require(rig.Server.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(SpawnedEventType)), "server event");
        var presented = new List<string>();
        Require(client.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(SpawnedEventType, value => presented.Add(value.Message))),
            "client event");
        Require(rig.Server.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            SpawnCommandType,
            RequestCodec,
            CounterCodec,
            (context, request) =>
            {
                var spawned = Require(
                    rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(request.Amount), context.SenderId),
                    "spawn inside the command");
                spawnedInsideHandlers.Add(spawned);
                Assert(spawned.IsSpawned && rig.Server.Session.GetObjects(CounterObjectType).Count == spawnedInsideHandlers.Count(item => item.IsSpawned),
                    "the handler sees its own spawn before commit");
                Require(context.Emit(SpawnedEventType, new TestEvent("spawned " + request.Amount)), "emit");
                return request.Amount == 0
                    ? OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "Zero is not a counter.")
                    : OperationResult<CounterValue>.Success(new CounterValue(request.Amount));
            })), "register spawning command");
        Require(client.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            SpawnCommandType,
            RequestCodec,
            CounterCodec,
            (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)))), "register client command");
        rig.Flush();

        var accepted = client.Session.SubmitAsync(SpawnCommandType, new AddRequest(4));
        rig.Flush();
        Assert(accepted.GetAwaiter().GetResult().Result.Succeeded, "a command may spawn an object");
        var discovered = client.Session.GetObjects(CounterObjectType);
        Assert(discovered.Count == 1 && discovered[0].State.Value == 4 && discovered[0].OwnerId.Equals(client.ParticipantId),
            "the committed spawn reaches the client");
        Assert(presented.SequenceEqual(new[] { "spawned 4" }), "the committed event is presented once");

        var rejected = client.Session.SubmitAsync(SpawnCommandType, new AddRequest(0));
        rig.Flush();
        Assert(rejected.GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.InvalidArgument, "the second command fails");
        Assert(!spawnedInsideHandlers[1].IsSpawned && rig.Server.Session.GetObjects(CounterObjectType).Count == 1 &&
               client.Session.GetObjects(CounterObjectType).Count == 1 && presented.Count == 1,
            "an aborted command's spawn and event never happen");
    }

    internal static void HostCodeAppliesObjectChangesOutsideInputs()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var client = rig.AddRemoteClient("apply-observer");
        RegisterObjectType(rig.Server.Session);
        RegisterObjectType(client.Session);
        rig.Flush();
        var serverObject = Require(
            rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(1), client.ParticipantId),
            "spawn an object host code will change");
        rig.Flush();
        var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);
        var changes = new List<int>();
        using var subscription = client.Session.SubscribeObjects(CounterObjectType, change =>
        {
            if (change.Kind == ReplicatedObjectChangeKind.Changed) changes.Add(change.State.Value);
        });
        var tick = rig.Server.Session.SubscribeTick(_ => Require(
            serverObject.Apply(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value * 2))),
            "apply from a tick"));
        rig.Advance(3);
        tick.Dispose();
        rig.Flush();
        Assert(serverObject.State.Value == 8 && serverObject.Version == 3, "each tick applies one canonical change");
        Assert(clientObject.State.Value == 8 && changes.Count > 0 && changes[^1] == 8, "applied changes replicate to clients");
        var refused = clientObject.Apply(current => OperationResult<CounterValue>.Success(current));
        Assert(!refused.Succeeded && refused.ErrorCode == ModErrorCode.NotAuthoritative, "a client cannot apply object changes");
        var rejected = serverObject.Apply(_ => OperationResult<CounterValue>.Failure(ModErrorCode.InvalidArgument, "rejected"));
        Assert(!rejected.Succeeded && serverObject.State.Value == 8 && serverObject.Version == 3, "a failed apply changes nothing");
    }

    internal static void ReconnectSnapshotsResetStatesTheHostDropped()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var serverTemporary = RegisterState(rig.Server.Session, "temporary", 0);
        var client = rig.AddRemoteClient("returning");
        var clientTemporary = RegisterState(client.Session, "temporary", 0);
        rig.Flush();
        Require(serverTemporary.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(5))), "host update");
        rig.Flush();
        Assert(clientTemporary.Value.Value == 5, "the client follows the host while connected");
        rig.Disconnect(client);
        serverTemporary.Dispose();
        rig.Reconnect(client);
        rig.Flush();
        Assert(client.IsReady && clientTemporary.Value.Value == 0 && clientTemporary.Version == 0,
            "an exact snapshot returns a state the host no longer has to its declared default");
    }

    internal static void RetiredTokenCallbacksSeeTheReplacementSession()
    {
        using var rig = MultiplayerTestRig.CreateStandalone();
        var facade = rig.Server.Session;
        var state = RegisterState(facade, "token-state", 4);
        Require(state.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(9))), "advance before replacement");
        string? seenId = null;
        var seenValue = -1;
        var replacementTokenLive = false;
        using var registration = facade.CurrentSessionToken.Register(() =>
        {
            seenId = facade.Snapshot.Id.Value;
            seenValue = state.Value.Value;
            replacementTokenLive = !facade.CurrentSessionToken.IsCancellationRequested;
        });
        rig.StartNewSession("topiaforge-token-session-two");
        Assert(seenId == "topiaforge-token-session-two" && seenValue == 4 && replacementTokenLive,
            "a retired token's callbacks run once the replacement is complete, never against a half-replaced session");
    }

    internal static void HandlersCannotRegisterOrNestSubmissions()
    {
        using var rig = MultiplayerTestRig.CreateStandalone();
        var session = rig.Server.Session;
        var refusals = new List<ModErrorCode>();
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            SpawnCommandType,
            RequestCodec,
            CounterCodec,
            (_, request) =>
            {
                refusals.Add(session.RegisterState(new ReplicatedStateDefinition<CounterValue>(
                    "late-state", new CounterValue(0), CounterCodec)).ErrorCode);
                refusals.Add(session.RegisterObjectType(ObjectDefinition()).ErrorCode);
                refusals.Add(session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(SpawnedEventType)).ErrorCode);
                refusals.Add(session.SubmitAsync(SpawnCommandType, new AddRequest(1)).GetAwaiter().GetResult().Result.ErrorCode);
                return OperationResult<CounterValue>.Success(new CounterValue(request.Amount));
            })), "register reentrant command");

        var result = session.SubmitAsync(SpawnCommandType, new AddRequest(2)).GetAwaiter().GetResult();
        Assert(result.Result.Succeeded, "the outer command still succeeds");
        Assert(refusals.Count == 4 && refusals.All(code => code == ModErrorCode.InvalidState),
            "registrations and nested submissions inside a handler are refused with InvalidState");
    }

    internal static void SessionReplacementResynchronizesRemoteClients()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(new MultiplayerNetworkConditions(latencyTicks: 2));
        var serverState = RegisterState(rig.Server.Session, "match-score", 0);
        RegisterObjectType(rig.Server.Session);
        var client = rig.AddRemoteClient("resynchronized");
        var clientState = RegisterState(client.Session, "match-score", 0);
        RegisterObjectType(client.Session);
        RegisterAddCommand(rig.Server.Session, serverState, rejectLarge: false);
        RegisterAddCommand(client.Session, clientState, rejectLarge: false);
        rig.Flush();
        Require(serverState.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(5))), "score before replacement");
        var oldObject = Require(rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(1)), "object before replacement");
        rig.Flush();
        var changes = new List<ReplicatedObjectChangeKind>();
        using var subscription = client.Session.SubscribeObjects(CounterObjectType, change => changes.Add(change.Kind));
        var oldToken = client.Session.CurrentSessionToken;
        var pending = client.Session.SubmitAsync(AddCommandType, new AddRequest(1));

        rig.StartNewSession("topiaforge-test-session-two");
        rig.Flush();

        Assert(client.IsReady && client.Session.Snapshot.Id.Value == "topiaforge-test-session-two" && oldToken.IsCancellationRequested,
            "the client follows the host into the replacement session");
        Assert(pending.GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.Cancelled,
            "a submission still pending at replacement is cancelled");
        Assert(serverState.Value.Value == 0 && clientState.Value.Value == 0 && clientState.Version == 0,
            "replicated state returns to its default on both sides, and the stale submission never applied");
        Assert(!oldObject.IsSpawned && client.Session.GetObjects(CounterObjectType).Count == 0 &&
               changes.SequenceEqual(new[] { ReplicatedObjectChangeKind.Despawned }),
            "old objects despawn on the client too, and its subscription sees it");

        var next = client.Session.SubmitAsync(AddCommandType, new AddRequest(3));
        rig.Flush();
        Assert(next.GetAwaiter().GetResult().Result.Succeeded && serverState.Value.Value == 3 && clientState.Value.Value == 3,
            "the replacement session accepts new work");
    }
}
