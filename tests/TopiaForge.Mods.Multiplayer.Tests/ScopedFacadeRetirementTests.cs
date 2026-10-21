extern alias provider;

using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.DroneSample;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A mod has one session facade for its own context and one for each scope of it that resolves the session, such as a
/// gamemode's server or a player's participant controller. Closing a scope's facade retires only what that facade made;
/// the mod's last facade retires the mod as a whole. Before this, any facade's close retired the whole mod: stopping a
/// scope despawned the mod's own drone (one object, then none) and cancelled the mod's own pending submissions.
/// </summary>
internal static class ScopedFacadeRetirementTests
{
    private const string ModId = "io.example.drones";
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddCommandType = new("add");
    private static readonly ReplicatedObjectType<CounterValue, AddRequest> BoxType = new("box");

    /// <summary>The audit's reproduction, through the real provider and the drone sample's own contract.</summary>
    internal static void AScopeThatStopsLeavesTheModsDrone()
    {
        using var solo = new Provider("io.github.furroxide.topiaforge.multiplayer", clockMicros: () => 0);
        var modLifetime = new FakeModLifetime();
        var scopeLifetime = new FakeModLifetime();
        var modSession = (IMultiplayerSession)solo.CreateOwnerFacade(typeof(IMultiplayerSession), ModId, modLifetime);
        var drones = new DroneSampleMod();
        Require(drones.BindMultiplayer(modSession), "the mod binds its contract to its own session");
        var drone = drones.GetDroneObjects().Single();
        That(drone.IsSpawned, "the drone sample spawns its drone in the solo session");

        var scopeSession = (IMultiplayerSession)solo.CreateOwnerFacade(typeof(IMultiplayerSession), ModId, scopeLifetime);
        That(!ReferenceEquals(scopeSession, modSession) && solo.Engine.OpenFacadeCount(ModId) == 2,
            "a scope that resolves the session gets a second facade for the same mod");
        Require(scopeSession.RegisterObjectType(BoxDefinition()), "the scope registers an object type of its own");
        var scopeBox = Require(scopeSession.SpawnObject(BoxType, new CounterValue(7)), "the scope spawns an object of its own");

        scopeLifetime.Dispose();
        That(drone.IsSpawned && drones.GetDroneObjects().Count == 1,
            "stopping the scope leaves the mod's drone: one object before, one after, while the mod still runs");
        That(!scopeBox.IsSpawned, "and retires the object the scope spawned");
        That(solo.Engine.OpenFacadeCount(ModId) == 1, "the mod's own facade is the one left open");

        modLifetime.Dispose();
        That(!drone.IsSpawned && drones.GetDroneObjects().Count == 0, "once the mod itself stops, its drone goes");
        That(solo.Engine.OpenFacadeCount(ModId) == 0, "and no facade of the mod is left open");
    }

    /// <summary>On a host, a scope's facade despawns what it spawned, canonically, and every peer sees exactly that.</summary>
    internal static void ClosingAFacadeDespawnsOnlyWhatItSpawned()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var mod = harness.Host.CreateFacade(ModId);
        Require(mod.RegisterObjectType(BoxDefinition()), "the host's mod registers its boxes");
        var client = harness.AddClient("pilot");
        var clientMod = client.Engine.CreateFacade(ModId);
        Require(clientMod.RegisterObjectType(BoxDefinition()), "the player's copy of the mod registers its boxes");
        var clientScope = client.Engine.CreateFacade(ModId);
        harness.Flush();

        var drone = Require(mod.SpawnObject(BoxType, new CounterValue(1)), "the mod spawns its drone");
        var scope = harness.Host.CreateFacade(ModId);
        var first = Require(scope.SpawnObject(BoxType, new CounterValue(2)), "the gamemode scope spawns a box");
        var second = Require(scope.SpawnObject(BoxType, new CounterValue(3)), "and another");
        Require(scope.DespawnObject(second.Id), "and despawns one of them itself");
        harness.Flush();
        That(clientMod.GetObjects(BoxType).Count == 2, "the player sees the drone and the scope's box");

        scope.Dispose();
        harness.Flush();
        That(drone.IsSpawned && !first.IsSpawned && mod.GetObjects(BoxType).Single().Id.Equals(drone.Id),
            "closing the scope's facade despawns only the box it spawned");
        That(clientMod.GetObjects(BoxType).Single().Id.Equals(drone.Id), "and the player sees it leave, and nothing else");

        clientScope.Dispose();
        harness.Flush();
        That(clientMod.GetObjects(BoxType).Count == 1, "closing a scope's facade on the player keeps its copy of the mod's drone");

        clientMod.Dispose();
        harness.Flush();
        That(client.Engine.OpenFacadeCount(ModId) == 0 && clientMod.GetObjects(BoxType).Count == 0,
            "the player's last facade of the mod drops the mod's copies");
        That(drone.IsSpawned, "which leaves the host's drone alone");

        mod.Dispose();
        harness.Flush();
        That(!drone.IsSpawned && harness.Host.OpenFacadeCount(ModId) == 0, "the host's last facade of the mod despawns the rest");
    }

    /// <summary>On a joined player, a scope's facade cancels the commands it submitted; inputs belong to the mod as a whole.</summary>
    internal static void ClosingAFacadeCancelsOnlyItsOwnSubmissions()
    {
        using var harness = new EngineHarness(latencyTicks: 2);
        var executed = new List<int>();
        var hostMod = harness.Host.CreateFacade(ModId);
        var hostScore = Require(hostMod.RegisterState(Score()), "the host's score");
        Require(hostMod.RegisterCommand(AddDefinition((_, request) =>
        {
            executed.Add(request.Amount);
            return Increment(hostScore, request.Amount);
        })), "the host's add command");
        Require(hostMod.RegisterObjectType(BoxDefinition()), "the host's boxes");
        var client = harness.AddClient("pilot");
        var mod = client.Engine.CreateFacade(ModId);
        var mirror = Require(mod.RegisterState(Score()), "the player's mirror");
        Require(mod.RegisterCommand(AddDefinition((_, request) => Increment(mirror, request.Amount))), "the player's add command");
        Require(mod.RegisterObjectType(BoxDefinition()), "the player's boxes");
        var scope = client.Engine.CreateFacade(ModId);
        harness.Flush();
        var box = Require(hostMod.SpawnObject(BoxType, new CounterValue(0), new ParticipantId("pilot")), "a box the player owns");
        harness.Flush();
        var copy = mod.GetObjects(BoxType).Single();

        var byMod = mod.SubmitAsync(AddCommandType, new AddRequest(1));
        var byScope = scope.SubmitAsync(AddCommandType, new AddRequest(10));
        var input = copy.SubmitInputAsync(new AddRequest(5));
        scope.Dispose();
        That(Cancelled(byScope) && byScope.Result.Result.ErrorMessage == "The part of the mod that submitted this stopped.",
            "closing the scope's facade cancels the command it submitted, saying why");
        That(!byMod.IsCompleted && !input.IsCompleted, "and nothing the mod submitted: its command and its box's input still wait");

        harness.Flush();
        That(byMod.Result.Result.Succeeded && input.Result.Result.Succeeded && executed.SequenceEqual(new[] { 1 }),
            "the host runs the mod's command and input, and never the scope's withdrawn command");
        That(hostScore.Value.Value == 1 && box.State.Value == 5, "so the canonical values carry exactly those");

        var late = mod.SubmitAsync(AddCommandType, new AddRequest(100));
        var lateInput = copy.SubmitInputAsync(new AddRequest(1));
        mod.Dispose();
        That(Cancelled(late) && Cancelled(lateInput), "the player's last facade of the mod cancels everything of the mod, its inputs included");
        harness.Flush();
        That(executed.SequenceEqual(new[] { 1 }) && box.State.Value == 5, "and the host runs none of it");
    }

    private static ReplicatedStateDefinition<CounterValue> Score() => new("score", new CounterValue(0), CounterCodec);

    private static MultiplayerCommandDefinition<AddRequest, CounterValue> AddDefinition(
        Func<MultiplayerCommandContext, AddRequest, OperationResult<CounterValue>> handler) =>
        new(AddCommandType, RequestCodec, CounterCodec, handler, PredictionMode.Owner);

    private static ReplicatedObjectTypeDefinition<CounterValue, AddRequest> BoxDefinition() =>
        new(
            BoxType,
            CounterCodec,
            RequestCodec,
            (_, state, input) => OperationResult<CounterValue>.Success(new CounterValue(state.Value + input.Amount)),
            PredictionMode.None,
            30,
            16 * 1024,
            MultiplayerDelivery.Immediate,
            0);

    private static OperationResult<CounterValue> Increment(IReplicatedState<CounterValue> state, int amount) =>
        state.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + amount)));

    private static bool Cancelled<T>(Task<MultiplayerCommandConfirmation<T>> submission) where T : class =>
        submission.IsCompleted && submission.Result.Result.ErrorCode == ModErrorCode.Cancelled;

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }
}
