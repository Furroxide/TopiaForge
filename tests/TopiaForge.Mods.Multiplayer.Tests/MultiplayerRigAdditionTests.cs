using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The rig's scripted faults, interruption, kicks, traffic accounting, owner sessions and link knobs.</summary>
internal static class MultiplayerRigAdditionTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly JsonTestCodec<TestEvent> EventCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AppendType = new("append");

    internal static void HeldFramesWaitForRelease()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var (_, client, serverState, clientState) = AppendPair(rig);
        rig.Network.HoldNext(client, rig.Server);
        var submission = client.Session.SubmitAsync(AppendType, new AddRequest(7));
        rig.Flush();
        Assert(!submission.IsCompleted && serverState.Value.Value == 0, "a held frame does not arrive, and Flush does not wait for it");
        rig.Network.Release(client, rig.Server);
        rig.Flush();
        Assert(submission.GetAwaiter().GetResult().Result.Succeeded && serverState.Value.Value == 7, "a released frame arrives");

        rig.Network.HoldNext(rig.Server, client);
        Require(serverState.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(9))), "server update");
        rig.Flush();
        Assert(clientState.Value.Value == 7, "a held host frame keeps the client on its previous value");
        rig.Network.Release(rig.Server, client);
        rig.Flush();
        Assert(clientState.Value.Value == 9, "the client catches up once the frame is released");
    }

    internal static void PartitionsDelayTrafficButKeepItsOrder()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var (_, client, serverState, clientState) = AppendPair(rig);
        var states = new List<MultiplayerSessionState>();
        using var observer = client.Session.SubscribeChanged(snapshot => states.Add(snapshot.State));
        rig.Network.Partition(client, 12);
        var submissions = new List<Task<MultiplayerCommandConfirmation<CounterValue>>>();
        for (var digit = 1; digit <= 3; digit++)
        {
            submissions.Add(client.Session.SubmitAsync(AppendType, new AddRequest(digit)));
            rig.Advance(2);
        }

        Assert(serverState.Value.Value == 0 && submissions.All(item => !item.IsCompleted), "nothing crosses a partition");
        rig.Flush();
        Assert(submissions.All(item => item.GetAwaiter().GetResult().Result.Succeeded) &&
               serverState.Value.Value == 123 && clientState.Value.Value == 123,
            "traffic held by a partition arrives in order once it heals");
        Assert(states.All(item => item == MultiplayerSessionState.Ready), "a silent partition never looks like a disconnect");
    }

    internal static void StalledProcessesCatchUpInOrder()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var (_, client, serverState, clientState) = AppendPair(rig);
        var seen = new List<int>();
        using var observer = clientState.SubscribeChanged(value => seen.Add(value.Value));
        rig.Network.Stall(client, 20);
        for (var value = 1; value <= 3; value++)
        {
            Require(serverState.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(value))), "update " + value);
            rig.Advance(3);
        }

        Assert(seen.Count == 0 && clientState.Value.Value == 0, "a stalled process sees nothing until it resumes");
        rig.Flush();
        Assert(seen.SequenceEqual(new[] { 1, 2, 3 }) && clientState.Value.Value == 3,
            "a resumed process applies every queued frame in order, one settle point each");

        rig.Network.Stall(rig.Server, 10);
        var submission = client.Session.SubmitAsync(AppendType, new AddRequest(4));
        rig.Advance(5);
        Assert(!submission.IsCompleted, "a stalled host cannot confirm");
        rig.Flush();
        Assert(submission.GetAwaiter().GetResult().Result.Succeeded && serverState.Value.Value == 34, "the host catches up");
    }

    internal static void PermanentLossBreaksTheConnection()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var (_, client, serverState, _) = AppendPair(rig);
        rig.Network.DropPermanently(client, rig.Server);
        var lost = client.Session.SubmitAsync(AppendType, new AddRequest(5));
        rig.Flush();
        Assert(lost.GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.Cancelled &&
               client.Session.Snapshot.State == MultiplayerSessionState.Connecting,
            "losing a frame for good breaks the connection and cancels what was waiting on it");
        Assert(rig.Server.Session.Snapshot.TryGetParticipant(client.ParticipantId!.Value, out var participant) && !participant!.IsConnected,
            "the host sees the participant leave");
        rig.Reconnect(client);
        rig.Flush();
        Assert(client.IsReady && serverState.Value.Value == 0, "the client rejoins; the lost submission never executed");
    }

    internal static void ReplayedFramesAreDiscarded()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var executions = 0;
        var serverState = Register(rig.Server.Session, "digits");
        var client = rig.AddRemoteClient("replayed");
        var clientState = Register(client.Session, "digits");
        rig.Flush();
        RegisterAppend(rig.Server.Session, serverState, () => executions++);
        RegisterAppend(client.Session, clientState, null);
        var notifications = 0;
        using var observer = clientState.SubscribeChanged(_ => notifications++);
        var submission = client.Session.SubmitAsync(AppendType, new AddRequest(1));
        rig.Flush();
        var before = notifications;
        rig.Network.ReplayLast(client, rig.Server);
        rig.Network.ReplayLast(rig.Server, client);
        rig.Flush();
        Assert(submission.GetAwaiter().GetResult().Result.Succeeded && executions == 1 && serverState.Value.Value == 1,
            "a replayed submission frame never executes twice");
        Assert(notifications == before && clientState.Value.Value == 1, "a replayed host frame changes nothing");
    }

    internal static void InterruptedClientsReconnectThenRecover()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var (_, client, serverState, clientState) = AppendPair(rig);
        rig.Interrupt(client);
        Assert(client.Session.Snapshot.State == MultiplayerSessionState.Reconnecting, "an interrupted client reports Reconnecting");
        Assert(rig.Server.Session.Snapshot.TryGetParticipant(client.ParticipantId!.Value, out var poor) &&
               poor!.Quality == MultiplayerConnectionQuality.Poor && poor.IsConnected,
            "the host keeps the participant and reports a poor connection");
        var frozen = client.Session.SubmitAsync(AppendType, new AddRequest(1)).GetAwaiter().GetResult();
        Assert(frozen.Result.ErrorCode == ModErrorCode.InvalidState, "a reconnecting client cannot submit");
        Require(serverState.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(8))), "host keeps playing");
        rig.Advance(10);
        Assert(clientState.Value.Value == 0, "canonical state is frozen while reconnecting");
        rig.Resume(client);
        rig.Flush();
        Assert(client.IsReady && clientState.Value.Value == 8, "the resumed client returns to Ready and catches up");
        Assert(rig.Server.Session.Snapshot.TryGetParticipant(client.ParticipantId.Value, out var recovered) &&
               recovered!.Quality != MultiplayerConnectionQuality.Poor,
            "the host stops reporting a poor connection");
    }

    internal static void KickedClientsSessionEnds()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var (_, client, _, _) = AppendPair(rig);
        rig.Kick(client);
        rig.Flush();
        Assert(client.Session.Snapshot.State == MultiplayerSessionState.Ended &&
               client.Session.Snapshot.EndReason == MultiplayerSessionEndReason.Kicked,
            "a kicked client's session ends with Kicked");
        Assert(rig.Server.Session.Snapshot.TryGetParticipant(client.ParticipantId!.Value, out var kicked) && !kicked!.IsConnected,
            "the host lists the kicked participant as disconnected");
    }

    internal static void TrafficCountsSessionBytes()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var (_, client, _, _) = AppendPair(rig);
        client.Session.SubmitAsync(AppendType, new AddRequest(1));
        rig.Flush();
        var clientTraffic = rig.Traffic(client);
        var serverTraffic = rig.Traffic(rig.Server);
        Assert(clientTraffic.FramesSent >= 3 && clientTraffic.BytesSent > 0, "a join, a ready acknowledgement and a command are counted");
        Assert(serverTraffic.BytesReceived == clientTraffic.BytesSent && clientTraffic.BytesReceived == serverTraffic.BytesSent,
            "on a clean link every byte one side sends, the other receives");
    }

    internal static void OwnerSessionsKeepWireIdsApart()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var client = rig.AddRemoteClient("two-mods");
        var serverA = Register(rig.CreateOwnerSession(rig.Server, "mod.a"), "score");
        var serverB = Register(rig.CreateOwnerSession(rig.Server, "mod.b"), "score");
        var clientA = Register(rig.CreateOwnerSession(client, "mod.a"), "score");
        var clientB = Register(rig.CreateOwnerSession(client, "mod.b"), "score");
        rig.Flush();
        Require(serverA.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(4))), "mod a update");
        Require(serverB.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(5))), "mod b update");
        rig.Flush();
        Assert(clientA.Value.Value == 4 && clientB.Value.Value == 5, "two mods may use the same wire id without colliding");
    }

    internal static void DownstreamRateCoalescesChanges()
    {
        int Notifications(MultiplayerNetworkConditions conditions)
        {
            using var rig = MultiplayerTestRig.CreateDedicatedServer(conditions);
            var serverState = Register(rig.Server.Session, "fast");
            var client = rig.AddRemoteClient("coalesced");
            var clientState = Register(client.Session, "fast");
            rig.Flush();
            var count = 0;
            using var observer = clientState.SubscribeChanged(_ => count++);
            for (var step = 1; step <= 60; step++)
            {
                Require(serverState.Update(_ => OperationResult<CounterValue>.Success(new CounterValue(step))), "update");
                rig.Advance();
            }

            rig.Flush();
            Assert(clientState.Value.Value == 60, "the latest value always arrives");
            return count;
        }

        var everyTick = Notifications(new MultiplayerNetworkConditions());
        var tenHertz = Notifications(new MultiplayerNetworkConditions().WithDownstreamRate(10));
        Assert(everyTick == 60 && tenHertz >= 9 && tenHertz <= 11,
            "a 10 Hz downstream rate coalesces sixty changes into about ten (" + everyTick + " vs " + tenHertz + ")");
    }

    internal static void BandwidthDelaysLargeFrames()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer(new MultiplayerNetworkConditions().WithBandwidth(6000));
        var notice = new ReplicatedStateDefinition<TestEvent>("notice", new TestEvent(string.Empty), EventCodec);
        var serverNotice = Require(rig.Server.Session.RegisterState(notice), "server notice");
        var client = rig.AddRemoteClient("narrow-link");
        var clientNotice = Require(client.Session.RegisterState(notice), "client notice");
        rig.Flush();
        Require(serverNotice.Update(_ => OperationResult<TestEvent>.Success(new TestEvent(new string('x', 3000)))), "large update");
        rig.Advance(10);
        Assert(clientNotice.Value.Message.Length == 0, "a 3 KB frame cannot cross a 6 KB/s link in ten ticks");
        var start = rig.Tick.Value;
        rig.Flush();
        Assert(clientNotice.Value.Message.Length == 3000 && rig.Tick.Value - start >= 15,
            "it arrives once the link has carried it (" + (rig.Tick.Value - start) + " more ticks)");
    }

    private static (MultiplayerTestNode Server, MultiplayerTestNode Client, IReplicatedState<CounterValue> ServerState, IReplicatedState<CounterValue> ClientState)
        AppendPair(MultiplayerTestRig rig)
    {
        var serverState = Register(rig.Server.Session, "digits");
        var client = rig.AddRemoteClient("client");
        var clientState = Register(client.Session, "digits");
        rig.Flush();
        RegisterAppend(rig.Server.Session, serverState, null);
        RegisterAppend(client.Session, clientState, null);
        return (rig.Server, client, serverState, clientState);
    }

    private static IReplicatedState<CounterValue> Register(IMultiplayerSession session, string id) =>
        Require(session.RegisterState(new ReplicatedStateDefinition<CounterValue>(id, new CounterValue(0), CounterCodec)), "register " + id);

    private static void RegisterAppend(IMultiplayerSession session, IReplicatedState<CounterValue> state, Action? executed) =>
        Require(
            session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                AppendType,
                RequestCodec,
                CounterCodec,
                (_, request) =>
                {
                    executed?.Invoke();
                    return state.Update(current =>
                        OperationResult<CounterValue>.Success(new CounterValue((current.Value * 10) + request.Amount)));
                },
                PredictionMode.Owner)),
            "register append");

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
