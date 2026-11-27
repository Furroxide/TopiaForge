using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The rig models the live session channel, which is reliable and ordered: a delayed or lost frame holds later frames
/// behind it. These tests replace the ones that asserted unordered delivery; stale and duplicate submissions that a
/// misbehaving peer could still send are covered by <see cref="EngineWhiteBoxTests"/>.
/// </summary>
internal static partial class MultiplayerRigTests
{
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AppendCommandType = new("append");
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> AppendObjectType = new("append-object");

    internal static void DelayedFramesStillApplyCommandsInOrder()
    {
        foreach (var cadence in new[] { 2, 3, 5 })
        {
            using var rig = MultiplayerTestRig.CreateDedicatedServer(
                new MultiplayerNetworkConditions(latencyTicks: 1, dropEvery: cadence + 1, reorderEvery: cadence, retryDelayTicks: 2));
            var serverState = RegisterState(rig.Server.Session, "digits", 0);
            var client = rig.AddRemoteClient("digit-owner");
            var clientState = RegisterState(client.Session, "digits", 0);
            rig.Flush();
            RegisterAppendCommand(rig.Server.Session, serverState);
            RegisterAppendCommand(client.Session, clientState);

            var submissions = new List<Task<MultiplayerCommandConfirmation<CounterValue>>>();
            for (var digit = 1; digit <= 4; digit++)
            {
                submissions.Add(client.Session.SubmitAsync(AppendCommandType, new AddRequest(digit)));
                rig.Advance(); // each submission leaves in its own frame, so the delays land on different ones
            }

            Assert(clientState.Value.Value == 1234, "pending inputs predict in submission order (cadence " + cadence + ")");
            rig.Flush();
            Assert(submissions.All(item => item.GetAwaiter().GetResult().Result.Succeeded),
                "a delayed frame holds the later ones behind it, so no input arrives stale (cadence " + cadence + ")");
            Assert(serverState.Value.Value == 1234 && clientState.Value.Value == 1234,
                "the host applies inputs in submission order (cadence " + cadence + ")");
        }
    }

    internal static void DelayedFramesStillApplyObjectInputsInOrder()
    {
        foreach (var cadence in new[] { 2, 3, 4 })
        {
            using var rig = MultiplayerTestRig.CreateDedicatedServer(
                new MultiplayerNetworkConditions(latencyTicks: 1, dropEvery: cadence + 2, reorderEvery: cadence, retryDelayTicks: 3));
            var client = rig.AddRemoteClient("object-owner");
            RegisterAppendObjectType(rig.Server.Session);
            RegisterAppendObjectType(client.Session);
            rig.Flush();
            var serverObject = Require(
                rig.Server.Session.SpawnObject(AppendObjectType, new CounterValue(0), client.ParticipantId),
                "spawn append object");
            rig.Flush();
            var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);

            var inputs = new List<Task<MultiplayerCommandConfirmation<CounterValue>>>();
            for (var digit = 1; digit <= 3; digit++)
            {
                inputs.Add(clientObject.SubmitInputAsync(new AddRequest(digit)));
                rig.Advance();
            }

            Assert(clientObject.State.Value == 123, "pending object inputs predict in order (cadence " + cadence + ")");
            rig.Flush();
            Assert(inputs.All(item => item.GetAwaiter().GetResult().Result.Succeeded),
                "no object input arrives stale behind a delayed frame (cadence " + cadence + ")");
            Assert(serverObject.State.Value == 123 && clientObject.State.Value == 123 &&
                   serverObject.Version == 3 && clientObject.Version == 3,
                "object inputs apply once each, in order (cadence " + cadence + ")");
        }
    }

    internal static void AcknowledgedPredictionsNeverDoubleApply()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(
            new MultiplayerNetworkConditions(latencyTicks: 2, reorderEvery: 5, dropEvery: 7, retryDelayTicks: 2));
        var serverSideState = RegisterState(rig.Server.Session, "acknowledged-state", 0);
        var client = rig.AddRemoteClient("acknowledged-owner");
        var clientSideState = RegisterState(client.Session, "acknowledged-state", 0);
        var objectType = new ReplicatedObjectType<CounterValue, AddRequest>("acknowledged-object");
        RegisterObjectWithSideState(rig.Server.Session, objectType, serverSideState);
        RegisterObjectWithSideState(client.Session, objectType, clientSideState);
        rig.Flush();

        var serverObject = Require(
            rig.Server.Session.SpawnObject(objectType, new CounterValue(0), client.ParticipantId),
            "spawn acknowledged object");
        rig.Flush();
        var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);
        var first = clientObject.SubmitInputAsync(new AddRequest(1));
        rig.Advance();
        var second = clientObject.SubmitInputAsync(new AddRequest(2));
        Assert(clientObject.State.Value == 3 && clientSideState.Value.Value == 3,
            "both object transactions predict before either confirmation");

        for (var tick = 0; tick < 64 && !(first.IsCompleted && second.IsCompleted); tick++)
        {
            rig.Advance();
            Assert(clientObject.State.Value == 3 && clientSideState.Value.Value == 3,
                "a confirmed input is never replayed on top of the state that already includes it (tick " + tick + ")");
        }

        Assert(first.GetAwaiter().GetResult().Result.Succeeded && second.GetAwaiter().GetResult().Result.Succeeded &&
               serverObject.State.Value == 3 && clientObject.State.Value == 3 &&
               serverSideState.Value.Value == 3 && clientSideState.Value.Value == 3,
            "object and related state converge exactly once");
    }

    internal static void DespawnedObjectsNeverResurrect()
    {
        using (var rig = MultiplayerTestRig.CreateDedicatedServer(new MultiplayerNetworkConditions(reorderEvery: 2)))
        {
            var client = rig.AddRemoteClient("short-lived-observer");
            RegisterObjectType(rig.Server.Session);
            RegisterObjectType(client.Session);
            rig.Flush();
            var changes = new List<ReplicatedObjectChange<CounterValue, AddRequest>>();
            using var subscription = client.Session.SubscribeObjects(CounterObjectType, changes.Add);

            var serverObject = Require(
                rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(4), client.ParticipantId),
                "spawn an object that leaves before the next flush");
            Assert(rig.Server.Session.DespawnObject(serverObject.Id).Succeeded, "despawn before the spawn is sent");
            rig.Flush();
            Assert(client.Session.GetObjects(CounterObjectType).Count == 0 && changes.Count == 0,
                "an object spawned and despawned within one flush never reaches the client");
        }

        using (var rig = MultiplayerTestRig.CreateDedicatedServer(new MultiplayerNetworkConditions(latencyTicks: 1, reorderEvery: 3)))
        {
            var client = rig.AddRemoteClient("retiring-observer");
            RegisterObjectType(rig.Server.Session);
            RegisterObjectType(client.Session);
            rig.Flush();
            var serverObject = Require(
                rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(7), client.ParticipantId),
                "spawn an object the client discovers");
            rig.Flush();
            var clientObject = rig.GetObject<CounterValue, AddRequest>(client, serverObject.Id);
            var changes = new List<ReplicatedObjectChange<CounterValue, AddRequest>>();
            using var subscription = client.Session.SubscribeObjects(CounterObjectType, changes.Add);

            Assert(serverObject.TransferOwnership(null).Succeeded, "change the object");
            Assert(rig.Server.Session.DespawnObject(serverObject.Id).Succeeded, "then despawn it in the same flush");
            rig.Flush();
            Assert(changes.Count == 1 && changes[0].Kind == ReplicatedObjectChangeKind.Despawned && changes[0].Object == null,
                "a change superseded by a despawn is dropped, and the despawn is reported once");
            Assert(!clientObject.IsSpawned && client.Session.GetObjects(CounterObjectType).Count == 0,
                "the retained handle reports the despawn");

            var second = Require(
                rig.Server.Session.SpawnObject(CounterObjectType, new CounterValue(9)),
                "spawn another object");
            rig.Advance();
            Assert(rig.Server.Session.DespawnObject(second.Id).Succeeded, "despawn it after its spawn left");
            rig.Flush();
            rig.Advance(5);
            Assert(changes.Count == 3 &&
                   changes[1].Kind == ReplicatedObjectChangeKind.Spawned && changes[1].Id.Equals(second.Id) &&
                   changes[2].Kind == ReplicatedObjectChangeKind.Despawned && changes[2].Id.Equals(second.Id) &&
                   client.Session.GetObjects(CounterObjectType).Count == 0,
                "a spawn and its despawn arrive in order behind delayed frames, and nothing brings the object back");
        }
    }

    private static void RegisterAppendCommand(IMultiplayerSession session, IReplicatedState<CounterValue> state) =>
        Require(
            session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                AppendCommandType,
                RequestCodec,
                CounterCodec,
                (_, request) => state.Update(current =>
                    OperationResult<CounterValue>.Success(new CounterValue((current.Value * 10) + request.Amount))),
                PredictionMode.Owner)),
            "register append command");

    private static void RegisterAppendObjectType(IMultiplayerSession session) =>
        Require(
            session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                AppendObjectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) => OperationResult<CounterValue>.Success(new CounterValue((state.Value * 10) + input.Amount)),
                PredictionMode.Owner)),
            "register append object type");

    private static void RegisterObjectWithSideState(
        IMultiplayerSession session,
        ReplicatedObjectType<CounterValue, AddRequest> objectType,
        IReplicatedState<CounterValue> sideState) =>
        Require(
            session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                objectType,
                CounterCodec,
                RequestCodec,
                (_, state, input) =>
                {
                    var updated = sideState.Update(current => OperationResult<CounterValue>.Success(
                        new CounterValue(current.Value + input.Amount)));
                    return updated.Succeeded
                        ? OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount))
                        : OperationResult<CounterValue>.Failure(updated.ErrorCode, updated.ErrorMessage);
                },
                PredictionMode.Owner)),
            "register object with side state");
}
