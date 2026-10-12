using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Engine-level robustness: what a misbehaving or malicious peer can send, the protocol primitives, and the engine
/// guarantees the public rig cannot reach. The rig's ordered transport never produces stale or duplicate submissions,
/// so these tests speak the protocol directly.
/// </summary>
internal static class EngineWhiteBoxTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddCommandType = new("add");

    internal static void StaleDuplicateAndOldEpochSubmissionsAreRejected()
    {
        using var harness = new EngineHarness();
        var executions = 0;
        var state = Require(harness.HostSession.RegisterState(
            new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "register score");
        Require(harness.HostSession.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddCommandType,
            RequestCodec,
            CounterCodec,
            (_, request) =>
            {
                executions++;
                return state.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount)));
            })), "register add");
        var peer = harness.AddRawPeer("raw");
        peer.Join("Raw");
        harness.Flush();
        var joined = peer.Receive().Select(message => message.Type).ToList();
        Assert(joined.Contains(MessageType.JoinAccepted) && joined.Contains(MessageType.SnapshotEnd) && joined[^1] == MessageType.Commit,
            "a raw join receives acceptance, a snapshot and a commit");

        var payload = Encode(RequestCodec, new AddRequest(1));
        peer.SendCommand(0, 5, "add", payload);
        peer.SendCommand(0, 5, "add", payload);
        peer.SendCommand(0, 3, "add", payload);
        peer.SendCommand(7, 9, "add", payload);
        harness.Flush();

        var results = peer.Receive()
            .Where(message => message.Type == MessageType.CommandResult)
            .Select(message => ReplicationMessages.ReadResult(message.Reader()))
            .ToList();
        Assert(results.Count == 4 && results[0].Sequence == 5 && results[0].Outcome.Succeeded,
            "the first submission executes");
        Assert(results.Skip(1).All(result => !result.Outcome.Succeeded && result.Outcome.Code == ModErrorCode.Conflict),
            "a duplicate, a stale sequence and an old session epoch are rejected as conflicts");
        Assert(executions == 1 && state.Value.Value == 1, "rejected submissions never reach the handler");
    }

    internal static void MalformedTrafficEndsOnlyTheOffendingConnection()
    {
        using var harness = new EngineHarness();
        var good = harness.AddClient("good");
        var early = harness.AddRawPeer("early");
        var truncated = harness.AddRawPeer("truncated");
        var unknown = harness.AddRawPeer("unknown");
        var undefined = harness.AddRawPeer("undefined");
        var tolerant = harness.AddRawPeer("tolerant");
        early.SendCommand(0, 1, "add", Encode(RequestCodec, new AddRequest(1)));
        foreach (var peer in new[] { truncated, unknown, undefined, tolerant }) peer.Join(peer.End.PeerIdentity);
        harness.Flush();
        foreach (var peer in new[] { truncated, unknown, undefined, tolerant }) peer.Receive();

        truncated.SendRaw(MessageType.Command, 100, 1, 2);
        unknown.SendRaw(0x3E, 0);
        undefined.Send((frame, body) =>
        {
            body.Reset();
            body.WriteVarUInt(0);
            body.WriteVarUInt(1);
            body.WriteVarUInt(17);
            body.WriteVarUInt(18);
            body.WriteBytes(new byte[] { 1 });
            frame.WriteEnvelope(MessageType.Command, body);
        });
        tolerant.Send((frame, body) =>
        {
            body.Reset();
            body.WriteString("a future extension");
            frame.WriteEnvelope(0x50, body);
            SessionMessages.WriteEpochMessage(frame, body, MessageType.ReadyAck, 0);
        });
        harness.Flush();

        foreach (var peer in new[] { early, truncated, unknown, undefined })
        {
            var ended = peer.Receive().LastOrDefault(message => message.Type == MessageType.SessionEnded);
            Assert(ended.Body != null && JoinMessages.ReadEndReason(ended.Reader()) == MultiplayerSessionEndReason.ProtocolMismatch &&
                   peer.End.State == EngineLinkState.Closed,
                "a violating peer (" + peer.End.PeerIdentity + ") is told why and disconnected");
        }

        Assert(harness.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 4, "each violation is reported once");
        Assert(good.Engine.Snapshot.State == MultiplayerSessionState.Ready, "a well-behaved client is unaffected");
        Assert(harness.Host.Snapshot.TryGetParticipant(new ParticipantId("tolerant"), out var tolerated) && tolerated!.IsConnected,
            "an unknown ignorable message is skipped by its length");
    }

    internal static void ClientEndsWhenTheHostViolatesTheProtocol()
    {
        var diagnostics = new RecordingDiagnostics();
        var options = new EngineOptions { StepRateHz = 60, Diagnostics = diagnostics };
        var network = new VirtualEngineTransport(new VirtualLinkConditions());
        var pair = network.Connect("client");
        var client = SessionEngine.CreateClient(options, new MultiplayerSessionId("placeholder"), 0);
        client.Connect(pair.ClientEnd, "Client");
        var host = new RawPeer(pair.HostEnd);
        host.Send((frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, 0));
        network.Now = 1;
        network.DeliverDue(1);
        client.Pump(20_000);
        Assert(client.Snapshot.State == MultiplayerSessionState.Ended &&
               client.Snapshot.EndReason == MultiplayerSessionEndReason.ProtocolMismatch &&
               diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 1,
            "a client ends the session, with a reason, when the host sends traffic before accepting it");
    }

    internal static void ProtocolPrimitivesRejectMalformedInput()
    {
        var writer = new ProtocolWriter();
        foreach (var value in new[] { 0UL, 127UL, 128UL, 1UL << 63, ulong.MaxValue })
        {
            writer.Reset();
            writer.WriteVarUInt(value);
            Assert(new ProtocolReader(writer.ToArray()).ReadVarUInt() == value, "varint round trip " + value);
        }

        AssertProtocolError(() => new ProtocolReader(new byte[] { 0x80, 0x00 }).ReadVarUInt(), "a non-minimal varint");
        AssertProtocolError(() => new ProtocolReader(Enumerable.Repeat((byte)0xFF, 9).Append((byte)0x02).ToArray()).ReadVarUInt(), "a 65-bit varint");
        AssertProtocolError(() => new ProtocolReader(Enumerable.Repeat((byte)0x80, 11).ToArray()).ReadVarUInt(), "an endless varint");
        AssertProtocolError(() => new ProtocolReader(new byte[] { 5, 1, 2 }).ReadBytes(64), "a length past the end");
        AssertProtocolError(() => new ProtocolReader(new byte[] { 2, 0xC3, 0x28 }).ReadString(8), "invalid UTF-8");
        AssertProtocolError(() => new ProtocolReader(new byte[] { 3, (byte)'a', 7, (byte)'b' }).ReadIdentity(), "a control character in an identity");
        AssertProtocolError(() => new ProtocolReader(new byte[] { 2 }).ReadBool(), "a boolean other than 0 or 1");

        var digest = new string('A', 64);
        writer.Reset();
        var body = new ProtocolWriter();
        JoinMessages.WriteJoinRequest(writer, body, new JoinRequestMessage(
            "1.4.0",
            "Player",
            new[] { new ContractEntry("owner.mod", "owner.contract", 2, digest) },
            new byte[] { 9 }));
        var join = ReadSingle(writer, MessageType.JoinRequest, JoinMessages.ReadJoinRequest);
        Assert(join.ProtocolVersion == "1.4.0" && join.DisplayName == "Player" && join.Content.SequenceEqual(new byte[] { 9 }) &&
               join.Contracts.Single().SchemaSha256 == new string('a', 64) && join.Contracts.Single().WireFormatRevision == 2,
            "a join request round-trips with its digest normalized to lowercase");

        writer.Reset();
        ReplicationMessages.WriteResult(writer, body, MessageType.CommandResult, 3, 4, WireOutcome.Failure((ModErrorCode)99, "odd"));
        var result = ReadSingle(writer, MessageType.CommandResult, ReplicationMessages.ReadResult);
        Assert(result.Sequence == 3 && result.ConfirmedAt == 4 && result.Outcome.Code == ModErrorCode.Unknown,
            "an unknown error code from a peer reads as Unknown");

        writer.Reset();
        var interning = new InternWriter();
        ReplicationMessages.WriteStateDelta(writer, body, interning, "owner.mod", "score", 1, new byte[] { 1 });
        var firstLength = writer.Length;
        ReplicationMessages.WriteStateDelta(writer, body, interning, "owner.mod", "score", 2, new byte[] { 2 });
        Assert(writer.Length - firstLength < firstLength, "a repeated owner and wire id travel as interned references");
        var reader = new ProtocolReader(writer.ToArray());
        var internReader = new InternReader();
        for (var version = 1UL; version <= 2; version++)
        {
            Assert(reader.ReadVarUInt() == MessageType.StateDelta, "state delta envelope");
            var envelope = reader.Slice(reader.ReadLength(1024));
            ReplicationMessages.ReadStateDelta(envelope, internReader, out var owner, out var id, out var readVersion, out var bytes);
            Assert(owner == "owner.mod" && id == "score" && readVersion == version && bytes[0] == version, "interned delta " + version);
        }

        AssertProtocolError(() => new InternReader().Read(new ProtocolReader(new byte[] { 1 })), "an interned id used before its definition");
    }

    internal static void SoloPumpDoesNotAllocate()
    {
        var options = new EngineOptions { StepRateHz = 20 };
        var engine = SessionEngine.CreateHost(
            options,
            MultiplayerProcessKind.Interactive,
            new MultiplayerSessionId("solo"),
            new SessionSeed(1),
            new ParticipantId("player"),
            "Player",
            0);
        var session = engine.CreateFacade("solo.mod");
        Require(session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "register state");
        var steps = 0;
        using var tick = session.SubscribeTick(_ => steps++);
        var clock = new FixedStepClock(20);
        var now = 0UL;
        for (; now < 100; now++) engine.Pump(clock.StepStartMicros(now));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (; now < 1100; now++) engine.Pump(clock.StepStartMicros(now));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated == 0, "a solo pump allocates nothing once warm (allocated " + allocated + " bytes)");
        Assert(steps == 1099 && engine.Snapshot.Tick.Value == 1099, "every step ran exactly once");
    }

    internal static void CatchUpStepsAreBounded()
    {
        var engine = SessionEngine.CreateHost(
            new EngineOptions { StepRateHz = 20, MaximumCatchUpSteps = 4 },
            MultiplayerProcessKind.Headless,
            new MultiplayerSessionId("catch-up"),
            new SessionSeed(1),
            null,
            "Host",
            0);
        var session = engine.CreateFacade("catch.mod");
        var ticks = new List<ulong>();
        using var tick = session.SubscribeTick(context => ticks.Add(context.Tick.Value));
        var clock = new FixedStepClock(20);
        engine.Pump(clock.StepStartMicros(1));
        engine.Pump(clock.StepStartMicros(100));
        Assert(ticks.SequenceEqual(new ulong[] { 1, 97, 98, 99, 100 }) && engine.Snapshot.Tick.Value == 100,
            "a long stall runs at most four catch-up steps and the tick stays aligned with time");
    }

    internal static void DeltasFollowTheirDeliveryKnobs()
    {
        using var harness = new EngineHarness(configure: options => options.DownstreamRateHz = 10);
        var coalesced = Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>(
            "coalesced", new CounterValue(0), CounterCodec)), "coalesced");
        var immediate = Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>(
            "immediate", new CounterValue(0), CounterCodec, MultiplayerDelivery.Immediate, 0)), "immediate");
        var limited = Require(harness.HostSession.RegisterState(new ReplicatedStateDefinition<CounterValue>(
            "limited", new CounterValue(0), CounterCodec, MultiplayerDelivery.Immediate, 5)), "limited");
        var client = harness.AddClient("observer");
        var counts = new Dictionary<string, int>();
        foreach (var id in new[] { "coalesced", "immediate", "limited" })
        {
            var mirror = Require(client.Session.RegisterState(new ReplicatedStateDefinition<CounterValue>(
                id, new CounterValue(0), CounterCodec)), "mirror " + id);
            mirror.SubscribeChanged(_ => counts[id] = counts.GetValueOrDefault(id) + 1);
        }

        harness.Flush();
        counts.Clear();
        for (var step = 1; step <= 60; step++)
        {
            foreach (var state in new[] { coalesced, immediate, limited })
            {
                Require(state.Update(current => OperationResult<CounterValue>.Success(new CounterValue(step))), "update");
            }

            harness.Advance();
        }

        harness.Flush();
        Assert(counts["immediate"] == 60, "an immediate state reaches the client every step (" + counts["immediate"] + ")");
        Assert(counts["coalesced"] >= 9 && counts["coalesced"] <= 11,
            "a coalesced state follows the 10 Hz downstream rate (" + counts["coalesced"] + ")");
        Assert(counts["limited"] >= 4 && counts["limited"] <= 6,
            "a per-state update limit caps even immediate delivery (" + counts["limited"] + ")");
    }

    internal static void CancellationFromAnotherThreadCompletesAtTheNextPump()
    {
        using var harness = new EngineHarness();
        Require(harness.HostSession.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddCommandType, RequestCodec, CounterCodec, (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)))),
            "register host command");
        var client = harness.AddClient("canceller");
        Require(client.Session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddCommandType, RequestCodec, CounterCodec, (_, request) => OperationResult<CounterValue>.Success(new CounterValue(request.Amount)))),
            "register client command");
        harness.Flush();
        using var cancellation = new CancellationTokenSource();
        var submission = client.Session.SubmitAsync(AddCommandType, new AddRequest(1), cancellation.Token);
        Task.Run(() => cancellation.Cancel()).GetAwaiter().GetResult();
        Assert(!submission.IsCompleted, "a cancellation from another thread waits for the owner thread");
        harness.Advance();
        Assert(submission.IsCompleted && submission.GetAwaiter().GetResult().Result.ErrorCode == ModErrorCode.Cancelled,
            "the next pump completes the submission as cancelled before it is ever sent");
    }

    internal static void MismatchedContractsAreRejectedAtJoin()
    {
        using var harness = new EngineHarness();
        Require(harness.HostSession.RegisterContract(Descriptor(new string('a', 64))), "host contract");
        var client = harness.AddClient("mismatched");
        Require(client.Session.RegisterContract(Descriptor(new string('b', 64))), "client contract");
        harness.Flush();
        Assert(client.Engine.Snapshot.State == MultiplayerSessionState.Ended &&
               client.Engine.Snapshot.EndReason == MultiplayerSessionEndReason.ProtocolMismatch,
            "a client whose contract digest differs is refused with ProtocolMismatch");
        Assert(!harness.Host.Snapshot.TryGetParticipant(new ParticipantId("mismatched"), out _),
            "a refused client never becomes a participant");
    }

    internal static void DepartedParticipantsExpireAfterTheGracePeriod()
    {
        using var harness = new EngineHarness(configure: options => options.ParticipantGraceMicros = 1_000_000);
        var client = harness.AddClient("leaver");
        harness.Flush();
        harness.Network.Sever(client.Link);
        harness.Advance();
        Assert(harness.Host.Snapshot.TryGetParticipant(new ParticipantId("leaver"), out var departed) && !departed!.IsConnected,
            "a departed participant stays listed, disconnected, during its grace period");
        harness.Advance(61);
        Assert(!harness.Host.Snapshot.TryGetParticipant(new ParticipantId("leaver"), out _),
            "the record expires once the grace period passes");
    }

    private static MultiplayerContractDescriptor Descriptor(string digest) =>
        new("white-box.contract", 1, digest, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    private static byte[] Encode<T>(IMultiplayerCodec<T> codec, T value) where T : class =>
        codec.Encode(value).Value ?? throw new InvalidOperationException("encode");

    private static T ReadSingle<T>(ProtocolWriter writer, byte expectedType, Func<ProtocolReader, T> read)
    {
        var reader = new ProtocolReader(writer.ToArray());
        Assert(reader.ReadVarUInt() == expectedType, "envelope type " + expectedType);
        var value = read(reader.Slice(reader.ReadLength(ProtocolLimits.MaximumFrameBytes)));
        Assert(reader.AtEnd, "one envelope");
        return value;
    }

    private static void AssertProtocolError(Action action, string what)
    {
        try
        {
            action();
        }
        catch (ProtocolException)
        {
            return;
        }

        throw new InvalidOperationException("Assertion failed: expected a protocol error for " + what + ".");
    }

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
