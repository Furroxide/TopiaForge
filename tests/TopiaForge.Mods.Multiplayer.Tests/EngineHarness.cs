using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Drives session engines directly over the virtual transport. Unlike the public rig, it exposes raw links, so a test
/// can speak the protocol itself and play a misbehaving peer.
/// </summary>
internal sealed class EngineHarness : IDisposable
{
    internal const string Owner = "white-box";
    private readonly FixedStepClock clock = new(60);
    private readonly List<SessionEngine> engines = new();

    internal EngineHarness(
        MultiplayerProcessKind hostKind = MultiplayerProcessKind.Headless,
        Action<EngineOptions>? configure = null,
        int latencyTicks = 0)
    {
        Options = new EngineOptions
        {
            StepRateHz = 60,
            DownstreamRateHz = 0,
            ClockSyncIntervalMicros = 0,
            ObjectIdPrefix = "box-",
            CallbackFailures = EngineCallbackFailureMode.Propagate,
            Diagnostics = Diagnostics,
            ParticipantIdForIdentity = identity => new ParticipantId(identity)
        };
        configure?.Invoke(Options);
        Network = new VirtualEngineTransport(new VirtualLinkConditions { LatencyTicks = latencyTicks });
        var hostParticipant = hostKind == MultiplayerProcessKind.Interactive ? new ParticipantId("host") : (ParticipantId?)null;
        Host = SessionEngine.CreateHost(
            Options,
            hostKind,
            new MultiplayerSessionId("white-box-session"),
            new SessionSeed(42),
            hostParticipant,
            "Host",
            0);
        HostSession = Host.CreateFacade(Owner);
        engines.Add(Host);
    }

    internal EngineOptions Options { get; }

    internal RecordingDiagnostics Diagnostics { get; } = new();

    internal VirtualEngineTransport Network { get; }

    internal SessionEngine Host { get; }

    internal EngineSessionFacade HostSession { get; }

    internal ulong Tick { get; private set; }

    internal long NowMicros => clock.StepStartMicros(Tick);

    internal (SessionEngine Engine, EngineSessionFacade Session, VirtualLinkEnd Link) AddClient(string identity)
    {
        var engine = SessionEngine.CreateClient(Options, new MultiplayerSessionId("white-box-session"), NowMicros);
        engines.Add(engine);
        var pair = Network.Connect(identity);
        Host.Accept(pair.HostEnd);
        engine.Connect(pair.ClientEnd, identity);
        return (engine, engine.CreateFacade(Owner), pair.ClientEnd);
    }

    internal RawPeer AddRawPeer(string identity)
    {
        var pair = Network.Connect(identity);
        Host.Accept(pair.HostEnd);
        return new RawPeer(pair.ClientEnd);
    }

    internal void Advance(int ticks = 1)
    {
        for (var index = 0; index < ticks; index++)
        {
            PumpAll();
            Tick++;
            Network.Now = Tick;
            Network.DeliverDue(Tick);
            PumpAll();
        }
    }

    internal void Flush(int maximumTicks = 256)
    {
        PumpAll();
        var count = 0;
        while ((Network.HasInFlight || engines.Any(engine => !engine.IsEnded && engine.HasPendingWork)) && count++ < maximumTicks)
        {
            Advance();
        }
    }

    internal void PumpAll()
    {
        Network.Now = Tick;
        foreach (var engine in engines.ToArray())
        {
            if (!engine.IsEnded) engine.Pump(NowMicros);
        }
    }

    public void Dispose()
    {
        foreach (var engine in engines)
        {
            if (!engine.IsEnded) engine.Shutdown(MultiplayerSessionEndReason.HostEnded);
        }
    }
}

/// <summary>
/// One client engine whose host is a <see cref="RawPeer"/>, so a test decides exactly which frames arrive and when the
/// client pumps between them.
/// </summary>
internal sealed class RawHostClient
{
    private ulong tick;

    internal RawHostClient()
    {
        Network = new VirtualEngineTransport(new VirtualLinkConditions());
        var pair = Network.Connect("client");
        Engine = SessionEngine.CreateClient(
            new EngineOptions
            {
                StepRateHz = 60,
                Diagnostics = Diagnostics,
                CallbackFailures = EngineCallbackFailureMode.Propagate
            },
            new MultiplayerSessionId("placeholder"),
            0);
        Session = Engine.CreateFacade(EngineHarness.Owner);
        ClientEnd = pair.ClientEnd;
        Host = new RawPeer(pair.HostEnd);
    }

    internal RecordingDiagnostics Diagnostics { get; } = new();

    internal VirtualEngineTransport Network { get; }

    internal SessionEngine Engine { get; }

    internal EngineSessionFacade Session { get; }

    internal VirtualLinkEnd ClientEnd { get; }

    internal RawPeer Host { get; }

    /// <summary>Gets the host's intern table toward this client; every interned message a test writes must share it.</summary>
    internal InternWriter Intern { get; } = new();

    /// <summary>Connects and accepts the join with an empty snapshot, leaving the client ready as participant "client".</summary>
    internal void Join()
    {
        Engine.Connect(ClientEnd, "Client");
        Host.Send((frame, body) =>
        {
            JoinMessages.WriteJoinAccepted(frame, body, new JoinAcceptedMessage
            {
                SessionId = "raw-session",
                Seed = 1,
                ParticipantId = "client",
                StepRateHz = 60,
                NextSequence = 1
            });
            SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, 0);
            SessionMessages.WriteEmpty(frame, body, MessageType.SnapshotEnd);
            SessionMessages.WriteParticipants(frame, body, new[]
            {
                new ParticipantEntry("client", "Client", true, false, MultiplayerConnectionQuality.Unknown, null, null)
            });
            SessionMessages.WriteCommit(frame, body, 0, 0);
        });
        Pump();
        Pump();
        Host.Receive();
    }

    /// <summary>Moves one tick: frames in flight arrive, then the client pumps once.</summary>
    internal void Pump()
    {
        tick++;
        Network.Now = tick;
        Network.DeliverDue(tick);
        Engine.Pump((long)(tick * 1_000_000UL / 60));
    }
}

internal sealed class RecordingDiagnostics : IEngineDiagnostics
{
    internal List<EngineDiagnostic> Reports { get; } = new();

    public void Report(EngineDiagnostic diagnostic) => Reports.Add(diagnostic);

    internal int Count(EngineDiagnosticKind kind) => Reports.Count(report => report.Kind == kind);
}

/// <summary>A peer that writes and reads protocol frames itself, with no engine behind it.</summary>
internal sealed class RawPeer
{
    private readonly ProtocolWriter frame = new();
    private readonly ProtocolWriter body = new();

    internal RawPeer(VirtualLinkEnd end)
    {
        End = end;
    }

    internal VirtualLinkEnd End { get; }

    internal void Send(Action<ProtocolWriter, ProtocolWriter> write)
    {
        frame.Reset();
        write(frame, body);
        End.Send(EngineChannel.Session, frame.AsSegment());
    }

    internal void SendRaw(params byte[] bytes) => End.Send(EngineChannel.Session, new ArraySegment<byte>(bytes));

    internal void Join(string displayName, IReadOnlyList<ContractEntry>? contracts = null, string protocolVersion = SessionEngine.ProtocolVersion) =>
        Send((f, b) => JoinMessages.WriteJoinRequest(
            f,
            b,
            new JoinRequestMessage(protocolVersion, displayName, contracts ?? Array.Empty<ContractEntry>(), Array.Empty<byte>())));

    /// <summary>Writes a command with every string defined inline, so it never depends on earlier intern state.</summary>
    internal void SendCommand(ulong epoch, ulong sequence, string commandId, byte[] payload) =>
        Send((f, b) => ReplicationMessages.WriteCommand(f, b, new InternWriter(), epoch, sequence, EngineHarness.Owner, commandId, payload));

    internal List<ReceivedMessage> Receive()
    {
        var messages = new List<ReceivedMessage>();
        while (End.TryReceive(out _, out var bytes))
        {
            var reader = new ProtocolReader(bytes);
            while (!reader.AtEnd)
            {
                var type = reader.ReadVarUInt();
                var length = reader.ReadLength(ProtocolLimits.MaximumFrameBytes);
                var start = bytes.Length - reader.Remaining;
                var copy = new byte[length];
                Buffer.BlockCopy(bytes, start, copy, 0, length);
                reader.Skip(length);
                messages.Add(new ReceivedMessage(type, copy));
            }
        }

        return messages;
    }
}

internal readonly struct ReceivedMessage
{
    internal ReceivedMessage(ulong type, byte[] body)
    {
        Type = type;
        Body = body;
    }

    internal ulong Type { get; }
    internal byte[] Body { get; }

    internal ProtocolReader Reader() => new(Body);
}
