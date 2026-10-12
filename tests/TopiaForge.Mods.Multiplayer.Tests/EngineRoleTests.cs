using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// One engine for a process's whole life: a solo session joins another host and comes back, and a host closes its
/// connections and plays on. Mods see each change as a replacement; their registrations and subscriptions survive.
/// </summary>
internal static class EngineRoleTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddCommandType = new("add");
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> MarkerType = new("marker");

    internal static void ASoloSessionJoinsAnotherHostAndComesBack()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var hostScore = Register(harness.HostSession);
        Run(harness.HostSession.SubmitAsync(AddCommandType, new AddRequest(3)), harness, "the host scores 3");

        var solo = SessionEngine.CreateHost(harness.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("solo-one"), new SessionSeed(1),
            new ParticipantId("traveller"), "Traveller", harness.NowMicros);
        harness.Track(solo);
        var session = solo.CreateFacade(EngineHarness.Owner);
        var score = Register(session);
        var states = new List<MultiplayerSessionState>();
        using var watch = session.SubscribeChanged(snapshot => states.Add(snapshot.State));
        var objectChanges = new List<ReplicatedObjectChangeKind>();
        using var objects = session.SubscribeObjects(MarkerType, change => objectChanges.Add(change.Kind));
        Run(session.SubmitAsync(AddCommandType, new AddRequest(5)), harness, "solo play scores 5 inline");
        var marker = Require(session.SpawnObject(MarkerType, new CounterValue(1)), "solo play spawns a marker");
        var soloToken = session.CurrentSessionToken;
        That(score.Value.Value == 5 && marker.IsSpawned && session.Snapshot.IsHost(), "solo play has its own values and objects");

        solo.SwitchToClient(new MultiplayerSessionId("joining"));
        That(soloToken.IsCancellationRequested && !session.CurrentSessionToken.IsCancellationRequested, "joining retires the solo session's token");
        That(score.Value.Value == 0 && !marker.IsSpawned && objectChanges.Last() == ReplicatedObjectChangeKind.Despawned,
            "values return to their defaults and solo objects despawn");
        That(session.Snapshot.State == MultiplayerSessionState.Connecting && session.Snapshot.Participants.Count == 0, "the process waits to join");

        harness.Join(solo, "traveller");
        harness.Flush();
        That(session.Snapshot.State == MultiplayerSessionState.Ready && session.Snapshot.Id.Equals(new MultiplayerSessionId("white-box-session")),
            "the same facade joins the host's session");
        That(score.Value.Value == 3 && !session.Snapshot.IsHost(), "and sees the host's values, as a client");
        Run(session.SubmitAsync(AddCommandType, new AddRequest(1)), harness, "the client's command reaches the host");
        That(hostScore.Value.Value == 4 && score.Value.Value == 4, "registrations made in solo play work in the joined session");

        var joinedToken = session.CurrentSessionToken;
        solo.SwitchToSolo(new SoloSessionStart(new MultiplayerSessionId("solo-two"), new SessionSeed(2), new ParticipantId("traveller"), "Traveller"));
        That(joinedToken.IsCancellationRequested && session.Snapshot.State == MultiplayerSessionState.Ready && session.Snapshot.IsHost() &&
             session.Snapshot.Id.Equals(new MultiplayerSessionId("solo-two")) && score.Value.Value == 0, "leaving starts a fresh solo session");
        harness.Flush();
        That(!harness.Host.Snapshot.TryGetParticipant(new ParticipantId("traveller"), out var gone) || !gone!.IsConnected,
            "and the host sees the player leave");
        Run(session.SubmitAsync(AddCommandType, new AddRequest(2)), harness, "solo commands run inline again");
        That(score.Value.Value == 2 && hostScore.Value.Value == 4, "the two sessions' values stay apart");
        That(!states.Contains(MultiplayerSessionState.Ended), "observers never saw the session end");
    }

    internal static void AnEndedClientSessionReturnsToSolo()
    {
        var endings = new List<MultiplayerSessionEndReason>();
        var round = 0;
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive, options => options.AfterClientSessionEnds = (reason, _) =>
        {
            endings.Add(reason);
            return new SoloSessionStart(new MultiplayerSessionId("back-" + ++round), new SessionSeed((ulong)round), new ParticipantId("guest"), "Guest");
        });
        Register(harness.HostSession);
        var (client, session, _) = harness.AddClient("guest");
        var score = Register(session);
        var states = new List<MultiplayerSessionState>();
        using var watch = session.SubscribeChanged(snapshot => states.Add(snapshot.State));
        harness.Flush();
        That(session.Snapshot.State == MultiplayerSessionState.Ready && !session.Snapshot.IsHost(), "the guest joins");

        harness.Host.Kick(new ParticipantId("guest"), MultiplayerSessionEndReason.Kicked, "Behave.");
        harness.Flush();
        That(endings.SequenceEqual(new[] { MultiplayerSessionEndReason.Kicked }), "the hook hears why the session ended");
        That(!client.IsEnded && client.Role == EngineRole.Host && session.Snapshot.State == MultiplayerSessionState.Ready &&
             session.Snapshot.Id.Equals(new MultiplayerSessionId("back-1")) && session.Snapshot.IsHost(), "the guest is back in solo play");
        That(!states.Contains(MultiplayerSessionState.Ended), "without ever showing an ended session");
        Run(session.SubmitAsync(AddCommandType, new AddRequest(7)), harness, "solo play works at once");
        That(score.Value.Value == 7, "with the same registrations");

        // Join again, and this time the host ends the session for everyone.
        client.SwitchToClient(new MultiplayerSessionId("again"));
        harness.Join(client, "guest");
        harness.Flush();
        That(session.Snapshot.State == MultiplayerSessionState.Ready && score.Value.Value == 0, "the guest rejoins and takes the host's value");
        harness.Host.CloseConnections(MultiplayerSessionEndReason.HostEnded, "The host stopped hosting.");
        harness.Flush();
        That(endings.Last() == MultiplayerSessionEndReason.HostEnded && session.Snapshot.Id.Equals(new MultiplayerSessionId("back-2")) &&
             session.Snapshot.IsHost(), "the guest returns to solo play when the host stops");
    }

    internal static void AHostClosesItsConnectionsAndPlaysOn()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var hostScore = Register(harness.HostSession);
        var (first, firstSession, _) = harness.AddClient("first");
        var (second, _, _) = harness.AddClient("second");
        Register(firstSession);
        harness.Flush();
        Run(firstSession.SubmitAsync(AddCommandType, new AddRequest(6)), harness, "a client scores 6");
        var marker = Require(harness.HostSession.SpawnObject(MarkerType, new CounterValue(9)), "the host spawns a marker");
        var hostToken = harness.HostSession.CurrentSessionToken;
        That(harness.HostSession.Snapshot.Participants.Count == 3, "three players are in the session");

        harness.Host.CloseConnections(MultiplayerSessionEndReason.HostEnded, "The host stopped hosting.");
        harness.Flush();
        That(first.IsEnded && second.IsEnded && first.Snapshot.EndReason == MultiplayerSessionEndReason.HostEnded,
            "every client's session ends with the reason, as before when no solo session is configured");
        That(harness.HostSession.Snapshot.Participants.Count == 1 && harness.HostSession.Snapshot.IsHost() && !hostToken.IsCancellationRequested,
            "the host plays on alone in the same session, with departed players gone at once");
        That(hostScore.Value.Value == 6 && marker.IsSpawned, "keeping its values and objects");

        var (third, thirdSession, _) = harness.AddClient("third");
        Register(thirdSession);
        harness.Flush();
        That(thirdSession.Snapshot.State == MultiplayerSessionState.Ready && harness.HostSession.Snapshot.Participants.Count == 2, "and it can host again later");

        // A client whose session ended, with no solo session configured, can still be brought back to solo play.
        first.SwitchToSolo(Solo("after-the-end"));
        That(!first.IsEnded && first.Snapshot.State == MultiplayerSessionState.Ready && first.Snapshot.IsHost() &&
             first.Snapshot.EndReason == MultiplayerSessionEndReason.None, "an ended client returns to solo play");
        Run(firstSession.SubmitAsync(AddCommandType, new AddRequest(2)), harness, "and its facade works again");
    }

    internal static void RoleChangesRefuseWhatTheyCannotDo()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        Register(harness.HostSession);
        var (client, session, _) = harness.AddClient("guest");
        Register(session);
        harness.Flush();
        Throws<InvalidOperationException>(() => harness.Host.SwitchToClient(new MultiplayerSessionId("x")), "a host with clients cannot join elsewhere");
        Throws<InvalidOperationException>(() => client.SwitchToClient(new MultiplayerSessionId("x")), "a client is already a client");
        Throws<InvalidOperationException>(() => harness.Host.SwitchToSolo(Solo("y")), "a host is already playing solo");
        Throws<InvalidOperationException>(() => client.CloseConnections(MultiplayerSessionEndReason.HostEnded, "x"), "a client has no connections to close");

        using var headless = new EngineHarness();
        Throws<InvalidOperationException>(() => headless.Host.SwitchToClient(new MultiplayerSessionId("x")), "a headless host has no player to join with");

        var solo = SessionEngine.CreateHost(harness.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("solo"), new SessionSeed(1),
            new ParticipantId("solo"), "Solo", harness.NowMicros);
        harness.Track(solo);
        var soloSession = solo.CreateFacade(EngineHarness.Owner);
        var inside = false;
        Require(soloSession.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            new MultiplayerCommandType<AddRequest, CounterValue>("switch"),
            RequestCodec,
            CounterCodec,
            (_, _) =>
            {
                Throws<InvalidOperationException>(() => solo.SwitchToClient(new MultiplayerSessionId("x")), "no role change inside a transaction");
                inside = true;
                return OperationResult<CounterValue>.Success(new CounterValue(0));
            })), "register the switching command");
        Run(soloSession.SubmitAsync(new MultiplayerCommandType<AddRequest, CounterValue>("switch"), new AddRequest(1)), harness, "the command runs");
        That(inside, "the refusal was checked inside the handler");
    }

    internal static void JoinPoliciesSeeTheLinkARequestCameOver()
    {
        IEngineLink? seen = null;
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive, options => options.JoinPolicy = new CapturingPolicy(link => seen = link));
        var (_, session, clientEnd) = harness.AddClient("guest");
        harness.Flush();
        That(session.Snapshot.State == MultiplayerSessionState.Ready && seen != null && seen.PeerIdentity == "guest" && !ReferenceEquals(seen, clientEnd),
            "the policy sees the host's end of the link the request came over");
    }

    private static SoloSessionStart Solo(string id) => new(new MultiplayerSessionId(id), new SessionSeed(1), new ParticipantId("solo"), "Solo");

    private static bool IsHost(this MultiplayerSessionSnapshot snapshot) =>
        snapshot.LocalParticipantId.HasValue && snapshot.HostParticipantId.HasValue && snapshot.LocalParticipantId.Value.Equals(snapshot.HostParticipantId.Value);

    private static IReplicatedState<CounterValue> Register(IMultiplayerSession session)
    {
        var score = Require(session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)), "register score");
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddCommandType,
            RequestCodec,
            CounterCodec,
            (_, request) => score.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount))),
            PredictionMode.None)), "register add");
        Require(session.RegisterObjectType(new ReplicatedObjectTypeDefinition<CounterValue, AddRequest>(
            MarkerType,
            CounterCodec,
            RequestCodec,
            (_, current, _) => OperationResult<CounterValue>.Success(current))), "register marker");
        return score;
    }

    private static void Run<T>(Task<MultiplayerCommandConfirmation<T>> task, EngineHarness harness, string message) where T : class
    {
        harness.Flush();
        That(task.IsCompleted && task.Result.Result.Succeeded, message + ": " + (task.IsCompleted ? task.Result.Result.ErrorMessage : "pending"));
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }

    private static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Assertion failed: " + message);
    }

    private sealed class CapturingPolicy : IJoinPolicy
    {
        private readonly Action<IEngineLink> capture;

        internal CapturingPolicy(Action<IEngineLink> capture)
        {
            this.capture = capture;
        }

        public JoinDecision Evaluate(JoinRequestContext context)
        {
            capture(context.Link);
            return DefaultJoinPolicy.Instance.Evaluate(context);
        }
    }
}
