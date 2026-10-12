using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Covers the session, player, and simulation contract added for live multiplayer.</summary>
internal static class SdkContractCompletionTests
{
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<TestEvent> EventCodec = new();

    internal static void DeterministicRandomMatchesFrozenVectors()
    {
        // Vectors from an independent reference implementation; they are frozen for protocol major version 1.
        AssertSequence(new DeterministicRandom(0), 0x99EC5F36CB75F2B4UL, 0xBF6E1F784956452AUL,
            0x1A5F849D4933E6E0UL, 0x6AA594F1262D2D2CUL);
        AssertSequence(new DeterministicRandom(0x0123456789ABCDEFUL), 0xA2C2A42038D4EC3DUL,
            0x05FC25D0738E7B0FUL, 0x625E7BFF938E701EUL);
        var player = new ParticipantId("player-1");
        AssertSequence(DeterministicRandom.ForCommand(new SessionSeed(42), player, 7),
            0xDD7BB6FC7E51A5DEUL, 0x580DED2A4225159BUL, 0x9C4179CC5516654BUL);
        AssertSequence(DeterministicRandom.ForCommand(new SessionSeed(42), player, 7, salt: 99),
            0x60B149AD806D18F1UL, 0xE6DDD7F5FD108E07UL);
        AssertSequence(DeterministicRandom.ForCommand(new SessionSeed(42), new ParticipantId("player-2"), 7),
            0x68E6CA5AE535E2A7UL, 0xDD8E74626338520BUL);

        var dice = DeterministicRandom.ForCommand(new SessionSeed(42), player, 7);
        var rolls = Enumerable.Range(0, 8).Select(_ => dice.Next(6)).ToArray();
        Assert(rolls.SequenceEqual(new[] { 4, 5, 1, 4, 0, 2, 1, 3 }), "bounded draws are unbiased and frozen");
        var doubles = new DeterministicRandom(0);
        Assert(doubles.NextDouble() == 0.6012629994179048 && doubles.NextDouble() == 0.7477740925472398,
            "doubles use the top 53 bits");
        var ranged = new DeterministicRandom(12345);
        Assert(Enumerable.Range(0, 6).Select(_ => ranged.Next(-5, 5)).SequenceEqual(new[] { -2, 3, 1, 0, -1, 1 }),
            "ranged draws offset an unbiased bounded draw");

        AssertThrows<ArgumentOutOfRangeException>(() => new DeterministicRandom(1).Next(0), "an empty bound is rejected");
        AssertThrows<ArgumentOutOfRangeException>(() => new DeterministicRandom(1).Next(5, 5), "an empty range is rejected");
        AssertThrows<ArgumentException>(
            () => DeterministicRandom.ForCommand(new SessionSeed(1), default, 1),
            "a command stream needs a real sender");

        var context = new MultiplayerCommandContext(
            player,
            new NetworkTick(900),
            7,
            new SessionSeed(42),
            CancellationToken.None,
            (_, _, _) => OperationResult<bool>.Success(true));
        Assert(context.CreateRandom().NextUInt64() == 0xDD7BB6FC7E51A5DEUL,
            "command contexts seed from session seed, sender, and sequence, never the tick");
        var legacy = new MultiplayerCommandContext(
            player,
            new NetworkTick(3),
            CancellationToken.None,
            (_, _, _) => OperationResult<bool>.Success(true));
        Assert(legacy.Sequence == 0 && legacy.Seed.Value == 0, "the legacy constructor carries no sequence or seed");
    }

    internal static void PresentationTargetsSelectExactRecipients()
    {
        var sender = new ParticipantId("sender");
        var other = new ParticipantId("other");
        Assert(PresentationTarget.Everyone.Selects(sender, sender) && PresentationTarget.Everyone.Selects(other, sender),
            "everyone selects every participant");
        Assert(PresentationTarget.Sender.Selects(sender, sender) && !PresentationTarget.Sender.Selects(other, sender),
            "sender selects only the sender");
        Assert(!PresentationTarget.Others.Selects(sender, sender) && PresentationTarget.Others.Selects(other, sender),
            "others excludes the sender");
        Assert(PresentationTarget.Only(other).Selects(other, sender) && !PresentationTarget.Only(other).Selects(sender, sender),
            "only selects exactly one participant");
        Assert(!PresentationTarget.AllExcept(other).Selects(other, sender) && PresentationTarget.AllExcept(other).Selects(sender, sender),
            "all-except excludes exactly one participant");

        foreach (var audience in new[] { MultiplayerAudience.Everyone, MultiplayerAudience.Sender, MultiplayerAudience.Others })
        {
            Assert(PresentationTarget.FromAudience(audience).TryGetAudience(out var roundTrip) && roundTrip == audience,
                "legacy audiences round-trip through targets");
        }

        Assert(!PresentationTarget.Only(other).TryGetAudience(out _) && !PresentationTarget.AllExcept(other).TryGetAudience(out _),
            "named targets have no legacy audience");
        Assert(PresentationTarget.Only(other).Equals(PresentationTarget.Only(new ParticipantId("other"))) &&
               !PresentationTarget.Only(other).Equals(PresentationTarget.AllExcept(other)),
            "targets compare by kind and participant");
        AssertThrows<ArgumentException>(() => PresentationTarget.Only(default), "a named target needs a real participant");

        var buffered = new List<PresentationTarget>();
        var context = new MultiplayerCommandContext(
            sender,
            new NetworkTick(1),
            1,
            new SessionSeed(1),
            CancellationToken.None,
            (_, _, target) =>
            {
                buffered.Add(target);
                return OperationResult<bool>.Success(true);
            });
        var eventType = new PresentationEventType<TestEvent>("targeted", EventCodec);
        Assert(context.Emit(eventType, new TestEvent("a"), PresentationTarget.Only(other)).Succeeded &&
               context.Emit(eventType, new TestEvent("b"), MultiplayerAudience.Others).Succeeded,
            "command contexts buffer targeted and legacy events");
        Assert(buffered.SequenceEqual(new[] { PresentationTarget.Only(other), PresentationTarget.Others }),
            "legacy audiences reach target-aware providers as equivalent targets");

        var legacyProvider = new MultiplayerCommandContext(
            sender,
            new NetworkTick(1),
            CancellationToken.None,
            (_, _, _) => OperationResult<bool>.Success(true));
        var refused = legacyProvider.Emit(eventType, new TestEvent("c"), PresentationTarget.AllExcept(other));
        Assert(!refused.Succeeded && refused.ErrorCode == ModErrorCode.Unavailable,
            "an audience-only provider refuses named targets instead of widening them");
    }

    internal static void ParticipantsAndSnapshotsCarryHostAndEndReason()
    {
        var host = new ParticipantId("host");
        var guest = new ParticipantId("guest");
        var key = new MultiplayerPlayerKey("k:4f1c");
        Assert(Ping(44) == 40 && Ping(45) == 50 && Ping(0) == 0 && Ping(null) == null,
            "pings round to the nearest 10 ms");
        AssertThrows<ArgumentOutOfRangeException>(() => Ping(-1), "negative pings are rejected");
        AssertThrows<ArgumentOutOfRangeException>(() => Ping(60001), "implausible pings are rejected");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new MultiplayerParticipant(guest, "Guest", false, true, false, (MultiplayerConnectionQuality)9, null, key),
            "undefined quality buckets are rejected");

        var hostRecord = new MultiplayerParticipant(host, "Host", true, true, true, MultiplayerConnectionQuality.Excellent, 0, key);
        var guestRecord = new MultiplayerParticipant(guest, "Guest", false, true, false, MultiplayerConnectionQuality.Good, 83, default);
        Assert(hostRecord.IsHost && hostRecord.PlayerKey.Equals(key) && guestRecord.PingMilliseconds == 80 &&
               !guestRecord.PlayerKey.IsValid,
            "participants carry host, quality, ping, and key");
        Assert(!new MultiplayerParticipant(guest, "Guest", false, true).IsHost, "the legacy constructor is not a host");

        var snapshot = Snapshot(MultiplayerSessionState.Ready, host, MultiplayerSessionEndReason.None, hostRecord, guestRecord);
        Assert(snapshot.HostParticipantId.Equals(host) && snapshot.EndReason == MultiplayerSessionEndReason.None,
            "snapshots name the host");
        Assert(snapshot.TryGetParticipant(guest, out var found) && ReferenceEquals(found, guestRecord) &&
               !snapshot.TryGetParticipant(new ParticipantId("missing"), out var missing) && missing == null,
            "snapshots find participants by identity");
        AssertThrows<ArgumentException>(
            () => Snapshot(MultiplayerSessionState.Ready, guest, MultiplayerSessionEndReason.None, hostRecord, guestRecord),
            "the host id must name the participant marked as host");
        AssertThrows<ArgumentException>(
            () => Snapshot(MultiplayerSessionState.Ready, null, MultiplayerSessionEndReason.None, hostRecord, guestRecord),
            "a host marker needs a host id");
        AssertThrows<ArgumentException>(
            () => Snapshot(
                MultiplayerSessionState.Ready,
                host,
                MultiplayerSessionEndReason.None,
                hostRecord,
                new MultiplayerParticipant(guest, "Guest", false, true, true, MultiplayerConnectionQuality.Good, null, default)),
            "a session has at most one host");
        AssertThrows<ArgumentException>(
            () => Snapshot(MultiplayerSessionState.Ready, host, MultiplayerSessionEndReason.Kicked, hostRecord),
            "only an ended session reports an end reason");
        var ended = Snapshot(MultiplayerSessionState.Ended, host, MultiplayerSessionEndReason.HostEnded, hostRecord);
        Assert(ended.EndReason == MultiplayerSessionEndReason.HostEnded, "ended sessions report why");
        var reconnecting = Snapshot(MultiplayerSessionState.Reconnecting, host, MultiplayerSessionEndReason.None, hostRecord);
        Assert(reconnecting.State == MultiplayerSessionState.Reconnecting, "reconnecting is a defined session state");
        Assert(MultiplayerModule.ProtocolVersion == "1.0.0" && MultiplayerModule.PeerVersionRange == ">=1.0.0 <2.0.0",
            "the protocol line is unchanged");
    }

    internal static void PosesAndDefinitionsValidateBoundedData()
    {
        var aim = new Ray(new Vec3(0, 1.6f, 0), new Vec3(0, 0, 1));
        var pose = new MultiplayerPose(new NetworkTick(5), new Vec3(1, 0, 2), aim, new Vec3(0, 0, 3), 80, 100,
            MultiplayerPoseFlags.Crouching | MultiplayerPoseFlags.Talking, string.Empty);
        Assert(pose.HeldItemId == null && pose.Flags.HasFlag(MultiplayerPoseFlags.Talking), "empty held items normalize to null");
        AssertThrows<ArgumentException>(
            () => new MultiplayerPose(new NetworkTick(5), new Vec3(float.NaN, 0, 0), aim, default, 1, 1),
            "poses reject non-finite positions");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new MultiplayerPose(new NetworkTick(5), default, aim, default, 120, 100),
            "health cannot exceed the maximum");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new MultiplayerPose(new NetworkTick(5), default, aim, default, 1, 1, (MultiplayerPoseFlags)64),
            "undefined pose flags are rejected");
        AssertThrows<ArgumentException>(
            () => new MultiplayerPose(new NetworkTick(5), default, aim, default, 1, 1, heldItemId: new string('x', 129)),
            "held item ids are bounded");

        var state = new ReplicatedStateDefinition<CounterValue>("state", new CounterValue(0), CounterCodec,
            MultiplayerDelivery.Immediate, 30);
        Assert(state.Delivery == MultiplayerDelivery.Immediate && state.MaximumUpdatesPerSecond == 30, "state knobs are kept");
        var defaults = new ReplicatedStateDefinition<CounterValue>("state", new CounterValue(0), CounterCodec);
        Assert(defaults.Delivery == MultiplayerDelivery.Coalesced && defaults.MaximumUpdatesPerSecond == 0,
            "the legacy constructor keeps coalesced provider-rate delivery");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new ReplicatedStateDefinition<CounterValue>("state", new CounterValue(0), CounterCodec,
                MultiplayerDelivery.Coalesced, 61),
            "update rates are bounded");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new ReplicatedStateDefinition<CounterValue>("state", new CounterValue(0), CounterCodec,
                (MultiplayerDelivery)7, 0),
            "undefined delivery modes are rejected");
        var attribute = new ReplicatedObjectAttribute("object");
        Assert(attribute.Delivery == MultiplayerDelivery.Coalesced && attribute.MaximumUpdatesPerSecond == 0,
            "attribute knobs default to the values that leave generated digests unchanged");

        AssertThrows<ArgumentException>(() => new ScoreboardColumnDefinition("", "Score"), "columns need an id");
        AssertThrows<ArgumentException>(
            () => new ScoreboardColumnDefinition("score", new string('S', 65)),
            "column titles are bounded");
    }

    internal static void RigCommandsSharePredictionInputsAndTargetEvents()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var first = rig.AddRemoteClient("first");
        var second = rig.AddRemoteClient("second");
        var commandType = new MultiplayerCommandType<AddRequest, CounterValue>("roll");
        var eventType = new PresentationEventType<TestEvent>("whisper", EventCodec);
        var draws = new List<(ulong Sequence, ulong Seed, ulong Draw)>();
        var received = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in new[] { rig.Server, first, second })
        {
            var name = node.ParticipantId?.Value ?? "server";
            Require(
                node.Session.RegisterPresentation(new PresentationEventDefinition<TestEvent>(
                    eventType,
                    node.Session.Snapshot.HasPresentation ? _ => received[name] = received.GetValueOrDefault(name) + 1 : null)),
                "register whisper on " + name);
            Require(
                node.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                    commandType,
                    RequestCodec,
                    CounterCodec,
                    (command, request) =>
                    {
                        draws.Add((command.Sequence, command.Seed.Value, command.CreateRandom().NextUInt64()));
                        var emitted = command.Emit(eventType, new TestEvent("psst"), PresentationTarget.Only(new ParticipantId("second")));
                        return emitted.Succeeded
                            ? OperationResult<CounterValue>.Success(new CounterValue(request.Amount))
                            : OperationResult<CounterValue>.Failure(emitted.ErrorCode, emitted.ErrorMessage);
                    },
                    PredictionMode.Owner)),
                "register roll on " + name);
        }

        rig.Flush();
        var submission = first.Session.SubmitAsync(commandType, new AddRequest(1));
        rig.Flush();
        Assert(submission.GetAwaiter().GetResult().Result.Succeeded, "the predicted command is accepted");
        Assert(draws.Count == 2 && draws[0] == draws[1],
            "prediction and the canonical run see the same sequence, seed, and random draw");
        Assert(received.GetValueOrDefault("second") == 1 && !received.ContainsKey("first"),
            "a targeted event reaches only the named participant");
    }

    internal static void RigTicksRunOnlyWithWorldAuthority()
    {
        using var rig = MultiplayerTestRig.CreateListenServer();
        var client = rig.AddRemoteClient("guest");
        rig.Flush();
        var serverTicks = new List<MultiplayerTickContext>();
        var clientTicks = 0;
        using var serverSubscription = rig.Server.Session.SubscribeTick(serverTicks.Add);
        using var clientSubscription = client.Session.SubscribeTick(_ => clientTicks++);
        rig.Advance(3);
        Assert(serverTicks.Count == 3 && clientTicks == 0, "only the authority receives canonical steps");
        Assert(serverTicks[2].Tick.Value == serverTicks[0].Tick.Value + 2 &&
               Math.Abs(serverTicks[0].DeltaSeconds - 1.0 / MultiplayerTestRig.TicksPerSecond) < 1e-12,
            "steps are consecutive and report the rig's fixed step");
    }

    internal static void InterimProvidersRegisterContractsAndExposePlayers()
    {
        using var rig = MultiplayerTestRig.CreateListenServer();
        var descriptor = new MultiplayerContractDescriptor(
            "example.contract",
            1,
            new string('a', 64),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var registered = rig.Server.Session.RegisterContract(descriptor);
        Assert(registered.Succeeded, "a contract registers once");
        var duplicate = rig.Server.Session.RegisterContract(descriptor);
        Assert(!duplicate.Succeeded && duplicate.ErrorCode == ModErrorCode.Conflict, "contract ids are unique per node");
        registered.Value!.Dispose();
        using var again = Require(rig.Server.Session.RegisterContract(descriptor), "a disposed contract can register again");

        var players = rig.Server.Session.Players;
        var local = players.Local;
        Assert(local != null && local.IsLocal && local.IsPresent && players.Players.Count == 1,
            "the directory lists the local player");
        Assert(players.TryGet(local!.Id, out var same) && ReferenceEquals(same, local), "player handles are stable");
        var damaged = local.Damage(new PlayerDamageRequest(1, "test"));
        Assert(!damaged.Succeeded && damaged.ErrorCode == ModErrorCode.Unavailable,
            "player actions report that they need the live engine");
        Assert(!rig.Server.Session.TryGetPose(local.Id, out _) && !rig.Server.Session.TryGetAvatar(local.Id, out _),
            "the interim rig has no replicated presence");
    }

    internal static void SubmissionSequencesAreSenderScoped()
    {
        using var rig = MultiplayerTestRig.CreateDedicatedServer();
        var client = rig.AddRemoteClient("pilot");
        var objectType = new ReplicatedObjectType<CounterValue, AddRequest>("sequenced");
        var canonical = new List<ulong>();
        foreach (var node in new[] { rig.Server, client })
        {
            var isServer = ReferenceEquals(node, rig.Server);
            Require(
                node.Session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
                    objectType,
                    CounterCodec,
                    RequestCodec,
                    (command, state, input) =>
                    {
                        if (isServer) canonical.Add(command.Sequence);
                        return OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount));
                    })),
                "register sequenced objects");
        }

        rig.Flush();
        var first = Require(rig.Server.Session.SpawnObject(objectType, new CounterValue(0), client.ParticipantId), "spawn first");
        var second = Require(rig.Server.Session.SpawnObject(objectType, new CounterValue(0), client.ParticipantId), "spawn second");
        rig.Flush();
        var a = rig.GetObject<CounterValue, AddRequest>(client, first.Id).SubmitInputAsync(new AddRequest(1));
        var b = rig.GetObject<CounterValue, AddRequest>(client, second.Id).SubmitInputAsync(new AddRequest(1));
        rig.Flush();
        Assert(a.GetAwaiter().GetResult().Result.Succeeded && b.GetAwaiter().GetResult().Result.Succeeded &&
               canonical.Count == 2 && canonical[0] != canonical[1],
            "inputs from one sender to different objects get distinct sequences, so their random streams differ");

        using var loopback = new global::TopiaForge.Multiplayer.LoopbackMultiplayerSession("sequence-test");
        var roll = new MultiplayerCommandType<AddRequest, CounterValue>("roll");
        var draws = new List<(ulong Sequence, ulong Draw)>();
        Require(
            loopback.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                roll,
                RequestCodec,
                CounterCodec,
                (command, request) =>
                {
                    draws.Add((command.Sequence, command.CreateRandom().NextUInt64()));
                    return OperationResult<CounterValue>.Success(new CounterValue(request.Amount));
                })),
            "register loopback roll");
        loopback.SubmitAsync(roll, new AddRequest(1)).GetAwaiter().GetResult();
        loopback.SubmitAsync(roll, new AddRequest(1)).GetAwaiter().GetResult();
        Assert(draws.Count == 2 && draws[0].Sequence != draws[1].Sequence && draws[0].Draw != draws[1].Draw,
            "loopback commands get distinct sequences and therefore distinct random streams");
    }

    private static int? Ping(int? milliseconds) =>
        new MultiplayerParticipant(
            new ParticipantId("p"), "Player", false, true, false, MultiplayerConnectionQuality.Unknown, milliseconds, default)
            .PingMilliseconds;

    private static MultiplayerSessionSnapshot Snapshot(
        MultiplayerSessionState state,
        ParticipantId? host,
        MultiplayerSessionEndReason reason,
        params MultiplayerParticipant[] participants) =>
        new(
            new MultiplayerSessionId("s"),
            state,
            MultiplayerProcessKind.Interactive,
            MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server,
            participants.FirstOrDefault(item => item.IsLocal)?.Id,
            participants,
            new NetworkTick(0),
            new SessionSeed(1),
            host,
            reason);

    private static void AssertSequence(DeterministicRandom random, params ulong[] expected)
    {
        foreach (var value in expected)
        {
            var actual = random.NextUInt64();
            Assert(actual == value, "expected 0x" + value.ToString("X16") + " but drew 0x" + actual.ToString("X16"));
        }
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException(message + ": " + result.ErrorMessage);
        return value;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("SDK contract test failed: " + message);
    }

    private static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("SDK contract test failed: " + message);
    }
}
