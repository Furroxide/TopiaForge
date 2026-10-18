using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.CounterSample;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The testing kit loads a mod with a generated contract as TopiaForge does in the game: the contract binds to the
/// context's multiplayer session after the context is attached and before <c>OnLoad</c>, and the binding ends with the
/// mod's lifetime. A test registers the session; it writes no binding code.
/// </summary>
internal static class TestingKitBindingTests
{
    internal static void TheRunnerBindsAfterTheContextAndBeforeOnLoad()
    {
        using var rig = MultiplayerTestRig.CreateStandalone();
        using var context = new FakeModContext();
        context.Extensions.Register(rig.Server.Session);
        var mod = new KitBindingMod();
        using var runner = new ModLifecycleRunner(mod, context);
        runner.Load();
        That(mod.Seen.SequenceEqual(new[] { "started for example.test-mod", "loaded" }),
            "the session hook runs at binding, with the context attached, and before OnLoad: " + string.Join(", ", mod.Seen));

        var bumped = mod.BumpAsync();
        That(bumped.IsCompleted && bumped.Result.Result.Succeeded && mod.Count == 1,
            "the generated command works as soon as the mod has loaded");
        runner.Unload();
        context.AssertNoLeaks();
    }

    internal static void TheBindingEndsWithTheMod()
    {
        using var rig = MultiplayerTestRig.CreateStandalone();
        using var context = new FakeModContext();
        context.Extensions.Register(rig.Server.Session);
        var mod = new KitBindingMod();
        using var runner = new ModLifecycleRunner(mod, context);
        runner.Load();
        runner.Unload();
        That(Throws<InvalidOperationException>(() => mod.BumpAsync(), "BindMultiplayer must succeed"),
            "unloading the mod releases the binding the runner made");
    }

    internal static void AModWithoutASessionLoadsUnbound()
    {
        using var context = new FakeModContext();
        var mod = new KitBindingMod();
        using var runner = new ModLifecycleRunner(mod, context);
        runner.Load();
        That(mod.Seen.SequenceEqual(new[] { "loaded" }), "nothing binds without a session: " + string.Join(", ", mod.Seen));
        That(Throws<InvalidOperationException>(() => mod.BumpAsync(), "BindMultiplayer must succeed"),
            "and the generated command says the contract is not bound");
        runner.Unload();
    }

    internal static void AFailedBindingFailsTheLoadAndSaysWhy()
    {
        using var first = MultiplayerTestRig.CreateStandalone();
        using var second = MultiplayerTestRig.CreateStandalone();
        // A mod without session hooks, so binding it by hand before it has a context is harmless.
        var mod = new CounterSampleMod();
        using var elsewhere = Require(mod.BindMultiplayer(first.Server.Session));
        using var context = new FakeModContext();
        context.Extensions.Register(second.Server.Session);
        using var runner = new ModLifecycleRunner(mod, context);
        var failure = Catch<InvalidOperationException>(runner.Load);
        That(failure != null && failure.Message.Contains("did not bind (Conflict)", StringComparison.Ordinal),
            "a contract bound to another session fails the load with the code and the reason: " + failure?.Message);
        That(runner.IsFinished && context.Logger.Entries.Count == 0, "OnLoad never runs, and the runner has cleaned up");
    }

    private static T Require<T>(OperationResult<T> result) where T : notnull
    {
        That(result.Succeeded, result.ErrorCode + ": " + result.ErrorMessage);
        return result.Value!;
    }

    private static bool Throws<T>(Action action, string message) where T : Exception =>
        Catch<T>(action)?.Message.Contains(message, StringComparison.Ordinal) == true;

    private static T? Catch<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return null;
        }
        catch (T exception)
        {
            return exception;
        }
    }
}

/// <summary>A contract mod whose session hook reads its context, as a mod's own hooks may.</summary>
[MultiplayerContract(Id = "test.kit-binding")]
public sealed partial class KitBindingMod : TopiaForgeMod
{
    [ReplicatedState("count")]
    private ReplicatedState<KitCount> count = new ReplicatedState<KitCount>(new KitCount());

    internal List<string> Seen { get; } = new List<string>();

    internal int Count => count.Value.Value;

    internal Task<MultiplayerCommandConfirmation<KitCount>> BumpAsync() => SubmitBumpAsync(new KitRequest());

    protected override void OnLoad() => Seen.Add("loaded");

    partial void OnMultiplayerSessionStarted(MultiplayerSessionSnapshot snapshot) =>
        Seen.Add("started for " + Context.Identity.Id);

    [MultiplayerCommand("bump", MaximumPerSecond = 4, MaximumPayloadBytes = 64)]
    private OperationResult<KitCount> Bump(MultiplayerCommandContext command, KitRequest request) =>
        count.Update(current => OperationResult<KitCount>.Success(new KitCount { Value = current.Value + 1 }));
}

/// <summary>The binding test's replicated count.</summary>
public sealed class KitCount
{
    /// <summary>Gets or sets the count.</summary>
    public int Value { get; set; }
}

/// <summary>A bump request; it carries nothing.</summary>
public sealed class KitRequest
{
}
