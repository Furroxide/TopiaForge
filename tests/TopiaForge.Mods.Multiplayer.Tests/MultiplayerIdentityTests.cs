using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class MultiplayerIdentityTests
{
    internal static void SnapshotsRejectInvalidOrAmbiguousIdentityData()
    {
        Assert(!default(MultiplayerSessionId).IsValid, "default session identity must be invalid");
        Assert(!default(ParticipantId).IsValid, "default participant identity must be invalid");
        Assert(!default(NetworkObjectId).IsValid, "default object identity must be invalid");

        AssertThrows<ArgumentException>(
            () => new MultiplayerParticipant(default, "Player", false, true),
            "participants must reject the default identity");
        AssertThrows<ArgumentException>(
            () => new MultiplayerCommandContext(
                default,
                new NetworkTick(0),
                CancellationToken.None,
                (_, _, _) => OperationResult<bool>.Success(true)),
            "command contexts must reject a default authenticated sender");
        AssertThrows<ArgumentException>(
            () => new ReplicatedObjectCommandContext(
                new ParticipantId("sender"),
                default,
                new NetworkTick(0),
                false,
                CancellationToken.None,
                (_, _, _) => OperationResult<bool>.Success(true)),
            "object command contexts must reject a default target identity");
        AssertThrows<ArgumentException>(
            () => new ReplicatedObjectChange<CounterValue, AddRequest>(
                ReplicatedObjectChangeKind.Despawned,
                default,
                null,
                new CounterValue(0),
                0,
                null),
            "replicated-object changes must reject a default object identity");
        AssertThrows<ArgumentException>(
            () => new ReplicatedObjectChange<CounterValue, AddRequest>(
                ReplicatedObjectChangeKind.Despawned,
                new NetworkObjectId("object"),
                default(ParticipantId),
                new CounterValue(0),
                0,
                null),
            "replicated-object changes must reject a default owner identity");
        AssertThrows<ArgumentException>(
            () => new MultiplayerParticipant(new ParticipantId("p1"), "\n", false, true),
            "participants must reject control-only display names");
        AssertThrows<ArgumentException>(
            () => new MultiplayerParticipant(
                new ParticipantId("p1"),
                new string('p', MultiplayerParticipant.MaximumDisplayNameLength + 1),
                false,
                true),
            "participants must enforce bounded display names");

        var localId = new ParticipantId("local");
        var local = new MultiplayerParticipant(localId, "Local player", true, false);
        var remote = new MultiplayerParticipant(new ParticipantId("remote"), "Remote player", false, true);
        var valid = Snapshot(localId, new[] { local, remote });
        Assert(valid.Participants.Count == 2, "valid bounded participant snapshots must be accepted");

        AssertThrows<ArgumentException>(
            () => new MultiplayerSessionSnapshot(
                default,
                MultiplayerSessionState.Ready,
                MultiplayerProcessKind.Interactive,
                MultiplayerExecutionSide.Client,
                localId,
                new[] { local },
                new NetworkTick(0),
                new SessionSeed(1)),
            "snapshots must reject the default session identity");
        AssertThrows<ArgumentException>(
            () => Snapshot(localId, new[] { local, local }),
            "snapshots must reject duplicate participant identities");
        AssertThrows<ArgumentException>(
            () => Snapshot(localId, new[] { remote }),
            "local identities must have one matching local record");
        AssertThrows<ArgumentException>(
            () => Snapshot(localId, new[] { new MultiplayerParticipant(localId, "Local", false, true) }),
            "the local participant record must be explicitly marked local");
        AssertThrows<ArgumentException>(
            () => Snapshot(localId, new[] { new MultiplayerParticipant(remote.Id, "Remote", true, true) }),
            "a local marker must match the declared local identity");
        AssertThrows<ArgumentException>(
            () => new MultiplayerSessionSnapshot(
                new MultiplayerSessionId("headless"),
                MultiplayerSessionState.Ready,
                MultiplayerProcessKind.Headless,
                MultiplayerExecutionSide.Server,
                localId,
                new[] { local },
                new NetworkTick(0),
                new SessionSeed(1)),
            "headless snapshots must not fabricate a local participant");
        AssertThrows<ArgumentException>(
            () => new MultiplayerSessionSnapshot(
                new MultiplayerSessionId("headless-client"),
                MultiplayerSessionState.Ready,
                MultiplayerProcessKind.Headless,
                MultiplayerExecutionSide.Client,
                null,
                new[] { remote },
                new NetworkTick(0),
                new SessionSeed(1)),
            "headless snapshots must not claim a local presentation/input side");

        var duplicateBound = Enumerable.Range(0, MultiplayerSessionSnapshot.MaximumParticipantCount + 1)
            .Select(index => new MultiplayerParticipant(
                new ParticipantId("participant-" + index),
                "Participant " + index,
                false,
                true))
            .ToArray();
        AssertThrows<ArgumentException>(
            () => new MultiplayerSessionSnapshot(
                new MultiplayerSessionId("bounded"),
                MultiplayerSessionState.Ready,
                MultiplayerProcessKind.Headless,
                MultiplayerExecutionSide.Server,
                null,
                duplicateBound,
                new NetworkTick(0),
                new SessionSeed(1)),
            "snapshots must enforce their participant count bound");

        var dedicated = new MultiplayerSessionSnapshot(
            new MultiplayerSessionId("dedicated"),
            MultiplayerSessionState.Ready,
            MultiplayerProcessKind.Headless,
            MultiplayerExecutionSide.Server,
            null,
            new[] { remote },
            new NetworkTick(0),
            new SessionSeed(1));
        Assert(!dedicated.HasPresentation && !dedicated.LocalParticipantId.HasValue,
            "headless snapshots may describe remote participants without fabricating a local one");
    }

    private static MultiplayerSessionSnapshot Snapshot(
        ParticipantId localId,
        IReadOnlyList<MultiplayerParticipant> participants) =>
        new MultiplayerSessionSnapshot(
            new MultiplayerSessionId("session"),
            MultiplayerSessionState.Ready,
            MultiplayerProcessKind.Interactive,
            MultiplayerExecutionSide.Client,
            localId,
            participants,
            new NetworkTick(0),
            new SessionSeed(1));

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
