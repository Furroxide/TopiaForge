using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Owner facades on a client engine: retiring one owner settles only its own work, and a facade that closes while a
/// registration is being made never hands out a dead handle.
/// </summary>
internal static class EngineOwnerTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddCommandType = new("add");

    internal static void RetiringAnOwnerCancelsOnlyItsSubmissions()
    {
        using var harness = new EngineHarness(latencyTicks: 2);
        var executed = new List<string>();
        var hostScores = new Dictionary<string, IReplicatedState<CounterValue>>(StringComparer.Ordinal);
        foreach (var owner in new[] { "mod.a", "mod.b" })
        {
            var hostSession = harness.Host.CreateFacade(owner);
            var score = Require(hostSession.RegisterState(Score()), owner + " host score");
            hostScores[owner] = score;
            RegisterAdd(hostSession, (_, request) =>
            {
                executed.Add(owner + ":" + request.Amount);
                return Increment(score, request.Amount);
            });
        }

        var client = harness.AddClient("pilot");
        var ownerA = client.Engine.CreateFacade("mod.a");
        var ownerB = client.Engine.CreateFacade("mod.b");
        var mirrorA = Require(ownerA.RegisterState(Score()), "mod.a mirror");
        var mirrorB = Require(ownerB.RegisterState(Score()), "mod.b mirror");
        RegisterAdd(ownerA, (_, request) => Increment(mirrorA, request.Amount));
        RegisterAdd(ownerB, (_, request) => Increment(mirrorB, request.Amount));
        harness.Flush();

        var sentByA = ownerA.SubmitAsync(AddCommandType, new AddRequest(1));
        var sentByB = ownerB.SubmitAsync(AddCommandType, new AddRequest(10));
        harness.PumpAll();
        var unsentByA = ownerA.SubmitAsync(AddCommandType, new AddRequest(100));
        Assert(mirrorB.Value.Value == 10 && client.Engine.PendingSubmissionCount == 3, "three submissions wait, predictions applied");

        ownerA.Dispose();
        Assert(Cancelled(sentByA) && Cancelled(unsentByA), "retiring an owner completes both of its submissions as cancelled at once");
        Assert(!sentByB.IsCompleted && mirrorB.Value.Value == 10, "another owner's submission keeps waiting, its prediction still applied");
        Assert(client.Engine.PendingSubmissionCount == 2,
            "the unsent submission is withdrawn, while the sent one stays listed until the host's commit covers it");

        harness.Flush();
        Assert(executed.SequenceEqual(new[] { "mod.a:1", "mod.b:10" }),
            "the host executes what was already sent, and never the withdrawn submission (" + string.Join(", ", executed) + ")");
        Assert(sentByB.Result.Result.Succeeded && mirrorB.Value.Value == 10 && hostScores["mod.a"].Value.Value == 1,
            "the other owner's submission completes normally");
        Assert(client.Engine.PendingSubmissionCount == 0, "the retired owner's sent submission leaves once its commit arrives");
    }

    internal static void ARegistrationOutlivedByItsFacadeIsCancelled()
    {
        using var harness = new EngineHarness();
        var facade = harness.Host.CreateFacade("closing.mod");
        var codec = new ClosingCodec(facade);
        var result = facade.RegisterState(new ReplicatedStateDefinition<CounterValue>("closing", new CounterValue(1), codec));
        Assert(codec.Closed && !result.Succeeded && result.ErrorCode == ModErrorCode.Cancelled,
            "a registration whose facade closed while it was being made reports Cancelled instead of a dead handle");
        var reopened = harness.Host.CreateFacade("closing.mod");
        Assert(reopened.RegisterState(new ReplicatedStateDefinition<CounterValue>("closing", new CounterValue(2), CounterCodec)).Succeeded,
            "and the abandoned registration was disposed, so the id is free again");
    }

    private static ReplicatedStateDefinition<CounterValue> Score() => new("score", new CounterValue(0), CounterCodec);

    private static bool Cancelled(Task<MultiplayerCommandConfirmation<CounterValue>> submission) =>
        submission.IsCompleted && submission.Result.Result.ErrorCode == ModErrorCode.Cancelled;

    private static OperationResult<CounterValue> Increment(IReplicatedState<CounterValue> state, int amount) =>
        state.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + amount)));

    private static void RegisterAdd(
        EngineSessionFacade session,
        Func<MultiplayerCommandContext, AddRequest, OperationResult<CounterValue>> handler) =>
        Require(session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
            AddCommandType, RequestCodec, CounterCodec, handler, PredictionMode.Owner)), "register add");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }

    /// <summary>A codec that closes the facade the first time it encodes, as a concurrent shutdown would.</summary>
    private sealed class ClosingCodec : IMultiplayerCodec<CounterValue>
    {
        private readonly IDisposable facade;

        internal ClosingCodec(IDisposable facade)
        {
            this.facade = facade;
        }

        public int MaximumEncodedBytes => CounterCodec.MaximumEncodedBytes;

        internal bool Closed { get; private set; }

        public OperationResult<byte[]> Encode(CounterValue value)
        {
            if (!Closed)
            {
                Closed = true;
                facade.Dispose();
            }

            return CounterCodec.Encode(value);
        }

        public OperationResult<CounterValue> Decode(byte[] bytes) => CounterCodec.Decode(bytes);
    }
}
