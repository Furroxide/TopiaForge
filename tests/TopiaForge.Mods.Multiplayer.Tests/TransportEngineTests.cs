extern alias provider;

using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Transport;
using CallbackFailureMode = provider::TopiaForge.Multiplayer.Engine.EngineCallbackFailureMode;
using EngineOptions = provider::TopiaForge.Multiplayer.Engine.EngineOptions;
using SessionEngine = provider::TopiaForge.Multiplayer.Engine.SessionEngine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The provider's own session engine, host and client, joined by the secure transport on a lossy network.</summary>
internal static class TransportEngineTests
{
    private const string Owner = "transport-test";
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddCommandType = new("add");

    internal static void EnginesRunASessionOverTheTransport()
    {
        var pair = new TransportPair(40);
        var conditions = pair.Network.Conditions;
        conditions.LatencyMicros = 25_000;
        conditions.JitterMicros = 10_000;
        conditions.LossRate = 0.05;
        var options = new EngineOptions
        {
            StepRateHz = 20,
            CallbackFailures = CallbackFailureMode.Propagate,
            ParticipantIdForIdentity = identity => new ParticipantId("player-" + identity.Substring(0, 8))
        };
        var sessionId = new MultiplayerSessionId("transport-session");
        var host = SessionEngine.CreateHost(options, MultiplayerProcessKind.Interactive, sessionId, new SessionSeed(7), new ParticipantId("host"), "Host", pair.Network.Now);
        var hostSession = host.CreateFacade(Owner);
        var hostScore = Register(hostSession);
        var (clientConnection, hostConnection) = pair.Establish(maximumMicros: 10_000_000);
        var client = SessionEngine.CreateClient(options, sessionId, pair.Network.Now);
        var clientSession = client.CreateFacade(Owner);
        var clientScore = Register(clientSession);
        host.Accept(new TransportEngineLink(hostConnection));
        client.Connect(new TransportEngineLink(clientConnection), "Client");
        That(Run(pair, host, client, () => client.Snapshot.State == MultiplayerSessionState.Ready, 20_000_000), "the client joins over the transport");
        var expected = new ParticipantId("player-" + Convert.ToHexString(pair.ClientNode.Endpoint!.StaticPublicKey).ToLowerInvariant().Substring(0, 8));
        That(host.Snapshot.TryGetParticipant(expected, out var joined) && joined!.IsConnected,
            "the host names the participant by its authenticated key, never by an address");

        var submission = clientSession.SubmitAsync(AddCommandType, new AddRequest(5));
        That(Run(pair, host, client, () => submission.IsCompleted && clientScore.Value.Value == 5, 10_000_000), "a command round-trips");
        That(submission.Result.Result.Succeeded && hostScore.Value.Value == 5, "the host ran it once and the client converged");

        client.Shutdown(MultiplayerSessionEndReason.Left);
        That(Run(pair, host, client, () => host.Snapshot.TryGetParticipant(expected, out var left) && !left!.IsConnected, 15_000_000),
            "leaving closes the link and the host sees the participant go");
        // Both engines close their links; whichever side's stream is acknowledged first sends the CLOSE.
        That(pair.Network.RunUntil(
                () => clientConnection.State == TransportConnectionState.Closed && hostConnection.State == TransportConnectionState.Closed,
                10_000_000) && EndedNormally(clientConnection) && EndedNormally(hostConnection),
            "both connections end normally (" + clientConnection.CloseReason + ", " + hostConnection.CloseReason + ")");
        That(pair.Client.ConnectionCount == 0 && pair.Host.ConnectionCount == 0, "and the endpoints forget them");
        That(pair.Network.Spins == 0, "no endpoint spun");
        host.Shutdown(MultiplayerSessionEndReason.HostEnded);
    }

    private static bool EndedNormally(TransportConnection connection) =>
        connection.CloseReason == TransportCloseReason.Local ||
        (connection.CloseReason == TransportCloseReason.Peer && connection.PeerCloseCode == provider::TopiaForge.Multiplayer.Transport.Packets.CloseCode.Normal);

    private static IReplicatedState<CounterValue> Register(IMultiplayerSession session)
    {
        var score = Require(session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "score");
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddCommandType,
            RequestCodec,
            CounterCodec,
            (_, request) => score.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount))))), "add");
        return score;
    }

    /// <summary>Pumps both engines every 5 ms of network time until <paramref name="condition"/> holds.</summary>
    private static bool Run(TransportPair pair, SessionEngine host, SessionEngine client, Func<bool> condition, long maximumMicros)
    {
        var deadline = pair.Network.Now + maximumMicros;
        while (pair.Network.Now < deadline)
        {
            if (!host.IsEnded) host.Pump(pair.Network.Now);
            if (!client.IsEnded) client.Pump(pair.Network.Now);
            if (condition()) return true;
            pair.Network.RunFor(5_000);
        }

        return false;
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }
}
